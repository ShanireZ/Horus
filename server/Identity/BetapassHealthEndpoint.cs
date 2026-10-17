using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 貝塔通**存活探測**的接收端(其 P94,契約見 BetaPass `docs/rp-contract.md`「`GET /internal/health`:必辦項」)。
///
/// ⏳★★ **本端目前不會被呼叫**(貝塔通 **P101**,owner 2026-08-11):Horus Server 是區域網路機、
///   **沒有可撥位址**,因此**有意不給它登記 `revoke_callback_url`** —— 而探活位址按撤權回呼
///   同源推導,沒登記就**不在探活名單裡**。★ 實作照舊保留:走拉取模型那天要用**同一套驗簽與處置**,
///   刪了將來還得重寫。判據與殘餘風險見 <see cref="Config.ServerConfig.BetapassRevokePushExpected"/>。
///
/// 以下描述的是**連線路徑真的通起來時**的口徑(即 `betapassRevokePushExpected=true` 的部署):
/// 貝塔通**每 5 分鐘**打一次;**連續 3 次失敗(15 分鐘)即判本站離線**,並**暫停對本站的撤權重投**,
/// 探到回來之後才從頭重投一遍。
///
/// ★★★ **「不實作它 = 撤權通知永遠送不到」這條理由已經不成立,別再照它推**(2026-08-11 實測)。
///   對側 `5bb16b6` 之後 `health/probe.ts` 只在**連不上**時判離線,**任何 HTTP 應答都算線上** ——
///   包括根本沒實作這個路由時框架回的 404。其 `rp-contract.md` 裡那句(以及本儲存庫此前的註解)
///   都還停在舊判據上。
///   ★★ **教訓比這條事實本身更值錢**:把「為什麼這麼做」掛在**對側某個行為**上,
///   對側一改,理由就爛掉而結論看起來還是對的 —— 而**爛掉的理由比錯的結論更難發現,
///   因為沒有任何門會去驗理由**。下面三條是重寫過的、不掛在對側當前實作上的理由。
///
/// **那為什麼仍然要實作它**:
/// ① **契約把它列為必辦項**(P94) —— 合規本身就是理由,不需要再借一個後果來加碼;
/// ② ★ **對側的判據已經改過一次,就可能改回去**。真改回去的那一刻,沒實作的站
///    **立刻**失效,而失效**沒有任何症狀**(「權限撤了、人還在裡面」不會被誰報上來)。
///    實作它的成本近乎零,而這個不對稱是實打實的;
/// ③ 實作了對側後台才是乾淨的「線上」;沒實作是「線上但 `HTTP 404`」——
///    ★ 那正是他們用來發現「這家 RP 壓根沒實作探活」的信號(見其 `probe.ts` 對 `error` 的說明)。
///
/// 三條硬要求,逐條落在下面:
/// ① **必須驗簽**(與 `/internal/revoke` 同一套:拉 `/jwks`、按 `kid` 取公鑰、`PS256`、`aud` 是不是自己)。
///    不驗就等於把「貝塔通接了哪些站」這份接入拓撲白送給任何能打到這個位址的人。
/// ② 回 **204 無回應體**。
///    ⚠ **判據以對側程式碼為準,不以其契約文字為準**:`docs/rp-contract.md` 裡
///    「任意 2xx 即算線上 / 非 2xx 會被判離線」那類說法**反覆與 `health/probe.ts` 對不上**
///    (2026-08-11 一天之內在三處各出現一次,修了兩處又在新寫的一節裡復發)。
///    ★ 對本端的實作沒有影響(成功本來就回 204),但**別照那句去推斷非 2xx 的後果**。
///    ★★ 要確認當下口徑就跑這一條,別讀檔案也別讀契約:
///      `git -C ../BetaPass show HEAD:src/identity/health/probe.ts | grep res.ok`
/// ③ ★ **不查庫、不做重活** —— 它每 5 分鐘就來一次,而它要回答的問題只有「這個處理程序還在不在」。
///    （連工作階段表都不碰:工作階段是不是空的與「本站在不線上」無關。）
///
/// ★ **為什麼不復用 `/internal/revoke` 當探活**:那個端點會**真的清工作階段**,
///   拿它探活等於每 5 分鐘把全體使用者從各站台踢一次。這是貝塔通專門開一個新端點的原因。
public static class BetapassHealthEndpoint
{
    /// ★ 位址由貝塔通按**登記的 `revoke_callback_url` 同源**推導(其 `health/probe.ts`),
    ///   所以路徑固定為 `/internal/health`,不能改名 —— 除非在後台單獨登記 `health_check_url` 覆蓋。
    public const string Path = "/internal/health";

    public static void Map(WebApplication app)
    {
        app.MapGet(Path, (HttpContext ctx) =>
        {
            ServerConfig cfg = ctx.RequestServices.GetRequiredService<ServerConfig>();

            // 兩條連線路徑都沒開 OIDC:本機根本不是貝塔通的 RP,也沒有可驗簽的公鑰。
            // 回 410 與 `/internal/revoke` 同口徑 = 「別再當我是接入方」。
            // ★ 它**不會**把本站判成離線(見類註解②:對側只有連不上才判離線),
            //   而是被如實記成一條 `HTTP 410` 擺在其後台 —— 這正是要的:
            //   「這臺機器不是 RP」應該有人看見,而不是安靜地當作線上。
            if (!cfg.OidcEnabled && !cfg.DashboardOidcEnabled)
                return Results.StatusCode(StatusCodes.Status410Gone);

            var probeState = ctx.RequestServices.GetRequiredService<BetapassProbeState>();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            string? bearer = ReadBearer(ctx.Request.Headers.Authorization.ToString());
            if (bearer is null)
            {
                Reject(ctx, probeState, now, "无 Bearer 令牌");
                return Results.Unauthorized();
            }

            var verifier = ctx.RequestServices.GetRequiredService<BetapassRevokeVerifier>();
            try
            {
                verifier.VerifyProbe(bearer);
            }
            catch (OidcValidationException ex)
            {
                Reject(ctx, probeState, now, ex.Message);
                return Results.Unauthorized();
            }

            // ★ 成功**不記紀錄**:它每 5 分鐘來一次,一條正常紀錄就會把上面那條告警淹掉 ——
            //   「告警被正常紀錄埋掉」與「壓根沒告警」在實際使用中是同一回事。
            //   要看「上一次通過是什麼時候」去 `GET /api/preflight` 的 `health_audience`。
            probeState.RecordOk(now);
            return Results.NoContent();   // 204·無回應體
        });
    }

    /// 記下這一發被拒,並**按分鐘節流**地叫一聲。
    ///
    /// ★ 告警裡帶上**本機在等哪些 `aud`** —— 排查「探活為什麼恆 401」時第一個要問的就是這個,
    ///   而它只有伺服器自己知道(對側後台只看得到一個 401)。
    private static void Reject(HttpContext ctx, BetapassProbeState state, double now, string reason)
    {
        if (!state.RecordRejected(now, reason)) return;
        var verifier = ctx.RequestServices.GetService(typeof(BetapassRevokeVerifier)) as BetapassRevokeVerifier;
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BetapassHealth")
            .LogWarning("贝塔通探活被拒:{Reason}。本机在等的 aud={Expected}(60 秒内不再重复此告警)",
                reason, verifier is null ? "(未构造)" : string.Join(" / ", verifier.ProbeAudiences));
    }

    private static string? ReadBearer(string header)
    {
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
