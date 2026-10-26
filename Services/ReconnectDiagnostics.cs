using System;
using System.IO;
using System.Threading;

namespace JuChuang.Services;

// RC6: diagnostic-only. No client input or window state is changed here.
internal static class ReconnectDiagnostics
{
    private static readonly object Sync = new();
    private static long _untilUtcTicks;
    private static int _mouseSamples;

    internal static bool Active => DateTime.UtcNow.Ticks < Interlocked.Read(ref _untilUtcTicks);

    internal static void Start()
    {
        Interlocked.Exchange(ref _untilUtcTicks, DateTime.UtcNow.AddMinutes(3).Ticks);
        Interlocked.Exchange(ref _mouseSamples, 0);
        Write("START reconnect diagnostic window=3min");
    }

    internal static void Mouse(string description)
    {
        if (!Active || Interlocked.Increment(ref _mouseSamples) > 100) return;
        Write("mouse " + description);
    }

    internal static void Write(string description)
    {
        if (!Active) return;
        DiagnosticLogService.WriteActivityEvent("reconnect:" + description);
    }
}
