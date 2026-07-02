using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Api;
using Microsoft.Extensions.Logging;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Horus.Server.Ingest;
using Horus.Server.Jobs;

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
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);   // 图片体上限 2MB(1080p webp~150KB),防放大 DoS

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
        sp.GetRequiredService<ILogger<Horus.Server.Identity.OidcExchange>>()));
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
    app.Use(async (ctx, next) =>
    {
        PathString p = ctx.Request.Path;
        bool exempt = p.StartsWithSegments("/api/login") || p.StartsWithSegments("/api/logout");
        if (p.StartsWithSegments("/api") && !exempt)
        {
            string got = ctx.Request.Cookies["horus_admin"] ?? "";
            if (got.Length == 0) got = ctx.Request.Headers["X-Horus-Admin"].ToString();
            if (got.Length == 0) got = ctx.Request.Query["t"].ToString();
            if (!Crypto.FixedTimeEquals(got, cfg.AdminToken!))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next();
    });

// ---- 采集端通道(Agent ↔ Server) ----
app.MapGet("/ingest/events", (HttpContext ctx, EventIngest h) => h.HandleAsync(ctx));      // WebSocket
app.MapPost("/ingest/images", (HttpContext ctx, ImageIngest h) => h.HandleAsync(ctx));      // HTTP 图片
app.MapPost("/ingest/keystroke", (HttpContext ctx, KeystrokeIngest h) => h.HandleAsync(ctx)); // HTTP 击键旁路

// ---- M4 身份层:OIDC 登录换会话(采集面·由一次性 code+PKCE 保护·不走 admin gate) ----
app.MapOidc();

// ---- 看板 / 管理 API ----
app.MapApi();

// ---- 静态看板(wwwroot 单页) ----
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Horus 监考服务器启动 db={Db} dataDir={Dir} 采集鉴权={Auth} 管理鉴权={Admin} 阈值={Th}",
    dataSource, dataDir, cfg.AuthEnabled ? "开" : "关(仅联调)", cfg.AdminAuthEnabled ? "开" : "关(仅联调)", cfg.RiskThreshold);

app.Run();

// WebApplicationFactory<Program> 测试入口需要可见的 Program 类
public partial class Program { }
