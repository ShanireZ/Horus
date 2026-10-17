namespace Horus.Server.Identity;

/// 本機工作階段失效的原因。<see cref="Ok"/> 之外的每一項都對應「三道門」裡的一道。
public enum SessionGate
{
    /// 三道門都沒到點,工作階段有效。
    Ok,
    /// **absolute**:建工作階段時寫死的到期時刻。★ 任何活動都推不動它。
    Absolute,
    /// **idle**:最後一次「人還在」距今太久 —— 擋的是「頁面開著但人走了」。
    Idle,
    /// **心跳**:最後一次收到心跳距今太久 —— 擋的是「關頁面 / 關瀏覽器走人」。
    Heartbeat,
}

/// **三道門**(貝塔通 P88–P92,契約見 BetaPass `docs/rp-contract.md`「三道門與存活心跳」)。
///
/// 取代了此前的單一 `expires_at` 判定。三道門各擋各的,**不要合併成一個判據** ——
/// 合併的後果是頁面開著就永不過期,idle 那道門直接失效。
///
/// | 門 | 判據欄位 | 本儲存庫預設 | 擋什麼 |
/// |---|---|---|---|
/// | 心跳 | `last_heartbeat_at` | 15 分鐘(5min × 3) | 關頁面 / 關瀏覽器走人 |
/// | idle | `last_seen_at` | 30 分鐘 | 頁面開著但人走了 |
/// | absolute | `expires_at` | 6 小時 | 前兩道被繞過時的最後一道 |
///
/// ★ **各值各平台完全自由調、不登記、貝塔通不攔**(其 P90 已正式取消原來那條
///   「RP 的 absolute 不得超過 IdP 的」)。備援改由「永不放棄的撤權重投」承擔 ——
///   時長設長的代價從「撤權可能永遠漏掉」降成了「撤權可能晚一點到」。
///   ★ 但「自由調」不等於「隨便填」:absolute 必須覆蓋得住一整場考試(見 `ServerConfig`)。
///
/// ★★ **判定是被動的**:過期發生在「下一次請求進來時」,**不要起定時任務或輪詢**。
///   本類因此只有純函式,沒有任何後台作業。
///
/// ★★ **寫入口只有心跳**(其 P92 翻案 P25):`last_seen_at` **只由帶 `active: true` 的心跳更新**,
///   **任何業務請求都不得續 idle**。不寫死這條,監考看板那種**持續自動輪詢**就會續 idle,
///   於是「看板開著 = 永不登出」,idle 那道門形同虛設 —— 這正是對側 P25 被翻案的直接原因。
public static class SessionGates
{
    /// 伺服器端心跳節流視窗(秒)。
    ///
    /// ★ 取 150 秒而不是更接近心跳間隔(5 分鐘):定時器有抖動(瀏覽器節流、系統負載、
    ///   leader 剛切換時的補發),視窗越接近間隔越容易吞掉一次心跳,而**吞一次就用掉
    ///   15 分鐘容忍視窗的 60%**。150 秒留了 2× 餘量。
    public const double ThrottleSeconds = 150;

    /// 判定順序:**revoked → absolute → idle → heartbeat**(更強的終止理由排前面)。
    ///
    /// ★ `revoked` 那道門不在這裡 —— 撤權(<c>RevokeBySub</c> / <c>RevokeByExam</c>)是**刪行**,
    ///   所以「查無此工作階段」本身就是它,順序上天然排在最前。
    public static SessionGate Evaluate(
        double now, double expiresAt, double lastSeenAt, double lastHeartbeatAt,
        double idleMinutes, double heartbeatMinutes)
    {
        if (now > expiresAt) return SessionGate.Absolute;
        if (now > lastSeenAt + idleMinutes * 60.0) return SessionGate.Idle;
        if (now > lastHeartbeatAt + heartbeatMinutes * 60.0) return SessionGate.Heartbeat;
        return SessionGate.Ok;
    }

    // ★ **這裡故意沒有「節流判據」的 C# 版本。**
    //   節流與「按欄位分開」整個落在 SQL 上(`SessionStore.Heartbeat` / `AdminSessionStore.Heartbeat`
    //   的 WHERE + 兩個獨立 CASE),讓「分開」成為結構上唯一寫得出的形狀。
    //   ★★ 另寫一份可單測的 C# 副本看著更好測,但那就是**同一個判據兩個來源** ——
    //   它會漂,而漂的表現是「測試全綠、線上把 active:true 吞掉了」。
    //   迴歸改為直接打 `Heartbeat()` 的回傳值與兩個時間戳列(見 SessionGateTests)。
}
