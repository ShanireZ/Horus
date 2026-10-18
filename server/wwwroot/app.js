/* ============================================================
   Horus 監考臺 · 前端邏輯（vanilla JS，無框架/無建置/無外部相依）
   消費已凍結的後端 API；?mock=1 或 window.USE_MOCK=true 走內建 mock。
   ============================================================ */
(function () {
  "use strict";

  /* ---------- 執行模式：真實 fetch vs 內建 mock ---------- */
  var USE_MOCK =
    window.USE_MOCK === true ||
    /[?&]mock=1\b/.test(window.location.search);

  /* ---------- 常數：kind 中文標籤 + 顏色 ---------- */
  // 每種可疑類型給一箇中文標籤與配色（前景/背景/邊框）。
  var KIND_META = {
    web_ai:             { label: "AI 网站",     fg: "#c9a2ff", bg: "#2b1f42", bd: "#6b4bb0" },
    search:             { label: "搜题",         fg: "#ffbe6b", bg: "#3a2a0e", bd: "#a9741f" },
    non_whitelist_proc: { label: "非白名单进程", fg: "#7fd3ff", bg: "#12303f", bd: "#2f6f92" },
    large_paste:        { label: "大段粘贴",     fg: "#ffd27f", bg: "#3a2c07", bd: "#a9841f" },
    usb:                { label: "USB 设备",     fg: "#ff9a9d", bg: "#3a1315", bd: "#c14045" },
    ide_plugin_suspect: { label: "IDE 插件",     fg: "#a0f0c0", bg: "#123324", bd: "#2f8a5c" },
    browser_unreadable: { label: "浏览器不可读", fg: "#c2c9d8", bg: "#242a37", bd: "#4a5163" },
    non_whitelist_web:  { label: "非白名单网站", fg: "#ffb3c1", bg: "#3a1520", bd: "#b0455f" },
    remote_tool:        { label: "远控工具",     fg: "#ff9a9d", bg: "#3a1315", bd: "#c14045" },
    suspect:            { label: "可疑",         fg: "#c2c9d8", bg: "#242a37", bd: "#4a5163" },
    // M5 採集健康告警(唯讀,不計入作弊裁決):與 Suspicion.KindFor 三分支對應
    screen_obscured:    { label: "屏幕遮挡",     fg: "#f9e2af", bg: "#3a2f0a", bd: "#a9741f" },
    capability_degraded:{ label: "能力降级",     fg: "#f9e2af", bg: "#3a2f0a", bd: "#a9741f" },
    watchdog_restart:   { label: "看门狗重启",   fg: "#f9e2af", bg: "#3a2f0a", bd: "#a9741f" },
    suspected_suspend:  { label: "疑似挂起",     fg: "#c9b6ff", bg: "#241a3a", bd: "#7a5ccc" }
  };
  function kindMeta(kind) {
    return KIND_META[kind] || { label: kind || "未知", fg: "#c2c9d8", bg: "#242a37", bd: "#4a5163" };
  }

  /* ---------- 常數：事件 type 中文標籤 ---------- */
  var TYPE_LABEL = {
    window_focus:  "窗口焦点",
    browser_url:   "浏览器地址",
    process_start: "进程启动",
    process_exit:  "进程退出",
    clipboard:     "剪贴板",
    alt_tab_burst: "快速切窗",
    usb:           "USB",
    screenshot:    "截屏",
    heartbeat:     "心跳"
  };
  function typeLabel(t) { return TYPE_LABEL[t] || t || "事件"; }

  /* ---------- 常數：可疑狀態中文標籤 ---------- */
  var STATUS_LABEL = {
    pending:   "待复核",
    reviewing: "复核中",
    confirmed: "已确认",
    dismissed: "已驳回"
  };

  /* ============================================================
     工具函式
     ============================================================ */

  // Unix 秒（含毫秒的浮點）→ 本機 HH:MM:SS
  function fmtTime(ts) {
    if (ts === null || ts === undefined || isNaN(ts)) return "--:--:--";
    var d = new Date(ts * 1000);
    var p = function (n) { return String(n).padStart(2, "0"); };
    return p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
  }

  // 轉義 HTML，防止 payload 裡的字串破壞 DOM
  function esc(s) {
    if (s === null || s === undefined) return "";
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");   // 單引號一併轉義,令 esc 對屬性定界方式免疫(防禦縱深)
  }

  // 短橫線容錯取值
  function dash(v) { return (v === null || v === undefined || v === "") ? "—" : v; }

  var $ = function (sel) { return document.querySelector(sel); };

  /* ---------- 頂部錯誤橫幅（toast） ---------- */
  var toastTimer = null;
  function showToast(msg) {
    var el = $("#toast");
    el.textContent = msg;
    el.hidden = false;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.hidden = true; }, 6000);
  }

  /* ============================================================
     管理鑑權（M2：HttpOnly cookie）
     ------------------------------------------------------------
     凍結契約：
       · 登入：POST /api/login {token} → 檢核通過則種 HttpOnly cookie `horus_admin`。
         cookie 由瀏覽器自動附帶（fetch credentials:same-origin + <img> 同源），
         **JS 讀不到**（防 XSS 竊取），**不進 URL**（防 ?t= 權杖經 Referer/紀錄外洩）。
       · 其餘 /api/* 無需再手動帶權杖頭；cookie 自動攜帶。
       · 權杖錯誤/缺失 → /api/* 回傳 401 → 彈登入門。
       · 伺服器未設定權杖（本機整合測試）時 /api/* 正常放行、/api/login 回傳 authRequired:false。
       · 登出：POST /api/logout → 清 cookie。
     前端不再持權杖明文，localStorage 不再用於權杖。
     ============================================================ */

  // 登入：檢核權杖並種 cookie。回傳 { ok, body }。
  function login(tok) {
    return fetch("/api/login", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "Accept": "application/json" },
      body: JSON.stringify({ token: tok })
    }).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (j) { return { ok: r.ok, body: j }; });
    });
  }
  // 登出：清 cookie（失敗靜默——UI 層照常彈門即可）。
  function logout() {
    return fetch("/api/logout", { method: "POST", credentials: "same-origin" }).catch(function () {});
  }

  // 圖片位元組 URL：cookie 同源自動攜帶，無需再把權杖拼進 ?t=（避免權杖進 URL / 紀錄 / Referer）。
  // 供 /api/images/{id} 縮略圖/燈箱大圖使用；/meta 走 api()。
  function imageUrl(imageId) {
    return "/api/images/" + encodeURIComponent(imageId);
  }

  /* ---------- 統一 fetch 封裝：自動加權杖頭 + 401 → 登入門 ---------- */
  // 401 處理：清空資料、停止輪詢、彈登入門、給中文提示，絕不靜默失敗或白屏。
  // 拋出的錯誤帶 .isAuth 標記，呼叫方的 .catch 只需照常 showToast（已彈門）。
  // msg：伺服器給的真實原因（撤權留痕）。★ 取不到時才退回那句籠統的。
  //   ★★ 「你的監考臺權限已關閉」這一種尤其要說清楚：取消 SSO 後（貝塔通 P84/P98）
  //   每點一次登入都要**完整輸一遍密碼**才被拒，不告訴他真實原因，他會一遍遍白輸。
  function handleUnauthorized(msg) {
    stopPolling();
    stopHeartbeat();   // 工作階段已死：再發心跳既救不活它，401 還會再彈一次門
    clearCurrentData();
    showLoginGate(msg || "登录已失效或过期，请重新登录");
  }

  function api(path, options) {
    if (USE_MOCK) {
      // mock 分流：GET 無 body → mockGet；POST → mockPost
      if (options && options.method === "POST") {
        return mockPost(path, options.body ? JSON.parse(options.body) : {});
      }
      return mockGet(path);
    }
    var opts = options || {};
    var headers = { "Accept": "application/json" };
    if (opts.headers) {
      Object.keys(opts.headers).forEach(function (k) { headers[k] = opts.headers[k]; });
    }
    // 管理鑑權走 HttpOnly cookie：credentials:same-origin 讓瀏覽器自動攜帶，無需手動帶權杖頭。
    // 整合測試模式（伺服器未配權杖）下 /api/* 照常放行；配了權杖但未登入/cookie 失效則 401 → 彈門。
    return fetch(path, {
      method: opts.method || "GET",
      credentials: "same-origin",
      headers: headers,
      body: opts.body
    }).then(function (r) {
      if (r.status === 401) {
        // 401 的回應體帶著「為什麼」（撤權留痕）；讀不出就退回籠統提示。
        return r.json().catch(function () { return {}; }).then(function (j) {
          handleUnauthorized(j && j.message);
          var authErr = new Error("未授权（401）");
          authErr.isAuth = true;
          throw authErr;
        });
      }
      if (!r.ok) throw new Error("HTTP " + r.status + " · " + path);
      return r.json();
    });
  }

  // 相容舊呼叫點：apiGet / apiPost 現在只是 api() 的薄封裝。
  function apiGet(path) { return api(path); }
  function apiPost(path, body) {
    return api(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  }

  /* ============================================================
     應用狀態
     ============================================================ */
  var state = {
    exams: [],
    currentExamId: null,
    seats: [],
    suspicious: [],
    queueStatus: "pending",   // 佇列過濾
    queueMode: "suspicion",   // 佇列視圖:suspicion(可疑複核)|health(採集健康)
    health: [],               // 採集健康列表(source='health')
    autoRefresh: true,
    pollTimer: null,
    inflight: false,          // 防止輪詢疊加
    drawerMode: null,         // "seat" | "suspicious"
    collectAuthMode: null,    // 採集面模式(psk/oidc/both):both 灰度期高亮 PSK 座位
    adminAuthMode: null,      // 管理端模式(token/oidc):退出按鈕據此決定走不走貝塔通登出
    imageSearchEnabled: false,// 按圖搜圖是否可用(嵌入器已配)
    lightboxImageId: null     // 當前燈箱圖 id(按圖搜圖用)
  };

  var POLL_MS = 5000;

  /* ============================================================
     初始化
     ============================================================ */
  function init() {
    bindTopbar();
    bindQueueFilter();
    bindQueueTabs();
    bindDrawer();
    bindLightbox();
    bindLoginGate();

    // ── 啟動引導 ──────────────────────────────────────────────
    // mock 模式：直接進（不經真實 /api，不受鑑權影響，用於目視佈局）。
    // 真實模式：首次載入先「不彈門」直接試拉資料——
    //   · 200（有權杖或整合測試放行）→ 直接用，登入門保持隱藏；
    //   · 401 → api() 裡的 handleUnauthorized() 自動彈門要權杖。
    // 即使本機已存權杖也照此流程：讓伺服器裁決，而不是前端預判。
    hideLoginGate();
    // 取一次採集面模式(both 灰度期看板要高亮 PSK 座位);失敗靜默(預設不高亮)。
    fetch("/api/authmode", { credentials: "same-origin" }).then(function (r) { return r.json(); })
      .then(function (j) {
        if (!j) return;
        if (j.collectAuthMode) state.collectAuthMode = j.collectAuthMode;
        state.imageSearchEnabled = !!j.imageSearchEnabled;
        state.adminAuthMode = j.mode || "token";   // 退出按鈕據此決定純本機登出還是走貝塔通
        // 只有 oidc 模式才有管理工作階段可續(token 模式沒有三道門這回事)。
        // ★ 若此刻其實沒登入,loadExams() 會先 401 並在 handleUnauthorized 裡把心跳停掉。
        if (j.mode === "oidc") { startHeartbeat(); loadWhoami(); }
      }).catch(function () {});
    loadExams();
  }

  function bindTopbar() {
    $("#examSelect").addEventListener("change", function (e) {
      switchExam(e.target.value);
    });
    $("#manualRefresh").addEventListener("click", function () {
      refreshCurrent();
    });
    $("#preflightBtn").addEventListener("click", openPreflightDrawer);
    // 考試管理：開始 / 結束 / 全場登出
    $("#startExamBtn").addEventListener("click", createExam);
    $("#endExamBtn").addEventListener("click", endCurrentExam);
    $("#logoutAllBtn").addEventListener("click", logoutAllCurrent);
    $("#whitelistBtn").addEventListener("click", openWhitelistDrawer);
    $("#autoRefresh").addEventListener("change", function (e) {
      state.autoRefresh = e.target.checked;
      restartPolling();
    });
    // 退出監考員登入。
    // ★ oidc 模式走 **RP-Initiated Logout**（貝塔通 P85）：整頁跳 /admin/logout，
    //   那邊先清本機工作階段、再把人送到貝塔通的「只退出本站 / 退出所有站台」二選一頁。
    //   ★★ 機房電腦輪流坐人（貝塔通 R9），「退出所有站台」是上一個人離開時唯一穩妥的收尾，
    //   只清本機的話他在別的站台還登著。
    // ★ token 模式沒有 IdP 工作階段這回事，保持原來的純本機登出。
    $("#logoutBtn").addEventListener("click", function () {
      stopPolling();
      stopHeartbeat();
      if (state.adminAuthMode === "oidc") { window.location.href = "/admin/logout"; return; }
      logout();
      clearCurrentData();
      showLoginGate("请重新登录");
    });
  }

  /* ============================================================
     考試管理：開始 / 結束 / 全場登出（後端端點已存在，此處只連 UI）
     ------------------------------------------------------------
       · 開始考試：建一個 active 考試（POST /api/exams）。學員機 Agent 每 5s 輪詢
         /oidc/active-exam，檢測到即彈貝塔通登入、開始採集。
       · 結束考試：POST .../end → 通知線上 Agent 停採、排空緩衝後回待命。
       · 全場登出：POST .../logout → 吊銷該考試全部採集工作階段 + 強斷連接（學員須重登）。
     結束 / 全場登出僅噹噹前考試為「進行中」時可點。
     ============================================================ */
  function currentExam() {
    return state.exams.filter(function (e) { return e.examId === state.currentExamId; })[0] || null;
  }

  // 結束 / 全場登出 / 白名單按鈕：僅當前考試 active 時啟用；開始考試始終可用。
  function updateExamControls() {
    var active = !!(currentExam() && currentExam().status === "active");
    var endBtn = $("#endExamBtn"), logoutAllBtn = $("#logoutAllBtn"), wlBtn = $("#whitelistBtn");
    if (endBtn) endBtn.disabled = !active;
    if (logoutAllBtn) logoutAllBtn.disabled = !active;
    if (wlBtn) wlBtn.disabled = !active;
  }

  // 生成安全的 examId：E-YYYYMMDD-HHMM-<4位隨機>。僅字母數字與連字元，滿足伺服器 IsSafeId。
  function genExamId() {
    var d = new Date();
    var p = function (n) { return String(n).padStart(2, "0"); };
    var stamp = d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate()) + "-" + p(d.getHours()) + p(d.getMinutes());
    var rand = Math.random().toString(36).slice(2, 6);
    return "E-" + stamp + "-" + rand;
  }

  function createExam() {
    var d = new Date();
    var defName = (d.getMonth() + 1) + "月" + d.getDate() + "日 测验";
    var name = window.prompt("新考试名称（学员看板显示用）：", defName);
    if (name === null) return;             // 使用者取消
    name = name.trim();
    if (!name) { showToast("考试名称不能为空"); return; }
    var examId = genExamId();
    apiPost("/api/exams", { examId: examId, name: name })
      .then(function () {
        showToast("已开始考试：" + name);   // loadExams 會自動選中最新的 active 考試
        loadExams();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;      // 401 已彈登入門
        showToast("开始考试失败：" + err.message);
      });
  }

  function endCurrentExam() {
    var ex = currentExam();
    if (!ex) return;
    if (!window.confirm("结束考试「" + ex.name + "」？\n在线学员的采集会停止，Agent 回到待命状态。")) return;
    apiPost("/api/exams/" + encodeURIComponent(ex.examId) + "/end", {})
      .then(function (r) {
        var notified = r && typeof r.notified === "number" ? "（通知 " + r.notified + " 台在线 Agent）" : "";
        showToast("已结束考试：" + ex.name + notified);
        loadExams();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;
        showToast("结束考试失败：" + err.message);
      });
  }

  function logoutAllCurrent() {
    var ex = currentExam();
    if (!ex) return;
    if (!window.confirm("全场登出「" + ex.name + "」？\n将吊销所有学员的采集会话并强制断开，学员需重新登录。")) return;
    apiPost("/api/exams/" + encodeURIComponent(ex.examId) + "/logout", {})
      .then(function (r) {
        var revoked = r && typeof r.revoked === "number" ? r.revoked : "?";
        showToast("已全场登出：吊销 " + revoked + " 个会话");
        refreshCurrent();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;
        showToast("全场登出失败：" + err.message);
      });
  }

  /* ============================================================
     「當前登入：XXX」
     ------------------------------------------------------------
     ★★ 這不是體驗優化。機房電腦**無還原卡**、瀏覽器混裝國產核心因而組策略不現實、
     Windows 共享 PC 模式也不現實（貝塔通 R9）—— 上一個人不點退出就走時，最長
     15 分鐘（關了瀏覽器）或 30 分鐘（頁面還開著）內，下一個人可以直接用他的工作階段。
     **身分層關不掉這個視窗**，唯一能真正生效的緩解就是讓下一個人立刻看見這不是自己的號。
     ★ 對考場尤其直接：學生輪流坐同一臺機器正是常態。
     ============================================================ */
  function loadWhoami() {
    var el = $("#whoami");
    if (!el) return;
    fetch("/api/me", { credentials: "same-origin", headers: { "Accept": "application/json" } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j || !j.oidc || !j.name) { el.hidden = true; return; }
        el.textContent = "当前登录：" + j.name;
        el.hidden = false;
      })
      .catch(function () { /* 取不到就不顯示——不為一個提示條把看板搞崩 */ });
  }

  /* ============================================================
     存活心跳（三道門的第三道 · 貝塔通 P88–P92 · rp-contract「三道門與存活心跳」）
     ------------------------------------------------------------
     它擋的是「關頁面 / 關瀏覽器走人」，與 idle 擋的「頁面開著但人走了」是兩件事。
     機房電腦無還原卡、組策略不可行（貝塔通 R9），所以這一層是身分側唯一的抓手。

     訊息 { active }：**任何**心跳續「心跳」那道門；**只有 active:true** 才額外續 idle。
       · active = 頁面可見 && 最近 5 分鐘內有過 mousemove / keydown / click / scroll。

     ★★ 心跳是 idle 的**唯一**續期入口。**看板的自動輪詢絕不能續 idle** ——
        看板本來就每 5 秒輪詢一次，輪詢算作活動的話 idle 那道門永遠不會到點，
        監考機上開著看板就等於永不登出。伺服器端 admin gate 因此唯讀不寫。

     ★ 跨標籤選主：只有 leader 發心跳。leader 判據 = 存活對等體裡 id 最小的那個；
       對等體每 5 秒廣播一次，**15 秒**沒廣播即視為已走（必須遠小於 5 分鐘的心跳間隔，
       否則 leader 一關就漏掉整個心跳週期）。

     ★ 「只剩一個標籤且它被瀏覽器凍結 → 無心跳 → 15 分鐘後登出」**是預期行為，不要去修**：
       頁面被凍結本來就說明沒人在用。上線前要對監考員說清楚，否則會被當 bug 報。
     ============================================================ */
  var HEARTBEAT_MS = 5 * 60 * 1000;    // 心跳間隔
  var ACTIVITY_WINDOW_MS = 5 * 60 * 1000; // 「最近有過輸入」的視窗
  var PEER_TTL_MS = 15 * 1000;         // leader 廣播超時
  var PEER_PING_MS = 5 * 1000;         // 對等體廣播間隔

  var hb = {
    on: false, id: null, chan: null, peers: {},
    lastInputAt: 0, timer: null, pingTimer: null, wasLeader: false, bound: false
  };

  // 本標籤自己「人還在」嗎
  function hbSelfActive() {
    return document.visibilityState === "visible"
        && (Date.now() - hb.lastInputAt) < ACTIVITY_WINDOW_MS;
  }

  // 上報用的 active：**本標籤或任一存活對等體活躍即為 true**。
  // ★ 契約給的演算法只看本標籤，但 leader 未必是使用者正在看的那個標籤 ——
  //   照字面實作的話，「在 B 標籤裡幹活、A 標籤當 leader」會被判成人不在，
  //   30 分鐘後無聲登出。判據本來要回答的是「這個人還在不在」，不是「哪個標籤在前台」。
  function hbAnyActive() {
    if (hbSelfActive()) return true;
    var now = Date.now(), ids = Object.keys(hb.peers);
    for (var i = 0; i < ids.length; i++) {
      var p = hb.peers[ids[i]];
      if (p.active && (now - p.at) < PEER_TTL_MS) return true;
    }
    return false;
  }

  // leader = 存活對等體（含自己）裡 id 最小的那個。純函式、無需協商，任一標籤消失都自愈。
  function hbIsLeader() {
    var now = Date.now(), min = hb.id, ids = Object.keys(hb.peers);
    for (var i = 0; i < ids.length; i++) {
      if ((now - hb.peers[ids[i]].at) < PEER_TTL_MS && ids[i] < min) min = ids[i];
    }
    return min === hb.id;
  }

  function hbSend() {
    if (!hb.on) return;
    fetch("/api/heartbeat", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ active: hbAnyActive() })
    }).then(function (r) {
      // 401 = 三道門裡某一道已經到點（或被貝塔通撤權）。彈門，別讓一個已死的工作階段被自己的心跳救活。
      if (r.status !== 401) return;
      return r.json().catch(function () { return {}; })
        .then(function (j) { handleUnauthorized(j && j.message); });
    }).catch(function () { /* 網路抖動：下一發再說，15 分鐘視窗容得下 3 次 */ });
  }

  function hbPing() {
    if (!hb.on) return;
    var now = Date.now();
    // 清掉已走的對等體
    Object.keys(hb.peers).forEach(function (id) {
      if ((now - hb.peers[id].at) >= PEER_TTL_MS) delete hb.peers[id];
    });
    if (hb.chan) {
      try { hb.chan.postMessage({ id: hb.id, active: hbSelfActive() }); } catch (e) {}
    }
    var leader = hbIsLeader();
    // ★ 剛接手就補一發：新 leader 自己那個 5 分鐘定時器的相位是隨機的，
    //   不補的話最壞要再等一整個週期，而上一任已經不發了。
    //   伺服器端 150 秒節流正好吸收掉這類補發（這也是視窗取 150 秒而非貼近 5 分鐘的原因之一）。
    if (leader && !hb.wasLeader) hbSend();
    hb.wasLeader = leader;
  }

  function startHeartbeat() {
    if (hb.on) return;
    hb.on = true;
    hb.id = String(Date.now() % 1e6) + "-" + Math.random().toString(36).slice(2, 10);
    hb.lastInputAt = Date.now();   // 剛登入進來，人顯然在
    try {
      hb.chan = new BroadcastChannel("horus-admin-heartbeat");
      hb.chan.onmessage = function (ev) {
        var m = ev && ev.data;
        if (m && m.id && m.id !== hb.id) hb.peers[m.id] = { at: Date.now(), active: !!m.active };
      };
    } catch (e) {
      hb.chan = null;   // 瀏覽器不支援 BroadcastChannel → 本標籤獨自當 leader，功能不降級
    }
    if (!hb.bound) {
      ["mousemove", "keydown", "click", "scroll"].forEach(function (t) {
        window.addEventListener(t, function () { hb.lastInputAt = Date.now(); }, { passive: true });
      });
      hb.bound = true;   // 監聽器只掛一次（stop/start 會反覆呼叫）
    }
    hb.wasLeader = hbIsLeader();   // 起步時不發：工作階段剛建，兩個時間戳本就是 now
    hb.pingTimer = setInterval(hbPing, PEER_PING_MS);
    hb.timer = setInterval(function () { if (hbIsLeader()) hbSend(); }, HEARTBEAT_MS);
  }

  function stopHeartbeat() {
    hb.on = false;
    if (hb.timer) { clearInterval(hb.timer); hb.timer = null; }
    if (hb.pingTimer) { clearInterval(hb.pingTimer); hb.pingTimer = null; }
    if (hb.chan) { try { hb.chan.close(); } catch (e) {} hb.chan = null; }
    hb.peers = {};
    hb.wasLeader = false;
  }

  /* ============================================================
     登入門：顯示/隱藏 + 表單提交
     ============================================================ */
  function showLoginGate(msg) {
    var hint = $("#loginHint");
    if (msg) {
      hint.textContent = msg;
      // 「無效/過期」類提示標紅，普通提示保持弱色
      hint.classList.toggle("is-error", /无效|过期/.test(msg));
    }
    $("#loginGate").hidden = false;
    var whoami = $("#whoami");
    if (whoami) whoami.hidden = true;   // 已經不是「當前登入」了，別把上一個人的名字留在螢幕上
    var input = $("#tokenInput");
    input.value = "";
    // M4·RBAC：據後端鑑權方式切換權杖輸入 / 貝塔通 OIDC 登入按鈕；token 模式才聚焦輸入框。
    applyAuthMode().then(function (oidc) {
      if (!oidc) setTimeout(function () { try { input.focus(); } catch (e) {} }, 0);
    });
  }

  // M4·RBAC：探測管理鑑權方式（token / oidc），據此切換登入門 UI。回傳 Promise<oidc:boolean>。
  function applyAuthMode() {
    return fetch("/api/authmode", { credentials: "same-origin", headers: { "Accept": "application/json" } })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        if (j && j.collectAuthMode) state.collectAuthMode = j.collectAuthMode;
        var oidc = !!(j && j.mode === "oidc");
        var form = $("#loginForm"), btn = $("#oidcLoginBtn");
        var title = $("#loginTitle"), hint = $("#loginHint");
        if (oidc) {
          if (form) form.hidden = true;
          if (btn) { btn.hidden = false; btn.href = j.loginUrl || "/admin/login"; }
          if (title) title.textContent = "监考员登录";
          if (hint && !hint.classList.contains("is-error"))
            hint.textContent = "用贝塔通账号登录。未开通监考台权限的账号会在贝塔通那边被拒，不会进到这里。";
        } else {
          if (form) form.hidden = false;
          if (btn) btn.hidden = true;
        }
        return oidc;
      })
      .catch(function () { return false; });   // 探測失敗 → 保留預設權杖門
  }
  function hideLoginGate() {
    $("#loginGate").hidden = true;
  }

  function bindLoginGate() {
    $("#loginForm").addEventListener("submit", function (e) {
      e.preventDefault();
      var tok = $("#tokenInput").value.trim();
      if (!tok) {
        var hint = $("#loginHint");
        hint.textContent = "令牌不能为空";
        hint.classList.add("is-error");
        return;
      }
      var btn = $("#tokenSubmit");
      if (btn) btn.disabled = true;
      // POST /api/login 檢核並種 HttpOnly cookie；成功後 cookie 自動攜帶，直接拉資料。
      login(tok).then(function (res) {
        if (btn) btn.disabled = false;
        if (res.ok && (!res.body || res.body.ok !== false)) {
          hideLoginGate();
          loadExams();        // cookie 已種；若仍 401 會再次彈門
        } else {
          var hint = $("#loginHint");
          hint.textContent = "令牌无效，请重试";
          hint.classList.add("is-error");
          var input = $("#tokenInput"); input.value = "";
          setTimeout(function () { try { input.focus(); } catch (e2) {} }, 0);
        }
      }).catch(function () {
        if (btn) btn.disabled = false;
        var hint = $("#loginHint");
        hint.textContent = "登录请求失败，请检查网络";
        hint.classList.add("is-error");
      });
    });
  }

  // 停止輪詢（401 / 退出權杖時用；restartPolling 會在有權杖資料後重新啟動）
  function stopPolling() {
    if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
  }

  // 清空當前視圖資料（401 / 切換權杖時用），避免殘留舊資料誤導
  function clearCurrentData() {
    state.exams = [];
    state.seats = [];
    state.suspicious = [];
    state.inflight = false;
    closeDrawer();
    closeLightbox();
    showEmptyEverything("请先登录以加载数据");
  }

  function bindQueueFilter() {
    $("#queueFilter").addEventListener("click", function (e) {
      var btn = e.target.closest(".chip");
      if (!btn) return;
      $("#queueFilter").querySelectorAll(".chip").forEach(function (c) {
        c.classList.toggle("is-active", c === btn);
      });
      state.queueStatus = btn.getAttribute("data-status");
      loadSuspicious();  // 單獨拉取（狀態過濾走伺服器端參數）
    });
  }

  // 視圖切換:可疑複核 <-> 採集健康。health 模式隱藏狀態過濾 chips(唯讀告警,無需裁決狀態)。
  function bindQueueTabs() {
    $("#queueTabs").addEventListener("click", function (e) {
      var btn = e.target.closest(".tab");
      if (!btn) return;
      var mode = btn.getAttribute("data-mode");
      if (mode === state.queueMode) return;
      state.queueMode = mode;
      $("#queueTabs").querySelectorAll(".tab").forEach(function (t) {
        var on = (t === btn);
        t.classList.toggle("is-active", on);
        t.setAttribute("aria-selected", on ? "true" : "false");
      });
      // health 模式不需要狀態過濾(唯讀告警);suspicion 模式恢復
      $("#queueFilter").hidden = (mode === "health");
      if (mode === "health") loadHealth();
      else loadSuspicious();
      renderQueue();
    });
  }

  function bindDrawer() {
    $("#drawerClose").addEventListener("click", closeDrawer);
    $("#drawerScrim").addEventListener("click", closeDrawer);
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { closeLightbox(); closeDrawer(); }
    });
  }

  function bindLightbox() {
    $("#lightboxClose").addEventListener("click", closeLightbox);
    $("#lightbox").addEventListener("click", function (e) {
      if (e.target.id === "lightbox") closeLightbox();
    });
    var sb = $("#searchSimilarBtn");
    if (sb) sb.addEventListener("click", searchSimilar);
  }

  /* ============================================================
     考試列表
     ============================================================ */
  function loadExams() {
    apiGet("/api/exams")
      .then(function (exams) {
        state.exams = Array.isArray(exams) ? exams : [];
        renderExamOptions();
        if (state.exams.length === 0) {
          showEmptyEverything("暂无考试，点「开始考试」建一场");
          updateExamControls();   // 無考試：結束/全場登出置灰
          return;
        }
        // 預設選中第一個 active，否則第一個
        var pick = state.exams.filter(function (e) { return e.status === "active"; })[0]
          || state.exams[0];
        state.currentExamId = pick.examId;
        $("#examSelect").value = pick.examId;
        updateExamControls();
        refreshCurrent();
        restartPolling();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門，無需再回報錯誤
        showToast("加载考试列表失败：" + err.message);
        showEmptyEverything("无法连接后端，等待重试");
      });
  }

  function renderExamOptions() {
    var sel = $("#examSelect");
    sel.innerHTML = "";
    if (state.exams.length === 0) {
      var opt = document.createElement("option");
      opt.textContent = "（暂无考试）";
      opt.value = "";
      sel.appendChild(opt);
      return;
    }
    state.exams.forEach(function (ex) {
      var opt = document.createElement("option");
      opt.value = ex.examId;
      var statusZh = ({ active: "进行中", ended: "已结束", archived: "已归档" })[ex.status] || ex.status;
      opt.textContent = ex.name + "  ·  " + statusZh;
      sel.appendChild(opt);
    });
  }

  function switchExam(examId) {
    if (!examId || examId === state.currentExamId) return;
    state.currentExamId = examId;
    // 切換考試：清空舊資料 + 重置定時器，避免疊加
    state.seats = [];
    state.suspicious = [];
    closeDrawer();
    updateExamControls();
    refreshCurrent();
    restartPolling();
  }

  function showEmptyEverything(msg) {
    setOverview(null);
    $("#seatGrid").innerHTML = "";
    var se = $("#seatEmpty"); se.hidden = false; se.textContent = msg;
    $("#queueBody").innerHTML = "";
    $("#queueEmpty").hidden = false;
  }

  /* ============================================================
     輪詢：切換考試/開關時清理舊定時器
     ============================================================ */
  function restartPolling() {
    stopPolling();
    // 登入門開著時不啟動輪詢（避免無權杖輪詢風暴）；登入成功後再由此處啟動。
    if ($("#loginGate") && !$("#loginGate").hidden) return;
    if (state.autoRefresh && state.currentExamId) {
      state.pollTimer = setInterval(refreshCurrent, POLL_MS);
    }
  }

  // 拉取當前考試的 seats + suspicious（合併一次刷新）
  function refreshCurrent() {
    if (!state.currentExamId) return;
    if (state.inflight) return;  // 上一輪未回，不疊加
    state.inflight = true;

    var examId = state.currentExamId;
    var calls = [
      apiGet("/api/exams/" + encodeURIComponent(examId) + "/seats"),
      apiGet("/api/exams/" + encodeURIComponent(examId) +
        "/suspicious?status=" + encodeURIComponent(state.queueStatus))
    ];
    // 採集健康視圖下,額外拉取 health 列表,保持面板即時
    if (state.queueMode === "health") {
      calls.push(apiGet("/api/exams/" + encodeURIComponent(examId) + "/health"));
    }
    Promise.all(calls).then(function (res) {
      if (examId !== state.currentExamId) return;  // 期間已切換
      state.seats = Array.isArray(res[0]) ? res[0] : [];
      state.suspicious = Array.isArray(res[1]) ? res[1] : [];
      if (state.queueMode === "health" && res[2]) {
        state.health = Array.isArray(res[2]) ? res[2] : [];
      }
      renderSeats();
      renderQueue();
      updateOverview();
      $("#clock").textContent = fmtTime(Date.now() / 1000);
    }).catch(function (err) {
      if (err && err.isAuth) return;   // 401 已停輪詢 + 彈登入門
      showToast("刷新失败：" + err.message);
      // 失敗時不清屏，保留上一份可見資料（剋制重試：等下一輪 5s）
    }).finally(function () {
      state.inflight = false;
    });
  }

  // 僅刷新可疑佇列（切換狀態過濾時用）
  function loadSuspicious() {
    if (!state.currentExamId) return;
    var examId = state.currentExamId;
    apiGet("/api/exams/" + encodeURIComponent(examId) +
      "/suspicious?status=" + encodeURIComponent(state.queueStatus))
      .then(function (list) {
        if (examId !== state.currentExamId) return;
        state.suspicious = Array.isArray(list) ? list : [];
        renderQueue();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門
        showToast("加载可疑队列失败：" + err.message);
      });
  }

  // 採集健康(唯讀):拉取 source='health' 的告警列表
  function loadHealth() {
    if (!state.currentExamId) return;
    var examId = state.currentExamId;
    apiGet("/api/exams/" + encodeURIComponent(examId) + "/health")
      .then(function (list) {
        if (examId !== state.currentExamId) return;
        state.health = Array.isArray(list) ? list : [];
        renderQueue();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門
        showToast("加载采集健康失败：" + err.message);
      });
  }

  // 採集健康按時間倒序
  function sortedHealth() {
    return state.health.slice().sort(function (a, b) {
      return (b.ts || 0) - (a.ts || 0);
    });
  }

  /* ============================================================
     概覽計數
     ============================================================ */
  function updateOverview() {
    var ex = state.exams.filter(function (e) { return e.examId === state.currentExamId; })[0];
    var seatCount = state.seats.length ||
      (ex ? ex.seatCount : 0) || 0;
    var onlineCount = state.seats.filter(function (s) { return s.online; }).length;
    // 待複核數：優先用 exam 彙總；無則從當前 seats 累加 suspiciousCount
    var pending = ex && typeof ex.pendingSuspicious === "number"
      ? ex.pendingSuspicious
      : state.seats.reduce(function (a, s) { return a + (s.suspiciousCount || 0); }, 0);
    setOverview({ seats: seatCount, online: onlineCount, pending: pending });
    updateExamControls();   // 每次刷新同步按鈕態（考試狀態可能已變）
  }
  function setOverview(o) {
    $("#statSeats").textContent = o ? o.seats : "--";
    $("#statOnline").textContent = o ? o.online : "--";
    $("#statPending").textContent = o ? o.pending : "--";
  }

  /* ============================================================
     左：座位熱力網格
     ============================================================ */
  // 配色規則：
  //   離線 → 灰；線上且 risk<40 且 suspiciousCount=0 → 綠；
  //   risk 40-69 → 琥珀；risk>=70 或 suspiciousCount>0 → 紅（角標顯示計數）
  function seatClass(s) {
    if (!s.online) return "seat--offline";
    var risk = s.maxRecentRisk || 0;
    var susp = s.suspiciousCount || 0;
    if (risk >= 70 || susp > 0) return "seat--danger";
    if (risk >= 40) return "seat--warn";
    return "seat--normal";
  }

  function renderSeats() {
    var grid = $("#seatGrid");
    var empty = $("#seatEmpty");
    if (!state.seats.length) {
      grid.innerHTML = "";
      empty.hidden = false;
      empty.textContent = "暂无座位，等待 Agent 上报";
      return;
    }
    empty.hidden = true;
    grid.innerHTML = "";

    state.seats.forEach(function (s) {
      var cls = seatClass(s);
      var el = document.createElement("div");
      el.className = "seat " + cls;
      el.setAttribute("role", "button");
      el.setAttribute("tabindex", "0");

      // tooltip：agentId / machineId / 最後心跳 / 事件數
      el.title =
        "座位 " + (s.seatId || "—") +
        "\nAgent: " + dash(s.agentId) +
        "\n机器: " + dash(s.machineId) +
        "\n最后心跳: " + fmtTime(s.lastHeartbeatTs) +
        "\n事件数: " + dash(s.eventCount) +
        "\n最近风险: " + dash(s.maxRecentRisk) +
        ((s.healthAlerts || 0) > 0 ? "\n采集健康告警: " + s.healthAlerts + "(异常重启/挂起/遮屏/降权)" : "");

      var badge = "";
      if ((s.suspiciousCount || 0) > 0) {
        badge = '<span class="seat__badge">' + s.suspiciousCount + "</span>";
      }
      var offlineTag = s.online ? "" : '<span class="seat__offline-tag">离线</span>';
      // M5：採集健康告警標(異常重新啟動/疑似掛起/遮屏/能力降級),提示監考員該座位採集可能被削弱。
      var healthTag = (s.healthAlerts || 0) > 0
        ? '<span title="采集健康告警" style="position:absolute;top:.3rem;left:.3rem;background:#f9e2af;color:#1e1e2e;'
          + 'font-size:.7rem;font-weight:700;border-radius:.4rem;padding:.05rem .3rem">⚠' + s.healthAlerts + '</span>'
        : "";
      // both 灰度期:仍走 PSK 的線上座位高亮(未遷移到 OIDC),切 oidc 前據此逐個確認。
      var pskTag = (state.collectAuthMode === "both" && s.authMode === "psk")
        ? '<span title="仍走 PSK（未迁移到 OIDC）" style="position:absolute;bottom:.3rem;left:.3rem;background:#89b4fa;color:#1e1e2e;'
          + 'font-size:.62rem;font-weight:700;border-radius:.35rem;padding:.03rem .25rem">PSK</span>'
        : "";

      el.innerHTML =
        badge + offlineTag + healthTag + pskTag +
        '<span class="seat__id">' + esc(s.seatId) + "</span>" +
        '<span class="seat__name">' + esc(dash(s.displayName)) + "</span>" +
        '<span class="seat__meta">风险 ' + dash(s.maxRecentRisk) +
          " · 事件 " + dash(s.eventCount) + "</span>";

      el.addEventListener("click", function () { openSeatDrawer(s); });
      el.addEventListener("keydown", function (e) {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openSeatDrawer(s); }
      });
      grid.appendChild(el);
    });
  }

  /* ============================================================
     右：可疑複核佇列
     ============================================================ */
  // 排序：score 降序，同分 ts 降序
  function sortedSuspicious() {
    return state.suspicious.slice().sort(function (a, b) {
      if ((b.score || 0) !== (a.score || 0)) return (b.score || 0) - (a.score || 0);
      return (b.ts || 0) - (a.ts || 0);
    });
  }

  // 佇列繪製:按當前視圖(suspicion/health)分派
  function renderQueue() {
    if (state.queueMode === "health") renderHealth();
    else renderSuspicionQueue();
  }

  // 可疑複核佇列(可裁決)
  function renderSuspicionQueue() {
    var body = $("#queueBody");
    var empty = $("#queueEmpty");
    var list = sortedSuspicious();
    if (!list.length) {
      body.innerHTML = "";
      empty.hidden = false;
      empty.textContent = "暂无可疑记录";
      return;
    }
    empty.hidden = true;
    body.innerHTML = "";

    list.forEach(function (item) {
      var m = kindMeta(item.kind);
      var tr = document.createElement("tr");

      var statusZh = STATUS_LABEL[item.status] || item.status || "—";

      tr.innerHTML =
        '<td class="seat-cell">' + esc(dash(item.seatId)) + "</td>" +
        '<td><span class="kind-tag" style="color:' + m.fg +
          ";background:" + m.bg + ";border-color:" + m.bd + '">' +
          esc(m.label) + "</span></td>" +
        '<td class="num">' + dash(item.score) + "</td>" +
        "<td>" + fmtTime(item.ts) + "</td>" +
        '<td><span class="status-tag status-tag--' + esc(item.status) + '">' +
          esc(statusZh) + "</span></td>";

      tr.addEventListener("click", function () { openSuspiciousDrawer(item); });
      body.appendChild(tr);
    });
  }

  // 採集健康佇列(唯讀告警;點擊行跳轉該座位詳情,可點名抓圖)
  function renderHealth() {
    var body = $("#queueBody");
    var empty = $("#queueEmpty");
    var list = sortedHealth();
    if (!list.length) {
      body.innerHTML = "";
      empty.hidden = false;
      empty.textContent = "本场暂无采集健康告警";
      return;
    }
    empty.hidden = true;
    body.innerHTML = "";

    list.forEach(function (item) {
      var m = kindMeta(item.kind);
      var seat = state.seats.filter(function (s) { return s.seatId === item.seatId; })[0];
      var tr = document.createElement("tr");

      tr.innerHTML =
        '<td class="seat-cell">' + esc(dash(item.seatId)) + "</td>" +
        '<td><span class="kind-tag" style="color:' + m.fg +
          ";background:" + m.bg + ";border-color:" + m.bd + '">' +
          esc(m.label) + "</span></td>" +
        '<td class="num">' + (item.score > 0 ? dash(item.score) : "—") + "</td>" +
        "<td>" + fmtTime(item.ts) + "</td>" +
        '<td><span class="status-tag" style="color:#f9e2af;background:rgba(249,226,175,.12);border-color:#a9741f">健康告警</span></td>';

      tr.addEventListener("click", function () {
        if (seat) openSeatDrawer(seat);
        else showToast("未找到座位 " + item.seatId + " 的实时信息");
      });
      body.appendChild(tr);
    });
  }

  /* ============================================================
     抽屜：打開/關閉
     ============================================================ */
  function openDrawer(mode, kicker) {
    state.drawerMode = mode;
    $("#drawerKicker").textContent = kicker;
    $("#drawer").classList.add("is-open");
    $("#drawer").setAttribute("aria-hidden", "false");
    $("#drawerScrim").hidden = false;
  }
  function closeDrawer() {
    $("#drawer").classList.remove("is-open");
    $("#drawer").setAttribute("aria-hidden", "true");
    $("#drawerScrim").hidden = true;
    state.drawerMode = null;
  }

  /* ---------- 考前預檢（M4 部署項）---------- */
  function openPreflightDrawer() {
    openDrawer("preflight", "考前预检");
    var body = $("#drawerBody");
    body.innerHTML = '<div class="loading">预检中…</div>';
    apiGet("/api/preflight").then(function (pf) {
      if (state.drawerMode !== "preflight") return;
      var checks = Array.isArray(pf.checks) ? pf.checks : [];
      var rows = checks.map(function (c) {
        var color = c.level === "fail" ? "#f38ba8" : (c.level === "warn" ? "#f9e2af" : "#a6e3a1");
        var tag = c.level === "fail" ? "✕" : (c.level === "warn" ? "!" : "✓");
        return '<div style="display:flex;gap:.6rem;padding:.55rem 0;border-bottom:1px solid rgba(255,255,255,.06)">' +
          '<span style="color:' + color + ';font-weight:700;min-width:1.2rem;text-align:center">' + tag + '</span>' +
          '<div><div style="font-weight:600">' + esc(c.label || c.id) + '</div>' +
          '<div style="color:#9aa0aa;font-size:.85em;margin-top:.15rem">' + esc(c.detail || "") + '</div></div></div>';
      }).join("");
      var summary = pf.ok
        ? '<div style="color:#a6e3a1;font-weight:600">✓ 预检通过' + (pf.warns ? '（' + pf.warns + ' 项提醒，可开考）' : '，可开考') + '</div>'
        : '<div style="color:#f38ba8;font-weight:600">✕ 有 ' + pf.fails + ' 项须修复才能开考</div>';
      body.innerHTML = '<div style="margin-bottom:.9rem;font-size:1.05em">' + summary + '</div>' + rows;
    }).catch(function (err) {
      if (err && err.isAuth) return;
      body.innerHTML = '<div class="loading">预检失败：' + esc(err.message || "未知错误") + '</div>';
    });
  }

  /* ---------- 白名單熱更下發 ---------- */
  var LUOGU_HOST_DEFAULT = "luogu.com.cn\nwww.luogu.com.cn";

  function linesToArr(txt) {
    return String(txt || "").split(/\r?\n/).map(function (s) { return s.trim(); })
      .filter(function (s) { return s.length > 0; });
  }

  function openWhitelistDrawer() {
    var ex = currentExam();
    if (!ex) return;
    openDrawer("whitelist", "白名单热更");
    var body = $("#drawerBody");
    body.innerHTML = '<div class="loading">读取当前配置…</div>';
    apiGet("/api/exams/" + encodeURIComponent(ex.examId) + "/config").then(function (r) {
      if (state.drawerMode !== "whitelist") return;
      var cfg = (r && r.config) || {};
      state.wlConfig = cfg;   // 保留其它欄位(閾值/截圖參數)，下發時合併
      var pushed = !!(cfg.whitelistHosts || cfg.whitelistProcs);
      var hosts = Array.isArray(cfg.whitelistHosts) ? cfg.whitelistHosts.join("\n") : "";
      var procs = Array.isArray(cfg.whitelistProcs) ? cfg.whitelistProcs.join("\n") : "";
      body.innerHTML =
        '<div class="wl">' +
          '<p class="wl__note">下发会<b>替换</b>该考试所有在线 Agent 的白名单并<b>持久化</b>（Agent 重连时自动补推，仍生效）。' +
          (pushed ? '下面是当前已下发的白名单。'
                  : '尚未下发过；Agent 正使用内置默认（洛谷 + 常见 IDE）。已为你预填洛谷判题站，按需增改。') +
          '</p>' +
          '<label class="wl__label" for="wlHosts">判题站白名单 · 域名（一行一个，必填）</label>' +
          '<textarea id="wlHosts" class="wl__ta" rows="5" spellcheck="false" autocomplete="off" placeholder="luogu.com.cn"></textarea>' +
          '<label class="wl__label" for="wlProcs">允许进程白名单 · 进程名去扩展名（一行一个）</label>' +
          '<p class="wl__sub">留空 = 不下发此项，Agent 保留内置默认（code / devenv / g++ / gcc 等常见 IDE/编译器）。</p>' +
          '<textarea id="wlProcs" class="wl__ta" rows="6" spellcheck="false" autocomplete="off" placeholder="code&#10;devenv&#10;g++&#10;gcc"></textarea>' +
          '<div class="wl__actions">' +
            '<button id="wlPushBtn" type="button" class="btn btn--ok">下发到在线 Agent</button>' +
            '<span id="wlHint" class="wl__hint" role="status" aria-live="polite"></span>' +
          '</div>' +
        '</div>';
      // 值經 .value 注入(非 innerHTML)，不受 payload 影響，無 XSS 面。
      $("#wlHosts").value = hosts || (pushed ? "" : LUOGU_HOST_DEFAULT);
      $("#wlProcs").value = procs;
      $("#wlPushBtn").addEventListener("click", function () { pushWhitelist(ex); });
    }).catch(function (err) {
      if (err && err.isAuth) return;
      body.innerHTML = '<div class="loading">读取配置失败：' + esc(err.message || "未知错误") + '</div>';
    });
  }

  function pushWhitelist(ex) {
    var hosts = linesToArr($("#wlHosts").value);
    var procs = linesToArr($("#wlProcs").value);
    var hint = $("#wlHint");
    var setHint = function (msg, bad) { if (hint) { hint.textContent = msg; hint.style.color = bad ? "#f38ba8" : "#9aa0aa"; } };
    if (!hosts.length) { setHint("判题站白名单不能为空（否则学员访问洛谷也会被误判为高危）", true); return; }

    // 合併現有設定(保留閾值/截圖參數等)，僅覆蓋白名單兩項；處理程序留空則不下發該項(不覆蓋 Agent 預設)。
    var cfg = {}, cur = state.wlConfig || {};
    Object.keys(cur).forEach(function (k) { cfg[k] = cur[k]; });
    cfg.whitelistHosts = hosts;
    if (procs.length) cfg.whitelistProcs = procs; else delete cfg.whitelistProcs;

    var btn = $("#wlPushBtn");
    if (btn) btn.disabled = true;
    setHint("下发中…", false);
    apiPost("/api/exams/" + encodeURIComponent(ex.examId) + "/config", cfg).then(function (r) {
      var n = r && typeof r.pushedTo === "number" ? r.pushedTo : 0;
      showToast("白名单已下发：推送到 " + n + " 台在线 Agent（已持久化，重连仍生效）");
      closeDrawer();
    }).catch(function (err) {
      if (btn) btn.disabled = false;
      if (err && err.isAuth) return;
      setHint("下发失败：" + (err.message || "未知错误"), true);
    });
  }

  /* ---------- 可疑詳情 ---------- */
  function openSuspiciousDrawer(item) {
    openDrawer("suspicious", "可疑详情");
    var m = kindMeta(item.kind);
    var body = $("#drawerBody");

    // refs 解析：event:* 直接展示文字；image:* 繪製證據圖（可點擊放大）
    var refsHtml = "";
    var refs = Array.isArray(item.refs) ? item.refs : [];
    if (refs.length) {
      refsHtml = '<div class="refs">';
      refs.forEach(function (ref) {
        var parts = String(ref).split(":");
        var kind = parts[0], id = parts.slice(1).join(":");
        if (kind === "image") {
          refsHtml +=
            '<div class="ref-line"><b>image</b> ' + esc(id) + "</div>" +
            '<div class="evidence" data-img="' + esc(id) + '">' +
            '<img src="' + esc(imageUrl(id)) +
            '" alt="证据图 ' + esc(id) + '" loading="lazy" />' +
            "</div>";
        } else if (kind === "event") {
          // 直接展示 ref 文字；若已載入對應事件則附摘要（按契約無需單獨請求）
          refsHtml += '<div class="ref-line"><b>event</b> ' + esc(id) + "</div>";
        } else {
          refsHtml += '<div class="ref-line">' + esc(ref) + "</div>";
        }
      });
      refsHtml += "</div>";
    } else {
      refsHtml = '<div class="ref-line">（无关联引用）</div>';
    }

    var statusZh = STATUS_LABEL[item.status] || item.status || "—";
    var decided = item.decidedAt ? fmtTime(item.decidedAt) : "—";

    body.innerHTML =
      '<span class="kind-tag" style="color:' + m.fg + ";background:" + m.bg +
        ";border-color:" + m.bd + ';font-size:12px;padding:4px 10px">' + esc(m.label) + "</span>" +
      '<dl class="dl" style="margin-top:14px">' +
        "<dt>可疑ID</dt><dd class='mono'>" + esc(dash(item.id)) + "</dd>" +
        "<dt>座位</dt><dd class='mono'>" + esc(dash(item.seatId)) + "</dd>" +
        "<dt>分数</dt><dd class='mono'>" + dash(item.score) + "</dd>" +
        "<dt>时间</dt><dd class='mono'>" + fmtTime(item.ts) + "</dd>" +
        "<dt>状态</dt><dd>" + esc(statusZh) + "</dd>" +
        "<dt>复核人</dt><dd>" + esc(dash(item.reviewer)) + "</dd>" +
        "<dt>裁决时间</dt><dd class='mono'>" + decided + "</dd>" +
        "<dt>备注</dt><dd>" + esc(dash(item.note)) + "</dd>" +
      "</dl>" +
      '<div class="section-title">关联引用 (refs)</div>' + refsHtml +
      // 決策區
      '<div class="decide-actions">' +
        '<button class="btn btn--primary" id="btnConfirm">确认违规</button>' +
        '<button class="btn btn--ok" id="btnDismiss">驳回</button>' +
      "</div>" +
      '<div class="decide-form is-hidden" id="decideForm">' +
        '<div class="decide-form__title" id="decideTitle">填写复核意见</div>' +
        '<div class="form-row"><label>复核员</label>' +
          '<input type="text" id="fReviewer" value="监考员" /></div>' +
        '<div class="form-row"><label>备注</label>' +
          '<textarea id="fNote" rows="3" placeholder="可选，说明裁决理由"></textarea></div>' +
        '<div class="decide-actions" style="margin-top:0">' +
          '<button class="btn" id="fCancel">取消</button>' +
          '<button class="btn btn--primary" id="fSubmit">提交裁决</button>' +
        "</div>" +
      "</div>";

    // 證據圖點擊放大 + 載入失敗佔位(用 addEventListener 而非內聯 onerror:CSP script-src 'self' 會屏蔽內聯處理器)
    body.querySelectorAll(".evidence").forEach(function (ev) {
      ev.addEventListener("click", function () {
        var id = ev.getAttribute("data-img");
        openLightbox(imageUrl(id), id);
      });
      var img = ev.querySelector("img");
      if (img) img.addEventListener("error", function () {
        ev.innerHTML = '<div class="loading">图片加载失败</div>';
      });
    });

    // 決策表單交互：確認/駁回 → 展開表單 → 提交 POST
    var pendingDecision = null; // "confirmed" | "dismissed"
    var form = body.querySelector("#decideForm");
    function openForm(decision) {
      pendingDecision = decision;
      body.querySelector("#decideTitle").textContent =
        decision === "confirmed" ? "确认违规 — 填写复核意见" : "驳回 — 填写复核意见";
      form.classList.remove("is-hidden");
    }
    body.querySelector("#btnConfirm").addEventListener("click", function () { openForm("confirmed"); });
    body.querySelector("#btnDismiss").addEventListener("click", function () { openForm("dismissed"); });
    body.querySelector("#fCancel").addEventListener("click", function () {
      form.classList.add("is-hidden"); pendingDecision = null;
    });
    body.querySelector("#fSubmit").addEventListener("click", function () {
      if (!pendingDecision) return;
      var reviewer = body.querySelector("#fReviewer").value.trim() || "监考员";
      var note = body.querySelector("#fNote").value.trim();
      submitDecision(item.id, pendingDecision, reviewer, note);
    });
  }

  function submitDecision(id, status, reviewer, note) {
    apiPost("/api/suspicious/" + encodeURIComponent(id) + "/decide",
      { status: status, reviewer: reviewer, note: note })
      .then(function (res) {
        if (!res || !res.ok) throw new Error("裁决未确认");
        closeDrawer();
        loadSuspicious();     // 刷新佇列
        refreshCurrent();     // 順帶刷新座位/概覽
        showToast("已" + (status === "confirmed" ? "确认违规" : "驳回") + "：" + id);
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門（抽屜已被清屏關閉）
        showToast("裁决提交失败：" + err.message);
      });
  }

  /* ---------- 座位詳情 ---------- */
  function openSeatDrawer(s) {
    openDrawer("seat", "座位详情 · " + (s.seatId || ""));
    var body = $("#drawerBody");

    // 座位的登入身分（未登入/PSK 模式 s.identity 為 null，不繪製這些行）。
    // ★ 只剩三項（貝塔通 P81）：sub / 真實姓名 / 帳號。道號、境界、戰力是問天錄的業務欄位，
    //   身分中心不散佈；「監考員/參考學員」那一行更是隨 P83 整個消失 ——
    //   看板上出現的**每個座位都是考生**，監考員根本不從這條連線路徑進來。
    var id = s.identity;
    var idRows = id ? (
        "<dt>姓名</dt><dd>" + esc(dash(id.name)) + "</dd>" +
        "<dt>账号</dt><dd>" + esc(dash(id.username)) + "</dd>" +
        "<dt>标识</dt><dd class='mono'>" + esc(dash(id.sub)) + "</dd>"
      ) : "";

    var canCapture = !!(s.online && s.agentId);
    var actionBar = canCapture
      ? '<div class="drawer-actions">' +
          '<button type="button" class="btn btn--warn" id="captureBtn">点名抓图</button>' +
          '<span class="drawer-actions__hint">向该座位 Agent 推送一次立即截屏指令</span>' +
        '</div>'
      : "";

    body.innerHTML =
      actionBar +
      '<dl class="dl">' +
        "<dt>座位</dt><dd class='mono'>" + esc(dash(s.seatId)) + "</dd>" +
        "<dt>姓名</dt><dd>" + esc(dash(s.displayName)) + "</dd>" +
        "<dt>学号</dt><dd class='mono'>" + esc(dash(s.studentId)) + "</dd>" +
        idRows +
        "<dt>Agent</dt><dd class='mono'>" + esc(dash(s.agentId)) + "</dd>" +
        "<dt>机器</dt><dd class='mono'>" + esc(dash(s.machineId)) + "</dd>" +
        "<dt>采集鉴权</dt><dd>" + (s.authMode === "oidc" ? "OIDC（已迁移）"
          : s.authMode === "psk" ? "<span style='color:#89b4fa'>PSK（未迁移）</span>" : "离线") + "</dd>" +
        "<dt>在线</dt><dd>" + (s.online ? "在线" : "离线") + "</dd>" +
        "<dt>最后心跳</dt><dd class='mono'>" + fmtTime(s.lastHeartbeatTs) + "</dd>" +
        "<dt>最近事件</dt><dd class='mono'>" + fmtTime(s.lastEventTs) + "</dd>" +
        "<dt>最近风险</dt><dd class='mono'>" + dash(s.maxRecentRisk) + "</dd>" +
        "<dt>事件数</dt><dd class='mono'>" + dash(s.eventCount) + "</dd>" +
        "<dt>可疑数</dt><dd class='mono'>" + dash(s.suspiciousCount) + "</dd>" +
        ((s.healthAlerts || 0) > 0
          ? "<dt>采集健康</dt><dd style='color:#f9e2af'>⚠ " + s.healthAlerts + " 告警（见下方时间线：异常重启/挂起/遮屏/降权）</dd>"
          : "") +
      "</dl>" +
      '<div class="section-title">事件时间线</div>' +
      '<div class="loading" id="tlLoading">加载事件中…</div>' +
      '<div class="timeline" id="timeline"></div>';

    // 點名抓圖:僅線上且有 agentId 的座位顯示按鈕(後端 D2:POST /api/agents/{agentId}/capture)
    var capBtn = $("#captureBtn");
    if (capBtn) capBtn.addEventListener("click", function () { captureNow(s.agentId); });

    // 拉取該座位事件（limit=200，按 id 倒序，最新在前）
    var examId = state.currentExamId;
    apiGet("/api/exams/" + encodeURIComponent(examId) +
      "/events?seatId=" + encodeURIComponent(s.seatId) + "&limit=200")
      .then(function (events) {
        if (state.drawerMode !== "seat") return; // 抽屜已關或已切換
        renderTimeline(Array.isArray(events) ? events : []);
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門（抽屜已被清屏關閉）
        var l = $("#tlLoading");
        if (l) l.textContent = "事件加载失败：" + err.message;
      });
  }

  // 監考員點名抓圖:向後端推 capture_now,toast 推送結果(後端 D2 已實作對線上 Agent 抓一張)
  function captureNow(agentId) {
    apiPost("/api/agents/" + encodeURIComponent(agentId) + "/capture", { reason: "proctor_call" })
      .then(function (r) {
        if (r && r.pushed === true) {
          showToast("已向 " + agentId + " 推送点名抓图指令，稍后查看证据图");
        } else {
          showToast("该座位 Agent 不在线，无法抓图");
        }
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已彈登入門
        showToast("点名抓图失败：" + err.message);
      });
  }

  // 事件 risk → 等級類
  function riskClass(risk) {
    var r = risk || 0;
    if (r >= 70) return "risk--high";
    if (r >= 40) return "risk--mid";
    return "risk--low";
  }

  // 按 type 生成關鍵 payload 摘要
  function payloadSummary(ev) {
    var p = ev.payload || {};
    switch (ev.type) {
      case "browser_url": {
        if (p.url === null || p.note === "url_unreadable") {
          return '<span class="url-bad">URL 不可读</span>' +
            (p.process ? ' <span class="mono">' + esc(p.process) + "</span>" : "");
        }
        var wl = p.whitelisted;
        var cls = wl ? "url-ok" : "url-bad";
        return '<span class="mono ' + cls + '">' + esc(p.url) + "</span>" +
          (wl ? '<span class="flag">白名单</span>' : '<span class="flag flag--bad">非白名单</span>');
      }
      case "process_start": {
        return '进程 <span class="mono">' + esc(dash(p.name)) + "</span>" +
          (p.pid !== undefined ? ' <span class="mono">pid=' + esc(p.pid) + "</span>" : "") +
          (p.whitelisted === false ? '<span class="flag flag--bad">非白名单</span>' :
            (p.whitelisted === true ? '<span class="flag">白名单</span>' : ""));
      }
      case "process_exit":
        return '进程退出 <span class="mono">' + esc(dash(p.name)) + "</span>";
      case "clipboard": {
        return "剪贴板 " +
          '<span class="mono">len=' + dash(p.len) + " lines=" + dash(p.lines) + "</span>" +
          (p.large ? '<span class="flag flag--bad">大段</span>' : "");
      }
      case "usb":
        return '插入设备 <span class="mono">' + esc(dash(p.drive)) + "</span>";
      case "window_focus":
        return '窗口 <span class="mono">' + esc(dash(p.title)) + "</span>" +
          (p.process ? ' <span class="mono">(' + esc(p.process) + ")</span>" : "");
      case "alt_tab_burst":
        return "检测到快速切窗";
      case "screenshot":
        return "截屏取证";
      case "heartbeat":
        return '心跳 <span class="mono">' + esc(dash(p.status)) + "</span>";
      default:
        return '<span class="mono">' + esc(JSON.stringify(p)) + "</span>";
    }
  }

  function renderTimeline(events) {
    var l = $("#tlLoading");
    if (l) l.remove();
    var tl = $("#timeline");
    if (!tl) return;
    if (!events.length) {
      tl.innerHTML = '<div class="empty">该座位暂无事件</div>';
      return;
    }
    tl.innerHTML = "";
    events.forEach(function (ev) {
      var item = document.createElement("div");
      item.className = "tl-item";

      // 證據縮略圖
      var thumb = "";
      if (ev.evidenceImageId) {
        thumb =
          '<img class="thumb" src="' + esc(imageUrl(ev.evidenceImageId)) +
          '" alt="证据缩略图" loading="lazy" data-img="' + esc(ev.evidenceImageId) + '" />';
      }

      // 有效風險 = max(Agent 自報, 伺服器復判);著色用有效風險,避免"謊報 risk=0"在明細裡顯示成低危。
      var evAgent = (typeof ev.risk === "number") ? ev.risk : 0;
      var evServer = (typeof ev.serverRisk === "number") ? ev.serverRisk : 0;
      var evEff = Math.max(evAgent, evServer);
      // 伺服器復判高於 Agent 自報 → 顯式標註 "自報→伺服器"(篡改逃逸取證提示)
      var riskText = evServer > evAgent ? ("risk " + evAgent + "→" + evServer) : ("risk " + dash(ev.risk));

      item.innerHTML =
        '<div class="tl-time">' + fmtTime(ev.ts) + "</div>" +
        '<div class="tl-main">' +
          '<div class="tl-head">' +
            '<span class="tl-type ' + riskClass(evEff) + '">' + esc(typeLabel(ev.type)) + "</span>" +
            '<span class="tl-risk">' + riskText + " · seq " + dash(ev.seq) + "</span>" +
          "</div>" +
          '<div class="tl-payload">' + payloadSummary(ev) + "</div>" +
          thumb +
        "</div>";

      var thumbEl = item.querySelector(".thumb");
      if (thumbEl) {
        thumbEl.addEventListener("click", function () {
          var tid = thumbEl.getAttribute("data-img");
          openLightbox(imageUrl(tid), tid);
        });
        // 載入失敗隱藏(addEventListener 而非內聯 onerror:CSP 會屏蔽內聯處理器)
        thumbEl.addEventListener("error", function () { thumbEl.style.display = "none"; });
      }
      tl.appendChild(item);
    });
  }

  /* ============================================================
     燈箱
     ============================================================ */
  function openLightbox(src, imageId) {
    $("#lightboxImg").src = src;
    $("#lightbox").hidden = false;
    // M3 按圖搜圖:有 imageId 且嵌入器已配才顯示按鈕;每次打開清空上次結果。
    state.lightboxImageId = imageId || null;
    var btn = $("#searchSimilarBtn"), res = $("#similarResults");
    if (res) { res.hidden = true; res.innerHTML = ""; }
    if (btn) {
      var usable = !!(state.imageSearchEnabled && state.lightboxImageId);
      btn.hidden = !state.lightboxImageId;             // 有圖才顯示
      btn.disabled = !usable;                          // 未啟用嵌入器 → 禁用態引導
      btn.title = usable ? "以当前图为查询,捞本场相似帧"
                         : "按图搜图不可用:未部署 CLIP 模型(model.onnx)";
    }
  }
  function closeLightbox() {
    $("#lightbox").hidden = true;
    $("#lightboxImg").src = "";
    state.lightboxImageId = null;
    var res = $("#similarResults"); if (res) { res.hidden = true; res.innerHTML = ""; }
  }

  // 按圖搜圖:以當前燈箱圖為查詢,撈本場相似影格,繪製縮略圖條(點擊切換查看)。
  function searchSimilar() {
    if (!state.imageSearchEnabled) { showToast("按图搜图不可用：未部署 CLIP 模型（model.onnx）"); return; }
    var imageId = state.lightboxImageId, examId = state.currentExamId;
    if (!imageId || !examId) return;
    var res = $("#similarResults");
    res.hidden = false;
    res.innerHTML = '<span style="color:#9aa0aa;align-self:center">搜索相似帧中…</span>';
    api("/api/exams/" + encodeURIComponent(examId) + "/search-image", {
      method: "POST", body: JSON.stringify({ imageId: imageId, topN: 12 })
    }).then(function (j) {
      var results = (j && Array.isArray(j.results)) ? j.results : [];
      if (!results.length) { res.innerHTML = '<span style="color:#9aa0aa;align-self:center">无相似帧（本场已嵌入 ' + ((j && j.corpusSize) || 0) + ' 图）</span>'; return; }
      res.innerHTML = results.map(function (r) {
        return '<div style="flex:0 0 auto;text-align:center">' +
          '<img src="' + esc(imageUrl(r.imageId)) + '" data-img="' + esc(r.imageId) + '" ' +
          'style="height:76px;border-radius:.3rem;cursor:pointer" alt="相似帧" loading="lazy" />' +
          '<div style="color:#a6e3a1;font-size:.7rem">' + (r.score != null ? r.score.toFixed(3) : "") + '</div></div>';
      }).join("");
      // 點擊相似縮略圖 → 切到該圖繼續看/再搜
      res.querySelectorAll("img[data-img]").forEach(function (im) {
        im.addEventListener("click", function () {
          var id = im.getAttribute("data-img");
          openLightbox(imageUrl(id), id);
        });
      });
    }).catch(function (err) {
      if (err && err.isAuth) return;
      res.innerHTML = '<span style="color:#f38ba8;align-self:center">搜索失败：' + esc(err.message || "") + '</span>';
    });
  }

  /* ============================================================
     ★ 內建 Mock 資料（結構與契約逐欄位一致）
     覆蓋：2 場考試、多種狀態座位、若干可疑、若干事件
     ============================================================ */
  var NOW = Date.now() / 1000;

  var MOCK = {
    exams: [
      {
        examId: "exam_2026_algo_final",
        name: "2026 算法期末（A 卷）",
        status: "active",
        startedAt: NOW - 3600,
        endedAt: null,
        seatCount: 6,
        onlineCount: 5,
        pendingSuspicious: 3
      },
      {
        examId: "exam_2026_ds_makeup",
        name: "数据结构 · 补考",
        status: "ended",
        startedAt: NOW - 86400,
        endedAt: NOW - 82800,
        seatCount: 2,
        onlineCount: 0,
        pendingSuspicious: 0
      }
    ],

    // 按 examId 索引的座位
    seats: {
      exam_2026_algo_final: [
        { seatId: "A-01", studentId: "20260101", displayName: "赵一", agentId: "agt-a1",
          machineId: "PC-LAB-01", online: true, lastHeartbeatTs: NOW - 4, lastEventTs: NOW - 20,
          maxRecentRisk: 12, eventCount: 34, suspiciousCount: 0 },
        { seatId: "A-02", studentId: "20260102", displayName: "钱二", agentId: "agt-a2",
          machineId: "PC-LAB-02", online: true, lastHeartbeatTs: NOW - 6, lastEventTs: NOW - 55,
          maxRecentRisk: 52, eventCount: 41, suspiciousCount: 0 },
        { seatId: "A-03", studentId: "20260103", displayName: "孙三", agentId: "agt-a3",
          machineId: "PC-LAB-03", online: true, lastHeartbeatTs: NOW - 3, lastEventTs: NOW - 8,
          maxRecentRisk: 88, eventCount: 57, suspiciousCount: 2 },
        { seatId: "A-04", studentId: "20260104", displayName: "李四", agentId: "agt-a4",
          machineId: "PC-LAB-04", online: true, lastHeartbeatTs: NOW - 5, lastEventTs: NOW - 120,
          maxRecentRisk: 30, eventCount: 22, suspiciousCount: 1 },
        { seatId: "A-05", studentId: "20260105", displayName: "周五", agentId: "agt-a5",
          machineId: "PC-LAB-05", online: false, lastHeartbeatTs: NOW - 240, lastEventTs: NOW - 300,
          maxRecentRisk: 65, eventCount: 18, suspiciousCount: 0 },
        { seatId: "A-06", studentId: "20260106", displayName: "吴六", agentId: "agt-a6",
          machineId: "PC-LAB-06", online: true, lastHeartbeatTs: NOW - 2, lastEventTs: NOW - 40,
          maxRecentRisk: null, eventCount: null, suspiciousCount: 0 }
      ],
      exam_2026_ds_makeup: [
        { seatId: "B-01", studentId: "20259901", displayName: "郑七", agentId: "agt-b1",
          machineId: "PC-HALL-01", online: false, lastHeartbeatTs: NOW - 82900, lastEventTs: NOW - 82950,
          maxRecentRisk: 5, eventCount: 12, suspiciousCount: 0 },
        { seatId: "B-02", studentId: "20259902", displayName: "王八", agentId: "agt-b2",
          machineId: "PC-HALL-02", online: false, lastHeartbeatTs: NOW - 82880, lastEventTs: NOW - 82920,
          maxRecentRisk: 20, eventCount: 15, suspiciousCount: 0 }
      ]
    },

    // 按 examId 索引的可疑（含各狀態，用於過濾演示）
    suspicious: {
      exam_2026_algo_final: [
        { id: "susp_1001", seatId: "A-03", ts: NOW - 10, kind: "web_ai", score: 94,
          status: "pending", refs: ["event:5501", "image:img_a03_ai"], reviewer: null,
          decidedAt: null, note: null },
        { id: "susp_1002", seatId: "A-03", ts: NOW - 90, kind: "search", score: 81,
          status: "pending", refs: ["event:5490"], reviewer: null, decidedAt: null, note: null },
        { id: "susp_1003", seatId: "A-04", ts: NOW - 130, kind: "usb", score: 76,
          status: "pending", refs: ["event:5480", "image:img_a04_usb"], reviewer: null,
          decidedAt: null, note: null },
        { id: "susp_1004", seatId: "A-02", ts: NOW - 300, kind: "large_paste", score: 68,
          status: "reviewing", refs: ["event:5450"], reviewer: "监考员", decidedAt: null,
          note: "疑似粘贴代码，待人工复核" },
        { id: "susp_1005", seatId: "A-05", ts: NOW - 600, kind: "non_whitelist_proc", score: 72,
          status: "confirmed", refs: ["event:5400", "image:img_a05_proc"], reviewer: "监考员",
          decidedAt: NOW - 500, note: "确认运行外部搜题工具" },
        { id: "susp_1006", seatId: "A-01", ts: NOW - 800, kind: "browser_unreadable", score: 45,
          status: "dismissed", refs: ["event:5380"], reviewer: "监考员", decidedAt: NOW - 700,
          note: "浏览器标题不可读，实为白名单站点，误报" }
      ],
      exam_2026_ds_makeup: []
    },

    // 按 examId 索引的採集健康告警(source='health',唯讀;M5 三件套)
    health: {
      exam_2026_algo_final: [
        { id: "hlth_1", seatId: "A-03", ts: NOW - 35, kind: "watchdog_restart", score: 0,
          status: "pending", refs: ["event:5455"], reviewer: null, decidedAt: null, note: "采集进程看门狗重启，已自动恢复" },
        { id: "hlth_2", seatId: "A-06", ts: NOW - 22, kind: "capability_degraded", score: 0,
          status: "pending", refs: [], reviewer: null, decidedAt: null, note: "截图能力降级（窗口最小化为不可读）" },
        { id: "hlth_3", seatId: "A-02", ts: NOW - 70, kind: "screen_obscured", score: 0,
          status: "pending", refs: [], reviewer: null, decidedAt: null, note: "屏幕被遮挡，无法截图" }
      ],
      exam_2026_ds_makeup: []
    },

    // 按 seatId 索引的事件（最新在前）
    events: {
      "A-03": [
        { id: 5501, seatId: "A-03", seq: 57, ts: NOW - 8, recvTs: NOW - 7, type: "browser_url",
          payload: { process: "chrome.exe", url: "https://chat.some-ai.example/ask", whitelisted: false },
          risk: 95, evidenceImageId: "img_a03_ai" },
        { id: 5500, seatId: "A-03", seq: 56, ts: NOW - 30, recvTs: NOW - 29, type: "alt_tab_burst",
          payload: {}, risk: 60, evidenceImageId: null },
        { id: 5490, seatId: "A-03", seq: 55, ts: NOW - 90, recvTs: NOW - 89, type: "browser_url",
          payload: { process: "chrome.exe", url: "https://www.baidu.com/s?wd=最短路径算法", whitelisted: false },
          risk: 78, evidenceImageId: null },
        { id: 5471, seatId: "A-03", seq: 54, ts: NOW - 150, recvTs: NOW - 149, type: "clipboard",
          payload: { len: 812, lines: 24, large: true }, risk: 55, evidenceImageId: null },
        { id: 5460, seatId: "A-03", seq: 53, ts: NOW - 210, recvTs: NOW - 209, type: "window_focus",
          payload: { title: "OJ 考试系统 - 第 3 题", process: "chrome.exe", hwnd: 12345 },
          risk: 5, evidenceImageId: null },
        { id: 5455, seatId: "A-03", seq: 52, ts: NOW - 260, recvTs: NOW - 259, type: "heartbeat",
          payload: { status: "ok" }, risk: 0, evidenceImageId: null }
      ],
      "A-04": [
        { id: 5480, seatId: "A-04", seq: 22, ts: NOW - 130, recvTs: NOW - 129, type: "usb",
          payload: { drive: "E:\\" }, risk: 76, evidenceImageId: "img_a04_usb" },
        { id: 5475, seatId: "A-04", seq: 21, ts: NOW - 300, recvTs: NOW - 299, type: "process_start",
          payload: { name: "notepad.exe", pid: 4412, cmd: "notepad.exe C:\\temp\\a.txt", whitelisted: true },
          risk: 8, evidenceImageId: null }
      ],
      "A-02": [
        { id: 5450, seatId: "A-02", seq: 41, ts: NOW - 300, recvTs: NOW - 299, type: "clipboard",
          payload: { len: 1560, lines: 60, large: true }, risk: 68, evidenceImageId: null },
        { id: 5449, seatId: "A-02", seq: 40, ts: NOW - 360, recvTs: NOW - 359, type: "browser_url",
          payload: { process: "msedge.exe", url: null, note: "url_unreadable" }, risk: 40, evidenceImageId: null }
      ],
      "A-01": [
        { id: 5380, seatId: "A-01", seq: 34, ts: NOW - 800, recvTs: NOW - 799, type: "browser_url",
          payload: { process: "chrome.exe", url: "https://oj.exam.local/problem/3", whitelisted: true },
          risk: 3, evidenceImageId: null }
      ],
      "A-05": [
        { id: 5400, seatId: "A-05", seq: 18, ts: NOW - 600, recvTs: NOW - 599, type: "process_start",
          payload: { name: "search_helper.exe", pid: 9981, cmd: "search_helper.exe", whitelisted: false },
          risk: 72, evidenceImageId: "img_a05_proc" }
      ]
    }
  };

  // 內聯 1x1 WebP 佔點陣圖（mock 下證據圖不發真實請求，避免 404）
  var MOCK_IMG_DATAURL =
    "data:image/svg+xml;utf8," +
    encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200">' +
      '<rect width="320" height="200" fill="#171c28"/>' +
      '<rect x="8" y="8" width="304" height="184" fill="none" stroke="#2a3346"/>' +
      '<text x="160" y="100" fill="#6b768c" font-family="monospace" font-size="14" ' +
      'text-anchor="middle">MOCK 证据图</text></svg>'
    );

  // Mock 路由：模擬 GET
  function mockGet(path) {
    return new Promise(function (resolve) {
      setTimeout(function () {
        // /api/images/{id}/meta —— 本前端不主動請求，備援回傳
        var mMeta = path.match(/^\/api\/images\/([^/]+)\/meta$/);
        if (mMeta) {
          resolve({ imageId: decodeURIComponent(mMeta[1]), seatId: "A-03", ts: NOW - 8,
            trigger: "web_ai", phash: "f0e1d2c3", width: 320, height: 200, bytes: 4096,
            isEvidence: true, uploadedToOcr: false });
          return;
        }
        if (path === "/api/exams") { resolve(MOCK.exams); return; }

        var mSeats = path.match(/^\/api\/exams\/([^/]+)\/seats$/);
        if (mSeats) { resolve(MOCK.seats[decodeURIComponent(mSeats[1])] || []); return; }

        var mSusp = path.match(/^\/api\/exams\/([^/]+)\/suspicious/);
        if (mSusp) {
          var examId = decodeURIComponent(mSusp[1]);
          var all = MOCK.suspicious[examId] || [];
          var sp = new URLSearchParams(path.split("?")[1] || "");
          var st = sp.get("status") || "pending";
          resolve(st === "all" ? all : all.filter(function (x) { return x.status === st; }));
          return;
        }

        var mHealth = path.match(/^\/api\/exams\/([^/]+)\/health$/);
        if (mHealth) {
          resolve(MOCK.health[decodeURIComponent(mHealth[1])] || []);
          return;
        }

        var mEv = path.match(/^\/api\/exams\/([^/]+)\/events/);
        if (mEv) {
          var sp2 = new URLSearchParams(path.split("?")[1] || "");
          var seatId = sp2.get("seatId");
          resolve((MOCK.events[seatId] || []).slice());
          return;
        }
        resolve([]);
      }, 120); // 模擬網路延遲
    });
  }

  // Mock 路由：模擬 POST decide
  function mockPost(path, body) {
    return new Promise(function (resolve) {
      setTimeout(function () {
        // 點名抓圖(D2):mock 下演示視為線上、可推送
        var mCap = path.match(/^\/api\/agents\/([^/]+)\/capture$/);
        if (mCap) {
          var aid = decodeURIComponent(mCap[1]);
          resolve({ ok: true, agentId: aid, pushed: true });
          return;
        }
        var m = path.match(/^\/api\/suspicious\/([^/]+)\/decide$/);
        if (!m) { resolve({ ok: false }); return; }
        var id = decodeURIComponent(m[1]);
        // 在所有考試裡找到該條目並更新
        Object.keys(MOCK.suspicious).forEach(function (ex) {
          MOCK.suspicious[ex].forEach(function (it) {
            if (it.id === id) {
              it.status = body.status;
              it.reviewer = body.reviewer;
              it.note = body.note;
              it.decidedAt = Date.now() / 1000;
            }
          });
        });
        // 同步 pendingSuspicious 計數
        MOCK.exams.forEach(function (ex) {
          var list = MOCK.suspicious[ex.examId] || [];
          ex.pendingSuspicious = list.filter(function (x) { return x.status === "pending"; }).length;
        });
        var updated = null;
        Object.keys(MOCK.suspicious).forEach(function (ex) {
          MOCK.suspicious[ex].forEach(function (it) { if (it.id === id) updated = it; });
        });
        resolve({ ok: true, item: updated });
      }, 120);
    });
  }

  // Mock 模式下把證據圖 <img src="/api/images/*"> 重定向到佔點陣圖
  if (USE_MOCK) {
    document.addEventListener("error", function (e) {
      var t = e.target;
      if (t && t.tagName === "IMG" && /\/api\/images\//.test(t.src) &&
          t.src.indexOf("data:") !== 0) {
        t.src = MOCK_IMG_DATAURL;
      }
    }, true);
    // 更直接：攔截 img.src 賦值不可行，這裡用 MutationObserver 備援替換
    var mo = new MutationObserver(function (muts) {
      muts.forEach(function (mu) {
        mu.addedNodes && mu.addedNodes.forEach(function (n) {
          if (n.querySelectorAll) {
            n.querySelectorAll('img[src^="/api/images/"]').forEach(function (img) {
              img.src = MOCK_IMG_DATAURL;
            });
          }
          if (n.tagName === "IMG" && /^\/api\/images\//.test(n.getAttribute("src") || "")) {
            n.src = MOCK_IMG_DATAURL;
          }
        });
      });
    });
    mo.observe(document.body, { childList: true, subtree: true });
  }

  /* ---------- 啟動 ---------- */
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
