using Horus.Contracts;
using Horus.Server.Api;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Ingest;

// ---- 配置加载(JSON) + 环境变量覆盖(便于测试/部署) ----
string cfgPath = Environment.GetEnvironmentVariable("HORUS_CONFIG")
                 ?? (args.Length > 0 ? args[0] : "server.config.json");
ServerConfig cfg = ServerConfig.Load(cfgPath);
cfg = cfg with
{
    DataDir = Environment.GetEnvironmentVariable("HORUS_DATADIR") ?? cfg.DataDir,
    DbPath = Environment.GetEnvironmentVariable("HORUS_DBPATH") ?? cfg.DbPath,
    PskBase64 = Environment.GetEnvironmentVariable("HORUS_PSK_B64") ?? cfg.PskBase64,
    KeystrokeSecretBase64 = Environment.GetEnvironmentVariable("HORUS_KSK_B64") ?? cfg.KeystrokeSecretBase64,
    AdminToken = Environment.GetEnvironmentVariable("HORUS_ADMIN_TOKEN") ?? cfg.AdminToken,
    Urls = Environment.GetEnvironmentVariable("HORUS_URLS") ?? cfg.Urls,
};

// ---- 解析数据目录与 DB 数据源 ----
string dataDir = Path.GetFullPath(cfg.DataDir);
Directory.CreateDirectory(dataDir);
string dataSource = cfg.DbPath == ":memory:"
    ? ":memory:"
    : Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(dataDir, cfg.DbPath);

// Fail-closed:非 loopback 绑定却缺 PSK / 管理令牌 = 采集或管理面裸奔,拒绝启动(allowInsecure 仅联调可绕)。
string[] urls = cfg.Urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool lanExposed = urls.Any(u =>
{
    try { string h = new Uri(u).Host; return h is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"); }
    catch { return true; }
});
if (lanExposed && !cfg.AllowInsecure && (!cfg.AuthEnabled || !cfg.AdminAuthEnabled))
    throw new InvalidOperationException(
        "拒绝启动:绑定了非本机地址却未配置 PSK 或 AdminToken(采集/管理面将裸奔)。请配置两者,或仅联调时设 allowInsecure=true。");

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

WebApplication app = builder.Build();

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
