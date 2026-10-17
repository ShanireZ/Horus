namespace Horus.Agent.Transport;

/// 把信號源的 CaptureReason 映射為 api-contract §2.1 約定的 trigger 取值。
/// 否則生產庫 images.trigger 會是 browser_non_whitelist_url 之類的髒值,與契約/歸檔/統計錯配。
public static class TriggerMap
{
    public static string ToContract(string reason) => reason switch
    {
        "baseline_random" => "baseline_random",
        "browser_non_whitelist_url" or "browser_url_unreadable" => "event:browser",
        "non_whitelist_process" => "event:process",
        "large_paste" => "event:paste",
        "usb_insert" => "event:usb",
        _ => "event:manual",   // capture_now 及未知原因 → 手動/其它
    };
}
