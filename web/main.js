/*
 * Horus 主页的两件事：下载链接、Cloudflare Web Analytics。
 * ★ 无构建链，浏览器直接跑这一份。
 */

/* ════════════════════════════════════════════════════════════════════════
 * ★★★ 下载地址 —— **要落 R2 链接就只改这一处**。
 *
 * owner 2026-08-27 定：产物放 Cloudflare R2。在那之前 `url` 保持 `null`，
 * 页面把按钮显式置成禁用并写「即将开放」。
 *
 * ★★ **`null` 是有意的，不要填一个占位 URL**：一个填了值的假地址会让按钮
 *   看起来是好的，点下去才 404 —— 而那时没人分得清「链接配错了」与
 *   「产物还没上传」。`null` 让「还没有东西可下」这件事在页面上直接看得见。
 *
 * ★ `version` / `size` 同理：有就显示，没有就不显示，**不编**。
 * ════════════════════════════════════════════════════════════════════════ */
const DOWNLOADS = {
  agent: {
    /** @type {string | null} R2 上的 Horus.Agent.exe。★ 填它就是全部改动。 */
    url: null,
    file: 'Horus.Agent.exe',
    size: '158 MB',
    /** ★ 一个 158 MB 的 exe 值得给摘要 —— 拿到之后能自己核。 */
    sha256: 'a1bb0eb19cf1b1068593a5eada4c56d69fdf0170e0f87234a80bbfbb674d1390',
  },
  server: {
    /** @type {string | null} R2 上的 Horus-Server.zip */
    url: null,
    file: 'Horus-Server.zip',
    size: '47 MB',
    sha256: '14443e9366230b755693db8266d45cd4590c4502defc33aa54f08fbe530fcb8b',
  },
};

/**
 * ★★ 这两份产物是从**哪一版源码**建出来的 —— 写在这里，因为它救过一次：
 *   `dist/` 里原先那两个 exe 是 2026-07-03 建的，嵌着的默认 issuer 是 `https://betaoi.cc`，
 *   ★★★ **比 `f335b59` 订正的那一版还早一代，而那个域现在根本不是贝塔通** ——
 *   Agent 是「零配置双击就跑」，嵌进去的默认值就是它实际会去连的地址。
 * ⇒ 换产物时**连这一行一起换**，别让页面上的说法与文件对不上。
 */
const BUILT_FROM = 'f335b59'; // 最后一笔动过 C# 源码的提交（2026-08-26）

function applyDownloads() {
  for (const [key, info] of Object.entries(DOWNLOADS)) {
    const button = document.querySelector(`[data-download="${key}"]`);
    const meta = document.querySelector(`[data-meta="${key}"]`);
    if (button === null || meta === null) continue;

    if (info.url === null) {
      // ★ 语义与样式各守一半：aria-disabled 给读屏，CSS 给眼睛，preventDefault 给鼠标。
      button.setAttribute('aria-disabled', 'true');
      button.removeAttribute('href');
      button.setAttribute('role', 'button');
      meta.textContent = '即将开放';
      continue;
    }

    button.href = info.url;
    button.removeAttribute('aria-disabled');
    /*
     * ★ 不加 `download` 属性：产物在 R2（跨源），而跨源的 `download` 会被浏览器忽略，
     *   ⇒ 加了它只会让人以为「已经指定了文件名」，实际由响应头说了算。
     */
    meta.textContent = `${info.file} · ${info.size} · 构建自 ${BUILT_FROM}`;
    if (typeof info.sha256 === 'string') {
      const sum = document.createElement('code');
      sum.className = 'card__sum';
      sum.textContent = `sha256 ${info.sha256}`;
      // ★ 摘要用 <code> 而不是塞进 meta 那一行：它要能被整段选中复制，
      //   而与文件名挤在一行时人只会复制到半截。
      meta.after(sum);
    }
  }
}

/* ════════════════════════════════════════════════════════════════════════
 * Cloudflare Web Analytics。
 *
 * ★★ 判据与成均 `src/domain/platform/analytics.ts` 的 `shouldLoadBeacon` 同一条：
 *   **完整相等，不是 `endsWith('.betaoi.cn')`** —— 后者会把任何
 *   `xxx.betaoi.cn`（乃至别人控制的 `evil-hr.betaoi.cn`）也算进来。
 *
 * ★ Horus 在 `PLAN.md` §3.9 里**没有备用域**（只有 `hr.betaoi.cn`），
 *   因此这里不存在「.cc 由 Cloudflare 自动注入、源码不得重复加载」那一档。
 *   ⚠ 哪天给它开了 `.cc`，这一行要跟着改：那时手工加载就是一页两个 beacon。
 *
 * ★★★ 本机监考部署（局域网、IP、localhost）**必须一个字节都不外联** ——
 *   这是 AGENTS.md 里写死的：「本地监考部署不得产生新的分析外联」。
 *   ⇒ 只放行那一个确切的公网主机名，其余一律不加载。
 *
 * ─────────────────────────────────────────────────────────────────────────
 * ⏳ **2026-08-27 上线当天的已知状态：beacon 脚本加载得到，但回传被 CORS 拦掉。**
 *
 *   线上实测控制台：
 *     Access to XMLHttpRequest at 'https://cloudflareinsights.com/cdn-cgi/rum'
 *     from origin 'https://hr.betaoi.cn' has been blocked by CORS policy:
 *     No 'Access-Control-Allow-Origin' header is present on the requested resource.
 *
 *   ★ **不是 CSP** —— 站点块的 `connect-src` 里有 `https://cloudflareinsights.com`，
 *     且 CSP 拦截的报错文案是「Refused to connect」而不是 CORS。
 *   ★★ **最可能的原因是 `hr.betaoi.cn` 还没登记进 Cloudflare Web Analytics 那个站点**
 *     （与 Turnstile 那边「每项目一个、各自写死精确主机名」是同一套做法）——
 *     ⚠ **但这一条没有被证实**：拿 curl 伪造 Origin 去打那个端点，
 *     已登记的 `cj.betaoi.cn` 与随便一个 `example.com` **返回的都是 404**，
 *     ⇒ **那条探针对已知好与已知坏给出同一个答案，它什么都证明不了。**
 *     真要判，得在浏览器里用真实 beacon 请求打一次已登记的主机名做对照。
 *
 *   ★★★ **有意留着这段代码不关掉**：登记做上的那一刻它自己就好了，
 *     不需要谁记得回来改一行。★ 而在那之前，**控制台那两条报错就是「还没登记」
 *     这件事唯一的、活的提示** —— 关掉它等于把提示也一起关掉，
 *     然后这条待办就变成一句躺在交接单里、没人回头核的话。
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
