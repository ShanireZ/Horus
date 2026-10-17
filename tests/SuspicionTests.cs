using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis;
using Xunit;

namespace Horus.Server.Tests;

/// B3/T4:M5 kind 映射 + 風險分 + 健康來源,鎖成可斷言契約(防改值/改映射後無聲漂移)。
public class SuspicionTests
{
    private static JsonElement DefaultPayload => JsonDocument.Parse("{}").RootElement;

    [Theory]
    [InlineData(SignalType.ScreenshotObscured, "screen_obscured")]
    [InlineData(SignalType.CapabilityDegraded, "capability_degraded")]
    [InlineData(SignalType.WatchdogRestart, "watchdog_restart")]
    [InlineData(SignalType.SuspectedSuspend, "suspected_suspend")]
    [InlineData(SignalType.ProcessStart, "non_whitelist_proc")]
    [InlineData(SignalType.Clipboard, "large_paste")]
    [InlineData(SignalType.Usb, "usb")]
    [InlineData(SignalType.WindowFocus, "suspect")]
    public void KindFor_映射正确(SignalType type, string expected)
        => Assert.Equal(expected, Suspicion.KindFor(type, DefaultPayload));

    [Theory]
    [InlineData(SignalType.ScreenshotObscured, 60)]
    [InlineData(SignalType.CapabilityDegraded, 55)]
    [InlineData(SignalType.WatchdogRestart, 55)]
    [InlineData(SignalType.SuspectedSuspend, 0)]
    [InlineData(SignalType.WindowFocus, 0)]
    public void RiskModel_M5分值正确(SignalType type, int expected)
        => Assert.Equal(expected, RiskModel.Derive(type, DefaultPayload, null, null, 200));

    [Theory]
    [InlineData("screen_obscured", "health")]
    [InlineData("capability_degraded", "health")]
    [InlineData("watchdog_restart", "health")]
    [InlineData("suspected_suspend", "health")]
    [InlineData("web_ai", "suspicion")]
    [InlineData("suspect", "suspicion")]
    public void SourceForKind_健康与作弊分离(string kind, string expected)
        => Assert.Equal(expected, Suspicion.SourceForKind(kind));
}
