using System.Text.Json;
using Horus.Contracts;   // Json.Wire(線協定共享)

namespace Horus.Agent.Config;

/// Agent 設定。從 JSON 載入(camelCase);PSK 為 base64 字串,自動轉 byte[]。
/// **零設定部署(2026-07-03)**:所有欄位都有內建預設,設定檔案**整個可選** —— 缺檔案即全預設。
///   身分 agentId/machineId 留空 → 由**主機名自動推導**(machineId=主機名·agentId="ag-"+主機名),逐臺免配。
///   examId/seatId/psk 在 oidc 模式無需設定(examId 伺服器端派發·seatId 由 OIDC 身分派生·psk 不用)。
///   唯一常需按現場核對的是 serverWsBase/serverHttpBase(內建預設見下,IP 不符才覆蓋)。
public sealed class AgentConfig
{
    /// 考試與座位:**OIDC 模式不在設定裡出現** —— examId 由伺服器端派發(當前活躍考試)、seatId 由 OIDC 身分派生
    /// (username),登入成功後由 Program 填入;psk(legacy)模式仍須在設定中提供。
    public string ExamId { get; set; } = "";
    public string SeatId { get; set; } = "";
    /// 本機身分:留空則 Load 後由主機名自動推導(agentId="ag-"+主機名·machineId=主機名),逐臺免配。
    public string AgentId { get; set; } = "";
    public string MachineId { get; set; } = "";

    // 伺服器位址:內建預設 = 考場伺服器(IP 不符才在設定覆蓋)。唯一常需現場核對的欄位。
    public string ServerWsBase { get; init; } = "ws://192.168.32.145:8080";     // ws://host:port
    public string ServerHttpBase { get; init; } = "http://192.168.32.145:8080"; // http://host:port
    public byte[]? Psk { get; init; }                     // 預共享 HMAC 金鑰(base64)。OIDC 模式可省。

    // ---- M4 身分層:OIDC 登入(取代共享 PSK)----
    /// 採集鑑權:"oidc"(預設·經貝塔通登入換工作階段) | "psk"(legacy·須配 psk+examId+seatId)。both 由伺服器側決定。
    public string AuthMode { get; init; } = "oidc";
    /// 貝塔通 OIDC issuer。P147/P149 當前主域為 cc；cn 休眠，啟用須另審。
    /// 這是權杖標識符；切換後舊 issuer 的權杖被拒，需重新登入。
    /// 內建值供零設定部署；整合測試前須核對當前 discovery，裝機範例不另寫一份。
    public string? OidcIssuer { get; init; } = "https://pass.betaoi.cc";
    /// 協定端點取回前綴，留空使用 issuer。保留設定覆蓋能力，不據此啟用休眠域。
    public string? OidcEndpointBase { get; init; }

    /// 授權端點。★ 貝塔通掛**根路徑**(`/auth`),不是舊 wentian 的 `/oauth/authorize`。
    public string OidcAuthorizeBase =>
        (string.IsNullOrEmpty(OidcEndpointBase) ? OidcIssuer : OidcEndpointBase)!.TrimEnd('/') + "/auth";
    /// Horus 在 wentian 註冊的 client_id(預設 horus-client)。
    public string OidcClientId { get; init; } = "horus-client";
    /// 請求的 scope。★ 貝塔通 P81:**不要請求 `horus_profile`** —— 那是 wentian 的自訂 scope,
    /// 貝塔通不登記它;請求了會被**裁掉而不是整體拒絕**,於是「請求成功但 claims 是空的」,
    /// 症狀會跑到業務程式碼裡,最難查。`profile` 給的 `preferred_username` 就是座位標識的來源。
    public string OidcScope { get; init; } = "openid profile";

    public bool OidcMode => string.Equals(AuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    // 截圖
    public int TargetHeight { get; init; } = 1080;
    public int WebpQuality { get; init; } = 75;
    public int BaselineMinSeconds { get; init; } = 30;
    public int BaselineMaxSeconds { get; init; } = 90;

    // 閾值
    public int LargePasteThreshold { get; init; } = 200;

    // 白名單:內建預設 = 洛谷判題站 + 常見 IDE/編譯器(備援);伺服器下發的白名單會在執行環境覆蓋(以下發為準)。
    public List<string> WhitelistHosts { get; init; } = new() { "luogu.com.cn", "www.luogu.com.cn" };
    public List<string> WhitelistProcs { get; init; } = new()
    {
        "code", "devenv", "cl", "g++", "gcc", "edge", "explorer", "horus.agent", "dev-c++", "redpanda",
    };

    // 設定載入專用選項:camelCase + **允許 // 註解與尾逗號**(便於部署範本自文件化)。
    // 不復用 Json.Wire(那是線協定/canonical 序列化器,須逐位元組穩定,不能加寬鬆解析)。
    private static readonly JsonSerializerOptions ConfigOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// 從路徑載入;**檔案不存在則用全部內建預設**(零設定部署)。載入後補齊主機名自動身分。
    public static AgentConfig Load(string path)
    {
        AgentConfig cfg = File.Exists(path)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ConfigOpt)
              ?? throw new InvalidOperationException("配置解析失败: " + path)
            : new AgentConfig();   // 無設定檔案 → 全內建預設
        cfg.ApplyIdentityDefaults();
        return cfg;
    }

    /// 身分預設補齊:agentId/machineId 留空 → 由主機名自動推導(machineId=主機名·agentId="ag-"+主機名)。
    /// 已在設定顯式指定則尊重原值。主機名跨重新啟動穩定 → seq 空間/雜湊鏈不受影響。
    public void ApplyIdentityDefaults()
    {
        string host = SafeHostName();
        if (string.IsNullOrWhiteSpace(MachineId)) MachineId = host;
        if (string.IsNullOrWhiteSpace(AgentId)) AgentId = "ag-" + host;
    }

    private static string SafeHostName()
    {
        try { string h = Environment.MachineName; return string.IsNullOrWhiteSpace(h) ? "unknown-host" : h; }
        catch { return "unknown-host"; }
    }
}
