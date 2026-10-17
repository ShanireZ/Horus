namespace Horus.Server.Identity;

/// 最近一次**探活實際發生了什麼**(不是「設定成什麼」)。
///
/// ★★★ **為什麼需要它**:預檢原本只報「本機認哪些 `aud`」—— 那是**設定讀數**,
///   它永遠顯示正常,哪怕對側一次都沒探到過、或者每一發都被拒。
///   本儲存庫自己反覆寫過「設定化最容易做成裝飾品,**斷言必須落在行為上**」,
///   而那一項恰恰就是裝飾品。這個類把它換成跑起來的事實。
///
/// ★ 它能直接答出的兩件事,都是別處答不出的:
///   ① **探活從來沒來過** —— 多半是貝塔通後台**沒登記撤權回呼**(探活位址按它同源推導,
///      所以那一條登記同時決定探活與撤權兩件事);
///   ② **來了但被拒** —— 口徑對不上(最常見:線上貝塔通還是 P100 之前的版本,還在發裸 `client_id`)。
///   ★★ 這兩件事在別處**都沒有症狀**:貝塔通把 401 也算線上,唯一跡象在**它的**後台,
///   而那要有人去看才算數。
///
/// ★ 純記憶體、處理程序重新啟動即清 —— 它不是稽核臺賬,只是給預檢一個當下的事實。
///   丟了最多是「重新啟動後顯示成從未探過」,而探活每 5 分鐘就來一次,一輪就補上。
public sealed class BetapassProbeState
{
    private readonly object _gate = new();
    private double _lastOkAt;
    private double _lastRejectedAt;
    private double _lastWarnAt;
    private string? _lastRejectReason;

    /// 驗簽通過。
    public void RecordOk(double now)
    {
        lock (_gate) { _lastOkAt = now; }
    }

    /// 驗簽被拒。@returns 這一發**該不該記 Warning**。
    ///
    /// ★ 按分鐘節流:口徑對不上時探活會**每 5 分鐘失敗一次、永遠失敗**,
    ///   照記就是一條永不停止的告警流;但完全不記(此前記的是 Debug)又等於沒有任何主動信號。
    ///   ★★ 節流的另一半用途:這個端點是**公開可達**的,不節流的話誰都能拿垃圾權杖灌滿紀錄。
    public bool RecordRejected(double now, string reason)
    {
        lock (_gate)
        {
            _lastRejectedAt = now;
            _lastRejectReason = reason;
            if (now - _lastWarnAt < 60) return false;
            _lastWarnAt = now;
            return true;
        }
    }

    /// 供預檢讀。三種狀態:從未探過 / 最近一次被拒 / 最近一次通過。
    public (double? OkAt, double? RejectedAt, string? Reason) Snapshot()
    {
        lock (_gate)
        {
            return (_lastOkAt > 0 ? _lastOkAt : null,
                    _lastRejectedAt > 0 ? _lastRejectedAt : null,
                    _lastRejectReason);
        }
    }
}
