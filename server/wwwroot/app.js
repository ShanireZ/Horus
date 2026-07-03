/* ============================================================
   Horus 监考台 · 前端逻辑（vanilla JS，无框架/无构建/无外部依赖）
   消费已冻结的后端 API；?mock=1 或 window.USE_MOCK=true 走内置 mock。
   ============================================================ */
(function () {
  "use strict";

  /* ---------- 运行模式：真实 fetch vs 内置 mock ---------- */
  var USE_MOCK =
    window.USE_MOCK === true ||
    /[?&]mock=1\b/.test(window.location.search);

  /* ---------- 常量：kind 中文标签 + 颜色 ---------- */
  // 每种可疑类型给一个中文标签与配色（前景/背景/边框）。
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
    suspect:            { label: "可疑",         fg: "#c2c9d8", bg: "#242a37", bd: "#4a5163" }
  };
  function kindMeta(kind) {
    return KIND_META[kind] || { label: kind || "未知", fg: "#c2c9d8", bg: "#242a37", bd: "#4a5163" };
  }

  /* ---------- 常量：事件 type 中文标签 ---------- */
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

  /* ---------- 常量：可疑状态中文标签 ---------- */
  var STATUS_LABEL = {
    pending:   "待复核",
    reviewing: "复核中",
    confirmed: "已确认",
    dismissed: "已驳回"
  };

  /* ============================================================
     工具函数
     ============================================================ */

  // Unix 秒（含毫秒的浮点）→ 本地 HH:MM:SS
  function fmtTime(ts) {
    if (ts === null || ts === undefined || isNaN(ts)) return "--:--:--";
    var d = new Date(ts * 1000);
    var p = function (n) { return String(n).padStart(2, "0"); };
    return p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
  }

  // 转义 HTML，防止 payload 里的字符串破坏 DOM
  function esc(s) {
    if (s === null || s === undefined) return "";
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // 短横线容错取值
  function dash(v) { return (v === null || v === undefined || v === "") ? "—" : v; }

  var $ = function (sel) { return document.querySelector(sel); };

  /* ---------- 顶部错误横幅（toast） ---------- */
  var toastTimer = null;
  function showToast(msg) {
    var el = $("#toast");
    el.textContent = msg;
    el.hidden = false;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.hidden = true; }, 6000);
  }

  /* ============================================================
     管理鉴权（M2：HttpOnly cookie）
     ------------------------------------------------------------
     冻结契约：
       · 登录：POST /api/login {token} → 校验通过则种 HttpOnly cookie `horus_admin`。
         cookie 由浏览器自动附带（fetch credentials:same-origin + <img> 同源），
         **JS 读不到**（防 XSS 窃取），**不进 URL**（防 ?t= 令牌经 Referer/日志外泄）。
       · 其余 /api/* 无需再手动带令牌头；cookie 自动携带。
       · 令牌错误/缺失 → /api/* 返回 401 → 弹登录门。
       · 服务器未配置令牌（本地联调）时 /api/* 正常放行、/api/login 返回 authRequired:false。
       · 登出：POST /api/logout → 清 cookie。
     前端不再持令牌明文，localStorage 不再用于令牌。
     ============================================================ */

  // 登录：校验令牌并种 cookie。返回 { ok, body }。
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
  // 登出：清 cookie（失败静默——UI 层照常弹门即可）。
  function logout() {
    return fetch("/api/logout", { method: "POST", credentials: "same-origin" }).catch(function () {});
  }

  // 图片字节 URL：cookie 同源自动携带，无需再把令牌拼进 ?t=（避免令牌进 URL / 日志 / Referer）。
  // 供 /api/images/{id} 缩略图/灯箱大图使用；/meta 走 api()。
  function imageUrl(imageId) {
    return "/api/images/" + encodeURIComponent(imageId);
  }

  /* ---------- 统一 fetch 封装：自动加令牌头 + 401 → 登录门 ---------- */
  // 401 处理：清空数据、停止轮询、弹登录门、给中文提示，绝不静默失败或白屏。
  // 抛出的错误带 .isAuth 标记，调用方的 .catch 只需照常 showToast（已弹门）。
  function handleUnauthorized() {
    stopPolling();
    clearCurrentData();
    showLoginGate("登录已失效或过期，请重新登录");
  }

  function api(path, options) {
    if (USE_MOCK) {
      // mock 分流：GET 无 body → mockGet；POST → mockPost
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
    // 管理鉴权走 HttpOnly cookie：credentials:same-origin 让浏览器自动携带，无需手动带令牌头。
    // 联调模式（服务器未配令牌）下 /api/* 照常放行；配了令牌但未登录/cookie 失效则 401 → 弹门。
    return fetch(path, {
      method: opts.method || "GET",
      credentials: "same-origin",
      headers: headers,
      body: opts.body
    }).then(function (r) {
      if (r.status === 401) {
        handleUnauthorized();
        var authErr = new Error("未授权（401）");
        authErr.isAuth = true;
        throw authErr;
      }
      if (!r.ok) throw new Error("HTTP " + r.status + " · " + path);
      return r.json();
    });
  }

  // 兼容旧调用点：apiGet / apiPost 现在只是 api() 的薄封装。
  function apiGet(path) { return api(path); }
  function apiPost(path, body) {
    return api(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  }

  /* ============================================================
     应用状态
     ============================================================ */
  var state = {
    exams: [],
    currentExamId: null,
    seats: [],
    suspicious: [],
    queueStatus: "pending",   // 队列过滤
    autoRefresh: true,
    pollTimer: null,
    inflight: false,          // 防止轮询叠加
    drawerMode: null,         // "seat" | "suspicious"
    collectAuthMode: null     // 采集面模式(psk/oidc/both):both 灰度期高亮 PSK 座位
  };

  var POLL_MS = 5000;

  /* ============================================================
     初始化
     ============================================================ */
  function init() {
    bindTopbar();
    bindQueueFilter();
    bindDrawer();
    bindLightbox();
    bindLoginGate();

    // ── 启动引导 ──────────────────────────────────────────────
    // mock 模式：直接进（不经真实 /api，不受鉴权影响，用于目视布局）。
    // 真实模式：首次加载先「不弹门」直接试拉数据——
    //   · 200（有令牌或联调放行）→ 直接用，登录门保持隐藏；
    //   · 401 → api() 里的 handleUnauthorized() 自动弹门要令牌。
    // 即使本地已存令牌也照此流程：让服务器裁决，而不是前端预判。
    hideLoginGate();
    // 取一次采集面模式(both 灰度期看板要高亮 PSK 座位);失败静默(默认不高亮)。
    fetch("/api/authmode", { credentials: "same-origin" }).then(function (r) { return r.json(); })
      .then(function (j) { if (j && j.collectAuthMode) state.collectAuthMode = j.collectAuthMode; }).catch(function () {});
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
    $("#autoRefresh").addEventListener("change", function (e) {
      state.autoRefresh = e.target.checked;
      restartPolling();
    });
    // 更换/退出令牌：清 cookie → 停轮询 → 清屏 → 弹登录门
    $("#logoutBtn").addEventListener("click", function () {
      logout();
      stopPolling();
      clearCurrentData();
      showLoginGate("请输入监考管理令牌");
    });
  }

  /* ============================================================
     登录门：显示/隐藏 + 表单提交
     ============================================================ */
  function showLoginGate(msg) {
    var hint = $("#loginHint");
    if (msg) {
      hint.textContent = msg;
      // 「无效/过期」类提示标红，普通提示保持弱色
      hint.classList.toggle("is-error", /无效|过期/.test(msg));
    }
    $("#loginGate").hidden = false;
    var input = $("#tokenInput");
    input.value = "";
    // M4·RBAC：据后端鉴权方式切换令牌输入 / cpplearn OIDC 登录按钮；token 模式才聚焦输入框。
    applyAuthMode().then(function (oidc) {
      if (!oidc) setTimeout(function () { try { input.focus(); } catch (e) {} }, 0);
    });
  }

  // M4·RBAC：探测管理鉴权方式（token / oidc），据此切换登录门 UI。返回 Promise<oidc:boolean>。
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
            hint.textContent = "用 cpplearn 账号登录，仅长老（监考员）可进入监考看板。";
        } else {
          if (form) form.hidden = false;
          if (btn) btn.hidden = true;
        }
        return oidc;
      })
      .catch(function () { return false; });   // 探测失败 → 保留默认令牌门
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
      // POST /api/login 校验并种 HttpOnly cookie；成功后 cookie 自动携带，直接拉数据。
      login(tok).then(function (res) {
        if (btn) btn.disabled = false;
        if (res.ok && (!res.body || res.body.ok !== false)) {
          hideLoginGate();
          loadExams();        // cookie 已种；若仍 401 会再次弹门
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

  // 停止轮询（401 / 退出令牌时用；restartPolling 会在有令牌数据后重启）
  function stopPolling() {
    if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
  }

  // 清空当前视图数据（401 / 切换令牌时用），避免残留旧数据误导
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
      loadSuspicious();  // 单独拉取（状态过滤走服务端参数）
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
  }

  /* ============================================================
     考试列表
     ============================================================ */
  function loadExams() {
    apiGet("/api/exams")
      .then(function (exams) {
        state.exams = Array.isArray(exams) ? exams : [];
        renderExamOptions();
        if (state.exams.length === 0) {
          showEmptyEverything("暂无考试，等待 Agent 上报");
          return;
        }
        // 默认选中第一个 active，否则第一个
        var pick = state.exams.filter(function (e) { return e.status === "active"; })[0]
          || state.exams[0];
        state.currentExamId = pick.examId;
        $("#examSelect").value = pick.examId;
        refreshCurrent();
        restartPolling();
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已弹登录门，无需再报错
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
    // 切换考试：清空旧数据 + 重置定时器，避免叠加
    state.seats = [];
    state.suspicious = [];
    closeDrawer();
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
     轮询：切换考试/开关时清理旧定时器
     ============================================================ */
  function restartPolling() {
    stopPolling();
    // 登录门开着时不启动轮询（避免无令牌轮询风暴）；登录成功后再由此处启动。
    if ($("#loginGate") && !$("#loginGate").hidden) return;
    if (state.autoRefresh && state.currentExamId) {
      state.pollTimer = setInterval(refreshCurrent, POLL_MS);
    }
  }

  // 拉取当前考试的 seats + suspicious（合并一次刷新）
  function refreshCurrent() {
    if (!state.currentExamId) return;
    if (state.inflight) return;  // 上一轮未回，不叠加
    state.inflight = true;

    var examId = state.currentExamId;
    Promise.all([
      apiGet("/api/exams/" + encodeURIComponent(examId) + "/seats"),
      apiGet("/api/exams/" + encodeURIComponent(examId) +
        "/suspicious?status=" + encodeURIComponent(state.queueStatus))
    ]).then(function (res) {
      if (examId !== state.currentExamId) return;  // 期间已切换
      state.seats = Array.isArray(res[0]) ? res[0] : [];
      state.suspicious = Array.isArray(res[1]) ? res[1] : [];
      renderSeats();
      renderQueue();
      updateOverview();
      $("#clock").textContent = fmtTime(Date.now() / 1000);
    }).catch(function (err) {
      if (err && err.isAuth) return;   // 401 已停轮询 + 弹登录门
      showToast("刷新失败：" + err.message);
      // 失败时不清屏，保留上一份可见数据（克制重试：等下一轮 5s）
    }).finally(function () {
      state.inflight = false;
    });
  }

  // 仅刷新可疑队列（切换状态过滤时用）
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
        if (err && err.isAuth) return;   // 401 已弹登录门
        showToast("加载可疑队列失败：" + err.message);
      });
  }

  /* ============================================================
     概览计数
     ============================================================ */
  function updateOverview() {
    var ex = state.exams.filter(function (e) { return e.examId === state.currentExamId; })[0];
    var seatCount = state.seats.length ||
      (ex ? ex.seatCount : 0) || 0;
    var onlineCount = state.seats.filter(function (s) { return s.online; }).length;
    // 待复核数：优先用 exam 汇总；无则从当前 seats 累加 suspiciousCount
    var pending = ex && typeof ex.pendingSuspicious === "number"
      ? ex.pendingSuspicious
      : state.seats.reduce(function (a, s) { return a + (s.suspiciousCount || 0); }, 0);
    setOverview({ seats: seatCount, online: onlineCount, pending: pending });
  }
  function setOverview(o) {
    $("#statSeats").textContent = o ? o.seats : "--";
    $("#statOnline").textContent = o ? o.online : "--";
    $("#statPending").textContent = o ? o.pending : "--";
  }

  /* ============================================================
     左：座位热力网格
     ============================================================ */
  // 配色规则：
  //   离线 → 灰；在线且 risk<40 且 suspiciousCount=0 → 绿；
  //   risk 40-69 → 琥珀；risk>=70 或 suspiciousCount>0 → 红（角标显示计数）
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

      // tooltip：agentId / machineId / 最后心跳 / 事件数
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
      // M5：采集健康告警标(异常重启/疑似挂起/遮屏/能力降级),提示监考员该座位采集可能被削弱。
      var healthTag = (s.healthAlerts || 0) > 0
        ? '<span title="采集健康告警" style="position:absolute;top:.3rem;left:.3rem;background:#f9e2af;color:#1e1e2e;'
          + 'font-size:.7rem;font-weight:700;border-radius:.4rem;padding:.05rem .3rem">⚠' + s.healthAlerts + '</span>'
        : "";
      // both 灰度期:仍走 PSK 的在线座位高亮(未迁移到 OIDC),切 oidc 前据此逐个确认。
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
     右：可疑复核队列
     ============================================================ */
  // 排序：score 降序，同分 ts 降序
  function sortedSuspicious() {
    return state.suspicious.slice().sort(function (a, b) {
      if ((b.score || 0) !== (a.score || 0)) return (b.score || 0) - (a.score || 0);
      return (b.ts || 0) - (a.ts || 0);
    });
  }

  function renderQueue() {
    var body = $("#queueBody");
    var empty = $("#queueEmpty");
    var list = sortedSuspicious();
    if (!list.length) {
      body.innerHTML = "";
      empty.hidden = false;
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

  /* ============================================================
     抽屉：打开/关闭
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

  /* ---------- 考前预检（M4 部署项）---------- */
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

  /* ---------- 可疑详情 ---------- */
  function openSuspiciousDrawer(item) {
    openDrawer("suspicious", "可疑详情");
    var m = kindMeta(item.kind);
    var body = $("#drawerBody");

    // refs 解析：event:* 直接展示文本；image:* 渲染证据图（可点击放大）
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
          // 直接展示 ref 文本；若已加载对应事件则附摘要（按契约无需单独请求）
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
      // 决策区
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

    // 证据图点击放大 + 加载失败占位(用 addEventListener 而非内联 onerror:CSP script-src 'self' 会屏蔽内联处理器)
    body.querySelectorAll(".evidence").forEach(function (ev) {
      ev.addEventListener("click", function () {
        openLightbox(imageUrl(ev.getAttribute("data-img")));
      });
      var img = ev.querySelector("img");
      if (img) img.addEventListener("error", function () {
        ev.innerHTML = '<div class="loading">图片加载失败</div>';
      });
    });

    // 决策表单交互：确认/驳回 → 展开表单 → 提交 POST
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
        loadSuspicious();     // 刷新队列
        refreshCurrent();     // 顺带刷新座位/概览
        showToast("已" + (status === "confirmed" ? "确认违规" : "驳回") + "：" + id);
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已弹登录门（抽屉已被清屏关闭）
        showToast("裁决提交失败：" + err.message);
      });
  }

  /* ---------- 座位详情 ---------- */
  function openSeatDrawer(s) {
    openDrawer("seat", "座位详情 · " + (s.seatId || ""));
    var body = $("#drawerBody");

    // M4·RBAC：OIDC 登录的 cpplearn 身份画像（未登录/PSK 模式 s.identity 为 null，不渲染这些行）。
    var id = s.identity;
    var idRows = id ? (
        "<dt>身份</dt><dd>" + (id.userType === "elder"
          ? "<span class='mono'>监考员（长老）</span>"
          : "<span class='mono'>参考学员（弟子）</span>") + "</dd>" +
        "<dt>账号</dt><dd>" + esc(dash(id.username)) + "</dd>" +
        (id.nickname ? "<dt>昵称</dt><dd>" + esc(id.nickname) + "</dd>" : "") +
        (id.daoName ? "<dt>道号</dt><dd>" + esc(id.daoName) + "</dd>" : "") +
        (id.realm ? "<dt>境界</dt><dd>" + esc(id.realm) +
          (id.realmLevel ? " · " + esc(String(id.realmLevel)) + " 层" : "") + "</dd>" : "") +
        "<dt>战力</dt><dd class='mono'>" + dash(id.combatPower) + "</dd>"
      ) : "";

    body.innerHTML =
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

    // 拉取该座位事件（limit=200，按 id 倒序，最新在前）
    var examId = state.currentExamId;
    apiGet("/api/exams/" + encodeURIComponent(examId) +
      "/events?seatId=" + encodeURIComponent(s.seatId) + "&limit=200")
      .then(function (events) {
        if (state.drawerMode !== "seat") return; // 抽屉已关或已切换
        renderTimeline(Array.isArray(events) ? events : []);
      })
      .catch(function (err) {
        if (err && err.isAuth) return;   // 401 已弹登录门（抽屉已被清屏关闭）
        var l = $("#tlLoading");
        if (l) l.textContent = "事件加载失败：" + err.message;
      });
  }

  // 事件 risk → 等级类
  function riskClass(risk) {
    var r = risk || 0;
    if (r >= 70) return "risk--high";
    if (r >= 40) return "risk--mid";
    return "risk--low";
  }

  // 按 type 生成关键 payload 摘要
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

      // 证据缩略图
      var thumb = "";
      if (ev.evidenceImageId) {
        thumb =
          '<img class="thumb" src="' + esc(imageUrl(ev.evidenceImageId)) +
          '" alt="证据缩略图" loading="lazy" data-img="' + esc(ev.evidenceImageId) + '" />';
      }

      // 有效风险 = max(Agent 自报, 服务器复判);着色用有效风险,避免"谎报 risk=0"在明细里显示成低危。
      var evAgent = (typeof ev.risk === "number") ? ev.risk : 0;
      var evServer = (typeof ev.serverRisk === "number") ? ev.serverRisk : 0;
      var evEff = Math.max(evAgent, evServer);
      // 服务器复判高于 Agent 自报 → 显式标注 "自报→服务器"(篡改逃逸取证提示)
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
          openLightbox(imageUrl(thumbEl.getAttribute("data-img")));
        });
        // 加载失败隐藏(addEventListener 而非内联 onerror:CSP 会屏蔽内联处理器)
        thumbEl.addEventListener("error", function () { thumbEl.style.display = "none"; });
      }
      tl.appendChild(item);
    });
  }

  /* ============================================================
     灯箱
     ============================================================ */
  function openLightbox(src) {
    $("#lightboxImg").src = src;
    $("#lightbox").hidden = false;
  }
  function closeLightbox() {
    $("#lightbox").hidden = true;
    $("#lightboxImg").src = "";
  }

  /* ============================================================
     ★ 内置 Mock 数据（结构与契约逐字段一致）
     覆盖：2 场考试、多种状态座位、若干可疑、若干事件
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

    // 按 examId 索引的可疑（含各状态，用于过滤演示）
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

  // 内联 1x1 WebP 占位图（mock 下证据图不发真实请求，避免 404）
  var MOCK_IMG_DATAURL =
    "data:image/svg+xml;utf8," +
    encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200">' +
      '<rect width="320" height="200" fill="#171c28"/>' +
      '<rect x="8" y="8" width="304" height="184" fill="none" stroke="#2a3346"/>' +
      '<text x="160" y="100" fill="#6b768c" font-family="monospace" font-size="14" ' +
      'text-anchor="middle">MOCK 证据图</text></svg>'
    );

  // Mock 路由：模拟 GET
  function mockGet(path) {
    return new Promise(function (resolve) {
      setTimeout(function () {
        // /api/images/{id}/meta —— 本前端不主动请求，兜底返回
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

        var mEv = path.match(/^\/api\/exams\/([^/]+)\/events/);
        if (mEv) {
          var sp2 = new URLSearchParams(path.split("?")[1] || "");
          var seatId = sp2.get("seatId");
          resolve((MOCK.events[seatId] || []).slice());
          return;
        }
        resolve([]);
      }, 120); // 模拟网络延迟
    });
  }

  // Mock 路由：模拟 POST decide
  function mockPost(path, body) {
    return new Promise(function (resolve) {
      setTimeout(function () {
        var m = path.match(/^\/api\/suspicious\/([^/]+)\/decide$/);
        if (!m) { resolve({ ok: false }); return; }
        var id = decodeURIComponent(m[1]);
        // 在所有考试里找到该条目并更新
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
        // 同步 pendingSuspicious 计数
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

  // Mock 模式下把证据图 <img src="/api/images/*"> 重定向到占位图
  if (USE_MOCK) {
    document.addEventListener("error", function (e) {
      var t = e.target;
      if (t && t.tagName === "IMG" && /\/api\/images\//.test(t.src) &&
          t.src.indexOf("data:") !== 0) {
        t.src = MOCK_IMG_DATAURL;
      }
    }, true);
    // 更直接：拦截 img.src 赋值不可行，这里用 MutationObserver 兜底替换
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

  /* ---------- 启动 ---------- */
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
