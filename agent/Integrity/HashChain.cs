using Honus.Contracts;

namespace Honus.Agent.Integrity;

/// 事件哈希链的**有状态**封装(维护 _prev)。实际 canonical / 哈希 / 签名逻辑在
/// Honus.Contracts.EventCanonical,与服务器验签共用同一实现。
/// 非线程安全:调用方需串行化 Seal(保证链顺序与 seq 一致)。
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
