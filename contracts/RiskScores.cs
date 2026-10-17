namespace Horus.Contracts;

/// 風險分常數(集中定義,Agent 自報與伺服器復判共用同一份,避免兩路"真相來源"漂移 · 品質#2)。
///
/// 值即 0–100 風險分。系統只初篩、人工裁決(架構鐵律 §3),故取值偏「寧高勿低」——
/// 高分進可疑佇列交人工複核,低分僅作軟信號;漏報(放走作弊)比誤報(多一次人工複核)代價更高。
///
/// Agent 端自報分與伺服器 <c>RiskModel.Derive</c> 復判分都引用此處,任一處調分只需改這一份。
public static class RiskScores
{
    // ---- 瀏覽器 URL ----
    public const int BrowserUrlAi = 80;            // 命中 AI 站黑名單(伺服器獨立復判)
    public const int BrowserUrlSearch = 70;        // 命中搜索引擎黑名單
    public const int BrowserUrlNonWhitelist = 80;  // 非白名單站(無 AI/搜索標籤,但確非白名單)
    public const int BrowserUrlUnreadable = 40;    // 位址欄讀不到 → 降級:強制抓圖交人工看

    // ---- 處理程序 / 遠控 ----
    public const int RemoteTool = 70;              // 遠控工具處理程序(teamviewer/anydesk/…)
    public const int NonWhitelistProc = 70;        // 非白名單處理程序

    // ---- 剪貼板 / 粘貼 ----
    public const int LargePaste = 60;              // 大段粘貼

    // ---- 設備 ----
    public const int Usb = 50;                     // USB 設備接入

    // ---- 視窗行為 ----
    public const int AltTabBurst = 40;             // 頻繁 Alt+Tab

    // ---- M5 採集端硬化健康信號 ----
    public const int ScreenObscured = 60;          // 螢幕遮擋 / 黑影格
    public const int CapabilityDegraded = 55;      // 採集能力降級(非管理員 / 信號源啟動失敗)
    public const int WatchdogRestart = 55;         // 採集處理程序異常重新啟動(規避取證)

    // ---- 擊鍵(發自判題後端,伺服器復判同源)----
    public const int KeystrokeIdleThenBlock = 70;  // 空窗後突現整段程式碼
    public const int KeystrokePaste = 60;          // 粘貼
    public const int KeystrokeBurst = 55;          // 超人輸入速度
}
