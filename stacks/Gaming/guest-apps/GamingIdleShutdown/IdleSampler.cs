using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamingIdleShutdown;

public readonly record struct IdleSample(uint InputIdleMs, double CpuPercent, double NetKBytesPerSec);

/// <summary>
/// Samples the three idle signals. CPU (summed process time delta) and network
/// (NIC byte delta) are cross-platform; input-idle is Windows-only and guarded so
/// the app still builds/runs on the macOS dev box.
/// </summary>
public sealed class IdleSampler
{
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private long _lastNetBytes = ReadNetBytes();
    private TimeSpan _lastCpuTotal = ReadProcCpu();

    public IdleSample Sample(int pollSeconds)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSampleUtc).TotalSeconds;
        if (elapsed <= 0) elapsed = pollSeconds;

        var cpuNow = ReadProcCpu();
        var cpuDelta = (cpuNow - _lastCpuTotal).TotalSeconds;
        _lastCpuTotal = cpuNow;
        var cpuPct = Math.Clamp(cpuDelta / (elapsed * Environment.ProcessorCount) * 100.0, 0, 100);

        var netNow = ReadNetBytes();
        var netDelta = Math.Max(0, netNow - _lastNetBytes);
        _lastNetBytes = netNow;
        var netKBps = netDelta / 1024.0 / elapsed;

        _lastSampleUtc = now;
        return new IdleSample(GetInputIdleMs(), cpuPct, netKBps);
    }

    private static TimeSpan ReadProcCpu()
    {
        var total = TimeSpan.Zero;
        foreach (var p in Process.GetProcesses())
        {
            try { total += p.TotalProcessorTime; }
            catch { /* access denied / process exited between enumerate and read */ }
            finally { p.Dispose(); }
        }
        return total;
    }

    private static long ReadNetBytes()
    {
        long sum = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = ni.GetIPv4Statistics();
                sum += s.BytesSent + s.BytesReceived;
            }
        }
        catch { /* some virtual adapters refuse stats */ }
        return sum;
    }

    private static uint GetInputIdleMs()
    {
        if (!OperatingSystem.IsWindows()) return 0;   // dev on non-Windows: treat as "just had input"
        return WindowsInput.IdleMs();
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsInput
    {
        public static uint IdleMs()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            return unchecked((uint)Environment.TickCount - lii.dwTime);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    }
}
