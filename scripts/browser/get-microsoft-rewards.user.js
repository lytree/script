// ==UserScript==
// @name         Get Microsoft Rewards (修复/增强版)
// @namespace    https://github.com/lytree/GMScript
// @version      1.2.0
// @description  微软 Rewards 自动助手：集成本地自动搜索、每日活动与自动签到。业务日期按太平洋时间（PT）计算，修正与接口 UTC 日期差 8 小时导致的取不到当天任务；面板图标可拖拽、双击复位。
// @author       lytree (二次开发); 原作者 Muverix / 基于 QingJ《Get Microsoft Rewards》
// @license      MIT
// @icon         https://rewards.bing.com/rewardscdn/images/rewards.png
// @homepage     https://github.com/lytree/GMScript
// @supportURL   https://github.com/lytree/GMScript/issues
// @source       https://github.com/Muverix/Get-Microsoft-Rewards
// @original-author Muverix
// @original-source https://github.com/Muverix/Get-Microsoft-Rewards
// @match        https://www.bing.com/*
// @match        https://cn.bing.com/*
// @match        https://rewards.bing.com/*
// @match        https://login.live.com/*
// @grant        GM_xmlhttpRequest
// @grant        GM_addStyle
// @grant        GM_setValue
// @grant        GM_getValue
// @grant        GM_notification
// @grant        GM_cookie
// @grant        GM_registerMenuCommand
// @grant        GM_openInTab
// @grant        GM_log
// @grant        GM_info
// @connect      bing.com
// @connect      rewards.bing.com
// @connect      www.bing.com
// @connect      cn.bing.com
// @connect      login.live.com
// @connect      prod.rewardsplatform.microsoft.com
// @connect      hot.baiwumm.com
// @connect      hotapi.nntool.cc
// @connect      cnxiaobai.com
// @run-at       document-end
// @downloadURL  https://raw.githubusercontent.com/lytree/GMScript/main/userscripts/get-microsoft-rewards.user.js
// @updateURL    https://raw.githubusercontent.com/lytree/GMScript/main/userscripts/get-microsoft-rewards.user.js
// ==/UserScript==

/* =========================================================================
 * 📜 版权声明与免责条款 (License & Disclaimer)
 * -------------------------------------------------------------------------
 * 1. 本脚本基于 QingJ 的开源项目《Get Microsoft Rewards》二次开发。
 * 2. 借助 Gemini AI，参考并融合了 liyan20001124-byte 的《微软积分商城签到（全能智能重构版）》的核心逻辑与优化思路。
 * 3. 本脚本遵循 MIT 开源许可协议，仅供个人学习研究与自动化测试使用。
 * 4. 作者不对使用本脚本可能产生的任何风险（如账号积分变动或风控等）承担任何法律责任，请合理使用。
 *
 * 🔗 相关项目链接 (Fork & Original Sources)
 * -------------------------------------------------------------------------
 * - 本仓库（二次开发 / Fork）：    https://github.com/lytree/GMScript
 * - 原作者 Muverix 的项目仓库：    https://github.com/Muverix/Get-Microsoft-Rewards
 * - 上游 QingJ 原版项目：          https://github.com/QingJ/Get-Microsoft-Rewards
 * - 参考项目 liyan20001124-byte：  https://github.com/liyan20001124-byte/Microsoft-Rewards-Script
 * ========================================================================= */

(function () {
    'use strict';

    // 版本号与脚本头保持一致，日志里不再写死
    const SCRIPT_VERSION = (typeof GM_info !== 'undefined' && GM_info?.script?.version) || '1.2.0';

    // ========== 配置 ==========
    const CONFIG = {
        pc: { minDelay: 15000, maxDelay: 30000 },
        mobile: { minDelay: 20000, maxDelay: 35000 },
        ua: {
            pc: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36 Edg/151.0.0.0',
            mobile: 'Mozilla/5.0 (Linux; Android 16; MEIZU 20 Build/BQ2A.251110.001-BP2A.250605.031.A3; ) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/150.0.7871.181 Mobile Safari/537.36 BingSapphire/33.8.440724006'
        },
        mobileApi: {appId: 'SAAndroid/34.0.440821006'},
        read: { minDelay: 8000, maxDelay: 15000 },
        // 多个热搜API备用源
        hotApis: [
            { url: 'https://hot.baiwumm.com/api/', sources: ['weibo', 'douyin', 'baidu', 'zhihu', 'toutiao'] },
            { url: 'https://hotapi.nntool.cc/', sources: ['weibo', 'douyin', 'baidu', 'toutiao', 'zhihu'] },
            { url: 'https://cnxiaobai.com/DailyHotApi/', sources: ['weibo', 'douyin', 'baidu', 'toutiao'] }
        ],
        keywords: ["天气预报", "今日新闻", "体育赛事", "股票行情", "电影推荐", "科技资讯", "美食食谱", "旅游攻略"],
        // 暂停机制配置
        pause: {
            enabled: true,           // 是否启用暂停机制
            interval: 10,            // 每执行多少次搜索后暂停
            duration: 15 * 60 * 1000 // 暂停时长（毫秒），15分钟
        }
    };

    // ========== 状态 ==========
    let state = {
        level: 1, levelName: '', points: 0,
        readRunning: false,
        pcCur: 0, pcMax: 0,
        mobileCur: 0, mobileMax: 0,
        promosTotal: 0, promosDone: 0,
        signDone: false, signPoints: -1,
        readCur: 0, readMax: 0,
        running: false, allRunning: false,
        busyCount: 0,
        accessToken: null,
        accessTokenExpiresAt: 0,
        tokenPromise: null,
        searchToken: null,
        updating: false,
        updatingPromise: null,
        checkedIn: false,
        lastStatus: '',
        country: '',
        // 搜索进度和暂停状态
        searchCur: 0, searchMax: 0,
        searchCount: 0,           // 当前搜索计数（用于暂停判断）
        isPaused: false,          // 是否处于暂停状态
        pauseEndTime: 0,          // 暂停结束时间戳
        countdownStartTime: 0,    // 倒计时开始时间（精确计时）
        countdownDuration: 0,     // 倒计时总时长
        manualPaused: false,      // 手动暂停
        pausePromise: null,
        pauseResolver: null
    };
    let dashboard = null;
    let loginCookie = '';

    // ========== 进度保存/恢复 ==========
    const STORAGE_KEY = 'mr_search_progress';
    // 旧版本把折叠图标坐标存在这里；现已改为不持久化，仅用于启动时清理残留
    const PANEL_POS_KEY = 'mr_panel_pos';
    const PANEL_GAP = 8;   // 面板与视口边缘的最小间距

    function saveProgress() {
        const today = getDateHyphen();
        const data = {
            date: today,
            searchCount: state.searchCount
        };
        GM_setValue(STORAGE_KEY, JSON.stringify(data));
    }

    function loadProgress() {
    try {
        const saved = GM_getValue(STORAGE_KEY);
        if (!saved) return null;
        const data = JSON.parse(saved);
        if (data.date === getDateHyphen()) {
            state.searchCount = data.searchCount || 0;
            return data;
        } else {
            resetProgress(); // 👈 跨天重置本地和内存中的计数
        }
    } catch (e) { }
    return null;
}

    async function withAccessTokenRequest(requestFn) {
        // 1. 获取 Token，若为空尝试强制刷新一次
        let token = await getAccessToken();
        if (!token) {
            console.warn('[Rewards] 初始 Token 为空，尝试强制刷新...');
            token = await getAccessToken({ forceRefresh: true });
        }

        if (!token) {
            log('⚠️ 未获取到活动Token，请确认登录状态或刷新页面重试');
            return null;
        }

        try {
            const res = await requestFn(token);

            // 2. 检查返回结果字符串（适配 gmRequest 正常返回 401 错误 JSON 的情况）
            if (typeof res === 'string' && (res.includes('invalid_token') || res.includes('Unauthorized') || res.includes('\"code\":401'))) {
                console.warn('[Rewards] 响应中检测到 Token 失效，重新获取 Token 中...');
                state.accessToken = null;
                state.accessTokenExpiresAt = 0;
                token = await getAccessToken({ forceRefresh: true });
                if (token) {
                    return await requestFn(token);
                }
            }
            return res;
        } catch (e) {
            // 3. 捕获网络层抛出的 401 异常
            const is401 = (e && e.status === 401) || 
                          (e && e.responseStatus === 401) || 
                          (e && e.message && (e.message.includes('401') || e.message.includes('Unauthorized')));

            if (is401) {
                console.warn('[Rewards] 捕获到网络层 401 异常，重新获取 Token 中...');
                state.accessToken = null;
                state.accessTokenExpiresAt = 0;
                token = await getAccessToken({ forceRefresh: true });
                if (token) {
                    return await requestFn(token);
                }
            }
            throw e;
        }
    }
    function resetProgress() {
        state.searchCount = 0;
        GM_setValue(STORAGE_KEY, '');
    }

    // ========== 工具函数 ==========
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    const randomPick = arr => arr[Math.floor(Math.random() * arr.length)];
    const randomRange = (min, max) => Math.floor(Math.random() * (max - min + 1)) + min;
    const uuid = () => crypto.randomUUID();

    // ========== 业务日期 ==========
    // 坑：浏览器本地时区（北京 UTC+8）和 Rewards 的口径对不上，差得还不止一处：
    //   · Rewards 的每日任务/签到日界是太平洋时间 PT 午夜（北京 15:00 夏令时 / 16:00 冬令时）
    //   · dashboard 接口里 dailySetPromotions 的 key 有时按 UTC 出，比北京慢 8 小时
    // 所以这里不用 new Date().getDate() 那套本地值，统一用 Intl 按时区取日历日。
    const TZ_PT = 'America/Los_Angeles';
    const TZ_UTC = 'UTC';
    const pad2 = n => String(n).padStart(2, '0');

    const localParts = () => {
        const d = new Date();
        return { y: d.getFullYear(), m: d.getMonth() + 1, d: d.getDate() };
    };

    const tzFmtCache = new Map();
    function tzFormatter(tz) {
        let f = tzFmtCache.get(tz);
        if (!f) {
            f = new Intl.DateTimeFormat('en-US', {
                timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit'
            });
            tzFmtCache.set(tz, f);
        }
        return f;
    }

    function partsIn(tz) {
        try {
            const p = tzFormatter(tz).formatToParts(new Date());
            const pick = type => Number(p.find(x => x.type === type)?.value) || 0;
            return { y: pick('year'), m: pick('month'), d: pick('day') };
        } catch (e) {
            return localParts();   // 极老内核不支持 timeZone 时退回本地，至少不崩
        }
    }

    const dateStrOf = ({ y, m, d }) => `${m}/${d}/${y}`;            // 与接口 key 同格式：M/D/YYYY
    const ymdOf = ({ y, m, d }) => `${y}-${pad2(m)}-${pad2(d)}`;    // 进度存储用
    const ymdCompactOf = ({ y, m, d }) => `${y}${pad2(m)}${pad2(d)}`;
    const partsNum = p => p.y * 10000 + p.m * 100 + p.d;
    const maxParts = (...list) => list.reduce((a, b) => (partsNum(b) > partsNum(a) ? b : a));

    // 业务日：以 PT 为准（签到、搜索重置都跟这个走）
    const getDateStr = () => dateStrOf(partsIn(TZ_PT));
    const getDateHyphen = () => ymdOf(partsIn(TZ_PT));

    // 取当前业务日的候选 key：先 PT，再 UTC。两者相同时只留一个。
    const getBizDateKeys = () => {
        const pt = dateStrOf(partsIn(TZ_PT));
        const utc = dateStrOf(partsIn(TZ_UTC));
        return pt === utc ? [pt] : [pt, utc];
    };

    // 从 dailySetPromotions 里挑当天那一批。原来取不到就 Object.values().flat()，
    // 会把前几天的任务一起并进来重复执行；这里改成只回退到「不晚于业务日的最新一批」。
    function pickDailySet(map) {
        if (!map || typeof map !== 'object') return [];
        for (const key of getBizDateKeys()) {
            if (Array.isArray(map[key]) && map[key].length) return map[key];
        }
        const limit = Math.max(...getBizDateKeys().map(k => Date.parse(k)));
        const usable = Object.keys(map)
            .filter(k => Array.isArray(map[k]) && map[k].length && Number.isFinite(Date.parse(k)))
            .map(k => ({ ts: Date.parse(k), v: map[k] }))
            .filter(e => e.ts <= limit)
            .sort((a, b) => b.ts - a.ts);
        return usable.length ? usable[0].v : [];
    }

    const isJSON = s => { try { JSON.parse(s); return true; } catch { return false; } };
    const generateDapiId = () => {
    const raw1 = uuid().replace(/-/g, '');
    const raw2 = uuid().replace(/-/g, '');
    return (raw1 + raw2).slice(0, 56);
    };

    // GM_xmlhttpRequest 封装
    async function gmRequest(options) {
        const retries = options.retries ?? 2;
        const retryDelay = options.retryDelay ?? 1000;
        let attempt = 0;

        const shouldRetry = (err) => {
            const status = err?.status || 0;
            return status === 0 || status === 429 || status >= 500 || err?.message === 'Timeout';
        };

        while (true) {
            try {
                return await new Promise((resolve, reject) => {
                    GM_xmlhttpRequest({
                        timeout: 20000,
                        ...options,
                        onload: xhr => {
                            if (xhr.status >= 200 && xhr.status < 300) {
                                resolve(options.returnUrl ? xhr.finalUrl : xhr.responseText);
                            } else if (xhr.status >= 300 && xhr.status < 400) {
                                const loc = (xhr.responseHeaders || '').match(/Location:\s*(.*?)\s*[\r\n]/i);
                                resolve(loc ? loc[1] : xhr.responseText);
                            } else {
                                const err = new Error(`HTTP ${xhr.status}`);
                                err.status = xhr.status;
                                err.responseText = xhr.responseText;
                                err.finalUrl = xhr.finalUrl;
                                reject(err);
                            }
                        },
                        onerror: () => {
                            const err = new Error('Network Error');
                            err.status = 0;
                            reject(err);
                        },
                        ontimeout: () => {
                            const err = new Error('Timeout');
                            err.status = 0;
                            reject(err);
                        }
                    });
                });
            } catch (e) {
                if (attempt >= retries || !shouldRetry(e)) throw e;
                const delay = retryDelay * Math.pow(2, attempt);
                attempt++;
                await sleep(delay + randomRange(0, 250));
            }
        }
    }

    // 获取热搜词（支持多源自动切换）
    async function getHotQuery() {
        // 打乱API顺序，随机选择
        const apis = [...CONFIG.hotApis].sort(() => Math.random() - 0.5);

        for (const api of apis) {
            try {
                const src = randomPick(api.sources);
                const res = await gmRequest({ method: 'GET', url: api.url + src, timeout: 8000 });
                const data = JSON.parse(res);
                if (data.code === 200 && data.data?.length) {
                    const title = randomPick(data.data).title || '';
                    // 随机截取长度，更自然
                    const len = randomRange(8, 25);
                    return title.substring(0, len);
                }
            } catch { /* 尝试下一个API */ }
        }
        // 所有API都失败，使用本地关键词
        return `${randomPick(CONFIG.keywords)} ${Math.random().toString(36).slice(2, 6)}`;
    }

    // Cookie 管理
    function getCookies(url) {
    return new Promise(resolve => {
        try {
            if (typeof GM_cookie === 'undefined') {
                console.warn('[Rewards] GM_cookie不存在');
                return resolve('');
            }

            GM_cookie.list({ url }, cookies => {
                if (!cookies || !cookies.length) {
                    console.warn('[Rewards] Cookie为空:', url);
                    return resolve('');
                }

                const str = cookies
                    .map(c => `${c.name}=${c.value}`)
                    .join('; ');

                //console.log('[Rewards] Cookie长度:', str.length, url);
                resolve(str);
            });

        } catch (e) {
            console.error('[Rewards] Cookie获取失败', e);
            resolve('');
        }
    });
}

    function deleteCookie(name, host = 'bing.com') {
        return new Promise(resolve => {
            if (typeof GM_cookie !== 'undefined') {
                GM_cookie('delete', { url: `https://${host}`, name }, resolve);
            } else resolve();
        });
    }

    // ========== 样式 (极简版) ==========
    GM_addStyle(`
        #mr-panel {
            position: fixed;
            bottom: 20px;
            right: 20px;
            z-index: 2147483647;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background: #fff;
            border: 1px solid #e0e0e0;
            box-shadow: 0 4px 12px rgba(0,0,0,0.1);
            border-radius: 8px;
            user-select: none;
            -webkit-user-select: none;
            -webkit-touch-callout: none;
            -webkit-tap-highlight-color: transparent;
        }

        /* 收起状态 */
        #mr-panel.collapsed {
            width: 44px;
            height: 44px;
            border-radius: 50%;
            cursor: grab;
            touch-action: none;
            display: flex;
            justify-content: center;
            align-items: center;
            background: #fff;
            color: #0078d4;
            box-shadow: 0 4px 16px rgba(0,120,212,0.3);
            border: 1px solid #e0e0e0;
            transition: all 0.2s;
        }
        #mr-panel.collapsed:hover { transform: scale(1.1); box-shadow: 0 6px 20px rgba(0,120,212,0.4); }
        #mr-panel.collapsed svg { width: 24px; height: 24px; fill: currentColor; }
        #mr-panel.collapsed #mr-container { display: none; }

        /* 拖拽中：禁用过渡与 hover 缩放，否则位置会跟手不同步 */
        #mr-panel.dragging,
        #mr-panel.dragging:hover {
            transition: none !important;
            transform: none !important;
            cursor: grabbing !important;
            box-shadow: 0 10px 28px rgba(0,120,212,0.45) !important;
        }

        /* 展开状态 */
        #mr-panel:not(.collapsed) {
            width: 300px;
            max-height: calc(100vh - 16px);
            display: flex;
            flex-direction: column;
        }
        #mr-panel:not(.collapsed) svg { display: none; }
        #mr-panel:not(.collapsed) #mr-container {
            display: flex;
            flex-direction: column;
            min-height: 0;
            overflow: hidden;
        }
        #mr-panel:not(.collapsed) #mr-body {
            min-height: 0;
            overflow-y: auto;
        }

        #mr-header {
            flex-shrink: 0;
            padding: 12px 16px;
            border-bottom: 1px solid #eee;
            display: flex;
            justify-content: space-between;
            align-items: center;
            background: #f8f9fa;
            border-radius: 8px 8px 0 0;
            cursor: move;
            touch-action: none;
        }
        #mr-title { font-weight: 600; font-size: 14px; color: #333; }
        #mr-close { cursor: pointer; color: #999; font-size: 18px; line-height: 1; }
        #mr-close:hover { color: #333; }

        #mr-body { padding: 16px; }

        .mr-row { display: flex; justify-content: space-between; margin-bottom: 8px; font-size: 12px; color: #555; }
        .mr-val { font-weight: 600; color: #333; }

        .mr-progress-bg { height: 4px; background: #eee; border-radius: 2px; margin-bottom: 12px; overflow: hidden; }
        .mr-bar { height: 100%; background: #0078d4; }

        .mr-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin-top: 16px; }
        .mr-btn {
            border: 1px solid #d0d0d0;
            background: #fff;
            color: #333;
            padding: 6px 10px;
            border-radius: 4px;
            font-size: 12px;
            cursor: pointer;
        }
        .mr-btn:hover { background: #f0f0f0; border-color: #bbb; }
        .mr-btn:active { background: #e5e5e5; }
        .mr-full { grid-column: span 2; background: #0078d4; color: #fff; border: none; }
        .mr-full:hover { background: #006abc; }

        /* Auth */
        #mr-auth { margin-bottom: 12px; padding: 10px; background: #fff8e1; border: 1px solid #ffe0b2; border-radius: 4px; }
        .mr-input { width: 100%; padding: 4px; border: 1px solid #ccc; font-size: 11px; margin: 4px 0; }

        /* Log */
        #mr-log {
            margin-top: 12px;
            height: 80px;
            background: #fafafa;
            border: 1px solid #eee;
            padding: 8px;
            font-size: 10px;
            color: #666;
            overflow-y: auto;
            font-family: monospace;
        }
    `);

    // ========== UI 结构 ==========
    const panel = document.createElement('div');
    panel.id = 'mr-panel';
    panel.className = 'collapsed'; // 默认折叠
    // 礼盒 SVG
    const svgIcon = `<svg viewBox="0 0 24 24"><path d="M20 6h-3V4c0-1.1-.9-2-2-2H9c-1.1 0-2 .9-2 2v2H4c-1.1 0-2 .9-2 2v3h2v9c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-9h2V8c0-1.1-.9-2-2-2zm-9-2h2v2h-2V4zm0 16H6v-9h5v9zm6 0h-5v-9h5v9zm1.5-11H16V7h2v2zm-4.5 0h-2V7h2v2zm-4.5 0H7V7h2v2zm-3.5 0H4V7h1.5v2z"/></svg>`;

    panel.innerHTML = `
        ${svgIcon}
        <div id="mr-container">
            <div id="mr-header">
                <div id="mr-title"><span>🎁</span> Microsoft Rewards</div>
                <div id="mr-close">×</div>
            </div>

            <div id="mr-body">
                <!-- 状态 -->
                <div class="mr-row">
                    <span>等级 <span id="mr-level" style="font-weight:600">-</span></span>
                    <span style="color:#d83b01"><span id="mr-points">0</span> pts</span>
                </div>

                <!-- 进度 -->
                <div class="mr-row"><span>🔍 搜索进度</span><span id="mr-pc">0/0</span></div>
                <div class="mr-progress-bg"><div class="mr-bar" id="mr-pc-bar"></div></div>

                <div class="mr-row"><span>📅 每日签到</span><span id="mr-sign-status" style="font-weight:600">未签到</span></div>

                <div class="mr-row"><span>📖 阅读任务</span><span id="mr-read">0/0</span></div>
                <div class="mr-progress-bg"><div class="mr-bar" id="mr-read-bar" style="background:#ff8c00"></div></div>

                <!-- 授权 -->
                <div id="mr-auth" style="display:none">
                    <div style="font-weight:bold;margin-bottom:5px">⚠️ 需授权</div>
                    <button class="mr-btn" id="mr-auth-link" style="width:100%">🔗 获取授权码</button>
                    <input type="text" id="mr-auth-in" class="mr-input" placeholder="粘贴URL...">
                    <button class="mr-btn" id="mr-auth-save" style="width:100%">保存</button>
                    <button class="mr-btn" id="mr-auth-reopen" style="width:100%;margin-top:4px">🔄 重新打开授权链接</button>
                </div>

                <!-- 按钮 -->
                <div class="mr-grid">
                    <button id="btn-search" class="mr-btn">🔍 搜索</button>
                    <button id="btn-promo" class="mr-btn">🎯 活动 <span id="val-promo">0/0</span></button>
                    <button id="btn-sign" class="mr-btn">✅ 签到</button>
                    <button id="btn-read" class="mr-btn">📖 阅读</button>
                    <button id="btn-all" class="mr-btn mr-full">🚀 一键全部执行</button>
                    <button id="btn-reset" class="mr-btn mr-full" style="background:#fff4e5;border-color:#ffd591;color:#d4380d">🧹 清理授权 + 脚本全部状态</button>
                </div>

                <!-- 日志 -->
                <div id="mr-log"></div>
            </div>
        </div>
    `;
    document.body.appendChild(panel);

    // 元素引用
    const $ = id => document.querySelector(id);
    const nodes = {
        panel: $('#mr-panel'),
        close: $('#mr-close'),
        level: $('#mr-level'),
        points: $('#mr-points'),
        pc: $('#mr-pc'),
        pcBar: $('#mr-pc-bar'),
        signStatus: $('#mr-sign-status'),
        read: $('#mr-read'),
        readBar: $('#mr-read-bar'),
        btnSearch: $('#btn-search'),
        btnPromo: $('#btn-promo'),
        valPromo: $('#val-promo'),
        btnSign: $('#btn-sign'),
        btnRead: $('#btn-read'),
        btnAll: $('#btn-all'),
        boxAuth: $('#mr-auth'),
        btnAuthLink: $('#mr-auth-link'),
        inAuth: $('#mr-auth-in'),
        btnAuthSave: $('#mr-auth-save'),
        btnAuthReopen: $('#mr-auth-reopen'),
        btnReset: $('#btn-reset'),
        logBox: $('#mr-log')
    };

    // ========== 交互逻辑 ==========

    // ---------- 面板定位 / 拖拽 ----------
    // 折叠图标可拖，但位置只在当前页面会话内有效：刷新 / 换页后回到默认的右下角，不做持久化。
    // 展开时以图标为锚点自动避让视口边缘；标题栏也可拖，双击标题栏复位。
    const headerEl = $('#mr-header');
    const DRAG_THRESHOLD = 4;

    let iconPos = null;     // 本次页面会话内的折叠图标左上角（仅内存）
    let iconDrag = null;    // 图标拖拽会话
    let headerDrag = null;  // 标题栏拖拽会话
    let swallowClick = false;   // 拖拽松手后浏览器会补一个 click，不能当成"展开"

    const clamp = (v, min, max) => Math.min(Math.max(v, min), Math.max(min, max));
    const panelSize = () => ({
        w: nodes.panel.offsetWidth || 46,
        h: nodes.panel.offsetHeight || 46
    });
    // 用 offsetLeft/offsetTop 而不是 getBoundingClientRect：折叠图标的 hover 缩放会污染 rect，
    // 而 offset* 是纯布局值，跟手不会偏。
    const panelOrigin = () => ({ left: nodes.panel.offsetLeft, top: nodes.panel.offsetTop });

    function setIconPos(left, top) {
        iconPos = { left: Math.round(left), top: Math.round(top) };
    }

    function placePanel(left, top) {
        nodes.panel.style.left = `${Math.round(left)}px`;
        nodes.panel.style.top = `${Math.round(top)}px`;
        nodes.panel.style.right = 'auto';
        nodes.panel.style.bottom = 'auto';
    }

    function clearPanelPlacement() {
        nodes.panel.style.left = '';
        nodes.panel.style.top = '';
        nodes.panel.style.right = '';
        nodes.panel.style.bottom = '';
    }

    function expandPanel() {
        if (!nodes.panel.classList.contains('collapsed')) return;
        const anchor = panelOrigin();
        nodes.panel.classList.remove('collapsed');

        // 展开后尺寸变大，靠近右/下边缘时整体往里收，保证不溢出视口
        const size = panelSize();
        placePanel(
            clamp(Math.min(anchor.left, window.innerWidth - size.w - PANEL_GAP), PANEL_GAP, window.innerWidth - size.w - PANEL_GAP),
            clamp(Math.min(anchor.top, window.innerHeight - size.h - PANEL_GAP), PANEL_GAP, window.innerHeight - size.h - PANEL_GAP)
        );
    }

    function collapsePanel() {
        if (nodes.panel.classList.contains('collapsed')) return;
        nodes.panel.classList.add('collapsed');
        if (iconPos) placePanel(iconPos.left, iconPos.top);
        else clearPanelPlacement();   // 从未拖过 → 回到 CSS 默认的右下角
    }

    function resetPanelPosition() {
        iconPos = null;
        iconDrag = null;
        headerDrag = null;
        nodes.panel.classList.remove('dragging');
        clearPanelPlacement();
        log('📍 面板位置已复位');
    }

    // 展开 / 收起
    nodes.panel.onclick = () => {
        if (swallowClick) { swallowClick = false; return; }   // 只吞掉拖拽收尾那一次
        expandPanel();
    };
    nodes.close.onclick = (e) => {
        e.stopPropagation();
        collapsePanel();
    };

    const isInteractive = (target) =>
        target instanceof Element && !!target.closest('button, input, #mr-close');

    function beginDrag(event, source) {
        if (event.pointerType === 'mouse' && event.button !== 0) return;
        const collapsed = nodes.panel.classList.contains('collapsed');
        if (source === 'icon' ? !collapsed : collapsed) return;
        if (source === 'header' && isInteractive(event.target)) return;

        if (source === 'icon') nodes.panel.classList.add('dragging');   // 去掉 hover 缩放，拖动时视觉更稳
        const origin = panelOrigin();
        const size = panelSize();
        const host = source === 'icon' ? nodes.panel : headerEl;

        const session = {
            id: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            originLeft: origin.left,
            originTop: origin.top,
            w: size.w,
            h: size.h,
            moved: false
        };
        if (source === 'icon') iconDrag = session;
        else headerDrag = session;

        try { host.setPointerCapture(event.pointerId); } catch (e) { }
    }

    function moveDrag(event, session) {
        if (!session || event.pointerId !== session.id) return;
        const dx = event.clientX - session.startX;
        const dy = event.clientY - session.startY;

        if (!session.moved) {
            if (Math.hypot(dx, dy) < DRAG_THRESHOLD) return;   // 抖动阈值，避免点击被判成拖动
            session.moved = true;
        }
        event.preventDefault();

        placePanel(
            clamp(session.originLeft + dx, 0, window.innerWidth - session.w),
            clamp(session.originTop + dy, 0, window.innerHeight - session.h)
        );
    }

    function endDrag(event, source) {
        const session = source === 'icon' ? iconDrag : headerDrag;
        if (!session || event.pointerId !== session.id) return;
        const host = source === 'icon' ? nodes.panel : headerEl;
        try { host.releasePointerCapture(event.pointerId); } catch (e) { }

        if (session.moved) {
            const origin = panelOrigin();
            // 折叠态记图标坐标；拖动标题栏后把面板左上角作为新的图标锚点（仅当前会话）
            setIconPos(origin.left, origin.top);
            swallowClick = true;
            setTimeout(() => { swallowClick = false; }, 300);   // 兜底，避免标志卡住
        }

        if (source === 'icon') {
            iconDrag = null;
            nodes.panel.classList.remove('dragging');
        } else {
            headerDrag = null;
        }
    }

    nodes.panel.addEventListener('pointerdown', (e) => {
        if (nodes.panel.classList.contains('collapsed')) beginDrag(e, 'icon');
    });
    nodes.panel.addEventListener('pointermove', (e) => moveDrag(e, iconDrag));
    nodes.panel.addEventListener('pointerup', (e) => endDrag(e, 'icon'));
    nodes.panel.addEventListener('pointercancel', (e) => endDrag(e, 'icon'));

    headerEl.addEventListener('pointerdown', (e) => beginDrag(e, 'header'));
    headerEl.addEventListener('pointermove', (e) => moveDrag(e, headerDrag));
    headerEl.addEventListener('pointerup', (e) => endDrag(e, 'header'));
    headerEl.addEventListener('pointercancel', (e) => endDrag(e, 'header'));
    headerEl.addEventListener('dblclick', (e) => {
        if (isInteractive(e.target)) return;
        resetPanelPosition();
    });
    headerEl.title = '按住拖动 · 双击复位';

    window.addEventListener('resize', () => {
        if (!nodes.panel.style.left) return;
        const size = panelSize();
        const left = clamp(nodes.panel.offsetLeft, 0, window.innerWidth - size.w);
        const top = clamp(nodes.panel.offsetTop, 0, window.innerHeight - size.h);
        placePanel(left, top);
        if (nodes.panel.classList.contains('collapsed')) setIconPos(left, top);
    });

    // 位置不跨刷新保留：每次加载都从默认右下角开始，顺手清掉旧版本写进 GM 存储的残留坐标
    iconPos = null;
    try {
        if (GM_getValue(PANEL_POS_KEY, '')) GM_setValue(PANEL_POS_KEY, '');
    } catch (e) { }

    if (typeof GM_registerMenuCommand === 'function') {
        GM_registerMenuCommand('🎁 展开 / 收起面板', () => {
            if (nodes.panel.classList.contains('collapsed')) expandPanel();
            else collapsePanel();
        });
        GM_registerMenuCommand('📍 复位面板位置', resetPanelPosition);
    }

    const LOG_MAX_LINES = 200;
    const log = (msg) => {
        const box = nodes.logBox;
        if (!box) { console.log('[Rewards]', msg); return; }
        // 用户正向上翻看历史日志时不要强行拉到底部
        const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 24;

        const div = document.createElement('div');
        div.textContent = `[${new Date().toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit' })}] ${msg}`;
        box.appendChild(div);
        while (box.childNodes.length > LOG_MAX_LINES) {
            box.removeChild(box.firstChild);
        }
        if (atBottom) box.scrollTop = box.scrollHeight;
    };

    const updateAllButton = () => {
        if (!nodes.btnAll) return;
        if (state.manualPaused) {
            nodes.btnAll.textContent = '▶️ 继续执行';
        } else if (state.running || state.allRunning || state.busyCount > 0) {
            nodes.btnAll.textContent = '⏸️ 暂停执行';
        } else {
            nodes.btnAll.textContent = '🚀 一键全部执行';
        }
    };

    const setManualPause = (paused, opts = {}) => {
        if (paused === state.manualPaused) return;
        const silent = !!opts.silent;
        state.manualPaused = paused;
        if (paused) {
            if (!state.pausePromise) {
                state.pausePromise = new Promise(resolve => { state.pauseResolver = resolve; });
            }
        } else if (state.pauseResolver) {
            const resolve = state.pauseResolver;
            state.pauseResolver = null;
            state.pausePromise = null;
            resolve();
        }
        updateAllButton();
        if (!silent) {
            log(paused ? '⏸️ 已手动暂停' : '▶️ 已继续执行');
        }
    };

    const markBusy = (delta) => {
        state.busyCount = Math.max(0, state.busyCount + delta);
        updateAllButton();
    };

    const waitWhilePaused = async () => {
        if (!state.manualPaused) return 0;
        if (!state.pausePromise) {
            state.pausePromise = new Promise(resolve => { state.pauseResolver = resolve; });
        }
        const start = Date.now();
        await state.pausePromise;
        return Date.now() - start;
    };

    // 授权相关
    const AUTH_URL = 'https://login.live.com/oauth20_authorize.srf?client_id=0000000040170455&scope=service::prod.rewardsplatform.microsoft.com::MBI_SSL&response_type=code&redirect_uri=https://login.live.com/oauth20_desktop.srf';

    nodes.btnAuthLink.onclick = () => window.open(AUTH_URL, '_blank');

    // 重新打开授权链接（与获取授权码行为一致）
    nodes.btnAuthReopen.onclick = () => {
        try {
            window.open(AUTH_URL, '_blank');
            log('🔄 已重新打开授权链接');
        } catch (e) {
            log('❌ 打开授权链接失败: ' + e.message);
        }
    };

    // 清理授权 + 脚本全部状态
    async function clearAllState() {
        // 1. 停止正在运行的任务
        state.running = false;
        state.allRunning = false;
        if (state.manualPaused) setManualPause(false, { silent: true });

        // 2. 清空本地存储中的授权 / 进度 / 签到缓存
        try { GM_setValue('auth_code', ''); } catch (e) {}
        try { GM_setValue('refresh_token', ''); } catch (e) {}
        try { GM_setValue(STORAGE_KEY, ''); } catch (e) {}
        try { GM_setValue('Config.lastSignDate', ''); } catch (e) {}

        // 3. 清空内存中的令牌与状态
        state.accessToken = null;
        state.accessTokenExpiresAt = 0;
        state.tokenPromise = null;
        state.searchToken = null;
        state.searchCount = 0;
        state.checkedIn = false;
        state.pcCur = 0; state.pcMax = 0;
        state.searchCur = 0; state.searchMax = 0;
        state.readCur = 0; state.readMax = 0;
        state.promosDone = 0; state.promosTotal = 0;
        state.signDone = false; state.signPoints = -1;
        state.lastStatus = '';
        state.isPaused = false;
        state.pauseEndTime = 0;
        state.countdownStartTime = 0;
        state.countdownDuration = 0;

        // 4. 复位 UI
        if (nodes.inAuth) nodes.inAuth.value = '';
        if (nodes.boxAuth) nodes.boxAuth.style.display = 'none';
        if (nodes.btnSearch) nodes.btnSearch.textContent = '🔍 搜索';
        updateAllButton();
        render();

        log('🧹 已清理授权 + 脚本全部状态');

        // 5. 立即重新拉取数据（如已登录会进入未授权流程并弹回授权面板）
        try { await updateData(); } catch (e) {}
    }
    nodes.btnReset.onclick = clearAllState;

    nodes.btnAuthSave.onclick = async () => {
        const val = nodes.inAuth.value.trim();
        try {
            const urlObj = new URL(val);
            const code = urlObj.searchParams.get('code');
            
            if (code) {
                GM_setValue('auth_code', code);
                GM_setValue('refresh_token', ''); // 清空旧 token
                log('✅ 授权码已保存，正在尝试获取 Token...');
                nodes.boxAuth.style.display = 'none';
                nodes.inAuth.value = '';
                
                // 立即强制触发 Token 获取
                const token = await getAccessToken({ forceRefresh: true });
                if (token) {
                    log('🎉 授权成功！');
                    if (typeof updateData === 'function') {
                        await updateData(); 
                    }
                } else {
                    log('❌ 授权码兑换失败，请重新获取');
                }
            } else {
                log('❌ 格式错误：未在URL中找到 code 参数，请复制完整的白屏网址');
            }
        } catch (e) {
            log('❌ 格式错误：请直接粘贴你复制的完整网页链接 (https://...)');
        }
    };
    updateAllButton();

    // 自动抓取微软 OAuth Code
    async function checkAuth() {
        // return null;//test
        try {
            let cookie = '';
            if (typeof getCookies === 'function') {
                cookie = await getCookies('https://login.live.com');
            }

            const authUrl = 'https://login.live.com/oauth20_authorize.srf?client_id=0000000040170455&scope=service::prod.rewardsplatform.microsoft.com::MBI_SSL&response_type=code&redirect_uri=https://login.live.com/oauth20_desktop.srf';

            const res = await new Promise((resolve, reject) => {
                GM_xmlhttpRequest({
                    method: 'GET',
                    url: authUrl,
                    headers: {
                        'Cookie': cookie,
                        'User-Agent': (typeof CONFIG !== 'undefined' && CONFIG.ua?.pc) ? CONFIG.ua.pc : 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
                    },
                    onload: r => resolve(r),
                    onerror: e => reject(e)
                });
            });

            // 1. 优先从跳转后的 URL 截取 code
            const finalUrl = res.finalUrl || '';
            let codeMatch = finalUrl.match(/[?&]code=([^&]+)/);
            if (codeMatch) return decodeURIComponent(codeMatch[1]);

            // 2. 次之从响应内容或 Header 截取
            const body = res.responseText || '';
            codeMatch = body.match(/[?&]code=([^&"'\s]+)/);
            if (codeMatch) return decodeURIComponent(codeMatch[1]);

            return null;
        } catch (e) {
            console.error('[Rewards] checkAuth 异常:', e);
            return null;
        }
    }

    // ========== 核心逻辑 (简化版引用) ==========

    // 数据刷新
    // 统一获取 Token 的逻辑
    async function getSearchToken() {
        if (!state.searchToken) await updateData();
        return state.searchToken;
    }

    // 数据刷新函数（修复 allP 报错 + 本地签到日期双重校验）
    async function updateData() {
    if (state.updatingPromise) return state.updatingPromise;
    state.updating = true;
    state.updatingPromise = (async () => {
        try {
            const cookie = await getCookies('https://rewards.bing.com');

            let htmlRes = '';
            try {
                htmlRes = await gmRequest({
                    method: 'GET',
                    url: 'https://rewards.bing.com/',
                    headers: {
                        'User-Agent': CONFIG.ua.pc,
                        'Cookie': cookie,
                        'Referer': 'https://www.bing.com/'
                    }
                });

                const tokenMatch = htmlRes.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/i)
                                || htmlRes.match(/value="([^"]+)"[^>]*name="__RequestVerificationToken"/i)
                                || htmlRes.match(/RequestVerificationToken["']?\s*[:=]\s*["']([^"']+)["']/i)
                                || htmlRes.match(/"verificationToken"\s*:\s*["']([^"']+)["']/i);
                if (tokenMatch) {
                    state.searchToken = tokenMatch[1];
                }
            } catch (e) {}

            let dashData = null;

            if (htmlRes) {
                const dashMatch = htmlRes.match(/var\s+dashboard\s*=\s*({[\s\S]*?});\s*var/i)
                               || htmlRes.match(/window\.__INITIAL_STATE__\s*=\s*({[\s\S]*?});/i);
                if (dashMatch) {
                    try { dashData = JSON.parse(dashMatch[1]); } catch (e) {}
                }
            }

            if (!dashData || !dashData.userProfile) {
                try {
                    const token = await getAccessToken();
                    if (token) {
                        const dapiRes = await gmRequest({
                            method: 'GET',
                            url: `https://prod.rewardsplatform.microsoft.com/dapi/me?channel=SAAndroid&options=613&_=${Date.now()}`,
                            headers: {
                                'Authorization': `Bearer ${token}`,
                                'X-Rewards-AppId': CONFIG.mobileApi.appId,
                                'X-Rewards-IsMobile': 'true'
                            }
                        });
                        const parsed = JSON.parse(dapiRes);
                        dashData = parsed.response?.dashboard || parsed.response || parsed;
                    }
                } catch (e) {
                    console.error('[Rewards] DAPI 请求错误:', e);
                }
            }

            if (!dashData) {
                log('❌ 获取数据失败，请确认已登录 rewards.bing.com');
                return;
            }

            dashboard = dashData;

            // =========================================================================
            // 1. 解析等级与积分（捕获 profile.attributes.level）
            // =========================================================================
            const profile = dashData.userProfile || dashData.profile || {};
            const balance = dashData.balance;
            const userStatus = dashData.userStatus || dashData.status || {};

            if (typeof balance === 'number') {
                state.points = balance;
            } else if (balance && typeof balance === 'object') {
                state.points = balance.availablePoints ?? balance.points ?? 0;
            } else {
                state.points = profile.availablePoints ?? profile.points ?? dashData.availablePoints ?? 0;
            }

            //直接从 profile.attributes.level 获取新版等级代号（如 newLevel3）
            const rawLevelCandidate = 
                profile?.attributes?.level ||
                userStatus?.levelInfo?.activeLevel ||
                userStatus?.level ||
                (typeof balance === 'object' && balance?.levelInfo?.activeLevel) ||
                (typeof balance === 'object' && balance?.level) ||
                profile?.levelInfo?.activeLevel ||
                profile?.level ||
                dashData?.levelInfo?.activeLevel ||
                dashData?.level ||
                '';

            const rawLevelStr = String(rawLevelCandidate);

            // 🔍 控制台确认日志
            //console.log('[Rewards 最终等级匹配]: 捕获到等级代号 ->', rawLevelStr);

            // 微软新版体系精准匹配（newLevel3 / Level3 / Gold = 金牌）
            if (/Gold|金牌|Level3|newLevel3/i.test(rawLevelStr) || rawLevelStr === '3') {
                state.levelName = '🏅 金牌';
                state.level = 3;
            } else if (/Silver|银牌|Level2|newLevel2/i.test(rawLevelStr) || rawLevelStr === '2') {
                state.levelName = '🥈 银牌';
                state.level = 2;
            } else if (/Member|Bronze|会员|铜牌|Level1|newLevel1/i.test(rawLevelStr) || rawLevelStr === '1') {
                state.levelName = '🥉 会员';
                state.level = 1;
            } else {
                state.level = parseInt(rawLevelStr.replace(/\D/g, '')) || 1;
                state.levelName = rawLevelStr ? rawLevelStr : `Lv.${state.level}`;
            }
            // 2. 搜索进度解析
            let searchCur = 0, searchMax = 0;
            const candidateCounters = [];

            if (Array.isArray(dashData.counters)) candidateCounters.push(...dashData.counters);
            if (Array.isArray(dashData.userCounters)) candidateCounters.push(...dashData.userCounters);
            if (Array.isArray(dashData.userStatus?.counters)) candidateCounters.push(...dashData.userStatus.counters);

            const promos = [
                ...(Array.isArray(dashData.promotions) ? dashData.promotions : []),
                ...(Array.isArray(dashData.morePromotions) ? dashData.morePromotions : []),
                ...(Array.isArray(dashData.userStatus?.promotions) ? dashData.userStatus.promotions : [])
            ];

            promos.forEach(p => {
                const name = ((p.name || p.id || p.title || p.type || '') + '').toLowerCase();
                if (name.includes('search')) {
                    candidateCounters.push(p);
                }
            });

            candidateCounters.forEach(item => {
                if (!item || typeof item !== 'object') return;
                const cur = Number(item.pointProgress ?? item.progress ?? item.current ?? item.points ?? item.attributes?.progress) || 0;
                const max = Number(item.pointProgressMax ?? item.pointMax ?? item.max ?? item.maxPoints ?? item.attributes?.max) || 0;
                searchCur += cur;
                searchMax += max;
            });

            state.searchCur = searchCur;
            state.searchMax = searchMax > 0 ? searchMax : (searchCur > 0 ? searchCur : (state.level > 1 ? 90 : 60));
            state.pcCur = state.searchCur;
            state.pcMax = state.searchMax;

            // 3. 统一收集所有活动列表
            const today = getDateStr();
            const dailySet = pickDailySet(dashData.dailySetPromotions);
            const morePromos = dashData.morePromotions || dashData.promotions || [];
            const activities = dashData.activities || [];

            const allP = [
                ...(Array.isArray(dailySet) ? dailySet : []),
                ...(Array.isArray(morePromos) ? morePromos : []),
                ...(Array.isArray(activities) ? activities : [])
            ];

            // 4. 签到状态解析
            const lastSignDate = GM_getValue("Config.lastSignDate", "");
            const isLocalSigned = lastSignDate === today;

            let isRemoteSigned = false;

            // A. 从签到活动卡片判断
            const checkInPromo = allP.find(p => {
                if (!p || typeof p !== 'object') return false;
                const str = JSON.stringify(p).toLowerCase();
                if (str.includes('fosbury')) return false;         
                return (
                    str.includes('checkin') ||
                    str.includes('signin') ||
                    str.includes('dailycheckin') ||
                    str.includes('签到')
                );
            });

            if (checkInPromo) {
                const cur = Number(
                    checkInPromo.pointProgress ??
                    checkInPromo.progress ??
                    checkInPromo.attributes?.progress ??
                    0
                );

                const max = Number(
                   checkInPromo.pointProgressMax ??
                   checkInPromo.max ??
                   checkInPromo.attributes?.max ??
                   0
               );

               const complete =
                   checkInPromo.complete === true ||
                   checkInPromo.complete === 'true' ||
                   checkInPromo.attributes?.complete === true ||
                   checkInPromo.attributes?.complete === 'true' ||
                   checkInPromo.attributes?.complete === 'True';
           
              // 只有明确完成，或者存在有效进度且达到最大值，才算签到
              if (complete || (max > 0 && cur >= max)) {
                  isRemoteSigned = true;
              }
            }

            // B. 只接受明确的 claimedToday
            if (!isRemoteSigned) {
                try {
                    const claimed =
                        dashData?.claimedToday === true ||
                        dashData?.claimedToday === 'true' ||
                        dashData?.userStatus?.claimedToday === true ||
                        dashData?.userStatus?.claimedToday === 'true';
                
                    if (claimed) {
                        isRemoteSigned = true;
                    }
                } catch (e) {}
            }

            // C. 本地记录只允许匹配“今天”
            state.checkedIn = isRemoteSigned || isLocalSigned;

            // =========================================================================
            // 5. 阅读任务与活动列表统计（优先从活动列表中提取真实进度，修复日志进度不一致问题）
            const readOfferId = 'ENUS_readarticle3_30points';
            const readPromo = allP.find(p => {
                if (!p || typeof p !== 'object') return false;
                const offerId = p.attributes?.offerid || p.offerid || '';
                return offerId === readOfferId || offerId.toLowerCase().includes('readarticle');
            });

            if (readPromo && readPromo.attributes) {
                state.readCur = parseInt(readPromo.attributes.progress) || 0;
                state.readMax = parseInt(readPromo.attributes.max) || 30;
            } else {
                if (state.readCur === undefined) state.readCur = 0;
                if (state.readMax === undefined) state.readMax = 30;
            }

            state.promosTotal = allP.length;
            state.promosDone = allP.filter(p => p.complete || p.attributes?.complete === 'true' || p.attributes?.complete === 'True').length;

            render();
            const newStatus = `${state.level}-${state.points}-${state.searchCur}-${state.searchMax}`;
            if (state.lastStatus !== newStatus) {
                log(`✓ 数据已更新: Lv.${state.level} ${state.points}pts`);
                state.lastStatus = newStatus;
            }
        } catch (e) {
            console.error('updateData error:', e);
            log(`⚠️ 获取数据出错: ${e.message}`);
        } finally {
            state.updating = false;
            state.updatingPromise = null;
        }
    })();
    return state.updatingPromise;
}
    // UI 渲染函数（精准适配原生节点映射）
    function render() {
        if (!nodes.level) return;

        // 1. 等级与积分（优先显示金牌/银牌名称）
        nodes.level.textContent = state.levelName || `Lv.${state.level}`;
        nodes.points.textContent = (state.points || 0).toLocaleString();
        // 2. 搜索进度
        const pcCur = state.searchCur ?? state.pcCur ?? 0;
        const pcMax = state.searchMax ?? state.pcMax ?? 60;
        if (nodes.pc) nodes.pc.textContent = `${pcCur}/${pcMax}`;
        if (nodes.pcBar) nodes.pcBar.style.width = pcMax ? `${(pcCur / pcMax) * 100}%` : '0%';

        // 3. 签到状态
        if (nodes.signStatus) {
            nodes.signStatus.textContent = state.checkedIn ? '已签到' : '未签到';
            nodes.signStatus.style.color = state.checkedIn ? '#107c41' : '#d83b01';
        }

        // 4. 阅读任务
        const readCur = state.readCur ?? 0;
        const readMax = state.readMax ?? 30;
        if (nodes.read) nodes.read.textContent = `${readCur}/${readMax}`;
        if (nodes.readBar) nodes.readBar.style.width = readMax ? `${(readCur / readMax) * 100}%` : '0%';

        // 5. 活动任务
        if (nodes.valPromo) {
            nodes.valPromo.textContent = `${state.promosDone || 0}/${state.promosTotal || 0}`;
        }
    }

    // Token 获取
    // 获取/刷新 OAuth Access Token
    async function getAccessToken(opts = {}) {
        const forceRefresh = !!opts.forceRefresh;
        const now = Date.now();

        if (!forceRefresh && state.accessToken && state.accessTokenExpiresAt && now < (state.accessTokenExpiresAt - 60000)) {
            return state.accessToken;
        }

        if (state.tokenPromise) {
            return state.tokenPromise;
        }

        state.tokenPromise = (async () => {
            try {
                if (forceRefresh) {
                    state.accessToken = null;
                    state.accessTokenExpiresAt = 0;
                }

                let refreshToken = GM_getValue('refresh_token', '');//test
                let body = '';

                if (refreshToken) {
                    body = `client_id=0000000040170455&refresh_token=${encodeURIComponent(refreshToken)}&scope=service::prod.rewardsplatform.microsoft.com::MBI_SSL&grant_type=REFRESH_TOKEN`;
                } else {
                    log('🔑 正在获取微软活动 Token...');
                    
                    // 1. 尝试静默获取
                    let code = await checkAuth();
                    
                    // 2. 静默获取失败，检查是否有手动保存的 auth_code
                    if (!code) {
                        const savedCode = GM_getValue('auth_code', '');
                        if (savedCode) {
                            code = savedCode;
                            GM_setValue('auth_code', ''); // 授权码是一次性的，取出后立即清空
                            log('📥 已读取手动输入的授权码');
                        }
                    }

                    // 3. 仍然没有可用 code 时，弹出手动授权提示面板
                    if (!code) {
                        log('⚠️ 静默授权失败，请点击面板中的“获取授权码”链接手动授权');
                        if (nodes.boxAuth) nodes.boxAuth.style.display = 'block';
                        return null;
                    }

                    body = `client_id=0000000040170455&code=${encodeURIComponent(code)}&redirect_uri=https://login.live.com/oauth20_desktop.srf&grant_type=authorization_code`;
                }

                const res = await gmRequest({
                    method: 'POST',
                    url: 'https://login.live.com/oauth20_token.srf',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded'
                    },
                    data: body
                });

                const data = typeof res === 'string' ? JSON.parse(res) : res;

                if (data.access_token) {
                    state.accessToken = data.access_token;
                    state.accessTokenExpiresAt = Date.now() + (Number(data.expires_in) || 3600) * 1000;
                    if (data.refresh_token) {
                        GM_setValue('refresh_token', data.refresh_token);
                    }
                    log('🔑 活动 Token 建立成功');
                    return data.access_token;
                } else {
                    // 若旧 refresh_token 换取失败，清空并重新走 Code 授权
                    if (refreshToken) {
                        console.warn('[Rewards] RefreshToken 失效，尝试重新 Code 授权...');
                        GM_setValue('refresh_token', '');
                        state.tokenPromise = null;
                        return await getAccessToken({ forceRefresh: true });
                    }
                    log('⚠️ Token 获取失败: ' + (data.error_description || data.error || '未知错误'));
                }
            } catch (e) {
                log('❌ Auth Error: ' + e.message);
            } finally {
                state.tokenPromise = null;
            }
            return null;
        })();

        return state.tokenPromise;
    }
    //签到
    const runSign = async () => {
        nodes.btnSign.disabled = true;
        markBusy(1);
        await waitWhilePaused();
        log('⏳ 正在尝试 App 移动端签到...');

        const region = (state.country || dashboard?.userStatus?.country || 'cn').toLowerCase();

        // 优先获取动态 offerId，无则使用 Sapphire 默认 ID
        let offerId = "Gamification_Sapphire_DailyCheckIn";
        const allPromos = [
            ...pickDailySet(dashboard?.dailySetPromotions),
            ...(dashboard?.morePromotions || []),
            ...(dashboard?.activities || [])
        ];
        const targetPromo = allPromos.find(p => {
            const str = (JSON.stringify(p) || '').toLowerCase();
            return str.includes('checkin') || str.includes('签到') || str.includes('dailycheckin');
        });
        if (targetPromo) {
            offerId = targetPromo.offerId || targetPromo.id || targetPromo.offerid || offerId;
        }

        try {
            const res = await withAccessTokenRequest(token => gmRequest({
                method: 'POST',
                url: 'https://prod.rewardsplatform.microsoft.com/dapi/me/activities',
                headers: {
                    'content-type': 'application/json; charset=UTF-8',
                    'user-agent': CONFIG.ua.mobile,
                    'authorization': `Bearer ${token}`,
                    'x-rewards-appid': CONFIG.mobileApi.appId,
                    'x-rewards-ismobile': 'true',
                    'x-rewards-country': region,
                    'x-rewards-language': 'zh',
                    'x-rewards-partnerid': 'startapp',
                    'x-rewards-flights': 'rwgobig'
                },
                data: JSON.stringify({
                    amount: 1,
                    id: generateDapiId(),
                    type: 103,
                    country: region,
                    channel: 'SAAndroid',
                    attributes: {
                        offerid: offerId
                    }
                })
            }));

            if (res) {
                const data = typeof res === 'string' ? JSON.parse(res) : res;
                const response = data.response || {};
                const act = response.activity;

                const pts = Number(act?.p ?? act?.points ?? 0);
                const hasOfferId = act?.a?.offerid || act?.attributes?.offerid;

                if (act && pts > 0) {
                    log(`✅ App 签到成功 +${pts}分`);
                    GM_setValue("Config.lastSignDate", getDateStr());
                    state.checkedIn = true;
                    render();
                } else if (response.isDuplicate || (act && pts === 0 && hasOfferId)) {
                    log('ℹ️ 今日已经完成过签到');
                    GM_setValue("Config.lastSignDate", getDateStr());
                    state.checkedIn = true;
                    render();
                } else {
                    log('⚠️ 签到未成功触发，请稍后重试');
                    console.log('[Rewards 未成功签到响应]:', data);
                }
            } else {
                log('❌ 签到无响应');
            }

            await updateData();
        } catch (e) {
            log('❌ 签到出错: ' + e.message);
        } finally {
            nodes.btnSign.disabled = false;
            markBusy(-1);
        }
    };
    nodes.btnSign.onclick = runSign;

    
    // 阅读
    const runRead = async () => {
    nodes.btnRead.disabled = true;
    state.readRunning = true;
    markBusy(1);
    await waitWhilePaused();
    log('⏳ 开始阅读任务...');

    const region = (state.country || dashboard?.userStatus?.country || 'cn').toLowerCase();
    const readOfferId = 'ENUS_readarticle3_30points';

    try {
        // 2. 获取当前真实阅读进度
        const info = await withAccessTokenRequest(token => gmRequest({
            url: 'https://prod.rewardsplatform.microsoft.com/dapi/me?channel=SAAndroid&options=613',
            headers: {
                'authorization': `Bearer ${token}`,
                'user-agent': CONFIG.ua.mobile,
                'x-rewards-appid': CONFIG.mobileApi.appId,
                'x-rewards-ismobile': 'true',
                'x-rewards-country': region,
                'x-rewards-language': 'zh',
                'x-rewards-partnerid': 'startapp',
                'x-rewards-flights': 'rwgobig'
            }
        }));

        if (info) {
            const d = typeof info === 'string' ? JSON.parse(info) : info;
            const p = d.response?.promotions?.find(x => x.attributes?.offerid === readOfferId);

            if (p && p.attributes) {
                let cur = parseInt(p.attributes.progress) || 0;
                let max = parseInt(p.attributes.max) || 30;
                
                // 判断步长：若 max 为 30 则每篇 +3 分（共 10 篇）；若 max 为 10 则每篇 +1 分
                const step = max >= 30 ? 3 : 1;
                const totalArticles = Math.ceil(max / step);

                state.readCur = cur;
                state.readMax = max;
                render();

                if (cur >= max) {
                    log(`✅ 阅读任务已完成 (${cur}/${max})`);
                } else {
                    log(`📖 当前阅读进度 ${cur}/${max}，准备刷文章...`);

                    let currentArticleIdx = Math.floor(cur / step);
                    while (cur < max) {
                       // 方式一：改用已有的运行状态判定
                        if (!state.readRunning && !state.allRunning) break;

                        await waitWhilePaused();
                        currentArticleIdx++;
                        log(`📖 正在阅读第 ${currentArticleIdx}/${totalArticles} 篇文章 (进度: ${cur}/${max})...`);

                        const res = await withAccessTokenRequest(token => gmRequest({
                            method: 'POST',
                            url: 'https://prod.rewardsplatform.microsoft.com/dapi/me/activities',
                            headers: {
                                'content-type': 'application/json; charset=utf-8',
                                'user-agent': CONFIG.ua.mobile,
                                'authorization': `Bearer ${token}`,
                                'x-rewards-appid': CONFIG.mobileApi.appId,
                                'x-rewards-ismobile': 'true',
                                'x-rewards-country': region,
                                'x-rewards-language': 'zh',
                                'x-rewards-partnerid': 'startapp',
                                'x-rewards-flights': 'rwgobig'
                            },
                            data: JSON.stringify({
                                amount: 1,
                                country: region,
                                id: generateDapiId(),
                                type: 101,
                                channel: 'SAAndroid',
                                attributes: { offerid: readOfferId }
                            })
                        })).catch(() => null);

                        if (res) {
                            const resData = typeof res === 'string' ? JSON.parse(res) : res;
                            console.log(`[Rewards 阅读上报响应 ${currentArticleIdx}/${totalArticles}]:`, resData);

                            // 阅读成功后：进度 +step(3)，积分固定 +3
                            cur = Math.min(max, cur + step);
                            state.points = (state.points || 0) + 3;
                            state.readCur = cur;
                            render();

                            log(`🎉 第 ${currentArticleIdx}/${totalArticles} 篇阅读成功 (+3积分) | 进度: ${state.readCur}/${max} | 当前总积分: ${state.points}`);
                        } else {
                            log(`⚠️ 第 ${currentArticleIdx}/${totalArticles} 篇上报未收到有效响应，继续下一篇...`);
                        }

                        if (cur < max) {
                            // 👈 优化：使用 CONFIG 中的随机时间，并打印休眠日志
                            const readDelay = typeof randomRange === 'function' 
                                ? randomRange(CONFIG.read.minDelay, CONFIG.read.maxDelay) 
                                : 10000;
                            log(`☕ 随机等待 ${(readDelay / 1000).toFixed(1)} 秒...`);
                            await sleep(readDelay);
                        }
                    }

                    if (cur >= max) {
                        log(`✅ 阅读任务执行完毕 (最终进度: ${state.readCur}/${max})`);
                    } else {
                        log(`⏹️ 阅读任务已停止 (当前进度: ${state.readCur}/${max})`);
                    }
                }
            } else {
                log(`⚠️ 未找到阅读任务卡片 (offerid: ${readOfferId})`);
            }
        } else {
            log('❌ 获取阅读进度失败');
        }

        await updateData();
    } catch (e) {
        log('❌ 阅读出错: ' + e.message);
        console.error('[Rewards 阅读异常]:', e);
    } finally {
    state.readRunning = false;
    nodes.btnRead.disabled = false;
    markBusy(-1);
    }
};

nodes.btnRead.onclick = runRead;

    const runPromo = async () => {
        nodes.btnPromo.disabled = true;
        markBusy(1);
        await waitWhilePaused();
        log('⏳ 开始执行活动任务...');
        await updateData();

        // =========================================================================
        // 🌍 区域配置
        // -------------------------------------------------------------------------
        // 【修改说明】：
        // 1. 默认使用 state.country 或 dashboard.userStatus.country 获取当前区域，若未获取到则回退为 'cn'。
        // 2. 若需要强制锁定为 'cn'，可取消注释第一行并注释第二行。
        // =========================================================================
        //const customRegion = 'cn'; //锁定cn区域
        const customRegion = (state.country || dashboard?.userStatus?.country || 'cn').toLowerCase();// 自动全区域,失败回退cn区域


        const region = customRegion.toLowerCase();

        // 计算 YYYYMMDD 上限，用于拦截提前加载的未来每日任务。
        // 卡片 id 里的日期可能是 PT 也可能是 UTC，取三者中最晚的当上限：
        // 宁可放过一个未来任务（服务器会拒绝、没有进度），也不能把今天的任务误拦掉。
        const todayYmd = ymdCompactOf(maxParts(localParts(), partsIn(TZ_PT), partsIn(TZ_UTC)));

        const isTrue = (val) => {
            if (typeof val === 'boolean') return val;
            if (typeof val === 'string') return val.trim().toLowerCase() === 'true';
            return false;
        };

        // 1. 获取 Token
        let token = (typeof getSearchToken === 'function') ? await getSearchToken() : null;
        if (!token) token = state.searchToken;
        const accessToken = await getAccessToken();

        if (!token && !accessToken) {
            log('⚠️ 未获取到活动 Token，请刷新页面重试');
            nodes.btnPromo.disabled = false;
            markBusy(-1);
            return;
        }

        console.group('[Rewards 活动卡片诊断]');
        let rawTasks = [];

        // 递归挖掘任意对象中的卡片
        const extractCardsFromAnyObject = (obj, sourceName) => {
            if (!obj || typeof obj !== 'object') return;
            const found = [];
            const visited = new WeakSet();

            const search = (node) => {
                if (!node || typeof node !== 'object' || visited.has(node)) return;
                visited.add(node);

                const hasId = node.offerId || node.offerid || node.id || node.activityId;
                const hasAttr = node.title || node.name || node.points || node.pointProgressMax !== undefined || node.destinationUrl;

                if (hasId && hasAttr) {
                    found.push(node);
                    return;
                }

                if (Array.isArray(node)) {
                    for (const item of node) search(item);
                } else {
                    for (const key of Object.keys(node)) {
                        if (key.startsWith('jQuery') || key.startsWith('__')) continue;
                        search(node[key]);
                    }
                }
            };

            search(obj);
            if (found.length > 0) {
                console.log(`📌 从 [${sourceName}] 挖掘出 ${found.length} 个活动卡片数据`);
                rawTasks.push(...found);
            }
        };

        // 渠道 1：从本地 state / dashboard 提取
        if (typeof state !== 'undefined' && state.dashboard) extractCardsFromAnyObject(state.dashboard, "state.dashboard");
        if (typeof dashboard !== 'undefined' && dashboard) extractCardsFromAnyObject(dashboard, "全局 dashboard");

        // 渠道 2：通过 DAPI 移动端接口拉取
        if (accessToken) {
            try {
                const dapiRes = await gmRequest({
                    url: `https://prod.rewardsplatform.microsoft.com/dapi/me?channel=SAAndroid&version=33.8.440724006`,
                    headers: {
                        'authorization': `Bearer ${accessToken}`,
                        'user-agent': CONFIG.ua.mobile,
                        'x-rewards-appid': CONFIG.mobileApi.appId,
                        'x-rewards-country': region
                    }
                });
                const dapiData = typeof dapiRes === 'string' ? JSON.parse(dapiRes) : dapiRes;
                if (dapiData?.response) {
                    extractCardsFromAnyObject(dapiData.response, "DAPI 接口 response");
                }
            } catch (e) {
                console.warn('⚠️ DAPI 请求失败:', e.message);
            }
        }

        // 2. 去重与精细化多重校验
        const seenIds = new Set();
        const taskList = [];

        for (const p of rawTasks) {
            if (!p) continue;

            const id = String(p.offerId || p.id || p.offerid || '');
            const title = p.title || p.name || p.description || '未命名活动';
            const progress = Number(p.pointProgress || 0);
            const max = Number(p.pointProgressMax || p.max || 0);

            if (!id || seenIds.has(id)) continue;

            // 规则 A：排除系统通知、漫游提示、每日签到卡片（需单独签到接口）及异常进度卡片
            const idUpper = id.toUpperCase();
            if (idUpper.includes('NOTIFICATION') || 
                idUpper.includes('ROAMING') || 
                idUpper.includes('CHECKIN') || 
                idUpper.includes('CHECK_IN') || 
                max > 100000) {
                console.log(`❌ [跳过-非通用任务/签到卡片] ${title} (id: ${id})`);
                continue;
            }

            // 规则 B：日期精准拦截（对于 DailySet 任务，拦截未到期的未来任务）
            const dateMatch = id.match(/20\d{6}/);
            if (dateMatch) {
                const cardDate = dateMatch[0];
                if (cardDate > todayYmd) {
                    console.log(`❌ [跳过-未来未到期任务] ${title} (id: ${id}, 日期: ${cardDate})`);
                    continue;
                }
            }

            // 规则 C：区域限制隔离（锁定 CN 时，跳过含有外区编码如 ENUS, US, JP 的任务）
            if (region === 'cn') {
                const nonCnRegex = /_(ENUS|US|UK|JP|DE|FR|CA|AU)_/i;
                if (nonCnRegex.test(id)) {
                    console.log(`❌ [跳过-外区任务] ${title} (id: ${id})`);
                    continue;
                }
            }

            // 规则 D：完成度及锁定校验
            const isCompleted = isTrue(p.complete) ||
                                isTrue(p.completed) ||
                                isTrue(p.isCompleted) ||
                                isTrue(p.attributes?.complete) ||
                                p.state === 'CLAIMED' ||
                                (max > 0 && progress >= max);

            if (isCompleted) {
                console.log(`❌ [跳过-已完成] ${title} (id: ${id}, 进度: ${progress}/${max})`);
                continue;
            }

            if (p.priority <= -2 || p.exclusiveLockedFeatureStatus === 'locked') {
                console.log(`❌ [跳过-已锁定] ${title} (id: ${id})`);
                continue;
            }

            seenIds.add(id);
            taskList.push(p);
            console.log(`✅ [待执行活动] ${title} (id: ${id}, 进度: ${progress}/${max})`);
        }

        console.groupEnd();

        if (taskList.length === 0) {
            log('✅ 所有活动已完成！');
            nodes.btnPromo.disabled = false;
            markBusy(-1);
            return;
        }

        log(`🎯 准备上报 ${taskList.length} 个未完成活动 (当前区域: ${region.toUpperCase()})`);

        let count = 0;
        const restInterval = randomRange(3, 5);

        for (const p of taskList) {
            await waitWhilePaused();
            try {
                const title = p.title || p.name || p.description || '活动任务';
                const offerId = p.offerId || p.id || p.offerid;
                const hash = p.hash || p.activityId || p.attributes?.hash || '1';

                log(`▶️ (${count + 1}/${taskList.length}) 执行: ${title}`);

                // 通道 1：Web 端标准 reportactivity
                if (token) {
                    const formData = new URLSearchParams({
                        id: offerId,
                        hash: hash,
                        timeZone: '480',
                        activityAmount: '1',
                        dbs: '0',
                        form: '',
                        type: '',
                        __RequestVerificationToken: token
                    });

                    await gmRequest({
                        method: 'POST',
                        url: 'https://rewards.bing.com/api/reportactivity?X-Requested-With=XMLHttpRequest',
                        headers: {
                            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                            'X-Requested-With': 'XMLHttpRequest',
                            'Referer': 'https://rewards.bing.com/'
                        },
                        data: formData.toString()
                    }).catch(() => null);
                }

                // 通道 2：DAPI 移动端 101 上报
                if (accessToken) {
                    await gmRequest({
                        method: 'POST',
                        url: 'https://prod.rewardsplatform.microsoft.com/dapi/me/activities',
                        headers: {
                            'content-type': 'application/json; charset=UTF-8',
                            'user-agent': CONFIG.ua.mobile,
                            'authorization': `Bearer ${accessToken}`,
                            'x-rewards-appid': CONFIG.mobileApi.appId,
                            'x-rewards-ismobile': 'true',
                            'x-rewards-country': region,
                            'x-rewards-language': 'zh',
                            'x-rewards-partnerid': 'startapp',
                            'x-rewards-flights': 'rwgobig'
                        },
                        data: JSON.stringify({
                            amount: 1,
                            country: region,
                            id: generateDapiId(),
                            type: 101,
                            channel: 'SAAndroid',
                            attributes: { offerid: offerId }
                        })
                    }).catch(() => null);
                }

                count++;

                // 防封随机延迟
                if (count < taskList.length) {
                    if (count % restInterval === 0) {
                        const restTime = randomRange(10000, 18000);
                        log(`☕ 间隔停顿 ${(restTime / 1000).toFixed(1)} 秒...`);
                        await sleep(restTime);
                    } else {
                        const baseDelay = randomRange(4500, 8500);
                        log(`⏳ 间隔等待 ${(baseDelay / 1000).toFixed(1)} 秒...`);
                        await sleep(baseDelay);
                    }
                }
            } catch (e) {
                log(`❌ 活动执行失败: ${e.message}`);
            }
        }

        log(`✅ 活动全部完成，成功提交 ${count} 个活动`);
        await updateData();
        nodes.btnPromo.disabled = false;
        markBusy(-1);
    };

    nodes.btnPromo.onclick = runPromo;

    // 搜索模块：重构版（动态判定合并后的总搜索进度）
    const runSearch = async () => {
        if (state.running) {
            state.running = false;
            if (state.manualPaused) setManualPause(false, { silent: true });
            nodes.btnSearch.textContent = '🔍 搜索';
            updateAllButton();
            return;
        }
        state.running = true;
        nodes.btnSearch.textContent = '⏹ 停止';
        updateAllButton();
        await waitWhilePaused();

        // 搜索前先同步一次最新数据
        await updateData();

        // 执行单次搜索与奖励触发
        const doSearch = async (query, isMobile = false) => {
            const host = isMobile ? 'cn.bing.com' : 'www.bing.com';
            const cookie = await getCookies(`https://${host}`);
            await waitWhilePaused();

            const ua = isMobile ? CONFIG.ua.mobile : CONFIG.ua.pc;
            const searchUrl = `https://${host}/search?q=${encodeURIComponent(query)}&form=QBLH`;

            try {
                // 1. 发送标准的 Bing 搜索请求
                const searchResult = await gmRequest({
                    url: searchUrl,
                    headers: {
                        'User-Agent': ua,
                        'Cookie': cookie,
                        'Referer': `https://${host}/?form=QBLH`
                    }
                });

                // 提取 IG 用于上报
                const igMatch = searchResult.match(/,IG:"([^"]+)"/) || searchResult.match(/"IG":"([^"]+)"/);
                const ig = igMatch ? igMatch[1] : crypto.randomUUID().replace(/-/g, '').toUpperCase();

                // 2. 使用微软 Rewards 专用侧边栏/头部埋点上报积分
                const reportHeaders = {
                    'User-Agent': ua,
                    'Cookie': cookie,
                    'Referer': searchUrl,
                    'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8'
                };

                // 触发奖励计数埋点
                await gmRequest({
                    method: 'POST',
                    url: `https://${host}/rewardsapp/ncheader?ver=88888888&IID=SERP.5047&IG=${ig}&ajaxreq=1`,
                    headers: reportHeaders,
                    data: 'wb=1%3bi%3d1%3bv%3d1'
                }).catch(() => {});

                // 触发活动报告接口
                await gmRequest({
                    method: 'POST',
                    url: `https://${host}/rewardsapp/reportActivity?IG=${ig}&IID=SERP.5047&q=${encodeURIComponent(query)}&ajaxreq=1`,
                    headers: reportHeaders,
                    data: `url=${encodeURIComponent(searchUrl)}&V=web`
                }).catch(() => {});

                log(`✓ 搜索: "${query.substring(0, 12)}..."`);
            } catch (e) {
                console.error('[Rewards] 搜索异常:', e);
                log(`✗ 搜索失败: ${e.message}`);
            }
        };
        // 精确等待函数（优化日志输出频率，彻底杜绝刷屏）
        const preciseWait = async (ms) => {
            state.countdownStartTime = Date.now();
            state.countdownDuration = ms;
            let endTime = Date.now() + ms;

            const totalSecs = Math.ceil(ms / 1000);
            if (totalSecs > 60) {
                const mins = (totalSecs / 60).toFixed(1);
                log(`⏳ 等待中，预计需要 ${mins} 分钟...`);
            } else if (totalSecs > 0) {
                log(`⏳ 等待 ${totalSecs} 秒...`);
            }

            while (Date.now() < endTime && state.running) {
                if (state.manualPaused) {
                    const pausedMs = await waitWhilePaused();
                    endTime += pausedMs;
                    state.countdownStartTime += pausedMs;
                    continue;
                }
                const remaining = Math.max(0, endTime - Date.now());
                await sleep(Math.min(500, remaining));
            }
            
            state.countdownStartTime = 0;
            state.countdownDuration = 0;
        };

        // 暂停检查函数（优化：如果已满直接跳过暂停）
        const checkPause = async () => {
            if (!CONFIG.pause.enabled) return;
            if (state.searchCur >= state.searchMax) return;

            state.searchCount++;
            saveProgress();

            if (state.searchCount % CONFIG.pause.interval === 0) {
                state.isPaused = true;
                const pauseMinutes = CONFIG.pause.duration / 60000;
                log(`⏸️ 已完成 ${state.searchCount} 次搜索，触发防风控暂停 ${pauseMinutes} 分钟...`);
                state.pauseEndTime = Date.now() + CONFIG.pause.duration;
                await preciseWait(CONFIG.pause.duration);
                state.isPaused = false;
                state.pauseEndTime = 0;
                log(`▶️ 暂停结束，继续搜索...`);
            }
        };

        loadProgress();

        // 动态判定搜索进度 (合并后的总搜索任务)
        if (state.searchCur < state.searchMax) {
            log(`💻 开始执行通用搜索，当前进度: ${state.searchCur}/${state.searchMax}`);
            let searchIndex = 0;

            while (state.searchCur < state.searchMax && state.running) {
                await waitWhilePaused();

                searchIndex++;
                const q = await getHotQuery();
                await doSearch(q, false);

                // 👈 核心修复：每次做完立即同步预估积分 (+3分/次)
                state.searchCur = Math.min(state.searchMax, state.searchCur + 3);
                render();
                await checkPause();
                if (!state.running) break;
                await preciseWait(randomRange(CONFIG.pc.minDelay, CONFIG.pc.maxDelay));

                // 降低更新频率，每 5 次搜索同步一次数据，避免进度丢失
                if (searchIndex % 5 === 0) {
                    log(`🔄 已完成 ${searchIndex} 次搜索，同步一次数据...`);
                    await updateData();
                }

                // 达到预估上限后，再检查服务器真实进度
                if (state.searchCur >= state.searchMax) {
                    if (searchIndex % 5 !== 0) {
                        log(`🔄 搜索达到预估上限，最后同步一次数据...`);
                        await updateData();
                    }
                    log(`🎉 搜索积分已达今日上限 (${state.searchCur}/${state.searchMax})！`);
                    break;
                }
            }
            log(`💻 搜索结束，共执行 ${searchIndex} 次搜索 (最终进度: ${state.searchCur}/${state.searchMax})`);
        } else {
            log('💻 今日搜索任务已全部完成');
        }

        state.running = false;
        nodes.btnSearch.textContent = '🔍 搜索';
        updateAllButton();
        saveProgress();
    };
    nodes.btnSearch.onclick = runSearch;

    nodes.btnAll.onclick = async () => {
        if (state.manualPaused || state.running || state.allRunning || state.busyCount > 0) {
            setManualPause(!state.manualPaused);
            return;
        }
        state.allRunning = true;
        updateAllButton();
        log('🚀 一键执行开始');
        try {
            await waitWhilePaused();
            await runSign();
            await runRead();
            await runPromo();
            await runSearch();
        } finally {
            state.allRunning = false;
            updateAllButton();
        }
    };
    // Init
    (async () => {
        try {
            loginCookie = await getCookies('https://login.live.com');

            console.log('[Rewards] 登录Cookie长度:', loginCookie.length);
            await updateData();

            try {
                const info = await withAccessTokenRequest(token => gmRequest({
                    url: 'https://prod.rewardsplatform.microsoft.com/dapi/me?channel=SAAndroid&options=613',
                    headers: { 
                        'Authorization': `Bearer ${token}`, 
                        'X-Rewards-AppId': CONFIG.mobileApi.appId, 
                        'X-Rewards-IsMobile': 'true' 
                    }
                }));
                if (info) {
                    const d = typeof info === 'string' ? JSON.parse(info) : info;
                    const p = d.response?.promotions?.find(x => x.attributes?.offerid === 'ENUS_readarticle3_30points');
                    if (p && p.attributes) { 
                        state.readCur = parseInt(p.attributes.progress) || 0; 
                        state.readMax = parseInt(p.attributes.max) || 30; 
                        render(); 
                    }
                }
            } catch { }
        } catch { }
        // 三个时区各打一遍：接口 key 对不上时，一眼能看出是哪个口径
        const localDate = dateStrOf(localParts());
        const ptDate = dateStrOf(partsIn(TZ_PT));
        const utcDate = dateStrOf(partsIn(TZ_UTC));
        const ptHour = (() => {
            try {
                return Number(new Intl.DateTimeFormat('en-US', { timeZone: TZ_PT, hour: '2-digit', hour12: false }).format(new Date())) % 24;
            } catch (e) {
                return new Date().getHours();
            }
        })();
        log(`🕒 本地 ${localDate} · PT ${ptDate} · UTC ${utcDate}（PT 日切约 ${24 - ptHour} 小时后）`);

        if (localDate !== ptDate) {
            log('⚠️ 本地日期已翻篇，Rewards 仍在上一个业务日（以 PT 为准）');
            log('💡 这段时间跑签到/每日任务容易被算到前一天，建议 PT 日切后再跑');
        }
        log('🌟 脚本就绪 v' + SCRIPT_VERSION);
    })();

    setInterval(() => {
        if (document.hidden) return;   // 后台标签页不刷新，省请求也少触发风控
        updateData();
    }, 60000);

    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) updateData();
    });

})();
