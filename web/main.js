/*
 * Horus 主頁的兩件事：下載連結、Cloudflare Web Analytics。
 * ★ 無建置鏈，瀏覽器直接跑這一份。
 */

/* ════════════════════════════════════════════════════════════════════════
 * ★★★ 下載位址 —— **要落 R2 連結就只改這一處**。
 *
 * owner 2026-08-27 定：產物放 Cloudflare R2（桶 `horus-dl`，自訂域 `dl.betaoi.cc`）。
 * ✅ **同日已上傳並接上** —— 三方核過：本機檔案 = 從 dl.betaoi.cc 真下下來的位元組
 * = 下面寫著的 sha256。⚠ 換產物時**三樣一起換**，別讓頁面上的摘要與檔案對不上。
 *
 * ★★ **`url` 回到 `null` 是「還沒有東西可下」的正確表達**（上線前就是這個狀態）：
 *   頁面會把按鈕顯式置成禁用並寫「即將開放」。
 *   **不要填佔位 URL** —— 一個填了值的假位址會讓按鈕看起來是好的，點下去才 404，
 *   而那時沒人分得清「連結配錯了」與「產物還沒上傳」。
 *
 * ★ `size` / `sha256` 同理：有就顯示，沒有就不顯示，**不編**。
 * ════════════════════════════════════════════════════════════════════════ */
const DOWNLOADS = {
  agent: {
    url: 'https://dl.betaoi.cc/horus/Horus.Agent.exe',
    file: 'Horus.Agent.exe',
    size: '158 MB',
    /** ★ 一個 158 MB 的 exe 值得給摘要 —— 拿到之後能自己核。 */
    sha256: 'a1bb0eb19cf1b1068593a5eada4c56d69fdf0170e0f87234a80bbfbb674d1390',
  },
  server: {
    url: 'https://dl.betaoi.cc/horus/Horus-Server.zip',
    file: 'Horus-Server.zip',
    size: '47 MB',
    sha256: '0d67cecf1f25910be005334735d1116940a15d03d8257231af5e40a124916581',
  },
};

/**
 * ★★ 這兩份產物是從**哪一版原始碼**建出來的 —— 寫在這裡，因為它救過一次：
 *   `dist/` 裡原先那兩個 exe 是 2026-07-03 建的，嵌著的預設 issuer 是 `https://betaoi.cc`，
 *   ★★★ **比 `f335b59` 訂正的那一版還早一代，而那個域現在根本不是貝塔通** ——
 *   Agent 是「零設定雙擊就跑」，嵌進去的預設值就是它實際會去連的位址。
 * ⇒ 換產物時**連這一行一起換**，別讓頁面上的說法與檔案對不上。
 */
const BUILT_FROM = 'f335b59'; // 最後一筆動過 C# 原始碼的提交（2026-08-26）

function applyDownloads() {
  for (const [key, info] of Object.entries(DOWNLOADS)) {
    const button = document.querySelector(`[data-download="${key}"]`);
    const meta = document.querySelector(`[data-meta="${key}"]`);
    if (button === null || meta === null) continue;

    if (info.url === null) {
      // ★ 語義與樣式各守一半：aria-disabled 給讀屏，CSS 給眼睛，preventDefault 給滑鼠。
      button.setAttribute('aria-disabled', 'true');
      button.removeAttribute('href');
      button.setAttribute('role', 'button');
      meta.textContent = '即将开放';
      continue;
    }

    button.href = info.url;
    button.removeAttribute('aria-disabled');
    /*
     * ★ 不加 `download` 屬性：產物在 R2（跨源），而跨源的 `download` 會被瀏覽器忽略，
     *   ⇒ 加了它只會讓人以為「已經指定了檔案名」，實際由回應頭說了算。
     */
    meta.textContent = `${info.file} · ${info.size} · 构建自 ${BUILT_FROM}`;
    if (typeof info.sha256 === 'string') {
      const sum = document.createElement('code');
      sum.className = 'card__sum';
      sum.textContent = `sha256 ${info.sha256}`;
      // ★ 摘要用 <code> 而不是塞進 meta 那一行：它要能被整段選中複製，
      //   而與檔案名擠在一行時人只會複製到半截。
      meta.after(sum);
    }
  }
}

/* ════════════════════════════════════════════════════════════════════════
 * Cloudflare Web Analytics。
 *
 * ★★ 判據與成均 `src/domain/platform/analytics.ts` 的 `shouldLoadBeacon` 同一條：
 *   **完整相等，不是 `endsWith('.betaoi.cn')`** —— 後者會把任何
 *   `xxx.betaoi.cn`（乃至別人控制的 `evil-hr.betaoi.cn`）也算進來。
 *
 * P147/P149 當前公用網路主頁為 hr.betaoi.cc，由 Cloudflare 自動注入，本原始碼不重複載入。
 *   ANALYTICS_HOST 保留未來 cn 的確切匹配；cn 站台當前休眠，不新增控制檯設定。
 *
 * ★★★ 本機監考部署（區域網路、IP、localhost）**必須一個位元組都不外聯** ——
 *   這是 AGENTS.md 裡寫死的：「本機監考部署不得產生新的分析外聯」。
 *   ⇒ 只放行那一個確切的公用網路主機名，其餘一律不載入。
 *
 * ─────────────────────────────────────────────────────────────────────────
 * ⏳ **2026-08-27 上線當天的已知狀態：beacon 指令碼載入得到，但回傳被 CORS 攔掉。**
 *
 *   線上實測控制檯：
 *     Access to XMLHttpRequest at 'https://cloudflareinsights.com/cdn-cgi/rum'
 *     from origin 'https://hr.betaoi.cn' has been blocked by CORS policy:
 *     No 'Access-Control-Allow-Origin' header is present on the requested resource.
 *
 *   ★ **不是 CSP** —— 站台塊的 `connect-src` 裡有 `https://cloudflareinsights.com`，
 *     且 CSP 攔截的回報錯誤文案是「Refused to connect」而不是 CORS。
 *   ★★ **最可能的原因是 `hr.betaoi.cn` 還沒登記進 Cloudflare Web Analytics 那個站台**
 *     （與 Turnstile 那邊「每專案一個、各自寫死精確主機名」是同一套做法）——
 *     ⚠ **但這一條沒有被證實**：拿 curl 偽造 Origin 去打那個端點，
 *     已登記的 `cj.betaoi.cn` 與隨便一個 `example.com` **回傳的都是 404**，
 *     ⇒ **那條探針對已知好與已知壞給出同一個答案，它什麼都證明不了。**
 *     真要判，得在瀏覽器裡用真實 beacon 請求打一次已登記的主機名做對照。
 *
 *   ★★★ **有意留著這段程式碼不關掉**：登記做上的那一刻它自己就好了，
 *     不需要誰記得回來改一行。★ 而在那之前，**控制檯那兩條回報錯誤就是「還沒登記」
 *     這件事唯一的、活的提示** —— 關掉它等於把提示也一起關掉，
 *     然後這條待辦就變成一句躺在交接單裡、沒人回頭核的話。
 * ═════════════════════════════════════════════════════════════════════════ */
const ANALYTICS_HOST = 'hr.betaoi.cn';
const CF_BEACON_SRC = 'https://static.cloudflareinsights.com/beacon.min.js';
const CF_ANALYTICS_TOKEN = 'c113fb69d7e84d38a645c5160f6f1bda';

export function shouldLoadBeacon(hostname) {
  return hostname.toLowerCase() === ANALYTICS_HOST;
}

function loadBeacon() {
  if (!shouldLoadBeacon(window.location.hostname)) return;
  const script = document.createElement('script');
  script.type = 'module';
  script.defer = true;
  script.src = CF_BEACON_SRC;
  script.dataset.cfBeacon = JSON.stringify({ token: CF_ANALYTICS_TOKEN });
  document.head.append(script);
}

applyDownloads();
loadBeacon();
