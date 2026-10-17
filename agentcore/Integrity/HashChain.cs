using Horus.Contracts;

namespace Horus.Agent.Integrity;

/// 事件雜湊鏈的**有狀態**封裝(維護 _prev)。實際 canonical / 雜湊 / 簽名邏輯在
/// Horus.Contracts.EventCanonical,與伺服器驗簽共用同一實作。
/// 非執行緒安全:呼叫方需串行化 Seal(保證鏈順序與 seq 一致)。
public sealed class HashChain
{
    private readonly byte[] _psk;
    private string _prev;

    public HashChain(byte[] psk, string genesis = "GENESIS")
    {
        _psk = psk;
        _prev = genesis;
    }

    public (string hashPrev, string hashSelf, string sig) Seal(AgentEvent core, long seq)
    {
        string prev = _prev;
        string self = EventCanonical.HashSelf(prev, core, seq);
        string sig = EventCanonical.Sig(_psk, self, seq);
        _prev = self;
        return (prev, self, sig);
    }
}
