(() => {
  const host = window.location.hostname.toLowerCase();
  if (host !== "betaoi.cn" && !host.endsWith(".betaoi.cn")) return;

  const token = String(window.__HORUS_CLOUDFLARE_WEB_ANALYTICS_TOKEN || "").trim();
  if (!/^[0-9a-f]{32}$/i.test(token)) return;
  if (document.querySelector('script[src*="static.cloudflareinsights.com/beacon.min.js"]')) return;

  const beacon = document.createElement("script");
  beacon.type = "module";
  beacon.defer = true;
  beacon.src = "https://static.cloudflareinsights.com/beacon.min.js";
  beacon.dataset.cfBeacon = JSON.stringify({ token });
  document.head.append(beacon);
})();
