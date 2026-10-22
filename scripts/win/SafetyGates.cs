using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HuashuWinUse;

public static class SafetyGates
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    public const double IDLE_NEED = 2.0;   // In-presence threshold
    public const double IDLE_WAIT = 15.0;  // Max wait for user to stop typing

    private static readonly string FocusLockFile = Path.Combine(Path.GetTempPath(), "huashu-win-focus.lock");

    public static readonly HashSet<string> ShellOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "OpenConsole", "Code", "Cursor", "conhost", "wt"
    };

    public static double UserIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 3600.0;

        ulong now = GetTickCount64();
        ulong elapsedMs = now - lii.dwTime;
        double physicalIdle = elapsedMs / 1000.0;

        double? syntheticAgo = InputSimulator.GetSyntheticAgo();
        if (syntheticAgo.HasValue && syntheticAgo.Value < 10.0 && Math.Abs(syntheticAgo.Value - physicalIdle) < 0.8)
        {
            // Self-inflicted synthetic event detected; treat as clear
            return 3600.0;
        }

        return physicalIdle;
    }

    public static (bool passed, double waited, double lastIdle) WaitUserIdle(double need = IDLE_NEED, double maxWait = IDLE_WAIT)
    {
        var sw = Stopwatch.StartNew();
        double last = UserIdleSeconds();

        while (last < need)
        {
            if (sw.Elapsed.TotalSeconds >= maxWait)
            {
                return (false, sw.Elapsed.TotalSeconds, last);
            }
            Thread.Sleep(250);
            last = UserIdleSeconds();
        }

        return (true, sw.Elapsed.TotalSeconds, last);
    }

    public static string? CheckFocusLock()
    {
        try
        {
            if (!File.Exists(FocusLockFile)) return null;
            string content = File.ReadAllText(FocusLockFile).Trim();
            var parts = content.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && double.TryParse(parts[1], out double ts))
            {
                double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                double age = now - ts;
                if (age < 30.0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (!proc.HasExited)
                        {
                            return $"pid={pid}, age={age:F1}s";
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    public static bool IsScreenLocked()
    {
        IntPtr hDesk = OpenInputDesktop(0, false, 0x01FF);
        if (hDesk == IntPtr.Zero)
        {
            return true;
        }
        CloseDesktop(hDesk);
        return false;
    }

    public static bool AcquireFocusLock(double maxWait = 5.0)
    {
        var sw = Stopwatch.StartNew();
        int myPid = Environment.ProcessId;

        while (true)
        {
            string? holder = CheckFocusLock();
            if (holder == null || holder.StartsWith($"pid={myPid},"))
            {
                break;
            }
            if (sw.Elapsed.TotalSeconds >= maxWait)
            {
                return false;
            }
            Thread.Sleep(200);
        }

        try
        {
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllText(FocusLockFile, $"{myPid}:{now:F3}");
            return true;
        }
        catch { return false; }
    }

    public static void ReleaseFocusLock()
    {
        try
        {
            if (File.Exists(FocusLockFile))
            {
                string content = File.ReadAllText(FocusLockFile).Trim();
                int myPid = Environment.ProcessId;
                if (content.StartsWith($"{myPid}:"))
                {
                    File.Delete(FocusLockFile);
                }
            }
        }
        catch { }
    }

    public static (IntPtr hwnd, int pid, string owner) GetForegroundProcess()
    {
        IntPtr fgHwnd = GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return (IntPtr.Zero, 0, "?");

        GetWindowThreadProcessId(fgHwnd, out uint pid);
        string name = "?";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch { }

        return (fgHwnd, (int)pid, name);
    }
}
