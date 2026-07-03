using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Api;
using Microsoft.Extensions.Logging;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Horus.Server.Ingest;
using Horus.Server.Jobs;

// ---- 双击运维 UX:未处理异常(fail-closed / 配置错等)给友好中文提示并暂停,仅**真 exe**(非测试宿主)生效 ----
// 否则双击运行时窗口"黑框一闪"即逝,拒绝启动的原因根本看不见(真机验收实际踩过)。
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
        Environment.Exit(1);   // 已给出友好信息,不再让运行时二次打印堆栈
    };
}

// ---- 密钥加密工具:`Horus.Server protect-secret <明文key>` 打印 DPAPI 密文,粘进 config 的 visionApiKeyEnc(不存明文) ----
if (args.Length >= 1 && args[0] == "protect-secret")
{
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("protect-secret 仅 Windows(DPAPI)可用。"); return; }
    string plain = args.Length >= 2 ? args[1] : (Console.ReadLine() ?? "");
    Console.WriteLine(SecretProtect.Protect(plain));
    return;
}

// ---- 配置加载(JSON) + 环境变量覆盖(便于测试/部署) ----
string cfgPath = Environment.GetEnvironmentVariable("HORUS_CONFIG")
                 ?? (args.Length > 0 ? args[0] : "server.config.json");
ServerConfig cfg = ServerConfig.Load(cfgPath);

// ---- 自动加密:配置文件里若填了明文 visionApiKey,启动时加密为 visionApiKeyEnc 并清除明文(密文回写) ----
// UX = 运维直接在配置栏输入明文 key,启动一次即自动加密落盘、明文不再存在。DPAPI 机器绑定,仅 Windows。
if (OperatingSystem.IsWindows() && File.Exists(cfgPath) && !string.IsNullOrEmpty(cfg.VisionApiKey))
{
    try
    {
        string? enc = SecretProtect.EncryptVisionKeyInFile(cfgPath, cfg.VisionApiKey!);
        if (enc is not null)
        {
            cfg = cfg with { VisionApiKeyEnc = enc, VisionApiKey = null };
            Console.WriteLine("[Horus] 已将配置里的明文 visionApiKey 加密为 visionApiKeyEnc 并回写(明文已清除)。");
        }
        else
        {
            Console.Error.WriteLine("[Horus] 明文 key 自动加密失败(配置非 JSON 对象);本次仍用内存中的明文,请检查配置。");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Horus] 明文 key 自动加密回写失败(如文件只读):{ex.Message}。本次仍用内存中的明文。");
    }
}

cfg = cfg with
{
    DataDir = Environment.GetEnvironmentVariable("HORUS_DATADIR") ?? cfg.DataDir,
    DbPath = Environment.GetEnvironmentVariable("HORUS_DBPATH") ?? cfg.DbPath,
    PskBase64 = Environment.GetEnvironmentVariable("HORUS_PSK_B64") ?? cfg.PskBase64,
    KeystrokeSecretBase64 = Environment.GetEnvironmentVariable("HORUS_KSK_B64") ?? cfg.KeystrokeSecretBase64,
    AdminToken = Environment.GetEnvironmentVariable("HORUS_ADMIN_TOKEN") ?? cfg.AdminToken,
    Urls = Environment.GetEnvironmentVariable("HORUS_URLS") ?? cfg.Urls,
    VisionProvider = Environment.GetEnvironmentVariable("HORUS_VISION_PROVIDER") ?? cfg.VisionProvider,
    VisionBaseUrl = Environment.GetEnvironmentVariable("HORUS_VISION_BASEURL") ?? cfg.VisionBaseUrl,
    VisionModel = Environment.GetEnvironmentVariable("HORUS_VISION_MODEL") ?? cfg.VisionModel,
    VisionApiKey = Environment.GetEnvironmentVariable("HORUS_VISION_KEY") ?? cfg.VisionApiKey,
    // M3 按图搜图 embed env 覆盖
    EmbedProvider = Environment.GetEnvironmentVariable("HORUS_EMBED_PROVIDER") ?? cfg.EmbedProvider,
    EmbedBaseUrl = Environment.GetEnvironmentVariable("HORUS_EMBED_BASEURL") ?? cfg.EmbedBaseUrl,
    EmbedModel = Environment.GetEnvironmentVariable("HORUS_EMBED_MODEL") ?? cfg.EmbedModel,
    // M4 身份层(OIDC)env 覆盖
    AuthMode = Environment.GetEnvironmentVariable("HORUS_AUTH_MODE") ?? cfg.AuthMode,
    OidcIssuer = Environment.GetEnvironmentVariable("HORUS_OIDC_ISSUER") ?? cfg.OidcIssuer,
    OidcClientId = Environment.GetEnvironmentVariable("HORUS_OIDC_CLIENT_ID") ?? cfg.OidcClientId,
    OidcClientSecret = Environment.GetEnvironmentVariable("HORUS_OIDC_SECRET") ?? cfg.OidcClientSecret,
    OidcJwksJson = Environment.GetEnvironmentVariable("HORUS_OIDC_JWKS") ?? cfg.OidcJwksJson,
    // M4·RBAC 监考员看板 OIDC 登录(dashboard client)env 覆盖
    AdminAuthMode = Environment.GetEnvironmentVariable("HORUS_ADMIN_AUTH_MODE") ?? cfg.AdminAuthMode,
    OidcDashboardClientId = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_CLIENT_ID") ?? cfg.OidcDashboardClientId,
    OidcDashboardClientSecret = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_SECRET") ?? cfg.OidcDashboardClientSecret,
    OidcDashboardRedirectUri = Environment.GetEnvironmentVariable("HORUS_OIDC_DASHBOARD_REDIRECT") ?? cfg.OidcDashboardRedirectUri,
};

// ---- 诊断:`Horus.Server probe-embed [model]` —— 复用视觉 key 探测 embeddings 端点(部署前验证按图搜图可用性,不落库不起服务) ----
if (args.Contains("probe-embed"))
{
    string baseUrl = (cfg.EmbedBaseUrlEffective ?? "").TrimEnd('/');
    string key = SecretProtect.Resolve(cfg);   // 复用视觉 key(env>enc(DPAPI)>明文)
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
    // 生成一张 64×64 小图 → webp → data URI(探测图像 embedding)
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

// ---- 解析数据目录与 DB 数据源 ----
string dataDir = Path.GetFullPath(cfg.DataDir);
Directory.CreateDirectory(dataDir);
string dataSource = cfg.DbPath == ":memory:"
    ? ":memory:"
    : Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(dataDir, cfg.DbPath);

// 启动期校验密钥 base64 合法(否则运行期每请求访问 Psk/Ksk 会抛 FormatException → 500,难排查):此处 fail-fast 给清晰错误。
try { _ = cfg.Psk; _ = cfg.Ksk; }
catch (FormatException)
{
    throw new InvalidOperationException("pskBase64 / keystrokeSecretBase64 不是合法 base64(长度须 4 的倍数、仅 A-Za-z0-9+/= )。请检查配置。");
}

// Fail-closed:非 loopback 绑定却缺 PSK / 管理令牌 = 采集或管理面裸奔,拒绝启动(allowInsecure 仅联调可绕)。
string[] urls = cfg.Urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool lanExposed = urls.Any(u =>
{
    try { string h = new Uri(u).Host; return h is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"); }
    catch { return true; }
});
// 采集面安全 = 配了 PSK **或** OIDC(M4:oidc/both 模式凭 OIDC 会话鉴权,无需 PSK);管理面安全 = 配了 AdminToken。
bool collectInsecure = !cfg.AuthEnabled && !cfg.OidcEnabled;
if (lanExposed && !cfg.AllowInsecure && (collectInsecure || !cfg.AdminAuthEnabled))
    throw new InvalidOperationException(
        "拒绝启动:绑定了非本机地址却未配置采集面鉴权(PSK 或 OIDC)或 AdminToken(采集/管理面将裸奔)。请配置,或仅联调时设 allowInsecure=true。");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(urls);
// M4·RBAC·S8:Urls 含 https 时(监考员远端 OIDC 回调需 https)自签证书;仅 Kestrel 真跑时生效(测试走 TestServer 不触发)。
bool httpsBound = urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
System.Security.Cryptography.X509Certificates.X509Certificate2? httpsCert =
    httpsBound ? HttpsCert.LoadOrCreate(cfg, dataDir) : null;
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;   // 图片体上限 2MB(1080p webp~150KB),防放大 DoS
    if (httpsCert is not null) o.ConfigureHttpsDefaults(h => h.ServerCertificate = httpsCert);
});

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Db(dataSource));
builder.Services.AddSingleton(new Storage(dataDir));
builder.Services.AddSingleton<AgentHub>();          // 在线 Agent 注册表(config_update 下推)
builder.Services.AddSingleton<EventIngest>();
builder.Services.AddSingleton<ImageIngest>();
builder.Services.AddSingleton<KeystrokeIngest>();

// ---- 视觉分析(L2:视觉 LLM 取代 OCR + L3 Logo)。provider-agnostic:mock / OpenAI 兼容(DeepSeek/MiMo/Qwen/GLM) ----
if (cfg.VisionEnabled)
{
    if (string.Equals(cfg.VisionProvider, "openai", StringComparison.OrdinalIgnoreCase))
    {
        string vBaseUrl = cfg.VisionBaseUrl ?? throw new InvalidOperationException("visionProvider=openai 需配 visionBaseUrl");
        string vModel = cfg.VisionModel ?? throw new InvalidOperationException("visionProvider=openai 需配 visionModel");
        string vKey = SecretProtect.Resolve(cfg);   // env 明文 > visionApiKeyEnc(DPAPI 解密) > visionApiKey 明文
        builder.Services.AddSingleton<IVisionAnalyzer>(sp => new OpenAiCompatibleVisionAnalyzer(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, vBaseUrl, vModel, vKey,
            sp.GetRequiredService<ILogger<OpenAiCompatibleVisionAnalyzer>>()));   // 注入 logger:端点异常可见
    }
    else
        builder.Services.AddSingleton<IVisionAnalyzer>(new MockVisionAnalyzer());
}
builder.Services.AddSingleton<VisionAnalysisService>();               // 未注册 IVisionAnalyzer 时内部 no-op
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionAnalysisService>());

// ---- M3 CLIP 按图搜图:图像嵌入器(provider-agnostic:mock / OpenAI 兼容·复用视觉 baseUrl/key)+ 后台补嵌 ----
builder.Services.AddSingleton<Horus.Server.Analysis.Search.ImageSearchStore>();   // 检索始终可用(有 embedding 就能搜)
if (cfg.EmbedEnabled)
{
    if (string.Equals(cfg.EmbedProvider, "openai", StringComparison.OrdinalIgnoreCase))
    {
        string eEndpoint = cfg.EmbedEmbeddingsEndpoint ?? throw new InvalidOperationException("embedProvider=openai 需配 embedBaseUrl 或 visionBaseUrl");
        string eModel = cfg.EmbedModel ?? throw new InvalidOperationException("embedProvider=openai 需配 embedModel");
        // key:env > embedApiKey/Enc > **复用视觉 key(KEY一致)**
        string eKey = Environment.GetEnvironmentVariable("HORUS_EMBED_KEY") is { Length: > 0 } ek ? ek
            : !string.IsNullOrEmpty(cfg.EmbedApiKey) ? cfg.EmbedApiKey!
            : !string.IsNullOrEmpty(cfg.EmbedApiKeyEnc)
                ? (OperatingSystem.IsWindows() ? SecretProtect.Unprotect(cfg.EmbedApiKeyEnc!) : throw new PlatformNotSupportedException("embedApiKeyEnc 仅 Windows"))
                : SecretProtect.Resolve(cfg);   // 复用视觉 key
        builder.Services.AddSingleton<Horus.Server.Analysis.Search.IImageEmbedder>(sp =>
            new Horus.Server.Analysis.Search.OpenAiImageEmbedder(
                new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, eEndpoint, eModel, eKey, cfg.EmbedDim, cfg,
                sp.GetRequiredService<ILogger<Horus.Server.Analysis.Search.OpenAiImageEmbedder>>()));
    }
    else if (string.Equals(cfg.EmbedProvider, "onnx", StringComparison.OrdinalIgnoreCase))
    {
        // 本地 ONNX CLIP(MiMo 无 embeddings 端点·走本地·零出网)。模型部署提供。
        // ★约定文件名 = model.onnx(与 HF 仓 Qdrant/clip-ViT-B-32-vision 原名一致,下载免改名):
        //   embedOnnxModelPath 留空 → 默认找 dataDir\model.onnx;也可显式指定相对(按 dataDir)或绝对路径。
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
builder.Services.AddSingleton<Horus.Server.Analysis.Search.ImageEmbedService>();   // 未注册嵌入器时 no-op
builder.Services.AddHostedService(sp => sp.GetRequiredService<Horus.Server.Analysis.Search.ImageEmbedService>());

// ---- M4 身份层:OIDC 采集会话(取代共享 PSK)。AuthMode=oidc/both 时启用 ----
builder.Services.AddSingleton<Horus.Server.Identity.SessionStore>();   // both/oidc 下 ingest 会查会话
if (cfg.OidcEnabled)
{
    if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("authMode=oidc/both 需配 oidcIssuer");
    string oidcClientId = cfg.OidcClientId ?? throw new InvalidOperationException("authMode=oidc/both 需配 oidcClientId");
    string oidcSecret = Horus.Server.Identity.OidcSecret.Resolve(cfg);   // env > enc(DPAPI) > 明文
    string jwksJson = await Horus.Server.Identity.OidcJwks.LoadAsync(cfg);   // 内联优先;否则从 issuer 拉取 + 缓存
    var validator = new Horus.Server.Identity.OidcTokenValidator(jwksJson, cfg.OidcIssuer!, oidcClientId);
    builder.Services.AddSingleton(validator);
    builder.Services.AddSingleton(sp => new Horus.Server.Identity.OidcExchange(
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, validator,
        sp.GetRequiredService<Horus.Server.Identity.SessionStore>(), cfg, oidcSecret,
        sp.GetRequiredService<Db>(),   // 考试派发:exchange 需查当前活跃考试
        sp.GetRequiredService<ILogger<Horus.Server.Identity.OidcExchange>>()));
}

// ---- M4·RBAC:监考员看板 OIDC 登录(cpplearn dashboard web client·取代静态 adminToken)。AdminAuthMode=oidc 时启用 ----
builder.Services.AddSingleton<Horus.Server.Identity.AdminSessionStore>();   // gate 校验管理会话(oidc 模式)
if (cfg.DashboardOidcEnabled)
{
    if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcIssuer");
    string dashClientId = cfg.OidcDashboardClientId ?? throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcDashboardClientId");
    if (string.IsNullOrEmpty(cfg.OidcDashboardRedirectUri))
        throw new InvalidOperationException("adminAuthMode=oidc 需配 oidcDashboardRedirectUri(须与 cpplearn 注册的一致,如 https://<服务器>/cb)");
    string dashSecret = Horus.Server.Identity.OidcSecret.ResolveDashboard(cfg);
    string dashJwks = await Horus.Server.Identity.OidcJwks.LoadAsync(cfg);           // 与采集面共用 issuer 的 JWKS
    var dashValidator = new Horus.Server.Identity.OidcTokenValidator(dashJwks, cfg.OidcIssuer!, dashClientId);   // aud=dashboard client
    builder.Services.AddSingleton(sp => new Horus.Server.Identity.AdminOidcFlow(
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, dashValidator,
        sp.GetRequiredService<Horus.Server.Identity.AdminSessionStore>(), cfg, dashSecret,
        sp.GetRequiredService<ILogger<Horus.Server.Identity.AdminOidcFlow>>()));
}

// ---- M3 归档 / 清理作业:每日扫描到龄考试转 archive 库 + 清理 live(§13/§15)。ArchiveEnabled=false 时不起后台 ----
builder.Services.AddSingleton<ArchiveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArchiveService>());

WebApplication app = builder.Build();

// 全局异常兜底:任何未被端点捕获的异常统一返回 JSON 500(生产环境默认不泄堆栈),避免空体 500 / 契约漂移。
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
}));

app.UseWebSockets();

// ---- 安全响应头(所有响应):CSP 收紧脚本/样式来源(防 XSS 注入外链外发数据),附 nosniff / DENY / no-referrer ----
app.Use(async (ctx, next) =>
{
    IHeaderDictionary h = ctx.Response.Headers;
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    // no-store 覆盖**所有**响应(不止图片端点):`?t=` 后备令牌若进 URL,任何带令牌的响应都不落磁盘缓存/Referer(闭合第三轮 F8)。
    h["Cache-Control"] = "no-store";
    await next();
});

// ---- 管理/看板鉴权:所有 /api/*(除 /api/login、/api/logout)需带凭证。三选一:----
//   ① HttpOnly cookie `horus_admin`(浏览器;JS 读不到 → 防 XSS 窃取,<img> 同源自动携带 → 不落 URL)——首选
//   ② X-Horus-Admin 头(curl / 脚本客户端)
//   ③ ?t= 查询(向后兼容;UI 已不再使用,避免令牌进 URL / 日志 / Referer)
// 关闭学员机"用 config 下发关掉全场检测 / 拉全班证据图 / 抹自己可疑裁决"等路径。未配令牌则放行(仅联调)。
if (cfg.AdminAuthEnabled)
{
    AdminSessionStore adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
    app.Use(async (ctx, next) =>
    {
        PathString p = ctx.Request.Path;
        bool exempt = p.StartsWithSegments("/api/login") || p.StartsWithSegments("/api/logout")
                      || p.StartsWithSegments("/api/authmode");   // 公开:前端探测 token/oidc 登录方式
        if (p.StartsWithSegments("/api") && !exempt)
        {
            bool ok;
            if (cfg.DashboardOidcEnabled)
            {
                // M4·RBAC(R3):仅认 cpplearn **长老** OIDC 管理会话(HttpOnly cookie),**无静态令牌后门**。
                string sid = ctx.Request.Cookies["horus_admin"] ?? "";
                double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                AdminSession? s = adminSessions.Get(sid, now);
                ok = s is not null && s.IsElder;   // 弟子会话不存在(未建),此处双保险
            }
            else
            {
                // token 模式(默认·M1-M3 原样):静态 adminToken 三选一凭证(cookie / 头 / ?t=)。
                string got = ctx.Request.Cookies["horus_admin"] ?? "";
                if (got.Length == 0) got = ctx.Request.Headers["X-Horus-Admin"].ToString();
                if (got.Length == 0) got = ctx.Request.Query["t"].ToString();
                ok = Crypto.FixedTimeEquals(got, cfg.AdminToken!);
            }
            if (!ok)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next();
    });
}

// ---- 采集端通道(Agent ↔ Server) ----
app.MapGet("/ingest/events", (HttpContext ctx, EventIngest h) => h.HandleAsync(ctx));      // WebSocket
app.MapPost("/ingest/images", (HttpContext ctx, ImageIngest h) => h.HandleAsync(ctx));      // HTTP 图片
app.MapPost("/ingest/keystroke", (HttpContext ctx, KeystrokeIngest h) => h.HandleAsync(ctx)); // HTTP 击键旁路

// ---- M4 身份层:OIDC 登录换会话(采集面·由一次性 code+PKCE 保护·不走 admin gate) ----
app.MapOidc();

// ---- M4·RBAC:监考员看板 OIDC 登录(/admin/login + /cb·非 /api,不受 admin gate) ----
app.MapAdminOidc();

// ---- 看板 / 管理 API ----
app.MapApi();

// ---- 静态看板(wwwroot 单页) ----
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Horus 监考服务器启动 db={Db} dataDir={Dir} 采集鉴权={Auth} 管理鉴权={Admin} 阈值={Th}",
    dataSource, dataDir, cfg.AuthEnabled ? "开" : "关(仅联调)", cfg.AdminAuthEnabled ? "开" : "关(仅联调)", cfg.RiskThreshold);

// ---- 启动成功自动打开管理端看板(双击运维 UX·openDashboard 可关)。仅 Windows 交互式真 exe;测试宿主/输出重定向/服务化不弹 ----
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!cfg.OpenDashboard || !isRealHost || !OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
    if (Console.IsOutputRedirected) return;   // 脚本/无人值守运行不弹
    try
    {
        string? url = app.Urls
            .Select(u => u.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("//+", "//127.0.0.1").Replace("//*", "//127.0.0.1"))
            .OrderBy(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 1 : 0)   // http 优先:自签 https 首开有浏览器告警
            .FirstOrDefault();
        if (string.IsNullOrEmpty(url)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        app.Logger.LogInformation("已自动打开管理端看板 {Url}(配置 openDashboard=false 可关闭)", url);
    }
    catch { /* 打不开浏览器不影响服务 */ }
});

app.Run();

// WebApplicationFactory<Program> 测试入口需要可见的 Program 类
public partial class Program { }
