#!/usr/bin/env node
/**
 * 同步 README「关联脚本」中列出的第三方脚本到本地只读镜像，并记录来源。
 *
 *   node scripts/third-party/sync.mjs              # 下载 + 采集上游 commit + 更新 manifest
 *   node scripts/third-party/sync.mjs --check      # 只比对，不写盘；有差异退出码 1
 *   node scripts/third-party/sync.mjs --report     # 打印当前镜像的来源表（Markdown）
 *   node scripts/third-party/sync.mjs --only=betterx --only=douyin-dl
 *
 * 依赖：Node 18+ 内置 fetch，无第三方依赖。GitHub API 走未认证调用（每小时 60 次，
 * 六个脚本一次同步约 6 次）；限流或离线时只降级为"记版本不记 commit"，不影响同步。
 *
 * 设计取舍：
 *  - 落地文件名用稳定的 ASCII 名（manifest.file），上游文件名（manifest.upstreamFile）
 *    只记录不用 —— 上游若把版本号写进资产名（如 BetterX-v4.0.0.js），拿它当本地名会
 *    导致每次升级都多一个文件。
 *  - 不修改内容：脚本原样落盘，`@version` 只读不改。镜像不是分支，不做本地补丁。
 *  - 先试 manifest.url，再试 alternates（Release 资产常被墙，走镜像；大小与 sha256 兜底）。
 *  - 记 commit 而不只记版本：`@version` 说明不了"对应上游哪一次改动"，commit sha 才是
 *    后期判断"镜像落后多少、某行为从哪来的"时唯一能查的锚点。
 */

import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = dirname(fileURLToPath(import.meta.url));
const MANIFEST = join(ROOT, "manifest.json");
const OUT_DIR = ROOT;
const UA = "GMScript-sync/1.1 (+https://github.com/lytree/GMScript)";
const API = "https://api.github.com";

const args = process.argv.slice(2);
const checkOnly = args.includes("--check");
const reportOnly = args.includes("--report");
const only = args.filter((a) => a.startsWith("--only=")).map((a) => a.slice("--only=".length));

const sha256 = (buffer) => createHash("sha256").update(buffer).digest("hex");
const kb = (bytes) => `${(bytes / 1024).toFixed(1)} KB`;
const day = (iso) => (iso ? String(iso).slice(0, 10) : "—");

const green = (s) => `\x1b[32m${s}\x1b[0m`;
const red = (s) => `\x1b[31m${s}\x1b[0m`;
const yellow = (s) => `\x1b[33m${s}\x1b[0m`;
const dim = (s) => `\x1b[2m${s}\x1b[0m`;

const manifest = JSON.parse(await readFile(MANIFEST, "utf8"));

/* ---------------- 来源 ---------------- */

/** 从实际下载地址反推仓库与 ref，不依赖 manifest 里手写的字段。 */
function parseOrigin(entry, url) {
  const raw = url.match(/^https:\/\/raw\.githubusercontent\.com\/([^/]+)\/([^/]+)\/([^/]+)\//);
  if (raw) return { repo: `${raw[1]}/${raw[2]}`, refType: "branch", ref: raw[3] };
  const release = url.match(/github\.com\/([^/]+)\/([^/]+)\/releases\/download\/([^/]+)\//);
  if (release) return { repo: `${release[1]}/${release[2]}`, refType: "release", ref: release[3] };
  return { repo: entry.repo, refType: null, ref: null };
}

async function ghJson(path) {
  const response = await fetch(`${API}${path}`, {
    headers: { "user-agent": UA, accept: "application/vnd.github+json" },
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) throw new Error(`API ${response.status}`);
  return response.json();
}

/** 查该文件在上游当前对应的 commit。分支来源按 path 过滤；Release 来源直接按 tag 取。 */
async function resolveCommit({ repo, refType, ref }, upstreamFile) {
  try {
    if (refType === "branch") {
      const list = await ghJson(
        `/repos/${repo}/commits?path=${encodeURIComponent(upstreamFile)}&sha=${encodeURIComponent(ref)}&per_page=1`
      );
      const hit = Array.isArray(list) ? list[0] : null;
      if (!hit) return null;
      return { commit: hit.sha, committedAt: hit.commit?.committer?.date || hit.commit?.author?.date || "" };
    }
    if (refType === "release") {
      const hit = await ghJson(`/repos/${repo}/commits/${encodeURIComponent(ref)}`);
      return { commit: hit.sha, committedAt: hit.commit?.committer?.date || "" };
    }
  } catch {
    /* 限流 / 离线：来源信息降级，不影响同步 */
  }
  return null;
}

/* ---------------- 下载 ---------------- */

const readVersion = (text) => text.match(/^\/\/\s*@version\s+(\S+)\s*$/m)?.[1] || null;

async function fetchText(url, attempts = 2) {
  let lastError;
  for (let i = 0; i < attempts; i += 1) {
    try {
      const response = await fetch(url, {
        headers: { "user-agent": UA, accept: "*/*" },
        redirect: "follow",
        signal: AbortSignal.timeout(60_000),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const text = await response.text();
      if (!text.includes("==UserScript==")) throw new Error("响应不是油猴脚本（缺少 ==UserScript== 头）");
      return text;
    } catch (error) {
      lastError = error;
      // 4xx 是确定性失败（路径错、文件没了），重试无意义；网络错误才重试。
      if (/HTTP 4\d\d/.test(error.message)) break;
      if (i < attempts - 1) await new Promise((r) => setTimeout(r, 1200 * (i + 1)));
    }
  }
  throw lastError;
}

async function download(entry) {
  const failures = [];
  for (const url of [entry.url, ...(entry.alternates || [])]) {
    try {
      return { text: await fetchText(url), url };
    } catch (error) {
      failures.push(`${new URL(url).hostname}: ${error.message}`);
    }
  }
  throw new Error(failures.join(" / "));
}

const commitLabel = (source) => (source?.commit ? `@${source.commit.slice(0, 7)}` : dim("commit=?"));

/* ---------------- --report ---------------- */

/* ---------------- 来源表（stdout 与 SOURCES.md 共用） ---------------- */

function renderSources() {
  const rows = manifest.scripts.map((s) => {
    const source = s.source || {};
    const commit = source.commit
      ? `[\`${source.commit.slice(0, 7)}\`](https://github.com/${s.repo}/commit/${source.commit})`
      : "—";
    const ref = source.ref ? `\`${source.ref}\`` : "—";
    return `| ${s.name} | [${s.repo}](https://github.com/${s.repo}) | ${ref} | ${s.version} | ${commit} | ${day(
      source.committedAt
    )} | ${day(source.fetchedAt)} |`;
  });
  return [
    "| 脚本 | 上游 | ref | 版本 | 对应提交 | 提交日期 | 同步日期 |",
    "| --- | --- | --- | --- | --- | --- | --- |",
    ...rows,
  ].join("\n");
}

if (reportOnly) {
  console.log(renderSources());
  process.exit(0);
}

/* ---------------- 同步 ---------------- */

const targets = manifest.scripts.filter((s) => !only.length || only.includes(s.id));
if (!targets.length) {
  console.error(red(`没有匹配的脚本：${only.join(", ")}`));
  process.exit(1);
}
await mkdir(OUT_DIR, { recursive: true });

let dirty = false;
let failed = 0;
const rows = [];

for (const entry of targets) {
  const target = join(OUT_DIR, entry.file);
  let local = null;
  try {
    local = await readFile(target, "utf8");
  } catch {
    /* 首次同步 */
  }

  let text;
  let url;
  try {
    ({ text, url } = await download(entry));
  } catch (error) {
    failed += 1;
    rows.push([entry.id, red("下载失败"), error.message.slice(0, 60)]);
    continue;
  }

  const hash = sha256(Buffer.from(text, "utf8"));
  const version = readVersion(text);
  const same = local !== null && sha256(Buffer.from(local, "utf8")) === hash;

  // 用「主地址」而不是实际命中的镜像反推 ref：镜像可能落后，ref 应描述规范来源，
  // 实际由哪个镜像供的货，记在 resolvedUrl 里。
  const origin = parseOrigin(entry, entry.url);
  const upstream = await resolveCommit(origin, entry.upstreamFile);
  const prevCommit = entry.source?.commit || "";
  const commitMoved = Boolean(upstream?.commit && prevCommit && upstream.commit !== prevCommit);

  const source = {
    refType: origin.refType,
    ref: origin.ref,
    commit: upstream?.commit || prevCommit,
    committedAt: upstream?.committedAt || entry.source?.committedAt || "",
    resolvedUrl: url,
    // 内容变化的时刻；未变则沿用旧值。首次核对时补记当下 —— 否则报告里永远是"—"。
    fetchedAt: same ? entry.source?.fetchedAt || new Date().toISOString() : new Date().toISOString(),
  };
  const sourceChanged = JSON.stringify(source) !== JSON.stringify(entry.source || {});

  const versionNote =
    entry.version && version && entry.version !== version ? `${entry.version} → ${yellow(version)}` : version || "?";

  if (same) {
    if (sourceChanged && !checkOnly) {
      entry.source = source;
      dirty = true;
    }
    const hint = commitMoved ? dim("  （上游有新提交，本文件未变）") : "";
    rows.push([entry.id, green("未变"), `${versionNote}  ${commitLabel(source)}${hint}`]);
    continue;
  }

  dirty = true;
  if (checkOnly) {
    rows.push([
      entry.id,
      yellow(local ? "有更新" : "缺文件"),
      `${versionNote}  ${kb(Buffer.byteLength(text))}  ${commitLabel(source)}`,
    ]);
    continue;
  }

  await writeFile(target, text, "utf8");
  entry.version = version || entry.version;
  entry.size = Buffer.byteLength(text);
  entry.sha256 = hash;
  entry.source = source;
  rows.push([
    entry.id,
    local ? yellow("已更新") : green("已新增"),
    `${versionNote}  ${kb(entry.size)}  ${commitLabel(source)}  ${dim(`来自 ${new URL(url).hostname}`)}`,
  ]);
}

const width = Math.max(...rows.map((r) => r[0].length), 4);
for (const [id, state, detail] of rows) console.log(`${id.padEnd(width)}  ${state}  ${detail}`);

if (!checkOnly && dirty) {
  manifest.syncedAt = new Date().toISOString().slice(0, 10);
  await writeFile(MANIFEST, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  console.log(`\nmanifest.json 已更新（syncedAt=${manifest.syncedAt}）`);
} else if (checkOnly && dirty) {
  console.log("\n存在未同步的变更；去掉 --check 重新执行以写入。");
}

/* 来源表：与 manifest 同源生成，避免手工维护的第二份清单漂移 */
if (!checkOnly) {
  const sourcesMd = [
    "<!-- 由 scripts/third-party/sync.mjs 生成，请勿手工编辑。 -->",
    "",
    "# 镜像来源",
    "",
    "每个本地镜像对应上游的哪一次提交、哪个版本、何时同步。后期要判断「镜像落后多少」，",
    "「对应提交」那一列是唯一能查的锚点 —— 版本号只说明「第几版」，说明不了具体改了什么。",
    "",
    renderSources(),
    "",
    `- 生成时间：${new Date().toISOString().slice(0, 16).replace("T", " ")}（UTC）`,
    "- 完整下载地址、备用镜像、sha256 见 [`manifest.json`](manifest.json)",
    "- `ref` 为分支名时表示取自分支当前状态（会移动）；为 `vX.Y.Z` 时取自该 Release 资产（不会变）",
    "",
  ].join("\n");
  const sourcesPath = join(ROOT, "SOURCES.md");
  let previous = "";
  try {
    previous = await readFile(sourcesPath, "utf8");
  } catch {
    /* 首次生成 */
  }
  if (previous !== sourcesMd) {
    await writeFile(sourcesPath, sourcesMd, "utf8");
    console.log("SOURCES.md 已更新");
  }
}

if (failed) {
  console.error(red(`\n${failed} 个脚本下载失败。`));
  process.exit(1);
}
if (checkOnly && dirty) process.exit(1);
