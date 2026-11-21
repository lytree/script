# 浏览器油猴脚本

Tampermonkey / Violentmonkey 脚本，从 `lytree/GMScript` 的 `userscripts/` 迁移而来；v1.2.1 起**本目录就是发布源**。

| 文件 | 用途 |
| --- | --- |
| [`get-microsoft-rewards.user.js`](get-microsoft-rewards.user.js) | Microsoft Rewards 自动搜索 / 签到 / 阅读任务 |
| [`telegram-media-downloader-merged.user.js`](telegram-media-downloader-merged.user.js) | Telegram 受限频道媒体下载（WebA + WebK 双端） |

详细功能、版本变更与安装地址见 [`../README.md`](../README.md) 的「一、油猴脚本」一节。

> 这里的文件**就是发布源**：脚本内的 `@updateURL` / `@downloadURL` 指向本仓库 `scripts/browser/` 下的 raw 地址，
> 安装与自动更新都用 [`../README.md`](../README.md) 中列出的地址。
> 改动后递增 `@version` 再推送，已安装用户下次检查更新即可收到新版（改了 `@name` / `@namespace` 则会被当成新脚本，别动）。
