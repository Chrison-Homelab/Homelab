using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamingIdleShutdown;

/// <summary>
/// Reliable machine shutdown. The naive <c>shutdown.exe /s</c> fire-and-forget silently
/// failed when the app was launched with a privilege-stripped token (no SeShutdownPrivilege)
/// — see #153. This explicitly enables the privilege and calls ExitWindowsEx, falls back to
/// shutdown.exe with an exit-code check, and reports a detailed result so failures are loud.
/// </summary>
public static class SystemPower
{
    public static bool TryShutdown(out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "non-Windows host: shutdown skipped";
            return false;
        }
        return Win.TryShutdown(out detail);
    }

    [SupportedOSPlatform("windows")]
    private static class Win
    {
        public static bool TryShutdown(out string detail)
        {
            var privOk = EnableShutdownPrivilege(out var privDetail);

            if (privOk && ExitWindowsEx(EWX_SHUTDOWN | EWX_FORCEIFHUNG,
                    SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED))
            {
                detail = "ExitWindowsEx accepted";
                return true;
            }

            var exitWinErr = privOk ? Marshal.GetLastWin32Error() : -1;
            var viaExe = TryShutdownExe(out var exeDetail);   // fallback (shutdown.exe self-enables held privileges)
            detail = privOk
                ? $"ExitWindowsEx failed (win32={exitWinErr}); shutdown.exe -> {exeDetail}"
                : $"privilege not enabled ({privDetail}); shutdown.exe -> {exeDetail}";
            return viaExe;
        }

        private static bool TryShutdownExe(out string detail)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                { CreateNoWindow = true, UseShellExecute = false });
                if (p is null) { detail = "Process.Start returned null"; return false; }
                if (!p.WaitForExit(10_000)) { detail = "still running (assumed initiated)"; return true; }
                detail = $"exit={p.ExitCode}";
                return p.ExitCode == 0;
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }

        private static bool EnableShutdownPrivilege(out string detail)
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
            { detail = $"OpenProcessToken win32={Marshal.GetLastWin32Error()}"; return false; }
            try
            {
                if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out var luid))
                { detail = $"LookupPrivilegeValue win32={Marshal.GetLastWin32Error()}"; return false; }

                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                { detail = $"AdjustTokenPrivileges win32={Marshal.GetLastWin32Error()}"; return false; }

                if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                { detail = "ERROR_NOT_ALL_ASSIGNED (token doesn't hold SeShutdownPrivilege)"; return false; }

                detail = "enabled";
                return true;
            }
            finally { CloseHandle(hToken); }
        }

        private const uint EWX_SHUTDOWN = 0x00000001;
        private const uint EWX_FORCEIFHUNG = 0x00000010;
        private const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
        private const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
        private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;
        private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
