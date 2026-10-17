using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Api;
using Microsoft.Extensions.Logging;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Horus.Server.Ingest;
using Horus.Server.Jobs;
using System.Text.Json;

// ---- 雙擊維運 UX:未處理異常(fail-closed / 設定錯等)給友好中文提示並暫停,僅**真 exe**(非測試宿主)生效 ----
// 否則雙擊執行環境視窗"黑框一閃"即逝,拒絕啟動的原因根本看不見(真機驗收實際踩過)。
bool isRealHost = string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
    "Horus.Server", StringComparison.OrdinalIgnoreCase);
if (isRealHost)
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("[Horus] 启动失败:" + ((e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString()));
        if (Environment.UserInteractive && !Console.IsInputRedirected)
        {
            Console.Error.WriteLine("按回车键退出…");
            Console.ReadLine();
        }
        Environment.Exit(1);   // 已給出友好資訊,不再讓執行環境二次列印堆疊
    };
}

// ---- 金鑰加密工具:`Horus.Server protect-secret <明文key>` 列印 DPAPI 密文,粘進 config 的 visionApiKeyEnc(不存明文) ----
if (args.Length >= 1 && args[0] == "protect-secret")
{
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("protect-secret 仅 Windows(DPAPI)可用。"); return; }
    string plain = args.Length >= 2 ? args[1] : (Console.ReadLine() ?? "");
    Console.WriteLine(SecretProtect.Protect(plain));
    return;
}

// ---- 設定載入(JSON) + 環境變數覆蓋(便於測試/部署) ----
string cfgPath = Environment.GetEnvironmentVariable("HORUS_CONFIG")
                 ?? (args.Length > 0 ? args[0] : "server.config.json");
ServerConfig cfg = ServerConfig.Load(cfgPath);

// ---- 自動加密:設定檔案裡若填了明文金鑰,啟動時加密為對應 *Enc 欄位並清除明文(密文回寫) ----
// UX = 維運直接在設定欄輸入明文,啟動一次即自動加密寫入磁碟、明文不再存在。DPAPI 機器綁定,僅 Windows。
// 覆蓋三個金鑰:視覺 key + 採集 OIDC client_secret + 看板 dashboard client_secret。
// ★須在 env 覆蓋**之前**跑(只加密檔案裡的明文,絕不把 env 注入的秘密寫回磁碟)。逐個獨立回寫(互不影響)。
if (OperatingSystem.IsWindows() && File.Exists(cfgPath))
{
    // 回傳密文(成功)或 null(空/失敗)。失敗只告警不阻斷,本次仍用記憶體明文。
    string? Encrypt(string plainField, string encField, string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        try
        {
            string? enc = SecretProtect.EncryptSecretInFile(cfgPath, plainField, encField, plaintext!);
            if (enc is not null) { Console.WriteLine($"[Horus] 已将配置里的明文 {plainField} 加密为 {encField} 并回写(明文已清除)。"); return enc; }
            Console.Error.WriteLine($"[Horus] {plainField} 自动加密失败(配置非 JSON 对象);本次仍用内存明文,请检查配置。");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Horus] {plainField} 自动加密回写失败(如文件只读):{ex.Message}。本次仍用内存明文。"); }
        return null;
    }

    if (Encrypt("visionApiKey", "visionApiKeyEnc", cfg.VisionApiKey) is string ve)
        cfg = cfg with { VisionApiKeyEnc = ve, VisionApiKey = null };
    if (Encrypt("oidcClientSecret", "oidcClientSecretEnc", cfg.OidcClientSecret) is string oe)
        cfg = cfg with { OidcClientSecretEnc = oe, OidcClientSecret = null };
    if (Encrypt("oidcDashboardClientSecret", "oidcDashboardClientSecretEnc", cfg.OidcDashboardClientSecret) is string de)
        cfg = cfg with { OidcDashboardClientSecretEnc = de, OidcDashboardClientSecret = null };
}

cfg = cfg with
{
    DataDir = Environment.GetEnvironmentVariable("HORUS_DATADIR") ?? cfg.DataDir,
    DbPath = Environment.GetEnvironmentVariable("HORUS_DBPATH") ?? cfg.DbPath,
    ReadPoolSize = int.TryParse(Environment.GetEnvironmentVariable("HORUS_READ_POOL"), out int rp) && rp > 0 ? rp : cfg.ReadPoolSize,
    PskBase64 = Environment.GetEnvironmentVariable("HORUS_PSK_B64") ?? cfg.PskBase64,
    KeystrokeSecretBase64 = Environment.GetEnvironmentVariable("HORUS_KSK_B64") ?? cfg.KeystrokeSecretBase64,
    AdminToken = Environment.GetEnvironmentVariable("HORUS_ADMIN_TOKEN") ?? cfg.AdminToken,
    Urls = Environment.GetEnvironmentVariable("HORUS_URLS") ?? cfg.Urls,
    VisionProvider = Environment.GetEnvironmentVariable("HORUS_VISION_PROVIDER") ?? cfg.VisionProvider,
    VisionBaseUrl = Environment.GetEnvironmentVariable("HORUS_VISION_BASEURL") ?? cfg.VisionBaseUrl,
    VisionModel = Environment.GetEnvironmentVariable("HORUS_VISION_MODEL") ?? cfg.VisionModel,
    VisionApiKey = Environment.GetEnvironmentVariable("HORUS_VISION_KEY") ?? cfg.VisionApiKey,
    VisionConcurrency = int.TryParse(Environment.GetEnvironmentVariable("HORUS_VISION_CONCURRENCY"), out int vc) && vc > 0 ? vc : cfg.VisionConcurrency,
    // M3 按圖搜圖 embed env 覆蓋
    EmbedProvider = Environment.GetEnvironmentVariable("HORUS_EMBED_PROVIDER") ?? cfg.EmbedProvider,
    EmbedBaseUrl = Environment.GetEnvironmentVariable("HORUS_EMBED_BASEURL") ?? cfg.EmbedBaseUrl,
    EmbedModel = Environment.GetEnvironmentVariable("HORUS_EMBED_MODEL") ?? cfg.EmbedModel,
    // M4 身分層(OIDC)env 覆蓋
    AuthMode = Environment.GetEnvironmentVariable("HORUS_AUTH_MODE") ?? cfg.AuthMode,
    OidcIssuer = Environment.GetEnvironmentVariable("HORUS_OIDC_ISSUER") ?? cfg.OidcIssuer,
    OidcClientId = Environment.GetEnvironmentVariable("HORUS_OIDC_CLIENT_ID") ?? cfg.OidcClientId,
    OidcClientSecret = Environment.GetEnvironmentVariable("HORUS_OIDC_SECRET") ?? cfg.OidcClientSecret,
    OidcJwksJson = Environment.GetEnvironmentVariable("HORUS_OIDC_JWKS") ?? cfg.OidcJwksJson,
    // M4·RBAC 監考員看板 OIDC 登入(dashboard client)env 覆蓋
    AdminAuthMode = Environment.GetEnvironmentVariable("HORUS_ADMIN_AUTH_MODE") ?? cfg.AdminAuthMode,
    OidcDashboardClientId = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_CLIENT_ID") ?? cfg.OidcDashboardClientId,
    OidcDashboardClientSecret = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_SECRET") ?? cfg.OidcDashboardClientSecret,
    OidcDashboardRedirectUri = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_REDIRECT") ?? cfg.OidcDashboardRedirectUri,
    // 是否期待收到貝塔通的撤權廣播/探活(貝塔通 P101:Horus 有意不接)。只認顯式的 "true"。
    BetapassRevokePushExpected =
        string.Equals(Environment.GetEnvironmentVariable("HORUS_BETAPASS_REVOKE_PUSH"), "true", StringComparison.OrdinalIgnoreCase)
            || cfg.BetapassRevokePushExpected,
    // 探活 aud 的舊口徑逃生口:只認顯式的 "true"(誤拼當沒設,不至於把舊口徑悄悄放回來)。
    OidcHealthAudienceAcceptLegacy =
        string.Equals(Environment.GetEnvironmentVariable("HORUS_OIDC_HEALTH_LEGACY_AUD"), "true", StringComparison.OrdinalIgnoreCase)
            || cfg.OidcHealthAudienceAcceptLegacy,
    CloudflareWebAnalyticsToken = Environment.GetEnvironmentVariable("HORUS_CLOUDFLARE_WEB_ANALYTICS_TOKEN") ?? cfg.CloudflareWebAnalyticsToken,
};

// ---- 診斷:`Horus.Server probe-embed [model]` —— 重複使用視覺 key 探測 embeddings 端點(部署前驗證按圖搜圖可用性,不寫入資料庫不起服務) ----
if (args.Contains("probe-embed"))
{
    string baseUrl = (cfg.EmbedBaseUrlEffective ?? "").TrimEnd('/');
    string key = SecretProtect.Resolve(cfg);   // 重複使用視覺 key(env>enc(DPAPI)>明文)
    int mi = Array.IndexOf(args, "probe-embed");
    string model = mi + 1 < args.Length ? args[mi + 1] : (cfg.EmbedModel ?? cfg.VisionModel ?? "");
    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    Console.WriteLine($"[probe-embed] baseUrl={baseUrl} model={model} keyLen={key.Length}");
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    if (!string.IsNullOrEmpty(key)) probe.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
    try
    {
        using var mr = await probe.GetAsync(baseUrl + "/models");
        Console.WriteLine($"[probe-embed] GET /models → {(int)mr.StatusCode}: {Trunc(await mr.Content.ReadAsStringAsync(), 1000)}");
    }
    catch (Exception ex) { Console.WriteLine($"[probe-embed] GET /models 失败: {ex.Message}"); }
    // 生成一張 64×64 小圖 → webp → data URI(探測圖像 embedding)
    byte[] img;
    using (var im = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64,
               new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 40, 40, 255)))
    using (var ms = new MemoryStream()) { im.Save(ms, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder()); img = ms.ToArray(); }
    string dataUri = "data:image/webp;base64," + Convert.ToBase64String(img);
    string reqJson = System.Text.Json.JsonSerializer.Serialize(new { model, input = new[] { dataUri } });
    try
    {
        using var er = await probe.PostAsync(baseUrl + "/embeddings", new StringContent(reqJson, System.Text.Encoding.UTF8, "application/json"));
        string eb = await er.Content.ReadAsStringAsync();
        float[]? v = Horus.Server.Analysis.Search.OpenAiImageEmbedder.ParseEmbedding(eb);
        Console.WriteLine($"[probe-embed] POST /embeddings(图像) → {(int)er.StatusCode}: {Trunc(eb, 1500)}");
        Console.WriteLine(v is not null ? $"[probe-embed] ✅ 解析出向量维度={v.Length}(图像 embedding 可用)" : "[probe-embed] ❌ 未解析出 embedding 向量(端点无图像 embedding 模型?)");
    }
    catch (Exception ex) { Console.WriteLine($"[probe-embed] POST /embeddings 失败: {ex.Message}"); }
    return;
}

// ---- 解析資料目錄與 DB 資料源 ----
string dataDir = Path.GetFullPath(cfg.DataDir);
Directory.CreateDirectory(dataDir);
string dataSource = cfg.DbPath == ":memory:"
    ? ":memory:"
    : Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(dataDir, cfg.DbPath);

// 啟動期檢核金鑰 base64 合法(否則執行期每請求訪問 Psk/Ksk 會拋 FormatException → 500,難排查):此處 fail-fast 給清晰錯誤。
try { _ = cfg.Psk; _ = cfg.Ksk; }
catch (FormatException)
{
    throw new InvalidOperationException("pskBase64 / keystrokeSecretBase64 不是合法 base64(长度须 4 的倍数、仅 A-Za-z0-9+/= )。请检查配置。");
}

// Fail-closed:非 loopback 綁定卻缺 PSK / 管理權杖 = 採集或管理面裸奔,拒絕啟動(allowInsecure 僅整合測試可繞)。
string[] urls = cfg.Urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool lanExposed = urls.Any(u =>
{
    try { string h = new Uri(u).Host; return h is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"); }
    catch { return true; }
});
// 採集面安全 = 配了 PSK **或** OIDC(M4:oidc/both 模式憑 OIDC 工作階段鑑權,無需 PSK);管理面安全 = 配了 AdminToken。
bool collectInsecure = !cfg.AuthEnabled && !cfg.OidcEnabled;
if (lanExposed && !cfg.AllowInsecure && (collectInsecure || !cfg.AdminAuthEnabled))
    throw new InvalidOperationException(
        "拒绝启动:绑定了非本机地址却未配置采集面鉴权(PSK 或 OIDC)或 AdminToken(采集/管理面将裸奔)。请配置,或仅联调时设 allowInsecure=true。");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(urls);
// M4·RBAC·S8:Urls 含 https 時(監考員遠端 OIDC 回呼需 https)自簽證書;僅 Kestrel 真跑時生效(測試走 TestServer 不觸發)。
bool httpsBound = urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
System.Security.Cryptography.X509Certificates.X509Certificate2? httpsCert =
    httpsBound ? HttpsCert.LoadOrCreate(cfg, dataDir) : null;
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;   // 圖片體上限 2MB(1080p webp~150KB),防放大 DoS
    if (httpsCert is not null) o.ConfigureHttpsDefaults(h => h.ServerCertificate = httpsCert);
});

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Db(dataSource, cfg.ReadPoolSize));
builder.Services.AddSingleton(new Storage(dataDir));
builder.Services.AddSingleton<AgentHub>();          // 線上 Agent 註冊表(config_update 下推)
builder.Services.AddSingleton<EventIngest>();
builder.Services.AddSingleton<ImageIngest>();
builder.Services.AddSingleton<KeystrokeIngest>();

// ---- 視覺分析(L2:視覺 LLM 取代 OCR + L3 Logo)。provider-agnostic:mock / OpenAI 相容(DeepSeek/MiMo/Qwen/GLM) ----
if (cfg.VisionEnabled)
{
    if (string.Equals(cfg.VisionProvider, "openai", StringComparison.OrdinalIgnoreCase))
    {
        string vBaseUrl = cfg.VisionBaseUrl ?? throw new InvalidOperationException("visionProvider=openai 需配 visionBaseUrl");
        string vModel = cfg.VisionModel ?? throw new InvalidOperationException("visionProvider=openai 需配 visionModel");
        string vKey = SecretProtect.Resolve(cfg);   // env 明文 > visionApiKeyEnc(DPAPI 解密) > visionApiKey 明文
        builder.Services.AddSingleton<IVisionAnalyzer>(sp => new OpenAiCompatibleVisionAnalyzer(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, vBaseUrl, vModel, vKey,
            sp.GetRequiredService<ILogger<OpenAiCompatibleVisionAnalyzer>>()));   // 注入 logger:端點異常可見
    }
    else
        builder.Services.AddSingleton<IVisionAnalyzer>(new MockVisionAnalyzer());
}
builder.Services.AddSingleton<VisionAnalysisService>();               // 未註冊 IVisionAnalyzer 時內部 no-op
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionAnalysisService>());

// ---- M3 CLIP 按圖搜圖:圖像嵌入器(provider-agnostic:mock / OpenAI 相容·重複使用視覺 baseUrl/key)+ 後台補嵌 ----
builder.Services.AddSingleton<Horus.Server.Analysis.Search.ImageSearchStore>();   // 檢索始終可用(有 embedding 就能搜)
if (cfg.EmbedEnabled)
{
    if (string.Equals(cfg.EmbedProvider, "openai", StringComparison.OrdinalIgnoreCase))
    {
        string eEndpoint = cfg.EmbedEmbeddingsEndpoint ?? throw new InvalidOperationException("embedProvider=openai 需配 embedBaseUrl 或 visionBaseUrl");
        string eModel = cfg.EmbedModel ?? throw new InvalidOperationException("embedProvider=openai 需配 embedModel");
        // key:env > embedApiKey/Enc > **重複使用視覺 key(KEY一致)**
        string eKey = Environment.GetEnvironmentVariable("HORUS_EMBED_KEY") is { Length: > 0 } ek ? ek
            : !string.IsNullOrEmpty(cfg.EmbedApiKey) ? cfg.EmbedApiKey!
            : !string.IsNullOrEmpty(cfg.EmbedApiKeyEnc)
                ? (OperatingSystem.IsWindows() ? SecretProtect.Unprotect(cfg.EmbedApiKeyEnc!) : throw new PlatformNotSupportedException("embedApiKeyEnc 仅 Windows"))
                : SecretProtect.Resolve(cfg);   // 重複使用視覺 key
        builder.Services.AddSingleton<Horus.Server.Analysis.Search.IImageEmbedder>(sp =>
            new Horus.Server.Analysis.Search.OpenAiImageEmbedder(
                new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, eEndpoint, eModel, eKey, cfg.EmbedDim, cfg,
                sp.GetRequiredService<ILogger<Horus.Server.Analysis.Search.OpenAiImageEmbedder>>()));
    }
    else if (string.Equals(cfg.EmbedProvider, "onnx", StringComparison.OrdinalIgnoreCase))
    {
        // 本機 ONNX CLIP(MiMo 無 embeddings 端點·走本機·零出網)。模型部署提供。
        // ★約定檔案名 = model.onnx(與 HF 倉 Qdrant/clip-ViT-B-32-vision 原名一致,下載免改名):
        //   embedOnnxModelPath 留空 → 預設找 dataDir\model.onnx;也可顯式指定相對(按 dataDir)或絕對路徑。
        string modelPath = string.IsNullOrWhiteSpace(cfg.EmbedOnnxModelPath) ? "model.onnx" : cfg.EmbedOnnxModelPath!;
        string modelFull = Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(dataDir, modelPath);
        if (!File.Exists(modelFull))
            throw new InvalidOperationException($"ONNX CLIP 模型不存在:{modelFull}(约定:下载 model.onnx 原名放进 dataDir;或用 embedOnnxModelPath 指定路径)");
        builder.Services.AddSingleton<Horus.Server.Analysis.Search.IImageEmbedder>(sp =>
            new Horus.Server.Analysis.Search.OnnxClipEmbedder(
                modelFull, cfg.EmbedOnnxInput, cfg.EmbedOnnxOutput, cfg.EmbedDim,
                sp.GetRequiredService<ILogger<Horus.Server.Analysis.Search.OnnxClipEmbedder>>()));
    }
    else
        builder.Services.AddSingleton<Horus.Server.Analysis.Search.IImageEmbedder>(new Horus.Server.Analysis.Search.MockImageEmbedder(cfg.EmbedDim));
}
builder.Services.AddSingleton<Horus.Server.Analysis.Search.ImageEmbedService>();   // 未註冊嵌入器時 no-op
builder.Services.AddHostedService(sp => sp.GetRequiredService<Horus.Server.Analysis.Search.ImageEmbedService>());

// ---- M4 身分層:OIDC 採集工作階段(取代共享 PSK)。AuthMode=oidc/both 時啟用 ----
builder.Services.AddSingleton<Horus.Server.Identity.SessionStore>();   // both/oidc 下 ingest 會查工作階段
// 探活的**實際結果**(不是設定):預檢據此報「探活來過沒有 / 通過沒有」。
// ★ 無條件註冊 —— 預檢在兩條 OIDC 連線路徑都沒開時也會讀它,而那時它如實報「從未探過」。
builder.Services.AddSingleton<Horus.Server.Identity.BetapassProbeState>();

// 撤權通知的驗籤器:要按**兩個** client_id 分別試 aud(採集面 / 監考臺),而 OidcTokenValidator
// 建構時綁死單一 aud,故按候選各備一個。★ **單例** —— 每次請求現 new 等於每一發通知都重新
// 解析 JWKS 並 RSA.Create 若干把,而那些句柄從不 Dispose;端點頻率低不會立刻出事,
// 正因如此也不會有人來修。
// ★ 與驗 id_token 共用同一套公鑰 —— 貝塔通就是同一套簽名金鑰,零新增金鑰材料(其 P44 的判據)。
if (cfg.OidcEnabled || cfg.DashboardOidcEnabled)
{
    string revokeJwks = await Horus.Server.Identity.OidcJwks.LoadAsync(cfg);
    builder.Services.AddSingleton(new Horus.Server.Identity.BetapassRevokeVerifier(revokeJwks, cfg));
}
if (cfg.OidcEnabled)
{
    if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("authMode=oidc/both 需配 oidcIssuer");
    string oidcClientId = cfg.OidcClientId ?? throw new InvalidOperationException("authMode=oidc/both 需配 oidcClientId");
    string oidcSecret = Horus.Server.Identity.OidcSecret.Resolve(cfg);   // env > enc(DPAPI) > 明文
    string jwksJson = await Horus.Server.Identity.OidcJwks.LoadAsync(cfg);   // 內聯優先;否則從 issuer 拉取 + 快取
    var validator = new Horus.Server.Identity.OidcTokenValidator(jwksJson, cfg.OidcIssuer!, oidcClientId);
    builder.Services.AddSingleton(validator);
    builder.Services.AddSingleton(sp => new Horus.Server.Identity.OidcExchange(
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, validator,
        sp.GetRequiredService<Horus.Server.Identity.SessionStore>(), cfg, oidcSecret,
        sp.GetRequiredService<Db>(),   // 考試派發:exchange 需查當前活躍考試
        sp.GetRequiredService<ILogger<Horus.Server.Identity.OidcExchange>>()));
}

// ---- M4·RBAC:監考員看板 OIDC 登入(wentian dashboard web client·取代靜態 adminToken)。AdminAuthMode=oidc 時啟用 ----
builder.Services.AddSingleton<Horus.Server.Identity.AdminSessionStore>();   // gate 檢核管理工作階段(oidc 模式)
if (cfg.DashboardOidcEnabled)
{
    if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcIssuer");
    string dashClientId = cfg.OidcDashboardClientId ?? throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcDashboardClientId");
    if (string.IsNullOrEmpty(cfg.OidcDashboardRedirectUri))
        throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcDashboardRedirectUri(须与 wentian 注册的一致,如 https://<服务器>/cb)");
    string dashSecret = Horus.Server.Identity.OidcSecret.ResolveDashboard(cfg);
    string dashJwks = await Horus.Server.Identity.OidcJwks.LoadAsync(cfg);           // 與採集面共用 issuer 的 JWKS
    var dashValidator = new Horus.Server.Identity.OidcTokenValidator(dashJwks, cfg.OidcIssuer!, dashClientId);   // aud=dashboard client
    builder.Services.AddSingleton(sp => new Horus.Server.Identity.AdminOidcFlow(
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, dashValidator,
        sp.GetRequiredService<Horus.Server.Identity.AdminSessionStore>(), cfg, dashSecret,
        sp.GetRequiredService<ILogger<Horus.Server.Identity.AdminOidcFlow>>()));
}

// ---- M3 歸檔 / 清理作業:每日掃描到齡考試轉 archive 庫 + 清理 live(§13/§15)。ArchiveEnabled=false 時不起後台 ----
builder.Services.AddSingleton<ArchiveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArchiveService>());

WebApplication app = builder.Build();

// 全域異常備援:任何未被端點捕獲的異常統一回傳 JSON 500(生產環境預設不洩堆疊),避免空體 500 / 契約漂移。
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
}));

app.UseWebSockets();

// ---- 安全回應頭(所有回應):CSP 收緊指令碼/樣式來源(防 XSS 注入外鏈外發資料),附 nosniff / DENY / no-referrer ----
app.Use(async (ctx, next) =>
{
    IHeaderDictionary h = ctx.Response.Headers;
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' https://static.cloudflareinsights.com; " +
        "connect-src 'self' https://challenges.cloudflare.com https://cloudflareinsights.com; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    // no-store 覆蓋**所有**回應(不止圖片端點):`?t=` 後備權杖若進 URL,任何帶權杖的回應都不落磁碟快取/Referer(閉合第三輪 F8)。
    h["Cache-Control"] = "no-store";
    await next();
});

// ---- 管理/看板鑑權:所有 /api/*(除 /api/login、/api/logout)需帶憑證。三選一:----
//   ① HttpOnly cookie `horus_admin`(瀏覽器;JS 讀不到 → 防 XSS 竊取,<img> 同源自動攜帶 → 不落 URL)——首選
//   ② X-Horus-Admin 頭(curl / 指令碼用戶端)
//   ③ ?t= 查詢(向後相容;UI 已不再使用,避免權杖進 URL / 紀錄 / Referer)
// 關閉學員機"用 config 下發關掉全場檢測 / 拉全班證據圖 / 抹自己可疑裁決"等路徑。未配權杖則放行(僅整合測試)。
if (cfg.AdminAuthEnabled)
{
    AdminSessionStore adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
    app.Use(async (ctx, next) =>
    {
        PathString p = ctx.Request.Path;
        bool exempt = p.StartsWithSegments("/api/login") || p.StartsWithSegments("/api/logout")
                      || p.StartsWithSegments("/api/authmode");   // 公開:前端探測 token/oidc 登入方式
        if (p.StartsWithSegments("/api") && !exempt)
        {
            bool ok;
            if (cfg.DashboardOidcEnabled)
            {
                // M4·RBAC(R3):僅認貝塔通 OIDC 管理工作階段(HttpOnly cookie),**無靜態權杖後門**。
                string sid = ctx.Request.Cookies["horus_admin"] ?? "";
                double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                AdminSession? s = adminSessions.Get(sid, now);
                // ★ 三道門(revoked → absolute → idle → heartbeat)都沒到點才放行。
                //   「是不是監考員」由貝塔通在授權階段回答(平台 `horus-admin`,其 P83) ——
                //   換不到 code 就建不了這個工作階段,所以本表裡的每一行都是監考員。
                // ★★ **這裡唯讀不寫。** 任何業務請求都不得續 idle(貝塔通 P92 翻案 P25):
                //   看板是持續自動輪詢的,在這裡順手 touch 一下時間戳,idle 那道門就**永遠不會到點**,
                //   監考機上開著看板等於永不登出。續期只發生在 `POST /api/heartbeat` 那一個入口。
                ok = s is not null;
            }
            else
            {
                // token 模式(預設·M1-M3 原樣):靜態 adminToken 三選一憑證(cookie / 頭 / ?t=)。
                string got = ctx.Request.Cookies["horus_admin"] ?? "";
                if (got.Length == 0) got = ctx.Request.Headers["X-Horus-Admin"].ToString();
                if (got.Length == 0) got = ctx.Request.Query["t"].ToString();
                ok = Crypto.FixedTimeEquals(got, cfg.AdminToken!);
            }
            if (!ok)
            {
                // ★ 401 順帶告訴前端**為什麼** —— 撤權是刪行,原因不留痕就永遠只能顯示
                //   一句籠統的「登入已失效」。「平台權限被關掉」尤其要說清楚:取消 SSO 後
                //   每點一次登入都要完整輸一遍密碼才被拒(貝塔通 P98),不說真話他會一遍遍白輸。
                //   ★ 查不到留痕(自然過期 / 三道門到點)時 message 為 null,前端退回原來那句。
                string? why = cfg.DashboardOidcEnabled
                    ? Horus.Server.Identity.RevocationNotices.Lookup(
                        app.Services.GetRequiredService<Db>(), ctx.Request.Cookies["horus_admin"] ?? "")
                    : null;
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "unauthorized",
                    reason = why,
                    message = why is null ? null : Horus.Server.Identity.RevocationNotices.Message(why),
                });
                return;
            }
        }
        await next();
    });
}

// ---- 採集端通道(Agent ↔ Server) ----
app.MapGet("/ingest/events", (HttpContext ctx, EventIngest h) => h.HandleAsync(ctx));      // WebSocket
app.MapPost("/ingest/images", (HttpContext ctx, ImageIngest h) => h.HandleAsync(ctx));      // HTTP 圖片
app.MapPost("/ingest/keystroke", (HttpContext ctx, KeystrokeIngest h) => h.HandleAsync(ctx)); // HTTP 擊鍵旁路

// ---- M4 身分層:OIDC 登入換工作階段(採集面·由一次性 code+PKCE 保護·不走 admin gate) ----
app.MapOidc();

// ---- M4·RBAC:監考員看板 OIDC 登入(/admin/login + /cb·非 /api,不受 admin gate) ----
app.MapAdminOidc();

// ---- 貝塔通撤權通知接收端(其 P44/P69·rp-contract)。非 /api,不受 admin gate ----
// ★ **必須始終掛上**:貝塔通判成功的口徑是 2xx,而 404/405 與 5xx 一樣會被重投
//   (「RP 還沒實作這個端點」正是最需要重投的情形)。未開 OIDC 時端點自己回 410「別再投了」,
//   而不是讓它落到 404 去被反覆敲。
Horus.Server.Identity.BetapassRevokeEndpoint.Map(app);

// ---- 貝塔通存活探測接收端(其 P94·rp-contract「必辦項」)。非 /api,不受 admin gate ----
// ★ 為什麼實作它:契約列為必辦項 + 對側判據改回去的那一刻沒實作的站會立刻失效且無症狀。
//   ★★ **「不實作 = 撤權通知永遠送不到」那條舊理由已不成立**,見 BetapassHealthEndpoint 類註解。
// 同樣必須始終掛上(理由同上一條)。
Horus.Server.Identity.BetapassHealthEndpoint.Map(app);

// ---- 看板 / 管理 API ----
app.MapApi();

// ---- 靜態看板(wwwroot 單頁) ----
// 公開 token 通過同源指令碼設定下發；loader 僅在 betaoi.cn 主機載入 beacon，LAN/IP 保持零分析外聯。
app.MapGet("/cloudflare-web-analytics-config.js", () => Results.Text(
    "window.__HORUS_CLOUDFLARE_WEB_ANALYTICS_TOKEN=" +
    JsonSerializer.Serialize(cfg.CloudflareWebAnalyticsToken ?? "") + ";",
    "application/javascript; charset=utf-8"));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Horus 监考服务器启动 db={Db} dataDir={Dir} 采集鉴权={Auth} 管理鉴权={Admin} 阈值={Th}",
    dataSource, dataDir, cfg.AuthEnabled ? "开" : "关(仅联调)", cfg.AdminAuthEnabled ? "开" : "关(仅联调)", cfg.RiskThreshold);

// ---- 啟動成功自動打開管理端看板(雙擊維運 UX·openDashboard 可關)。僅 Windows 交互式真 exe;測試宿主/輸出重定向/服務化不彈 ----
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!cfg.OpenDashboard || !isRealHost || !OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
    if (Console.IsOutputRedirected) return;   // 指令碼/無人值守執行不彈
    try
    {
        string? url = app.Urls
            .Select(u => u.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("//+", "//127.0.0.1").Replace("//*", "//127.0.0.1"))
            .OrderBy(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 1 : 0)   // http 優先:自籤 https 首開有瀏覽器告警
            .FirstOrDefault();
        if (string.IsNullOrEmpty(url)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        app.Logger.LogInformation("已自动打开管理端看板 {Url}(配置 openDashboard=false 可关闭)", url);
    }
    catch { /* 打不開瀏覽器不影響服務 */ }
});

app.Run();

// WebApplicationFactory<Program> 測試入口需要可見的 Program 類
public partial class Program { }
