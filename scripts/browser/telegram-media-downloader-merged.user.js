// ==UserScript==
// @name         Telegram Media Downloader (Merged)
// @name:zh-CN   Telegram 媒体下载器（合并增强版）
// @name:zh-TW   Telegram 媒體下載器（合併增強版）
// @name:ru      Telegram Media Downloader (Merged)
// @name:my      Telegram Media Downloader (Merged)
// @version      1.2.0
// @namespace    gmscript-telegram-media-downloader
// @description        Download images, videos, voice messages and files from Telegram Web — including restricted "no-forward" chats. Batch-save selected media, cancel/retry/resume downloads from a progress panel, and copy text from protected messages.
// @description:zh-CN  从 Telegram 网页版下载图片、视频、语音与文件（含禁止转发的受限频道）。支持批量保存所选媒体、可取消/重试/续传的下载面板，并恢复受保护消息的文字复制。
// @description:zh-TW  從 Telegram 網頁版下載圖片、影片、語音與檔案（含禁止轉發的受限頻道）。支援批次儲存所選媒體、可取消/重試/續傳的下載面板，並恢復受保護訊息的文字複製。
// @description:ru     Загружайте изображения, видео, голосовые сообщения и файлы из Telegram Web, включая каналы с запретом пересылки. Пакетная загрузка выбранных медиа, панель прогресса с отменой, повтором и докачкой, восстановление копирования текста в защищённых сообщениях.
// @description:my     Telegram ဝဘ်မှ ပုံ၊ ဗီဒီယို၊ အသံနှင့် ဖိုင်များကို ဒေါင်းလုဒ်လုပ်ပါ (ပြန်ပို့မရသော ကန့်သတ်ချက်ရှိ ချန်နယ်များအပါအဝင်)။ ရွေးချယ်ထားသော မီဒီယာများကို အစုလိုက် သိမ်းဆည်းနိုင်ပြီး ဒေါင်းလုဒ်ကို ပယ်ဖျက်/ထပ်စမ်း/ဆက်လုပ်နိုင်သည်။
// @author       Nestor Qin, Andrew, yoshiko.wisoky, copyMister — merged
// @license      GNU GPLv3
// @homepage     https://github.com/lytree/GMScript
// @supportURL   https://github.com/lytree/GMScript/issues
// @match        https://web.telegram.org/*
// @match        https://webk.telegram.org/*
// @match        https://webz.telegram.org/*
// @icon         https://img.icons8.com/color/452/telegram-app--v5.png
// @grant        unsafeWindow
// @noframes
// @run-at       document-start
// @downloadURL  https://raw.githubusercontent.com/lytree/GMScript/main/userscripts/telegram-media-downloader-merged.user.js
// @updateURL    https://raw.githubusercontent.com/lytree/GMScript/main/userscripts/telegram-media-downloader-merged.user.js
// ==/UserScript==

/*
 * 合并说明（四份上游脚本 → 单一实现）
 *
 *  A. "Telegram Media Downloader" (Nestor Qin, v2.01)
 *     → 保留：i18n（英/简中/繁中/俄/缅）、任务进度面板（取消/重试/续传）、
 *       4 MB Range 分片下载、Content-Range / Content-Length / MIME / 超时校验、
 *       File System Access API 直写磁盘、WebA(webz) 与 WebK(webk) 双端挂载、
 *       故事(Stories)、置顶音频、动画错误提示。
 *
 *  B. "Telegram Media Downloader (Optimized)" (yoshiko.wisoky)
 *     → 保留：类化结构带来的可读性思路；原生下载按钮隐藏态的还原策略
 *       （按用途而非字体码点识别）；失败后自动重试。
 *     → 舍弃：无校验的分片循环、硬编码英文 UI、字体私有区码点常量、重复的
 *       轮询观察器。
 *
 *  C. "Telegram Web Media Downloader (Batch) + Copy Text" (copyMister)
 *     → 保留：WebK 原生 `appDownloadManager.downloadToDisc({ media })` 通道
 *       （大文件/受限聊天最可靠）；右键菜单"下载"项；选择栏批量下载；
 *       受保护消息（.no-forwards）文字复制解锁。
 *     → 舍弃：强制跳转到 webk 的 location.replace（会打断 WebA 用户）。
 *
 *  D. "Telegram Media Downloader (Optimized & Enhanced)" / telegram-only 变体 (Andrew)
 *     → 保留：严格 @match + 运行时域名守卫（仅在 telegram.org 生效）；
 *       受限媒体查看器中被隐藏的下载/转发控件的还原（按用途与已学字形识别）；
 *       WebA 快拍、WebK 快拍与置顶音频的挂载点位。
 *     → 舍弃：GrabVideo Bot 第三方推广横幅、window.onerror 兜底吞错、
 *       无校验的分片循环与自带进度面板（已由 A/B 的实现取代）。
 *
 *  E. 本次优化
 *     → 单一 MutationObserver（100 ms 去抖）+ 作用域过滤，替代三套观察器；
 *     → 统一两条下载通道：原生媒体对象优先，URL 分片兜底（失败自动回退）；
 *     → 合并重复的图标/文件名/本地化逻辑；并发与队列集中管理；
 *     → 批量下载串行执行并限速，避免 Service Worker 抢占导致大文件退化为内存 Blob；
 *     → 所有新 UI 文案纳入同一套 i18n，切换语言实时刷新；
 *     → 新增「整组下载（相册 + 评论区）」与「下载目录」：选定目录后整组/批量/单条
 *       统一流式写入同一文件夹（File System Access API，句柄持久化到 IndexedDB）。
 *
 *  E. 本仓库合并（GMScript）
 *     → 与旧 `telegram-media-downloader.telegram-only.user.js`（v1.0.7）合并为单一脚本，
 *       旧版由此文件完全取代，不再单独维护。
 *     → 继承旧版的**严格 @match**（只放行 web/webk/webz 三个 Telegram Web 域名），
 *       不使用上游的 `https://*.telegram.org/*` 通配，杜绝在无关子域注入。
 *     → 继承旧版**剥离第三方推广注入**的原则：本文件不含任何推广、邀请、统计外链。
 *     → `@updateURL` / `@downloadURL` 固定指向本仓库 raw 地址，版本号只由 `@version` 承载。
 */

(function () {
  "use strict";

  /* ------------------------------------------------------------------ *
   * 0. 站点守卫（与 @match 形成双重保险）                                 *
   * ------------------------------------------------------------------ */
  const TELEGRAM_HOST_RE = /^(?:[a-z0-9-]+\.)*telegram\.org$/i;
  if (!TELEGRAM_HOST_RE.test(String(location.hostname || ""))) return;

  /* ------------------------------------------------------------------ *
   * 1. 常量                                                             *
   * ------------------------------------------------------------------ */
  const CONFIG = {
    maxConcurrentDownloads: 2,
    chunkSize: 4 * 1024 * 1024,
    idleTimeout: 30000,
    refreshDelay: 2000,
    observerDebounce: 100,
    historyLimit: 20,
    batchGap: 1000,
    panelId: "tel-downloader-progress-bar-container",
    cardPrefix: "tel-downloader-progress-",
    languageKey: "tel-media-downloader-language",
    menuItemId: "tel-menu-download",
    menuGroupItemId: "tel-menu-group",
    menuCopyLinkItemId: "tel-menu-copy-link",
    menuAria2ItemId: "tel-menu-aria2",
    batchButtonId: "tel-batch-download",
    pinnedButtonClass: "_tel_download_button_pinned_container",
    menuIcon: "\u{1F4E5}", // 菜单图标用 emoji：tgico 字体码点会随版本变化
  };

  const isWebK =
    location.hostname === "webk.telegram.org" || location.pathname.startsWith("/k/");
  const isWebA =
    location.hostname === "webz.telegram.org" || location.pathname.startsWith("/a/");

  /* ------------------------------------------------------------------ *
   * 2. 页面桥接（unsafeWindow）                                          *
   * ------------------------------------------------------------------ */
  const page = (() => {
    try {
      if (typeof unsafeWindow !== "undefined" && unsafeWindow) return unsafeWindow;
    } catch {
      /* 沙箱环境下访问失败时回退到当前 window */
    }
    return window;
  })();

  const logger = {
    error: (...args) => console.error("[Tel Download]", ...args),
  };

  /* ------------------------------------------------------------------ *
   * 3. i18n                                                             *
   * ------------------------------------------------------------------ */
  const UI_LANGUAGES = {
    en: "English",
    "zh-CN": "简体中文",
    "zh-TW": "繁體中文",
    ru: "Русский",
    my: "မြန်မာ",
  };

  const UI_MESSAGES = {
    en: {
      downloads: "Downloads", downloadsCount: "Downloads ({count})", mediaDownloads: "Media downloads",
      download: "Download", loadingMedia: "Media is loading", hide: "Hide", show: "Show",
      dismiss: "Dismiss download", progress: "Download progress", cancel: "Cancel", retry: "Retry",
      settings: "Settings", language: "Interface language", automatic: "Browser language",
      folder: "Download folder", chooseFolder: "Choose folder", folderDefault: "System download folder", folderChosen: "Folder: {name}",
      clearFolder: "Clear folder", folderDenied: "Folder access was denied",
      preparing: "Preparing", queued: "Queued", downloading: "Downloading", saving: "Saving",
      completed: "Completed", handedOff: "Sent to browser", failed: "Failed", cancelled: "Cancelled",
      rate: "{speed}/s", video: "video", audio: "audio", image: "image", unknownType: "an unknown file type",
      menuDownload: "Download", menuGroupDownload: "Download group + comments",
      menuCopyLink: "Copy media link", menuAria2: "Export aria2 task list",
      externalBlocked: "This media is a page-scoped blob and cannot be handed to an external downloader",
      batchShort: "D/L", batchTitle: "Download selected media",
      batchEmpty: "No media in selection",
      errorHttp: "Download request failed (HTTP {status})",
      errorMime: "Expected {kind}, received {mime}",
      errorRangeMissing: "Missing or invalid Content-Range",
      errorRangeInconsistent: "Inconsistent media range; download stopped to avoid a corrupt file",
      errorRangeUnsupported: "Server stopped supporting partial downloads",
      errorLength: "Invalid Content-Length", errorEmpty: "Empty media response",
      errorTooLarge: "Media response exceeds the expected size",
      errorIncomplete: "Incomplete media response; received byte count does not match",
      errorTimeout: "No data received for 30 seconds. Please retry.",
      errorUnexpected: "Download failed: {message}",
    },
    "zh-CN": {
      downloads: "下载", downloadsCount: "下载（{count}）", mediaDownloads: "媒体下载",
      download: "下载", loadingMedia: "媒体正在加载", hide: "收起", show: "展开",
      dismiss: "移除下载卡片", progress: "下载进度", cancel: "取消", retry: "重试",
      settings: "设置", language: "界面语言", automatic: "跟随浏览器",
      folder: "下载目录", chooseFolder: "选择文件夹", folderDefault: "系统默认下载目录", folderChosen: "目录：{name}",
      clearFolder: "清除目录", folderDenied: "文件夹权限被拒绝",
      preparing: "准备中", queued: "排队中", downloading: "下载中", saving: "保存中",
      completed: "已完成", handedOff: "已交给浏览器", failed: "失败", cancelled: "已取消",
      rate: "{speed}/秒", video: "视频", audio: "音频", image: "图片", unknownType: "未知文件类型",
      menuDownload: "下载", menuGroupDownload: "下载整组（含评论）",
      menuCopyLink: "复制媒体链接", menuAria2: "导出 aria2 任务列表",
      externalBlocked: "该媒体是页面内 blob，无法交给外部下载器",
      batchShort: "下载", batchTitle: "下载所选媒体",
      batchEmpty: "所选内容中没有媒体",
      errorHttp: "下载请求失败（HTTP {status}）",
      errorMime: "预期为{kind}，实际收到 {mime}",
      errorRangeMissing: "缺少 Content-Range，或其格式无效",
      errorRangeInconsistent: "媒体数据范围不一致；已停止下载，避免保存损坏的文件",
      errorRangeUnsupported: "服务器不再支持分段下载",
      errorLength: "Content-Length 无效", errorEmpty: "媒体响应为空",
      errorTooLarge: "收到的媒体数据超出预期大小",
      errorIncomplete: "媒体数据不完整；收到的字节数与预期不符",
      errorTimeout: "已连续 30 秒未收到数据，请重试。",
      errorUnexpected: "下载失败：{message}",
    },
    "zh-TW": {
      downloads: "下載", downloadsCount: "下載（{count}）", mediaDownloads: "媒體下載",
      download: "下載", loadingMedia: "媒體正在載入", hide: "收合", show: "展開",
      dismiss: "移除下載卡片", progress: "下載進度", cancel: "取消", retry: "重試",
      settings: "設定", language: "介面語言", automatic: "跟隨瀏覽器",
      folder: "下載目錄", chooseFolder: "選擇資料夾", folderDefault: "系統預設下載資料夾", folderChosen: "目錄：{name}",
      clearFolder: "清除目錄", folderDenied: "資料夾權限被拒絕",
      preparing: "準備中", queued: "排隊中", downloading: "下載中", saving: "儲存中",
      completed: "已完成", handedOff: "已交給瀏覽器", failed: "失敗", cancelled: "已取消",
      rate: "{speed}/秒", video: "影片", audio: "音訊", image: "圖片", unknownType: "未知檔案類型",
      menuDownload: "下載", menuGroupDownload: "下載整組（含評論）",
      menuCopyLink: "複製媒體連結", menuAria2: "匯出 aria2 任務清單",
      externalBlocked: "此媒體是頁面內 blob，無法交給外部下載器",
      batchShort: "下載", batchTitle: "下載所選媒體",
      batchEmpty: "所選內容中沒有媒體",
      errorHttp: "下載請求失敗（HTTP {status}）",
      errorMime: "預期為{kind}，實際收到 {mime}",
      errorRangeMissing: "缺少 Content-Range，或其格式無效",
      errorRangeInconsistent: "媒體資料範圍不一致；已停止下載，避免儲存損壞的檔案",
      errorRangeUnsupported: "伺服器不再支援分段下載",
      errorLength: "Content-Length 無效", errorEmpty: "媒體回應為空",
      errorTooLarge: "收到的媒體資料超出預期大小",
      errorIncomplete: "媒體資料不完整；收到的位元組數與預期不符",
      errorTimeout: "已連續 30 秒未收到資料，請重試。",
      errorUnexpected: "下載失敗：{message}",
    },
    ru: {
      downloads: "Загрузки", downloadsCount: "Загрузки ({count})", mediaDownloads: "Загрузка медиа",
      download: "Скачать", loadingMedia: "Медиа загружается", hide: "Свернуть", show: "Развернуть",
      dismiss: "Убрать карточку загрузки", progress: "Ход загрузки", cancel: "Отменить", retry: "Повторить",
      settings: "Настройки", language: "Язык интерфейса", automatic: "Язык браузера",
      folder: "Папка загрузок", chooseFolder: "Выбрать папку", folderDefault: "Системная папка загрузок", folderChosen: "Папка: {name}",
      clearFolder: "Очистить папку", folderDenied: "Доступ к папке отклонён",
      preparing: "Подготовка", queued: "В очереди", downloading: "Загрузка", saving: "Сохранение",
      completed: "Завершено", handedOff: "Передано браузеру", failed: "Ошибка", cancelled: "Отменено",
      rate: "{speed}/с", video: "видео", audio: "аудио", image: "изображение", unknownType: "неизвестный тип файла",
      menuDownload: "Скачать", menuGroupDownload: "Скачать группу и комментарии",
      menuCopyLink: "Копировать ссылку", menuAria2: "Экспорт задач aria2",
      externalBlocked: "Этот медиафайл — blob страницы, его нельзя передать внешнему загрузчику",
      batchShort: "Скачать", batchTitle: "Скачать выбранные медиа",
      batchEmpty: "В выбранном нет медиа",
      errorHttp: "Ошибка запроса загрузки (HTTP {status})",
      errorMime: "Ожидалось: {kind}. Получено: {mime}",
      errorRangeMissing: "Заголовок Content-Range отсутствует или некорректен",
      errorRangeInconsistent: "Диапазоны данных не совпадают; загрузка остановлена во избежание повреждения файла",
      errorRangeUnsupported: "Сервер больше не поддерживает загрузку по частям",
      errorLength: "Некорректный Content-Length", errorEmpty: "Получен пустой ответ с медиа",
      errorTooLarge: "Размер полученных данных превышает ожидаемый",
      errorIncomplete: "Данные неполные; число полученных байтов не совпадает с ожидаемым",
      errorTimeout: "Нет данных в течение 30 секунд. Повторите попытку.",
      errorUnexpected: "Не удалось скачать: {message}",
    },
    my: {
      downloads: "ဒေါင်းလုဒ်များ", downloadsCount: "ဒေါင်းလုဒ်များ ({count})", mediaDownloads: "မီဒီယာဒေါင်းလုဒ်များ",
      download: "ဒေါင်းလုဒ်လုပ်ရန်", loadingMedia: "မီဒီယာကို ဖွင့်နေသည်", hide: "ဖျောက်ရန်", show: "ပြရန်",
      dismiss: "ဒေါင်းလုဒ်ကတ်ကို ဖယ်ရှားရန်", progress: "ဒေါင်းလုဒ်တိုးတက်မှု", cancel: "ပယ်ဖျက်ရန်", retry: "ထပ်စမ်းရန်",
      settings: "ဆက်တင်များ", language: "မျက်နှာပြင်ဘာသာစကား", automatic: "ဘရောက်ဇာဘာသာစကား",
      folder: "ဒေါင်းလုဒ်ဖိုလ်ဒါ", chooseFolder: "ဖိုလ်ဒါ ရွေးချယ်ရန်", folderChosen: "ဖိုလ်ဒါ: {name}",
      clearFolder: "ဖိုလ်ဒါ ရှင်းလင်းရန်", folderDenied: "ဖိုလ်ဒါအသုံးပြုခွင့် ငြင်းပယ်ခံရသည်",
      preparing: "ပြင်ဆင်နေသည်", queued: "စောင့်ဆိုင်းနေသည်", downloading: "ဒေါင်းလုဒ်လုပ်နေသည်", saving: "သိမ်းဆည်းနေသည်",
      completed: "ပြီးစီးပြီ", handedOff: "ဘရောက်ဇာသို့ လွှဲပေးပြီး", failed: "မအောင်မြင်ပါ", cancelled: "ပယ်ဖျက်ပြီး",
      rate: "{speed}/စက္ကန့်", video: "ဗီဒီယို", audio: "အသံ", image: "ပုံ", unknownType: "အမည်မသိ ဖိုင်အမျိုးအစား",
      menuDownload: "ဒေါင်းလုဒ်လုပ်ရန်", menuGroupDownload: "အဖွဲ့လိုက် ဒေါင်းလုဒ်လုပ်ရန် (မှတ်ချက်များ အပါအဝင်)",
      menuCopyLink: "မီဒီယာလင့်ခ် ကူးယူရန်", menuAria2: "aria2 လုပ်ငန်းစာရင်း ထုတ်ယူရန်",
      externalBlocked: "ဤမီဒီယာသည် စာမျက်နှာအတွင်း blob ဖြစ်၍ ပြင်ပ downloader သို့ မပေးနိုင်ပါ",
      batchShort: "ဒေါင်းလုဒ်", batchTitle: "ရွေးချယ်ထားသော မီဒီယာများကို ဒေါင်းလုဒ်လုပ်ရန်",
      batchEmpty: "ရွေးချယ်မှုတွင် မီဒီယာ မရှိပါ",
      errorHttp: "ဒေါင်းလုဒ်တောင်းဆိုမှု မအောင်မြင်ပါ (HTTP {status})",
      errorMime: "မျှော်လင့်ထားသည်မှာ {kind}၊ ရရှိသည်မှာ {mime}",
      errorRangeMissing: "Content-Range မရှိပါ သို့မဟုတ် မမှန်ကန်ပါ",
      errorRangeInconsistent: "မီဒီယာဒေတာအပိုင်းများ မကိုက်ညီပါ။ ဖိုင်မပျက်စီးစေရန် ဒေါင်းလုဒ်ကို ရပ်လိုက်သည်",
      errorRangeUnsupported: "ဆာဗာသည် အပိုင်းလိုက်ဒေါင်းလုဒ်ကို မပံ့ပိုးတော့ပါ",
      errorLength: "Content-Length မမှန်ကန်ပါ", errorEmpty: "မီဒီယာတုံ့ပြန်မှုတွင် ဒေတာမပါပါ",
      errorTooLarge: "ရရှိသော မီဒီယာဒေတာသည် မျှော်လင့်ထားသည့် ပမာဏထက် ကျော်လွန်နေသည်",
      errorIncomplete: "မီဒီယာဒေတာ မပြည့်စုံပါ။ ရရှိသော ဘိုက်အရေအတွက် မကိုက်ညီပါ",
      errorTimeout: "စက္ကန့် ၃၀ ကြာ ဒေတာမရရှိပါ။ ထပ်စမ်းပါ။",
      errorUnexpected: "ဒေါင်းလုဒ် မအောင်မြင်ပါ: {message}",
    },
  };

  const matchLanguage = (tag) => {
    const parts = String(tag || "").toLowerCase().replace(/_/g, "-").split("-");
    if (parts[0] === "zh") {
      if (parts.includes("hant")) return "zh-TW";
      if (parts.includes("hans")) return "zh-CN";
      return parts.some((part) => ["tw", "hk", "mo"].includes(part)) ? "zh-TW" : "zh-CN";
    }
    return Object.hasOwn(UI_LANGUAGES, parts[0]) ? parts[0] : null;
  };
  const readLanguagePreference = () => {
    try {
      const value = localStorage.getItem(CONFIG.languageKey);
      return Object.hasOwn(UI_LANGUAGES, value) ? value : "auto";
    } catch {
      return "auto";
    }
  };
  const resolveLanguage = () =>
    languagePreference !== "auto"
      ? languagePreference
      : (navigator.languages || [navigator.language]).map(matchLanguage).find(Boolean) || "en";

  let languagePreference = readLanguagePreference();
  let uiLanguage = resolveLanguage();
  const translate = (language, key, params = {}) =>
    (UI_MESSAGES[language]?.[key] ?? UI_MESSAGES.en[key] ?? key).replace(
      /\{(\w+)\}/g,
      (placeholder, name) => (Object.hasOwn(params, name) ? String(params[name]) : placeholder)
    );
  const t = (key, params) => translate(uiLanguage, key, params);

  let integerFormat = new Intl.NumberFormat(uiLanguage, { maximumFractionDigits: 0 });
  let decimalFormat = new Intl.NumberFormat(uiLanguage, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  });

  const downloadError = (key, params = {}, name = "Error") => {
    // 诊断信息保持英文稳定，展示时再按当前语言渲染。
    const diagnostic = { ...params };
    if (key === "errorMime") {
      diagnostic.kind = translate("en", params.kind);
      diagnostic.mime ||= translate("en", "unknownType");
    }
    return Object.assign(new Error(translate("en", key, diagnostic)), {
      name,
      translationKey: key,
      params,
    });
  };
  const formatDownloadError = (error) => {
    if (!error) return "";
    if (!error.translationKey) {
      return t("errorUnexpected", { message: error.message || String(error) });
    }
    const params = { ...error.params };
    if (error.translationKey === "errorMime") {
      params.kind = t(params.kind);
      params.mime ||= t("unknownType");
    }
    return t(error.translationKey, params);
  };
  // 批量时的「已用时」心跳：大文件一条要下很久，进度条不动时靠它证明还活着。
  const formatElapsed = (ms) => {
    const total = Math.max(0, Math.floor(ms / 1000));
    return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
  };
  const localizeDownloadButton = (button, loading = false) => {
    const label = t(loading ? "loadingMedia" : "download");
    if (button.title !== label) button.title = label;
    if (button.getAttribute("aria-label") !== label) button.setAttribute("aria-label", label);
    if (button.lang !== uiLanguage) button.lang = uiLanguage;
  };

  /* ------------------------------------------------------------------ *
   * 4. 通用工具                                                          *
   * ------------------------------------------------------------------ */
  const hashCode = (text) => {
    let hash = 0;
    for (let i = 0; i < text.length; i += 1) {
      hash = ((hash << 5) - hash + text.charCodeAt(i)) | 0;
    }
    return hash >>> 0;
  };

  const getMediaMetadata = (url) => {
    try {
      return JSON.parse(decodeURIComponent(url.split("/").pop()));
    } catch {
      return {};
    }
  };

  const EXTENSIONS_BY_MIME = {
    "video/mp4": ["mp4", "m4v"], "video/webm": ["webm"],
    "video/quicktime": ["mov"], "video/x-matroska": ["mkv"],
    "audio/ogg": ["ogg", "oga", "opus"], "audio/mpeg": ["mp3"],
    "audio/mp4": ["m4a"], "audio/webm": ["webm"], "audio/aac": ["aac"],
    "audio/wav": ["wav"], "audio/x-wav": ["wav"],
    "image/jpeg": ["jpg", "jpeg"], "image/png": ["png"],
    "image/webp": ["webp"], "image/gif": ["gif"], "image/avif": ["avif"],
    "image/heic": ["heic"], "image/bmp": ["bmp"], "image/svg+xml": ["svg"],
  };

  const getDownloadFileName = (url, mime, kind) => {
    const metadata = getMediaMetadata(url);
    let name = typeof metadata?.fileName === "string" ? metadata.fileName : "";
    if (!name) {
      try {
        const basename = decodeURIComponent(new URL(url, location.href).pathname.split("/").pop());
        if (/\.[a-z0-9]{2,5}$/i.test(basename)) name = basename;
      } catch {
        /* 部分 blob URL 不带文件名 */
      }
    }
    name = (name || hashCode(url).toString(36)).replace(/[\x00-\x1f\x7f<>:"/\\|?*]/g, "_");
    const allowed =
      EXTENSIONS_BY_MIME[mime?.toLowerCase()] ||
      (mime ? null : kind === "video" ? ["mp4"] : kind === "audio" ? ["ogg"] : ["jpg"]);
    const dot = name.lastIndexOf(".");
    if (!allowed) return dot > 0 ? name : `${name}.bin`;
    if (dot > 0 && allowed.includes(name.slice(dot + 1).toLowerCase())) return name;
    return `${dot > 0 ? name.slice(0, dot) : name}.${allowed[0]}`;
  };

  const saveMediaBlob = (blob, fileName) => {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    // 给浏览器留出消费 URL 的时间再释放。
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  };

  const getMediaSource = (media) =>
    media?.getAttribute("src")
      ? media.src
      : media?.currentSrc || media?.querySelector("source")?.src;

  const iconSvg =
    '<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="vertical-align: middle" aria-hidden="true" focusable="false"><path d="M12 3v12m-5-5 5 5 5-5M5 16v4h14v-4"/></svg>';

  /* ------------------------------------------------------------------ *
   * 4.5 下载目录（File System Access API）                               *
   * ------------------------------------------------------------------ */
  const DIRECTORY_DB = "tel-downloader-fs";
  const DIRECTORY_KEY = "directory";
  let directoryHandle = null;

  const openDirectoryDb = () =>
    new Promise((resolve) => {
      try {
        const request = indexedDB.open(DIRECTORY_DB, 1);
        request.onupgradeneeded = () => request.result.createObjectStore("handles");
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => resolve(null);
      } catch {
        resolve(null);
      }
    });

  const readDirectoryHandle = async () => {
    const db = await openDirectoryDb();
    if (!db) return null;
    try {
      return await new Promise((resolve) => {
        const request = db.transaction("handles", "readonly").objectStore("handles").get(DIRECTORY_KEY);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => resolve(null);
      });
    } finally {
      db.close();
    }
  };

  const writeDirectoryHandle = async (handle) => {
    const db = await openDirectoryDb();
    if (!db) return;
    try {
      await new Promise((resolve, reject) => {
        const request = db
          .transaction("handles", "readwrite")
          .objectStore("handles")
          .put(handle, DIRECTORY_KEY);
        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error);
      });
    } finally {
      db.close();
    }
  };

  const deleteDirectoryHandle = async () => {
    const db = await openDirectoryDb();
    if (!db) return;
    try {
      await new Promise((resolve) => {
        const request = db
          .transaction("handles", "readwrite")
          .objectStore("handles")
          .delete(DIRECTORY_KEY);
        request.onsuccess = () => resolve();
        request.onerror = () => resolve();
      });
    } finally {
      db.close();
    }
  };

  // 必须在用户手势内同步调用：授权面板只在手势期间允许弹出。
  // 同一次手势里可能连续发起多个下载，用短缓存避免重复弹窗。
  let pendingPermission = null;
  const requestDirectoryPermission = (handle) => {
    if (!handle || typeof handle.requestPermission !== "function") return null;
    if (pendingPermission) return pendingPermission;
    try {
      const request = handle
        .requestPermission({ mode: "readwrite" })
        .catch(() => "denied")
        .finally(() => {
          setTimeout(() => {
            if (pendingPermission === request) pendingPermission = null;
          }, 0);
        });
      pendingPermission = request;
      return request;
    } catch {
      return null;
    }
  };

  // 外部下载器（aria2 / IDM）只能接收真实 HTTP(S) 地址。
  const isDirectUrl = (url) => /^https?:/i.test(url || "");

  const copyText = (text) => {
    const fallback = () => {
      try {
        const area = document.createElement("textarea");
        area.value = text;
        area.style.cssText = "position:fixed;opacity:0;left:-9999px;";
        document.body.appendChild(area);
        area.select();
        const ok = document.execCommand("copy");
        area.remove();
        return ok;
      } catch {
        return false;
      }
    };
    try {
      if (page.navigator?.clipboard?.writeText) {
        page.navigator.clipboard.writeText(text).catch(fallback);
        return true;
      }
    } catch {
      /* 落到兜底复制 */
    }
    return fallback();
  };

  const saveTextFile = (name, text) => {
    try {
      const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
      const link = document.createElement("a");
      link.href = URL.createObjectURL(blob);
      link.download = name;
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 5000);
    } catch (error) {
      logger.error("Export failed:", error);
    }
  };

  /* ------------------------------------------------------------------ *
   * 5. 原生下载通道（仅 WebK）                                            *
   * ------------------------------------------------------------------ */
  const mediaOf = (message) =>
    message && message.media && (message.media.document || message.media.photo) ? message : null;

  const lookupMessage = (peerId, mid) => {
    try {
      return page.mtprotoMessagePort?.getMessageByPeer(peerId, +mid) || null;
    } catch {
      return null;
    }
  };

  const nativeDownload = async (message) => {
    const media = mediaOf(message);
    const manager = page.appDownloadManager;
    if (!manager || typeof manager.downloadToDisc !== "function") {
      throw new Error("appDownloadManager unavailable");
    }
    if (!media) throw new Error("Message has no downloadable media");
    await manager.downloadToDisc({ media });
  };

  const getWebKViewerMessage = () => {
    const viewer = page.appMediaViewer;
    if (!viewer) return null;
    const target = viewer.target || {};
    const mid = target.mid ?? target.messageId ?? viewer.currentMessageId ?? viewer.messageId;
    const peerId = target.peerId ?? target.peer?.id ?? target.peer?.peerId ?? viewer.peerId;
    if (mid == null || peerId == null) return null;
    return mediaOf(lookupMessage(peerId, mid));
  };

  /* ------------------------------------------------------------------ *
   * 6. 受保护消息：恢复文字复制（document-start 立即注册）                 *
   * ------------------------------------------------------------------ */
  document.addEventListener(
    "copy",
    (event) => {
      const node = getSelection()?.anchorNode;
      const element = node && (node.nodeType === 1 ? node : node.parentElement);
      // Telegram 的拦截是冒泡阶段的 document 监听；捕获阶段先执行并拦下它。
      if (element?.closest(".no-forwards")) event.stopImmediatePropagation();
    },
    true
  );

  const COPY_STYLE = ".bubble.no-forwards,.bubble.no-forwards *{ -webkit-user-select: text !important; user-select: text !important; }";
  const installCopyStyle = () => {
    const style = document.createElement("style");
    style.id = "tel-copy-unlock-style";
    style.textContent = COPY_STYLE;
    (document.head || document.documentElement)?.appendChild(style);
  };
  if (document.head || document.documentElement) installCopyStyle();
  else
    document.addEventListener("DOMContentLoaded", installCopyStyle, {
      once: true,
    });

  /* ------------------------------------------------------------------ *
   * 7. 右键菜单：记录候选消息（WebK 受限聊天）                            *
   * ------------------------------------------------------------------ */
  const contextMenuState = { pending: null };

  document.addEventListener(
    "mouseup",
    (event) => {
      contextMenuState.pending = null;
      if (!isWebK || event.button !== 2) return;
      const bubble = event.target.closest?.("[data-mid]");
      if (!bubble) return;
      const peerId = bubble.dataset.peerId;
      const mid = bubble.dataset.mid;
      if (!peerId || mid == null) return;
      if (mediaOf(lookupMessage(peerId, mid))) {
        contextMenuState.pending = { peerId, mid, bubble };
      }
    },
    true
  );

  /* ------------------------------------------------------------------ *
   * 8. 主流程                                                            *
   * ------------------------------------------------------------------ */
  const main = () => {
    /* 8.1 WebK 原生下载按钮识别 ---------------------------------------- */
    // WebK 的图标字体码点会随版本变化：只从页面里原有按钮学习字形，绝不从自建按钮学。
    const WEB_K_DOWNLOAD_BUTTON_SELECTOR = [
      "button.quality-download-options-button-menu",
      "button.tgico-download",
      'button[aria-label="Download"]',
      'button[title="Download"]',
    ].join(", ");
    const getWebKDownloadButtons = (container) =>
      Array.from(container.querySelectorAll(WEB_K_DOWNLOAD_BUTTON_SELECTOR)).filter(
        (button) => !button.matches(`.tel-download, .${CONFIG.pinnedButtonClass}`)
      );
    const isUsableDownloadButton = (button) => {
      if (
        button.disabled ||
        button.getAttribute("aria-disabled") === "true" ||
        button.hidden ||
        button.classList.contains("hide") ||
        button.closest("[inert]")
      ) {
        return false;
      }
      const style = getComputedStyle(button);
      return (
        style.visibility !== "hidden" &&
        style.visibility !== "collapse" &&
        button.getClientRects().length > 0
      );
    };
    let webKDownloadGlyph = "";
    const createWebKDownloadIcon = (buttonIcon = false) =>
      `<span class="tgico tel-download-icon${buttonIcon ? " button-icon" : ""}" aria-hidden="true">${
        webKDownloadGlyph || iconSvg
      }</span>`;
    const refreshWebKDownloadIcon = () => {
      for (const button of getWebKDownloadButtons(document)) {
        const glyph = button.querySelector(":scope > .tgico")?.textContent.trim();
        // 图标必须是单个私有区码点，忽略标签与菜单内容。
        if (!glyph || !/^[\uE000-\uF8FF]$/.test(glyph)) continue;
        if (glyph !== webKDownloadGlyph) {
          // 仅存内存：重新加载页面后字体可能已更换。
          webKDownloadGlyph = glyph;
          document.querySelectorAll(".tel-download-icon").forEach((icon) => {
            icon.textContent = glyph;
          });
        }
        return;
      }
    };
    // 受限聊天里被隐藏的转发按钮：同样只学原生按钮的字形，不写死码点。
    const WEB_K_FORWARD_BUTTON_SELECTOR = [
      "button.tgico-forward",
      'button[aria-label="Forward"]',
      'button[title="Forward"]',
    ].join(", ");
    let webKForwardGlyph = "";
    const readButtonGlyph = (button) => {
      const glyph =
        button.querySelector(":scope > .tgico")?.textContent.trim() || button.textContent.trim();
      // 图标必须是单个私有区码点，忽略标签与菜单内容。
      return glyph && /^[\uE000-\uF8FF]$/.test(glyph) ? glyph : "";
    };
    const refreshWebKForwardGlyph = () => {
      for (const button of document.querySelectorAll(WEB_K_FORWARD_BUTTON_SELECTOR)) {
        if (button.matches(`.tel-download, .${CONFIG.pinnedButtonClass}`)) continue;
        const glyph = readButtonGlyph(button);
        if (!glyph) continue;
        // 仅存内存：重新加载页面后字体可能已更换。
        webKForwardGlyph = glyph;
        return;
      }
    };
    // 旧版 WebK 的隐藏动作按钮不带用途类名，只剩图标字体码点；
    // 码点会随版本变化，仅作兜底，识别失败时不影响其他逻辑。
    const LEGACY_DOWNLOAD_GLYPHS = new Set(["\uE983", "\uE962", "\uE980", "\uE979", "\uE95A"]);
    const LEGACY_FORWARD_GLYPHS = new Set(["\uE9A4", "\uE99A", "\uE960", "\uE976"]);

    /* 8.2 分片下载引擎 ------------------------------------------------- */
    const contentRangeRegex = /^bytes (\d+)-(\d+)\/(\d+)$/;

    const fetchMediaPart = async (url, offset, total, kind, options = {}) => {
      const controller = new AbortController();
      const abort = () => controller.abort(options.signal.reason);
      let timer;
      let reader;
      const resetTimeout = () => {
        clearTimeout(timer);
        timer = setTimeout(
          () => controller.abort(downloadError("errorTimeout", {}, "TimeoutError")),
          CONFIG.idleTimeout
        );
      };
      const checkAborted = () => {
        if (controller.signal.aborted) throw controller.signal.reason;
      };
      if (options.signal?.aborted) abort();
      options.signal?.addEventListener("abort", abort, { once: true });
      resetTimeout();
      try {
        checkAborted();
        const res = await fetch(url, {
          headers: { Range: `bytes=${offset}-${offset + CONFIG.chunkSize - 1}` },
          signal: controller.signal,
        });
        if (res.status !== 200 && res.status !== 206) {
          throw downloadError("errorHttp", { status: res.status });
        }
        const mime = res.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase();
        if (!mime?.startsWith(`${kind}/`)) {
          throw downloadError("errorMime", { kind, mime });
        }
        let expectedSize;
        let totalSize;
        if (res.status === 206) {
          const match = res.headers.get("Content-Range")?.match(contentRangeRegex);
          if (!match) throw downloadError("errorRangeMissing");
          const [start, end, size] = match.slice(1).map(Number);
          if (
            ![start, end, size].every(Number.isSafeInteger) ||
            start !== offset ||
            end < start ||
            size <= end ||
            (total !== null && total !== size)
          ) {
            throw downloadError("errorRangeInconsistent");
          }
          expectedSize = end - start + 1;
          totalSize = size;
        } else {
          if (offset !== 0) throw downloadError("errorRangeUnsupported");
          const length = res.headers.get("Content-Length");
          if (length !== null && !res.headers.has("Content-Encoding")) {
            if (!/^\d+$/.test(length) || !Number.isSafeInteger(Number(length))) {
              throw downloadError("errorLength");
            }
            expectedSize = Number(length);
            totalSize = expectedSize;
          }
        }
        if (!res.body) throw downloadError("errorEmpty");
        reader = res.body.getReader();
        controller.signal.addEventListener(
          "abort",
          () => {
            reader.cancel().catch(() => {});
          },
          { once: true }
        );
        const chunks = [];
        let received = 0;
        options.onProgress?.(offset, totalSize ?? null);
        while (true) {
          checkAborted();
          const { value, done } = await reader.read();
          checkAborted();
          if (done) break;
          received += value.byteLength;
          if (expectedSize !== undefined && received > expectedSize) {
            throw downloadError("errorTooLarge");
          }
          // 等磁盘写完再读下一块，避免慢盘导致写入队列无限堆积。
          clearTimeout(timer);
          if (options.write) await options.write(value);
          else chunks.push(value);
          checkAborted();
          resetTimeout();
          options.onProgress?.(offset + received, totalSize ?? null);
        }
        if (!received || (expectedSize !== undefined && received !== expectedSize)) {
          throw downloadError("errorIncomplete");
        }
        return {
          blob: options.write ? null : new Blob(chunks, { type: mime }),
          mime,
          totalSize: totalSize ?? received,
          nextOffset: offset + received,
        };
      } catch (error) {
        controller.abort(error);
        throw controller.signal.reason || error;
      } finally {
        clearTimeout(timer);
        options.signal?.removeEventListener("abort", abort);
        if (reader) {
          await reader.cancel().catch(() => {});
          reader.releaseLock();
        }
      }
    };

    /* 8.3 任务队列 ----------------------------------------------------- */
    const downloadTasks = new Map();
    const activeDownloads = new Map();
    const downloadQueue = [];
    let runningDownloads = 0;

    const formatBytes = (bytes) => {
      const units = ["B", "KB", "MB", "GB", "TB"];
      const index = Math.min(
        Math.floor(Math.log(Math.max(bytes, 1)) / Math.log(1024)),
        units.length - 1
      );
      return `${(index ? decimalFormat : integerFormat).format(bytes / 1024 ** index)} ${
        units[index]
      }`;
    };

    const updateTaskPanel = () => {
      const panel = document.getElementById(CONFIG.panelId);
      if (!panel) return;
      panel.hidden = !panel.querySelector(".tel-task");
      if (panel.hidden) setSettingsOpen(false);
      panel.querySelector(".tel-panel-title").textContent = activeDownloads.size
        ? t("downloadsCount", { count: integerFormat.format(activeDownloads.size) })
        : t("downloads");
    };

    const dismissTask = (task) => {
      document.getElementById(CONFIG.cardPrefix + task.id)?.remove();
      if (task.finished) downloadTasks.delete(task.id);
      updateTaskPanel();
    };

    const showTask = (task) => {
      const panel = document.getElementById(CONFIG.panelId);
      const list = panel.querySelector(".tel-task-list");
      list.hidden = false;
      const toggle = panel.querySelector(".tel-panel-toggle");
      toggle.textContent = t("hide");
      toggle.setAttribute("aria-expanded", "true");
      let card = document.getElementById(CONFIG.cardPrefix + task.id);
      if (!card) {
        card = document.createElement("div");
        card.id = CONFIG.cardPrefix + task.id;
        card.className = "tel-task";
        card.tabIndex = -1;
        card.innerHTML = `
          <div class="tel-task-heading"><p class="filename" dir="auto"></p><button type="button" data-action="dismiss">&times;</button></div>
          <span class="tel-task-state" role="status" aria-live="polite"></span>
          <div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100"><div></div><p></p></div>
          <p class="tel-task-details"></p><p class="tel-task-error"></p>
          <div class="tel-task-actions"><button type="button" data-action="cancel"></button><button type="button" data-action="retry" hidden></button></div>`;
        card.querySelector('[data-action="dismiss"]').onclick = () => dismissTask(task);
        card.querySelector('[data-action="cancel"]').onclick = () => {
          if (task.finished || task.state === "saving") return;
          task.controller.abort(new DOMException("Download cancelled", "AbortError"));
          if (!task.running) finishTask(task, "cancelled");
        };
        card.querySelector('[data-action="retry"]').onclick = () => {
          dismissTask(task);
          downloadMedia(task.url, task.kind);
        };
        list.prepend(card);
      }
      renderTask(task);
      return card;
    };

    const renderTask = (task) => {
      const card = document.getElementById(CONFIG.cardPrefix + task.id);
      if (!card) return;
      const label = t(task.state);
      card.dataset.state = task.state;
      const title = card.querySelector(".filename");
      title.textContent = task.fileName;
      title.title = task.fileName;
      const state = card.querySelector(".tel-task-state");
      if (state.textContent !== label) state.textContent = label;
      const progress = card.querySelector(".progress");
      progress.setAttribute("aria-label", t("progress"));
      card.querySelector('[data-action="dismiss"]').setAttribute("aria-label", t("dismiss"));
      const percent = task.total ? Math.min(100, Math.floor((task.received * 100) / task.total)) : null;
      progress.classList.toggle("tel-indeterminate", task.state === "downloading" && percent === null);
      if (percent === null) progress.removeAttribute("aria-valuenow");
      else progress.setAttribute("aria-valuenow", percent);
      progress.setAttribute("aria-valuetext", label);
      progress.querySelector("div").style.width = (percent ?? 0) + "%";
      progress.querySelector("p").textContent =
        task.state === "downloading" && percent !== null
          ? `${integerFormat.format(percent)}%`
          : label;
      const speed = task.received / Math.max((performance.now() - task.startedAt) / 1000, 0.1);
      // 批量任务按「条」计数，并露出当前正在处理的那一条与已用时，否则看起来像进度不动。
      card.querySelector(".tel-task-details").textContent = task.batch
        ? `${integerFormat.format(task.received)} / ${integerFormat.format(task.total ?? 0)}` +
          (task.state === "downloading"
            ? ` · ${formatElapsed(performance.now() - task.startedAt)}` +
              (task.currentName ? ` · ${task.currentName}` : "")
            : "")
        : formatBytes(task.received) +
          (task.total !== null ? ` / ${formatBytes(task.total)}` : "") +
          (task.state === "downloading" && task.received
            ? ` · ${t("rate", { speed: formatBytes(speed) })}`
            : "");
      const error = card.querySelector(".tel-task-error");
      error.textContent = formatDownloadError(task.error);
      error.hidden = !task.error;
      const cancel = card.querySelector('[data-action="cancel"]');
      cancel.textContent = t("cancel");
      cancel.hidden = task.finished;
      cancel.disabled = task.state === "saving";
      const retry = card.querySelector('[data-action="retry"]');
      retry.textContent = t("retry");
      retry.hidden = Boolean(task.batch) || !["failed", "cancelled"].includes(task.state);
      task.renderedAt = performance.now();
      task.renderedBytes = task.received;
      updateTaskPanel();
    };

    const finishTask = (task, state, error) => {
      if (task.finished) return;
      task.finished = true;
      clearTimeout(task.progressTimer);
      clearInterval(task.ticker);
      task.state = state;
      task.error = error;
      if (activeDownloads.get(task.key) === task) activeDownloads.delete(task.key);
      if (error) logger.error(error, task.fileName);
      renderTask(task);
      if (!document.getElementById(CONFIG.cardPrefix + task.id)) downloadTasks.delete(task.id);
      updateTaskPanel();
      task.resolve({ state, fileName: task.fileName, received: task.received });
    };

    // 目录内重名时自动加序号，避免整组下载互相覆盖。
    const uniqueFileName = async (directory, name) => {
      const dot = name.lastIndexOf(".");
      const base = dot > 0 ? name.slice(0, dot) : name;
      const extension = dot > 0 ? name.slice(dot) : "";
      for (let index = 0; index < 500; index += 1) {
        const candidate = index ? `${base} (${index})${extension}` : name;
        try {
          await directory.getFileHandle(candidate, { create: false });
        } catch (error) {
          if (error?.name === "NotFoundError") return candidate;
          if (error?.name === "TypeMismatchError") continue;
          throw error;
        }
      }
      return `${base}-${Date.now()}${extension}`;
    };

    const createDirectoryWritable = async (directory, task) => {
      task.fileName = await uniqueFileName(directory, task.fileName);
      const fileHandle = await directory.getFileHandle(task.fileName, { create: true });
      return fileHandle.createWritable();
    };

    const runTask = async (task) => {
      const parts = [];
      let writable;
      const checkCancelled = () => {
        if (task.controller.signal.aborted) throw task.controller.signal.reason;
      };
      try {
        checkCancelled();
        task.state = "preparing";
        renderTask(task);
        if (task.directory) {
          try {
            writable = await createDirectoryWritable(task.directory, task);
          } catch (error) {
            // 权限被撤回或目录不可写：退回浏览器默认下载，不让整组卡死。
            logger.error("Folder write unavailable, falling back to browser download:", error);
            task.directory = null;
          }
        }
        if (!writable && task.handle) writable = await task.handle.createWritable();
        if (writable) renderTask(task);
        checkCancelled();
        task.state = "downloading";
        task.startedAt = performance.now();
        renderTask(task);
        let offset = 0;
        let total = null;
        let mime;
        do {
          const part = await fetchMediaPart(task.url, offset, total, task.kind, {
            signal: task.controller.signal,
            write: writable ? (chunk) => writable.write(chunk) : undefined,
            onProgress: (received, size) => {
              task.received = received;
              task.total = size;
              if (
                (!task.renderedBytes && received) ||
                performance.now() - task.renderedAt >= 100 ||
                received === size
              ) {
                renderTask(task);
              } else if (!task.progressTimer) {
                task.progressTimer = setTimeout(() => {
                  task.progressTimer = null;
                  renderTask(task);
                }, 100);
              }
            },
          });
          checkCancelled();
          if (!writable) parts.push(part.blob);
          offset = part.nextOffset;
          total = part.totalSize;
          mime = part.mime;
          // 已按目录写入的文件名不能改；自定义名（来自消息元数据）同样保留。
          if (!writable && !task.customFileName) {
            task.fileName = getDownloadFileName(task.url, mime, task.kind);
          }
          task.received = offset;
          task.total = total;
          renderTask(task);
        } while (offset < total);
        checkCancelled();
        task.state = "saving";
        renderTask(task);
        if (writable) await writable.close();
        else saveMediaBlob(new Blob(parts, { type: mime }), task.fileName);
        finishTask(task, writable ? "completed" : "handedOff");
      } catch (error) {
        if (writable) {
          try {
            await writable.abort();
          } catch {
            /* 已关闭或已出错 */
          }
        }
        if (task.controller.signal.aborted || error.name === "AbortError")
          finishTask(task, "cancelled");
        else finishTask(task, "failed", error);
      } finally {
        parts.length = 0;
        task.handle = null;
      }
    };

    const pumpDownloads = () => {
      while (runningDownloads < CONFIG.maxConcurrentDownloads && downloadQueue.length) {
        const task = downloadQueue.shift();
        if (task.finished) continue;
        runningDownloads++;
        task.running = true;
        runTask(task).finally(() => {
          task.running = false;
          runningDownloads--;
          pumpDownloads();
        });
      }
    };

    const downloadMedia = (url, kind, options = {}) => {
      if (typeof url !== "string" || !url) return Promise.resolve();
      const directory = options.directory ?? directoryHandle;
      // 目录写入需要 readwrite 权限，必须在点击手势内发起授权请求。
      if (directory) requestDirectoryPermission(directory);
      const key = `${kind}:${url}`;
      const existing = activeDownloads.get(key);
      if (existing) {
        const card = showTask(existing);
        card.scrollIntoView({ block: "nearest" });
        card.focus({ preventScroll: true });
        return existing.promise;
      }
      // 保留少量历史，但不保留媒体缓冲。
      for (const previous of downloadTasks.values()) {
        if (downloadTasks.size < CONFIG.historyLimit) break;
        if (previous.finished) dismissTask(previous);
      }
      const task = {
        id: Math.random().toString(36).slice(2) + Date.now().toString(36),
        key,
        url,
        kind,
        fileName: options.fileName || getDownloadFileName(url, getMediaMetadata(url)?.mimeType, kind),
        customFileName: options.fileName || "",
        directory: directory || null,
        state: "preparing",
        received: 0,
        total: null,
        controller: new AbortController(),
        startedAt: performance.now(),
        renderedAt: 0,
        finished: false,
      };
      task.promise = new Promise((resolve) => {
        task.resolve = resolve;
      });
      downloadTasks.set(task.id, task);
      activeDownloads.set(key, task);
      showTask(task);
      const enqueue = (handle) => {
        if (task.finished) return;
        task.handle = handle;
        task.state = "queued";
        renderTask(task);
        downloadQueue.push(task);
        pumpDownloads();
      };
      try {
        // 一律不弹保存框：指定了目录就流式写盘，没指定就用 <a download> 落系统下载目录。
        enqueue(null);
      } catch (error) {
        finishTask(task, "failed", error);
      }
      return task.promise;
    };

    /* 原生优先、URL 兜底的统一入口 ------------------------------------- */
    const startDownload = async (kind, getUrl, message = null) => {
      if (message) {
        try {
          await nativeDownload(message);
          return;
        } catch (error) {
          logger.error("Native download failed, falling back to URL:", error);
        }
      }
      const url = typeof getUrl === "function" ? getUrl() : getUrl;
      if (url) downloadMedia(url, kind);
    };

    /* 整组（相册 + 评论区）收集 ----------------------------------------- */
    const findMediaElement = (root) => {
      if (!root) return null;
      if (root.matches?.("img, video")) return root;
      const video = root.querySelector("video");
      if (video) return video;
      const albumItem = root.querySelector(".album img, .album video");
      if (albumItem) return albumItem;
      return root.querySelector("img.thumbnail, img.media-photo, img");
    };

    const getElementMediaSource = (element) => {
      if (!element) return "";
      if (element.tagName === "IMG" || element.tagName === "VIDEO") {
        return element.getAttribute("src") ? element.src : element.currentSrc || "";
      }
      const media = findMediaElement(element);
      return media ? getElementMediaSource(media) : "";
    };

    const getMessageFileName = (message, url, kind) => {
      const attributes = message?.media?.document?.attributes;
      const named = Array.isArray(attributes)
        ? attributes.find((item) => typeof item?.fileName === "string" && item.fileName)?.fileName
        : "";
      if (named) return named.replace(/[\x00-\x1f\x7f<>:"/\\|?*]/g, "_");
      if (message?.mid != null) {
        return kind === "video" ? `video-${message.mid}.mp4` : `photo-${message.mid}.jpg`;
      }
      return getDownloadFileName(url, null, kind);
    };

    // 从已渲染气泡取单条消息的媒体源；取不到时上层回退原生下载。
    const findRenderedMediaEntry = (message) => {
      const mid = message?.mid;
      if (mid == null) return null;
      const bubble = Array.from(document.querySelectorAll(".bubble[data-mid]")).find(
        (element) => element.dataset.mid === String(mid) && !element.closest(".chats-container")
      );
      if (!bubble) return null;
      const media = findMediaElement(bubble);
      const url = getElementMediaSource(media);
      if (!url) return null;
      const kind = media.tagName === "VIDEO" ? "video" : "image";
      return { url, kind, name: getMessageFileName(message, url, kind) };
    };

    const collectGroupEntries = (message, peerId, sourceBubble) => {
      const entries = new Map();
      const addEntry = (peer, msg, element) => {
        const media = findMediaElement(element);
        if (!media) return;
        const url = getElementMediaSource(media);
        const kind = media.tagName === "VIDEO" ? "video" : "image";
        const key = `${peer}:${msg?.mid ?? url}`;
        if (entries.has(key)) return;
        entries.set(key, {
          peerId: String(peer),
          message: msg || null,
          url,
          kind,
          name: getMessageFileName(msg, url, kind),
        });
      };

      addEntry(peerId, message, sourceBubble);

      // 1) 同一相册：同一 bubble 内带 data-mid 的子项
      const groupedId = message.groupedId;
      const bubble = sourceBubble?.closest(".bubble") || sourceBubble;
      if (groupedId != null && bubble) {
        (bubble.querySelector(".album") || bubble)
          .querySelectorAll("[data-mid]")
          .forEach((element) => {
            const mid = element.dataset.mid;
            if (mid == null) return;
            const peer = element.closest("[data-peer-id]")?.dataset.peerId || peerId;
            if (String(peer) !== String(peerId)) return;
            const candidate = lookupMessage(peer, mid);
            if (String(candidate?.groupedId) !== String(groupedId)) return;
            addEntry(peer, candidate, element);
          });
      }

      // 2) 评论区：侧栏中属于关联讨论组（其他 peer）的已渲染媒体
      const mainContainer = sourceBubble?.closest(".chat-container") || null;
      document.querySelectorAll(".bubble[data-mid]").forEach((element) => {
        const peer = element.closest("[data-peer-id]")?.dataset.peerId;
        if (!peer || String(peer) === String(peerId)) return;
        if (mainContainer && mainContainer.contains(element)) return;
        if (element.closest(".chats-container")) return; // 会话列表不是评论区
        const mid = element.dataset.mid;
        const candidate = lookupMessage(peer, mid);
        if (mediaOf(candidate)) addEntry(peer, candidate, element);
      });

      return Array.from(entries.values());
    };

    /* 自动归档：<根目录>/<会话名>/<日期>/<album-id|msg-id>/ -------------------- */
    const sanitizeSegment = (value) =>
      (value || "")
        .replace(/[\x00-\x1f\x7f<>:"/\\|?*]/g, "_")
        .replace(/\s+/g, " ")
        .trim()
        .slice(0, 60);

    const getChatFolderName = (peerId) => {
      const title = Array.from(document.querySelectorAll("#column-center .peer-title"))
        .find((element) => !element.closest(".chats-container"))
        ?.textContent?.trim();
      return sanitizeSegment(title) || `chat-${peerId || "unknown"}`;
    };

    const getGroupFolderName = (message) => {
      const id = message?.groupedId ?? message?.mid ?? Date.now();
      return sanitizeSegment(`${message?.groupedId != null ? "album" : "msg"}-${id}`);
    };

    const getDateSegment = () => {
      const now = new Date();
      return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(
        now.getDate()
      ).padStart(2, "0")}`;
    };

    const ensureChatDateFolder = async (root, peerId) => {
      const chatFolder = await root.getDirectoryHandle(getChatFolderName(peerId), { create: true });
      return chatFolder.getDirectoryHandle(getDateSegment(), { create: true });
    };

    const ensureArchiveFolder = async (root, message, peerId) => {
      const dateFolder = await ensureChatDateFolder(root, peerId);
      return dateFolder.getDirectoryHandle(getGroupFolderName(message), { create: true });
    };

    // 整组下载：有目录 → 归档到按会话/日期/相册命名的子目录；无目录 → 原生串行。
    const downloadGroup = async (entries, options = {}) => {
      if (!entries.length) return;
      const { message, peerId, root: rootPromise, permission } = options;
      if (rootPromise) {
        if (permission) await permission.catch(() => "denied");
        const root = await rootPromise.catch(() => null);
        if (root) {
          let archive = root;
          try {
            archive = await ensureArchiveFolder(root, message, peerId);
          } catch (error) {
            logger.error("Archive folder unavailable, using root folder:", error);
          }
          // 串行处理并挂聚合进度：原来是一口气全部入队，面板里看不到整体进度。
          const task = createBatchTask(entries.length, t("menuGroupDownload"));
          for (let i = 0; i < entries.length; i += 1) {
            if (task.controller.signal.aborted) break;
            const entry = entries[i];
            beginBatchItem(task, i, entry.name || batchItemName(entry.message));
            try {
              if (entry.url) {
                await downloadMedia(entry.url, entry.kind, {
                  directory: archive,
                  fileName: entry.name,
                });
              } else if (entry.message) {
                await nativeDownload(entry.message);
              }
            } catch (error) {
              logger.error("Batch item failed:", error);
            }
            settleBatchItem(task, i);
          }
          closeBatchTask(task, "completed");
          return;
        }
      }
      const messages = entries.map((entry) => entry.message).filter(Boolean);
      if (messages.length) await startBatchDownload(messages.map((message) => ({ message })));
    };

    /* 8.3.1 批量任务卡（选择栏批量与整组下载共用） --------------------- */
    const createBatchTask = (total, title) => {
      const task = {
        id: Math.random().toString(36).slice(2) + Date.now().toString(36),
        key: `batch:${Date.now()}`,
        url: "batch",
        kind: "batch",
        batch: true,
        fileName: title || t("batchTitle"),
        state: "downloading",
        received: 0,
        total,
        currentName: "",
        controller: new AbortController(),
        startedAt: performance.now(),
        renderedAt: 0,
        finished: false,
      };
      task.promise = new Promise((resolve) => {
        task.resolve = resolve;
      });
      downloadTasks.set(task.id, task);
      showTask(task);
      task.running = true;
      // 每秒重绘一次，让「已用时」持续走动，避免大文件期间看着像卡死。
      task.ticker = setInterval(() => {
        if (!task.finished) renderTask(task);
      }, 1000);
      return task;
    };

    // 处理第 index 条（0 起）之前调用：进度停在「已完成条数」，并露出当前文件名。
    const beginBatchItem = (task, index, name) => {
      task.received = index;
      task.currentName = name || "";
      renderTask(task);
    };

    // 无论成败都推进一格，否则失败项会让进度条永久卡住。
    const settleBatchItem = (task, index) => {
      task.received = index + 1;
      renderTask(task);
      // 目录模式下每条还会生成自己的卡，聚合卡需要保持在列表最上面。
      const card = document.getElementById(CONFIG.cardPrefix + task.id);
      const list = document.querySelector(`#${CONFIG.panelId} .tel-task-list`);
      if (card && list && list.firstElementChild !== card) list.prepend(card);
    };

    const closeBatchTask = (task, state) => {
      task.running = false;
      task.currentName = "";
      if (task.controller.signal.aborted || state === "cancelled") {
        finishTask(task, "cancelled");
        return;
      }
      task.received = task.total ?? task.received;
      finishTask(task, state);
      setTimeout(() => dismissTask(task), 8000);
    };

    // 批量项的文件名：优先用消息里的真实文件名，否则按 mid 推一个占位名。
    const batchItemName = (message) => {
      const media = mediaOf(message);
      if (!media) return "";
      return getMessageFileName(media, "", media.media?.document ? "video" : "image");
    };

    // 串行下载一组消息，进度显示在任务面板；返回 Promise。
    const startBatchDownload = async (items) => {
      if (!items.length) return;
      if (items.length === 1) {
        nativeDownload(items[0].message).catch((error) =>
          logger.error("Download failed:", error)
        );
        return;
      }
      const task = createBatchTask(items.length, t("menuGroupDownload"));
      for (let i = 0; i < items.length; i += 1) {
        if (task.controller.signal.aborted) break;
        beginBatchItem(task, i, items[i].name || batchItemName(items[i].message));
        try {
          await nativeDownload(items[i].message);
        } catch (error) {
          logger.error("Batch item failed:", error);
        }
        settleBatchItem(task, i);
        if (i < items.length - 1 && !task.controller.signal.aborted) {
          await new Promise((resolve) => setTimeout(resolve, CONFIG.batchGap));
        }
      }
      closeBatchTask(task, "handedOff");
    };

    /* 8.4 下载按钮工厂 ------------------------------------------------- */
    const createDownloadButton = (className, iconHTML, onClick) => {
      const button = document.createElement("button");
      button.className = className;
      button.innerHTML = iconHTML;
      button.type = "button";
      localizeDownloadButton(button);
      button.onclick = onClick;
      return button;
    };

    /* 8.5 WebA（webz / web.telegram.org/a）-------------------------------- */
    const getWebADownloadButtons = (container) =>
      Array.from(container.querySelectorAll("button")).filter(
        (button) =>
          !button.classList.contains("tel-download") &&
          (button.querySelector(".icon-download") ||
            button.title === "Download" ||
            button.getAttribute("aria-label") === "Download") &&
          isUsableDownloadButton(button)
      );
    const downloadCurrentWebAMedia = () => {
      const media = document.querySelector("#MediaViewer .MediaViewerSlide--active");
      const videoUrl = getMediaSource(media?.querySelector(".VideoPlayer video"));
      const imageUrl = getMediaSource(media?.querySelector(".MediaViewerContent > div > img"));
      if (videoUrl) downloadMedia(videoUrl, "video");
      else if (imageUrl) downloadMedia(imageUrl, "image");
    };

    const refreshWebA = () => {
      const story = document.getElementById("StoryViewer");
      if (story) {
        const header =
          story.querySelector(".GrsJNw3y") || story.querySelector(".DropdownMenu")?.parentNode;
        if (header && !header.querySelector(".tel-download") && !getWebADownloadButtons(header).length) {
          header.prepend(
            createDownloadButton(
              "Button TkphaPyQ tiny translucent-white round tel-download",
              '<i class="icon icon-download" aria-hidden="true"></i>',
              () => {
                const current = document.getElementById("StoryViewer");
                const videoUrl = getMediaSource(current?.querySelector("video"));
                const images = current?.querySelectorAll("img.PVZ8TOWS");
                const imageUrl = getMediaSource(images?.[images.length - 1]);
                if (videoUrl) downloadMedia(videoUrl, "video");
                else if (imageUrl) downloadMedia(imageUrl, "image");
              }
            )
          );
        }
      }
      const media = document.querySelector("#MediaViewer .MediaViewerSlide--active");
      const actions = document.querySelector("#MediaViewer .MediaViewerActions");
      if (!media || !actions) return;
      const video = media.querySelector(".VideoPlayer video");
      const image = media.querySelector(".MediaViewerContent > div > img");
      if (!getMediaSource(video) && !getMediaSource(image)) return;
      const makeButton = () =>
        createDownloadButton(
          "Button smaller translucent-white round tel-download",
          '<i class="icon icon-download" aria-hidden="true"></i>',
          downloadCurrentWebAMedia
        );
      if (getWebADownloadButtons(actions).length) {
        actions.querySelectorAll("button.tel-download").forEach((button) => button.remove());
      } else if (!actions.querySelector("button.tel-download")) {
        actions.prepend(makeButton());
      }
      const controls = media.querySelector(".VideoPlayerControls .buttons");
      if (video && controls && !controls.querySelector("button.tel-download")) {
        const spacer = controls.querySelector(".spacer");
        if (spacer) spacer.after(makeButton());
        else controls.appendChild(makeButton());
      }
    };

    /* 8.6 WebK（webk / web.telegram.org/k）------------------------------- */
    const downloadCurrentWebKMedia = () => {
      // 工具栏可能比媒体节点存活更久，两者都在点击时解析。
      const viewer = document.querySelector(".media-viewer-whole");
      if (!viewer) return;
      const buttons = viewer.querySelector(".media-viewer-topbar .media-viewer-buttons");
      const nativeDownloadButton =
        buttons &&
        getWebKDownloadButtons(buttons).find(
          (button) =>
            isUsableDownloadButton(button) &&
            !button.matches(
              '.quality-download-options-button-menu, .btn-menu-toggle, [aria-haspopup="menu"], [aria-haspopup="true"]'
            )
        );
      if (nativeDownloadButton) {
        nativeDownloadButton.click();
        return;
      }
      const media = viewer.querySelector(".media-viewer-movers .media-viewer-aspecter");
      const videoUrl = getMediaSource(media?.querySelector("video"));
      const imageUrl = getMediaSource(media?.querySelector("img.thumbnail"));
      if (videoUrl) {
        startDownload("video", videoUrl, isWebK ? getWebKViewerMessage() : null);
      } else if (imageUrl) {
        downloadMedia(imageUrl, "image");
      }
    };

    const getPinnedMedia = () => {
      const pinned = document.querySelector(".pinned-audio");
      const mid = pinned?.getAttribute("data-mid");
      if (!mid) return null;
      const peer = pinned.getAttribute("data-peer-id");
      const candidates = Array.from(document.querySelectorAll("audio-element")).filter(
        (element) =>
          element.closest(".bubble") &&
          element.getAttribute("data-mid") === mid &&
          (!peer ||
            element.closest("[data-peer-id]")?.getAttribute("data-peer-id") === peer)
      );
      const element =
        candidates.length === 1
          ? candidates[0]
          : candidates.find((candidate) => candidate.audio && !candidate.audio.paused);
      const url = getMediaSource(element?.audio);
      return url ? { url, kind: element.audio.tagName === "AUDIO" ? "audio" : "video" } : null;
    };

    const refreshWebK = () => {
      refreshWebKDownloadIcon();

      // 选择栏与右键菜单可能比容器晚一步出现，随定时刷新补挂。
      document.querySelectorAll(".selection-wrapper").forEach(addBatchButton);
      if (contextMenuState.pending) scheduleMenuScan();

      // 置顶媒体每次点击重新解析：来源可能在切换后加载，且 mid 可跨会话重复。
      const pinnedAudio = document.querySelector(".pinned-audio");
      const pinnedUtils = pinnedAudio?.querySelector(".pinned-container-wrapper-utils");
      if (pinnedUtils) {
        let button = pinnedUtils.querySelector(`.${CONFIG.pinnedButtonClass}`);
        if (!button) {
          button = document.createElement("button");
          button.className = `btn-icon tgico-download ${CONFIG.pinnedButtonClass}`;
          button.innerHTML = createWebKDownloadIcon(true);
          button.type = "button";
          button.onclick = (event) => {
            event.stopPropagation();
            const media = getPinnedMedia();
            if (!media) return;
            startDownload(media.kind, media.url);
          };
          pinnedUtils.appendChild(button);
        }
        const disabled = !getPinnedMedia();
        if (button.disabled !== disabled) button.disabled = disabled;
        localizeDownloadButton(button, disabled);
      }

      // 故事（Stories）
      const storiesContainer = document.getElementById("stories-viewer");
      if (storiesContainer) {
        const createStoryButton = () => {
          const downloadButton = document.createElement("button");
          downloadButton.className = "btn-icon rp tel-download";
          downloadButton.innerHTML = `${createWebKDownloadIcon()}<div class="c-ripple"></div>`;
          downloadButton.setAttribute("type", "button");
          localizeDownloadButton(downloadButton);
          downloadButton.onclick = () => {
            const video = storiesContainer.querySelector("video.media-video");
            const videoSrc = video?.src || video?.currentSrc || video?.querySelector("source")?.src;
            if (videoSrc) {
              downloadMedia(videoSrc, "video");
              return;
            }
            const imageSrc = storiesContainer.querySelector("img.media-photo")?.src;
            if (imageSrc) downloadMedia(imageSrc, "image");
          };
          return downloadButton;
        };
        const storyHeader = storiesContainer.querySelector("[class*='_ViewerStoryHeaderRight']");
        if (storyHeader && !storyHeader.querySelector(".tel-download")) {
          storyHeader.prepend(createStoryButton());
        }
        const storyFooter = storiesContainer.querySelector("[class*='_ViewerStoryFooterRight']");
        if (storyFooter && !storyFooter.querySelector(".tel-download")) {
          storyFooter.prepend(createStoryButton());
        }
      }

      // 所有打开的媒体都在 .media-viewer-movers > .media-viewer-aspecter 下
      const mediaContainer = document.querySelector(".media-viewer-whole");
      if (!mediaContainer) return;
      const mediaAspecter = mediaContainer.querySelector(
        ".media-viewer-movers .media-viewer-aspecter"
      );
      const mediaButtons = mediaContainer.querySelector(
        ".media-viewer-topbar .media-viewer-buttons"
      );
      if (!mediaAspecter || !mediaButtons) return;

      // 还原受限媒体里被隐藏的下载/转发控件；其余隐藏操作可能对该媒体本就不可用。
      refreshWebKForwardGlyph();
      const classifyHiddenAction = (button) => {
        const glyph = readButtonGlyph(button);
        if (
          button.matches(WEB_K_DOWNLOAD_BUTTON_SELECTOR) ||
          (webKDownloadGlyph && glyph === webKDownloadGlyph) ||
          LEGACY_DOWNLOAD_GLYPHS.has(glyph)
        ) {
          return "download";
        }
        if (
          button.matches(WEB_K_FORWARD_BUTTON_SELECTOR) ||
          (webKForwardGlyph && glyph === webKForwardGlyph) ||
          LEGACY_FORWARD_GLYPHS.has(glyph)
        ) {
          return "forward";
        }
        return null;
      };
      for (const button of mediaButtons.querySelectorAll("button.hide")) {
        if (button.disabled || button.getAttribute("aria-disabled") === "true") continue;
        const action = classifyHiddenAction(button);
        if (!action) continue;
        button.classList.remove("hide");
        // 仅在原按钮丢了用途类名时补类，避免图标重复渲染。
        if (!Array.from(button.classList).some((name) => name.startsWith("tgico-"))) {
          button.classList.add(`tgico-${action}`);
        }
      }

      // 按用途而非字体码点识别原生下载按钮。
      const nativeDownloadButtons = getWebKDownloadButtons(mediaButtons).filter(
        isUsableDownloadButton
      );
      if (nativeDownloadButtons.length > 0) {
        mediaButtons.querySelectorAll("button.tel-download").forEach((button) => {
          button.remove();
        });
      }
      const hasDownloadButton =
        nativeDownloadButtons.length > 0 || mediaButtons.querySelector("button.tel-download");

      if (mediaAspecter.querySelector(".ckin__player")) {
        // 1. 播放器视频
        const controls = mediaAspecter.querySelector(".default__controls.ckin__controls");
        if (controls && !controls.querySelector(".tel-download")) {
          const brControls = controls.querySelector(".bottom-controls .right-controls");
          const downloadButton = document.createElement("button");
          downloadButton.className = "btn-icon default__button tgico-download tel-download";
          downloadButton.innerHTML = createWebKDownloadIcon();
          downloadButton.setAttribute("type", "button");
          localizeDownloadButton(downloadButton);
          downloadButton.onclick = downloadCurrentWebKMedia;
          brControls?.prepend(downloadButton);
        }
      } else if (mediaAspecter.querySelector("video") && !hasDownloadButton) {
        // 2. 原生 video 节点（GIF 或未完成加载的视频）
        const downloadButton = document.createElement("button");
        downloadButton.className = "btn-icon tgico-download tel-download";
        downloadButton.innerHTML = createWebKDownloadIcon(true);
        downloadButton.setAttribute("type", "button");
        localizeDownloadButton(downloadButton);
        downloadButton.onclick = downloadCurrentWebKMedia;
        mediaButtons.prepend(downloadButton);
      } else if (!hasDownloadButton) {
        // 3. 没有下载按钮的图片
        const thumbnail = mediaAspecter.querySelector("img.thumbnail");
        if (!thumbnail || !thumbnail.src) return;
        const downloadButton = document.createElement("button");
        downloadButton.className = "btn-icon tgico-download tel-download";
        downloadButton.innerHTML = createWebKDownloadIcon(true);
        downloadButton.setAttribute("type", "button");
        localizeDownloadButton(downloadButton);
        downloadButton.onclick = downloadCurrentWebKMedia;
        mediaButtons.prepend(downloadButton);
      }
    };

    /* 8.7 右键菜单"下载"项 --------------------------------------------- */
    const makeMenuItem = (id, label, onClick) => {
      const item = document.createElement("div");
      item.className = "btn-menu-item rp-overflow";
      item.id = id;
      const icon = document.createElement("span");
      icon.className = "btn-menu-item-icon";
      icon.style.fontSize = "1rem";
      icon.textContent = CONFIG.menuIcon;
      const text = document.createElement("span");
      text.className = "btn-menu-item-text";
      text.textContent = label;
      item.append(icon, text);
      item.addEventListener("click", onClick);
      return item;
    };

    const addContextMenuItem = (menu) => {
      const pending = contextMenuState.pending;
      contextMenuState.pending = null;
      if (!pending || !menu) return;
      if (
        menu.querySelector(
          `#${CONFIG.menuItemId}, #${CONFIG.menuGroupItemId}, #${CONFIG.menuCopyLinkItemId}, #${CONFIG.menuAria2ItemId}`
        )
      )
        return;
      const first = menu.querySelector(".btn-menu-item");
      if (!first) return;
      const message = mediaOf(lookupMessage(pending.peerId, pending.mid));
      if (!message) return;

      const mediaElement = findMediaElement(pending.bubble);
      const mediaUrl = getElementMediaSource(mediaElement);
      const mediaKind = mediaElement?.tagName === "VIDEO" ? "video" : "image";

      const menuItems = [];
      // 原生菜单已带单个下载项时不重复添加，但整组入口仍然有用。
      if (!menu.querySelector(".tgico-download")) {
        menuItems.push(
          makeMenuItem(CONFIG.menuItemId, t("menuDownload"), () => {
            if (directoryHandle && mediaUrl) {
              downloadMedia(mediaUrl, mediaKind, {
                directory: directoryHandle,
                fileName: getMessageFileName(message, mediaUrl, mediaKind),
              });
            } else {
              nativeDownload(message).catch((error) =>
                logger.error("Menu download failed:", error)
              );
            }
          })
        );
      }
      menuItems.push(
        makeMenuItem(CONFIG.menuGroupItemId, t("menuGroupDownload"), () => {
          const group = collectGroupEntries(message, pending.peerId, pending.bubble);
          let rootPromise = null;
          let permission = null;
          // 只有用户在面板里主动指定过目录才归档；否则交给 Telegram 原生通道，
          // 直接落系统下载目录，不再弹出目录选择框。
          if (directoryHandle) {
            // 授权必须在点击手势内同步发起。
            permission = requestDirectoryPermission(directoryHandle);
            rootPromise = Promise.resolve(directoryHandle);
          }
          downloadGroup(group, {
            message,
            peerId: pending.peerId,
            root: rootPromise,
            permission,
          }).catch((error) => logger.error("Group download failed:", error));
        })
      );
      // 外部下载器入口：仅对真实 HTTP(S) 地址有效，blob 直接提示。
      menuItems.push(
        makeMenuItem(CONFIG.menuCopyLinkItemId, t("menuCopyLink"), () => {
          const urls = collectGroupEntries(message, pending.peerId, pending.bubble)
            .map((entry) => entry.url)
            .filter(isDirectUrl);
          if (!urls.length) {
            logger.warn(t("externalBlocked"));
            return;
          }
          copyText(urls.join("\n"));
        })
      );
      menuItems.push(
        makeMenuItem(CONFIG.menuAria2ItemId, t("menuAria2"), () => {
          const lines = collectGroupEntries(message, pending.peerId, pending.bubble)
            .filter((entry) => isDirectUrl(entry.url))
            .map(
              (entry) =>
                `${entry.url}\n  out=${entry.name.replace(/[\r\n]+/g, " ")}\n  continue=true`
            );
          if (!lines.length) {
            logger.warn(t("externalBlocked"));
            return;
          }
          saveTextFile(`tel-aria2-${Date.now()}.txt`, `${lines.join("\n")}\n`);
        })
      );
      // 倒序插入到首项之前，保证显示顺序为「下载 → 整组 → 复制链接 → aria2」。
      for (let i = menuItems.length - 1; i >= 0; i -= 1) {
        first.insertAdjacentElement("beforebegin", menuItems[i]);
      }
    };

    /* 8.8 选择栏批量下载 ------------------------------------------------ */
    let batchBusy = false;

    const localizeBatchButton = () => {
      const button = document.getElementById(CONFIG.batchButtonId);
      if (!button) return;
      const text = button.querySelector(".tel-batch-text");
      const label = t("batchShort");
      if (text && text.textContent !== label) text.textContent = label;
      button.title = t("batchTitle");
      button.setAttribute("aria-label", t("batchTitle"));
    };

    const downloadSelected = async (button) => {
      if (batchBusy) return;
      const selection = page.appImManager?.chat?.selection;
      const label = button.querySelector(".tel-batch-text");
      // 目录授权必须在这个点击手势内发起，否则后续 await 之后弹不出授权框。
      const permission = requestDirectoryPermission(directoryHandle);
      let task = null;
      const restore = () => {
        batchBusy = false;
        button.disabled = false;
        button.style.opacity = "";
        if (label) label.textContent = t("batchShort");
        localizeBatchButton();
      };
      const closeSelection = () => {
        try {
          selection?.cancelSelection?.();
        } catch {
          /* 部分分支无此方法 */
        }
      };
      let messages;
      try {
        messages = (await selection?.getSelectedMessages?.()) || [];
      } catch (error) {
        logger.error("Reading selection failed:", error);
        return;
      }
      const media = messages.map(mediaOf).filter(Boolean);
      if (!media.length) {
        if (label) label.textContent = t("batchEmpty");
        setTimeout(restore, 1500);
        return;
      }
      batchBusy = true;
      button.disabled = true;
      button.style.opacity = "0.6";
      // 一开跑就在面板里建聚合卡：按钮上的 n/N 太小容易看不见，面板才是主进度来源。
      task = createBatchTask(media.length, t("batchTitle"));

      // 未指定目录时不再弹选择框：直接走原生通道，落系统下载目录。
      const batchRoot = directoryHandle;
      if (batchRoot) {
        // 目录模式：按「会话/日期」归档，相册项各自进 album-<id> 子目录。
        // 单条仍走分片通道（有字节进度、各自建卡），聚合卡只负责 n/N 与取消。
        if (permission) await permission.catch(() => "denied");
        let dateFolder = batchRoot;
        try {
          dateFolder = await ensureChatDateFolder(batchRoot, "");
        } catch (error) {
          logger.error("Archive folder unavailable, using root folder:", error);
        }
        const folderCache = new Map();
        const folderFor = (groupedId) => {
          if (groupedId == null) return Promise.resolve(dateFolder);
          const key = String(groupedId);
          if (!folderCache.has(key)) {
            folderCache.set(
              key,
              dateFolder
                .getDirectoryHandle(sanitizeSegment(`album-${key}`), { create: true })
                .catch(() => dateFolder)
            );
          }
          return folderCache.get(key);
        };
        for (let i = 0; i < media.length; i += 1) {
          if (task.controller.signal.aborted) break;
          if (label) label.textContent = `${i + 1}/${media.length}`;
          const entry = findRenderedMediaEntry(media[i]);
          beginBatchItem(task, i, entry?.name || batchItemName(media[i]));
          const folder = await folderFor(media[i]?.groupedId);
          try {
            if (entry) {
              await downloadMedia(entry.url, entry.kind, {
                directory: folder,
                fileName: entry.name,
              });
            } else {
              await nativeDownload(media[i]);
            }
          } catch (error) {
            logger.error("Batch item failed:", error);
          }
          settleBatchItem(task, i);
        }
        restore();
        closeBatchTask(task, "completed");
        closeSelection();
        return;
      }

      // 串行且带间隔：多个并发 Service Worker 交付会退化为内存 Blob，大文件必失败。
      for (let i = 0; i < media.length; i += 1) {
        if (task.controller.signal.aborted) break;
        if (label) label.textContent = `${i + 1}/${media.length}`;
        beginBatchItem(task, i, batchItemName(media[i]));
        try {
          await nativeDownload(media[i]);
        } catch (error) {
          logger.error("Batch item failed:", error);
        }
        settleBatchItem(task, i);
        if (i < media.length - 1 && !task.controller.signal.aborted) {
          await new Promise((resolve) => setTimeout(resolve, CONFIG.batchGap));
        }
      }
      restore();
      closeBatchTask(task, "handedOff");
      closeSelection();
    };

    const addBatchButton = (wrapper) => {
      const bar = wrapper.querySelector(".selection-container");
      if (!bar || bar.querySelector(`#${CONFIG.batchButtonId}`)) return;
      const button = document.createElement("button");
      button.id = CONFIG.batchButtonId;
      button.className = "btn-primary btn-transparent text-bold";
      button.type = "button";
      button.style.cursor = "pointer";
      button.style.width = "auto";
      const icon = document.createElement("span");
      icon.style.fontSize = "1rem";
      icon.style.margin = "0 .4rem .2rem 0";
      icon.textContent = CONFIG.menuIcon;
      const text = document.createElement("span");
      text.className = "tel-batch-text";
      button.append(icon, text);
      localizeBatchButton();
      button.addEventListener("click", () => downloadSelected(button));
      bar.appendChild(button);
    };

    /* 8.9 进度面板 + 设置 ---------------------------------------------- */
    const panel = document.createElement("section");
    panel.id = CONFIG.panelId;
    panel.dir = "ltr";
    panel.hidden = true;
    panel.innerHTML = `
      <div class="tel-panel-heading">
        <strong class="tel-panel-title"></strong>
        <div class="tel-panel-tools">
          <button class="tel-settings-toggle" type="button" aria-expanded="false" aria-controls="tel-download-settings">
            <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M10 2h4l.6 3 2 1.2 2.9-.9 2 3.4-2.3 2v2.6l2.3 2-2 3.4-2.9-.9-2 1.2L14 22h-4l-.6-3-2-1.2-2.9.9-2-3.4 2.3-2v-2.6l-2.3-2 2-3.4 2.9.9 2-1.2Z"/><circle cx="12" cy="12" r="3"/></svg>
          </button>
          <button class="tel-panel-toggle" type="button" aria-expanded="true" aria-controls="tel-task-list"></button>
        </div>
      </div>
      <div id="tel-download-settings" class="tel-settings" hidden>
        <div class="tel-language-setting">
          <label for="tel-download-folder-button"></label>
          <button id="tel-download-folder-button" type="button"></button>
          <button class="tel-folder-clear" type="button" hidden>&times;</button>
        </div>
        <div class="tel-language-setting"><label for="tel-download-language"></label><select id="tel-download-language"></select></div>
      </div>
      <div id="tel-task-list" class="tel-task-list"></div>`;
    const languageSelect = panel.querySelector("select");
    const settings = panel.querySelector("#tel-download-settings");
    const settingsToggle = panel.querySelector(".tel-settings-toggle");
    const setSettingsOpen = (open) => {
      const returnFocus = !open && settings.contains(document.activeElement);
      settings.hidden = !open;
      settingsToggle.setAttribute("aria-expanded", String(open));
      if (open) languageSelect.focus();
      else if (returnFocus && !panel.hidden) settingsToggle.focus();
    };
    settingsToggle.onclick = () => setSettingsOpen(settings.hidden);
    panel.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && !settings.hidden) {
        event.preventDefault();
        event.stopPropagation();
        setSettingsOpen(false);
      }
    });
    // 等 click 再收起：面板贴底，设置一关就会移动，不能在 pointerdown 与 up 之间挪位。
    document.addEventListener("click", (event) => {
      if (
        !settings.hidden &&
        !settings.contains(event.target) &&
        !settingsToggle.contains(event.target)
      ) {
        setSettingsOpen(false);
      }
    });
    for (const [value, label] of [["auto", ""], ...Object.entries(UI_LANGUAGES)]) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      if (value !== "auto") option.lang = value;
      languageSelect.appendChild(option);
    }

    const folderButton = panel.querySelector("#tel-download-folder-button");
    const folderClear = panel.querySelector(".tel-folder-clear");

    const refreshFolderUi = () => {
      const folderLabel = panel.querySelector('label[for="tel-download-folder-button"]');
      if (folderLabel) folderLabel.textContent = t("folder");
      folderButton.disabled = typeof page.showDirectoryPicker !== "function";
      // 未指定目录时显示「系统默认下载目录」：不选也能下，点击才是指定归档目录。
      folderButton.textContent = directoryHandle
        ? t("folderChosen", { name: directoryHandle.name })
        : t("folderDefault");
      folderButton.title = directoryHandle ? directoryHandle.name : t("chooseFolder");
      folderClear.hidden = !directoryHandle;
      folderClear.title = t("clearFolder");
      folderClear.setAttribute("aria-label", t("clearFolder"));
    };

    folderButton.onclick = async () => {
      try {
        const handle = await page.showDirectoryPicker({
          id: "tel-downloads",
          mode: "readwrite",
          startIn: "downloads",
        });
        directoryHandle = handle;
        await writeDirectoryHandle(handle);
        refreshFolderUi();
      } catch (error) {
        if (error?.name !== "AbortError") logger.error("Folder selection failed:", error);
      }
    };

    folderClear.onclick = async () => {
      directoryHandle = null;
      await deleteDirectoryHandle();
      refreshFolderUi();
    };

    const refreshLanguage = () => {
      uiLanguage = resolveLanguage();
      integerFormat = new Intl.NumberFormat(uiLanguage, { maximumFractionDigits: 0 });
      decimalFormat = new Intl.NumberFormat(uiLanguage, {
        minimumFractionDigits: 1,
        maximumFractionDigits: 1,
      });
      panel.lang = uiLanguage;
      panel.setAttribute("aria-label", t("mediaDownloads"));
      settingsToggle.title = t("settings");
      settingsToggle.setAttribute("aria-label", t("settings"));
      panel.querySelector('label[for="tel-download-language"]').textContent = t("language");
      refreshFolderUi();
      languageSelect.querySelector('[value="auto"]').textContent = t("automatic");
      languageSelect.value = languagePreference;
      panel.querySelector(".tel-panel-toggle").textContent = t(
        panel.querySelector(".tel-task-list").hidden ? "show" : "hide"
      );
      localizeBatchButton();
      const menuItem = document.getElementById(CONFIG.menuItemId);
      if (menuItem) menuItem.querySelector(".btn-menu-item-text").textContent = t("menuDownload");
      const menuGroupItem = document.getElementById(CONFIG.menuGroupItemId);
      if (menuGroupItem) {
        menuGroupItem.querySelector(".btn-menu-item-text").textContent = t("menuGroupDownload");
      }
      for (const task of downloadTasks.values()) renderTask(task);
      updateTaskPanel();
      document
        .querySelectorAll(`button.tel-download, button.${CONFIG.pinnedButtonClass}`)
        .forEach((button) => {
          localizeDownloadButton(
            button,
            button.matches(`.${CONFIG.pinnedButtonClass}:disabled`)
          );
        });
    };

    languageSelect.onchange = () => {
      languagePreference = Object.hasOwn(UI_LANGUAGES, languageSelect.value)
        ? languageSelect.value
        : "auto";
      try {
        if (languagePreference === "auto") localStorage.removeItem(CONFIG.languageKey);
        else localStorage.setItem(CONFIG.languageKey, languagePreference);
      } catch {
        /* 存储被禁用时，当前标签页仍然生效 */
      }
      refreshLanguage();
    };
    window.addEventListener("languagechange", () => {
      if (languagePreference === "auto") refreshLanguage();
    });
    window.addEventListener("storage", (event) => {
      if (event.key === CONFIG.languageKey || event.key === null) {
        languagePreference = readLanguagePreference();
        refreshLanguage();
      }
    });
    panel.querySelector(".tel-panel-toggle").onclick = () => {
      const list = panel.querySelector(".tel-task-list");
      list.hidden = !list.hidden;
      if (list.hidden) setSettingsOpen(false);
      const toggle = panel.querySelector(".tel-panel-toggle");
      toggle.textContent = t(list.hidden ? "show" : "hide");
      toggle.setAttribute("aria-expanded", String(!list.hidden));
    };

    const style = document.createElement("style");
    style.textContent = `
      #${CONFIG.panelId} {
        --tel-bg: #fff; --tel-fg: #202b36; --tel-muted: #556472; --tel-line: #d9e1e8;
        --tel-track: #e9eef3; --tel-accent: #1967b3; --tel-error: #b42318;
        box-sizing: border-box; position: fixed; right: max(12px, env(safe-area-inset-right)); bottom: max(12px, env(safe-area-inset-bottom));
        width: min(340px, calc(100vw - 24px)); max-height: calc(100vh - 24px); z-index: 2147483646;
        background: var(--tel-bg); color: var(--tel-fg); border: 1px solid var(--tel-line); border-radius: 12px;
        box-shadow: 0 6px 28px #0003; font: 13px/1.4 system-ui, sans-serif; overflow: hidden;
        display: flex; flex-direction: column; text-align: start;
      }
      html.night #${CONFIG.panelId}, html.theme-dark #${CONFIG.panelId} {
        --tel-bg: #202a35; --tel-fg: #f2f5f8; --tel-muted: #b0bdca; --tel-line: #455361;
        --tel-track: #394754; --tel-accent: #73b7ff; --tel-error: #ff9f97;
      }
      #${CONFIG.panelId} [hidden], #${CONFIG.panelId}[hidden] { display: none !important; }
      #${CONFIG.panelId} * { box-sizing: border-box; }
      #${CONFIG.panelId} p { margin: 0; }
      #${CONFIG.panelId} .tel-panel-heading, #${CONFIG.panelId} .tel-task-heading {
        display: flex; align-items: center; justify-content: space-between; gap: 8px;
      }
      #${CONFIG.panelId} .tel-panel-heading {
        padding: 10px 12px; flex-shrink: 0;
        cursor: grab; touch-action: none; user-select: none; -webkit-user-select: none;
      }
      #${CONFIG.panelId}.tel-panel-dragging .tel-panel-heading { cursor: grabbing; }
      #${CONFIG.panelId}.tel-panel-dragging { box-shadow: 0 12px 40px #0006; }
      #${CONFIG.panelId} .tel-panel-title { min-width: 0; overflow-wrap: anywhere; }
      #${CONFIG.panelId} .tel-panel-tools { display: flex; align-items: center; gap: 2px; flex-shrink: 0; }
      #${CONFIG.panelId} .tel-language-setting { display: flex; align-items: center; gap: 8px; padding: 0 12px 10px; flex-shrink: 0; }
      #${CONFIG.panelId} .tel-settings { padding-top: 2px; }
      #${CONFIG.panelId} .tel-language-setting button { max-width: 55%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      #${CONFIG.panelId} .tel-folder-clear { max-width: none; font-size: 18px; color: var(--tel-muted); padding: 2px 8px; }
      #${CONFIG.panelId} .tel-language-setting label { flex: 1; min-width: 0; color: var(--tel-muted); overflow-wrap: anywhere; }
      #${CONFIG.panelId} select { width: 150px; max-width: 55%; min-width: 0; min-height: 32px; font: inherit; color: var(--tel-fg); background: var(--tel-bg); border: 1px solid var(--tel-line); border-radius: 6px; padding: 4px; }
      #${CONFIG.panelId} .tel-task-list { min-height: 0; overflow-y: auto; max-height: 440px; overscroll-behavior: contain; }
      #${CONFIG.panelId} .tel-task { padding: 12px; border-top: 1px solid var(--tel-line); }
      #${CONFIG.panelId} .filename { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
      #${CONFIG.panelId} button {
        appearance: none; background: transparent; color: var(--tel-accent); border: 1px solid transparent;
        font: inherit; border-radius: 6px; padding: 5px 8px; min-height: 32px; cursor: pointer; flex-shrink: 0;
      }
      #${CONFIG.panelId} button:hover { background: var(--tel-track); }
      #${CONFIG.panelId} .tel-settings-toggle { display: inline-flex; align-items: center; justify-content: center; width: 32px; padding: 5px; color: var(--tel-muted); }
      #${CONFIG.panelId} .tel-settings-toggle:hover, #${CONFIG.panelId} .tel-settings-toggle[aria-expanded="true"] { color: var(--tel-fg); background: var(--tel-track); }
      #${CONFIG.panelId} button:focus-visible, #${CONFIG.panelId} select:focus-visible, #${CONFIG.panelId} .tel-task:focus-visible { outline: 2px solid var(--tel-accent); outline-offset: -2px; }
      #${CONFIG.panelId} button:disabled { opacity: .5; cursor: default; }
      #${CONFIG.panelId} [data-action="dismiss"] { color: var(--tel-muted); font-size: 20px; }
      #${CONFIG.panelId} .progress { position: relative; min-height: 24px; margin: 8px 0; background: var(--tel-track); border-radius: 6px; overflow: hidden; }
      #${CONFIG.panelId} .progress > div { position: absolute; height: 100%; background: var(--tel-accent); opacity: .35; }
      #${CONFIG.panelId} .progress > p { position: relative; text-align: center; line-height: 1.5; padding: 3px 8px; overflow-wrap: anywhere; color: var(--tel-fg); }
      #${CONFIG.panelId} .tel-task-details { color: var(--tel-muted); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
      #${CONFIG.panelId} .tel-task-error { color: var(--tel-error); margin-top: 8px; overflow-wrap: anywhere; }
      #${CONFIG.panelId} .tel-task-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 4px; margin-top: 4px; }
      #${CONFIG.panelId} .tel-task-state { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
      #${CONFIG.panelId} [data-state="failed"] .progress > div { background: var(--tel-error); }
      #${CONFIG.panelId} .tel-indeterminate > div { width: 35% !important; animation: tel-download-progress 1.4s infinite ease-in-out; }
      @keyframes tel-download-progress { from { left: -35%; } to { left: 100%; } }
      @media (prefers-reduced-motion: reduce) { #${CONFIG.panelId} .tel-indeterminate > div { animation: none; left: 32%; } }
    `;
    (document.head || document.documentElement).appendChild(style);
    document.body.appendChild(panel);

    /* 8.9.1 面板拖拽：抓标题栏可移动，位置写 localStorage ------------- */
    const PANEL_POS_KEY = "tel-media-downloader-panel-position";
    let panelDrag = null;

    const writePanelPosition = (position) => {
      try {
        localStorage.setItem(PANEL_POS_KEY, JSON.stringify(position));
      } catch {
        /* 隐私模式 / 存储被禁用时，本次会话内仍然生效 */
      }
    };

    // 面板尺寸随任务数变化，每次定位都按当前 rect 夹回视口内。
    const clampPanelPosition = (left, top) => {
      const rect = panel.getBoundingClientRect();
      const width = rect.width || 340;
      const height = Math.min(rect.height || 120, window.innerHeight);
      return {
        left: Math.min(Math.max(0, left), Math.max(0, window.innerWidth - width)),
        top: Math.min(Math.max(0, top), Math.max(0, window.innerHeight - height)),
      };
    };

    const applyPanelPosition = (left, top, persist) => {
      const next = clampPanelPosition(left, top);
      panel.style.left = `${Math.round(next.left)}px`;
      panel.style.top = `${Math.round(next.top)}px`;
      panel.style.right = "auto";
      panel.style.bottom = "auto";
      if (persist) writePanelPosition(next);
      return next;
    };

    const resetPanelPosition = () => {
      panel.style.left = "";
      panel.style.top = "";
      panel.style.right = "";
      panel.style.bottom = "";
      panelDrag = null;
      panel.classList.remove("tel-panel-dragging");
      try {
        localStorage.removeItem(PANEL_POS_KEY);
      } catch {
        /* 同上 */
      }
    };

    const restorePanelPosition = () => {
      let saved = null;
      try {
        saved = JSON.parse(localStorage.getItem(PANEL_POS_KEY) || "null");
      } catch {
        saved = null;
      }
      if (Number.isFinite(saved?.left) && Number.isFinite(saved?.top)) {
        applyPanelPosition(saved.left, saved.top, false);
      }
    };

    const panelHeading = panel.querySelector(".tel-panel-heading");
    panelHeading?.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) return;
      // 标题栏右侧是设置/折叠按钮，拖动交给它们自己。
      if (event.target instanceof Element && event.target.closest("button")) return;
      const rect = panel.getBoundingClientRect();
      if (!rect.width) return;
      panelDrag = {
        id: event.pointerId,
        offsetX: event.clientX - rect.left,
        offsetY: event.clientY - rect.top,
      };
      panel.classList.add("tel-panel-dragging");
      panelHeading.setPointerCapture?.(event.pointerId);
      event.preventDefault();
    });
    panelHeading?.addEventListener("pointermove", (event) => {
      if (!panelDrag || event.pointerId !== panelDrag.id) return;
      applyPanelPosition(
        event.clientX - panelDrag.offsetX,
        event.clientY - panelDrag.offsetY,
        false
      );
    });
    const endPanelDrag = (event) => {
      if (!panelDrag || event.pointerId !== panelDrag.id) return;
      panelDrag = null;
      panel.classList.remove("tel-panel-dragging");
      panelHeading.releasePointerCapture?.(event.pointerId);
      const rect = panel.getBoundingClientRect();
      if (rect.width) {
        writePanelPosition({ left: Math.round(rect.left), top: Math.round(rect.top) });
      }
    };
    panelHeading?.addEventListener("pointerup", endPanelDrag);
    panelHeading?.addEventListener("pointercancel", endPanelDrag);
    // 双击标题栏复位到右下角：面板拖出视野后有个找回来的办法。
    panelHeading?.addEventListener("dblclick", (event) => {
      if (event.target instanceof Element && event.target.closest("button")) return;
      resetPanelPosition();
    });
    window.addEventListener("resize", () => {
      if (!panel.style.left) return;
      const rect = panel.getBoundingClientRect();
      if (rect.width) applyPanelPosition(rect.left, rect.top, true);
    });
    restorePanelPosition();

    refreshLanguage();
    readDirectoryHandle()
      .then((handle) => {
        if (!handle) return;
        directoryHandle = handle;
        refreshFolderUi();
      })
      .catch(() => {});

    /* 8.10 观察器与调度 ------------------------------------------------ */
    const refreshApp = isWebK ? refreshWebK : isWebA ? refreshWebA : null;
    const roots = isWebK
      ? ".media-viewer-whole, #stories-viewer, .pinned-audio, audio-element"
      : "#MediaViewer, #StoryViewer";
    let pendingRefresh;
    const scheduleRefresh = () => {
      if (!refreshApp || pendingRefresh) return;
      pendingRefresh = setTimeout(() => {
        pendingRefresh = null;
        refreshApp();
      }, CONFIG.observerDebounce);
    };

    const isOurs = (node) =>
      node instanceof Element &&
      Boolean(
        node.closest(
          `#${CONFIG.panelId}, .tel-download, .${CONFIG.pinnedButtonClass}, #${CONFIG.menuItemId}, #${CONFIG.menuGroupItemId}, #${CONFIG.menuCopyLinkItemId}, #${CONFIG.menuAria2ItemId}, #${CONFIG.batchButtonId}, #tel-copy-unlock-style`
        )
      );

    const handleAddedNode = (node) => {
      if (!isWebK) return;
      if (contextMenuState.pending) {
        const menu = node.matches?.("#bubble-contextmenu")
          ? node
          : node.querySelector?.("#bubble-contextmenu");
        if (menu) addContextMenuItem(menu);
      }
      const wrapper = node.classList?.contains("selection-wrapper")
        ? node
        : node.querySelector?.(".selection-wrapper");
      if (wrapper) addBatchButton(wrapper);
    };

    let pendingMenu;
    const scheduleMenuScan = () => {
      if (pendingMenu) return;
      pendingMenu = setTimeout(() => {
        pendingMenu = null;
        if (!contextMenuState.pending) return;
        const menu = document.getElementById("bubble-contextmenu");
        if (!menu) {
          // 菜单没出现（例如非右键路径），丢弃候选，避免污染下一次。
          contextMenuState.pending = null;
          return;
        }
        addContextMenuItem(menu);
      }, 50);
    };

    const observer = new MutationObserver((records) => {
      for (const record of records) {
        if (isOurs(record.target instanceof Element ? record.target : record.target.parentElement)) {
          continue;
        }
        for (const node of record.addedNodes) {
          if (node.nodeType !== 1) continue;
          handleAddedNode(node);
        }
      }
      if (contextMenuState.pending) scheduleMenuScan();
      if (!refreshApp) return;
      const relevant = records.some((record) => {
        const target =
          record.target instanceof Element ? record.target : record.target.parentElement;
        if (isOurs(target)) return false;
        return (
          target?.closest(roots) ||
          Array.from(record.addedNodes).some(
            (node) =>
              node instanceof Element && (node.matches(roots) || node.querySelector(roots))
          )
        );
      });
      if (relevant) scheduleRefresh();
    });
    observer.observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["class", "src", "data-mid", "data-peer-id", "disabled", "aria-disabled", "hidden"],
    });

    // 兜底：媒体源属性可能在 DOM 之外被改写。
    setInterval(() => {
      if (!document.hidden) refreshApp?.();
    }, CONFIG.refreshDelay);
    document.addEventListener("visibilitychange", scheduleRefresh);
    refreshApp?.();
  };

  if (document.body) main();
  else document.addEventListener("DOMContentLoaded", main, { once: true });
})();
