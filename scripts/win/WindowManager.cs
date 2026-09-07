using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HuashuWinUse;

public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public struct POINT
{
    public int X;
    public int Y;
}

public class WinInfo
{
    public IntPtr Hwnd { get; set; }
    public int Pid { get; set; }
    public string Owner { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool OnScreen { get; set; }

    public string Receipt => $"receipt={Hwnd.ToInt64()}:{Pid}:{X},{Y},{W}x{H}";

    public override string ToString() =>
        $"id={Hwnd.ToInt64()} pid={Pid} owner={Owner} on={(OnScreen ? 1 : 0)} origin=({X},{Y}) {W}x{H} title={Title}";
}

public static class WindowManager
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public const uint DESKTOP_ALL = 0x01FF;

    public static void AttachCurrentThreadToDefaultDesktop()
    {
        try
        {
            IntPtr hDesk = OpenDesktop("default", 0, false, DESKTOP_ALL);
            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    private static readonly HashSet<string> JunkClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "Shell_TrayWnd", "Button", "tooltips_class32", "MSCTFIME UI",
        "IME", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow"
    };

    private static readonly HashSet<string> JunkTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default IME", "MSCTFIME UI", "Settings", "Microsoft Text Input Application"
    };

    public static List<WinInfo> GetAllWindows(bool includeAll = false)
    {
        var list = new List<WinInfo>();

        EnumWindowsProc callback = (hWnd, lParam) =>
        {
            try
            {
                if (!includeAll && !IsWindowVisible(hWnd)) return true;

            int cloaked = 0;
            _ = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            bool isCloaked = cloaked != 0;

            RECT rect;
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
            if (hr != 0)
            {
                GetWindowRect(hWnd, out rect);
            }

            int w = rect.Width;
            int h = rect.Height;

            if (!includeAll && (w <= 10 || h <= 10)) return true;

            var sbTitle = new StringBuilder(512);
            GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString();

            var sbClass = new StringBuilder(256);
            GetClassName(hWnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();

            GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = "?";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch { }

            if (!includeAll)
            {
                if (string.IsNullOrWhiteSpace(title) && (JunkClasses.Contains(className) || procName == "explorer"))
                    return true;
                if (JunkTitles.Contains(title))
                    return true;
            }

            bool onScreen = IsWindowVisible(hWnd) && !IsIconic(hWnd) && !isCloaked;

            list.Add(new WinInfo
            {
                Hwnd = hWnd,
                Pid = (int)pid,
                Owner = procName,
                X = rect.Left,
                Y = rect.Top,
                W = w,
                H = h,
                Title = title,
                OnScreen = onScreen
            });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] EnumWindows error: {ex.Message}");
            }

            return true;
        };

        IntPtr hDesk = OpenDesktop("default", 0, false, DESKTOP_ALL);
        if (hDesk != IntPtr.Zero)
        {
            EnumDesktopWindows(hDesk, callback, IntPtr.Zero);
            CloseDesktop(hDesk);
        }
        else
        {
            EnumWindows(callback, IntPtr.Zero);
        }

        list.Sort((a, b) =>
        {
            int cmp = string.Compare(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : b.Hwnd.ToInt64().CompareTo(a.Hwnd.ToInt64());
        });

        return list;
    }

    public static WinInfo? GetWindow(IntPtr hwnd)
    {
        var all = GetAllWindows(includeAll: true);
        return all.Find(w => w.Hwnd == hwnd);
    }

    public static List<WinInfo> GetSiblings(WinInfo target)
    {
        var all = GetAllWindows(includeAll: true);
        return all.FindAll(s =>
            s.Hwnd != target.Hwnd &&
            Math.Abs(s.X - target.X) < 4 && Math.Abs(s.Y - target.Y) < 4 &&
            Math.Abs(s.W - target.W) < 4 && Math.Abs(s.H - target.H) < 4 &&
            (s.Pid == target.Pid || s.Owner.StartsWith(target.Owner, StringComparison.OrdinalIgnoreCase) || target.Owner.StartsWith(s.Owner, StringComparison.OrdinalIgnoreCase))
        );
    }

    public static (int x, int y, int w, int h)? GetStableBounds(IntPtr hwnd)
    {
        int lastX = -999999, lastY = -999999;
        int hits = 0;
        for (int i = 0; i < 90; i++)
        {
            var w = GetWindow(hwnd);
            if (w == null) return null;
            if (Math.Abs(w.X - lastX) <= 1 && Math.Abs(w.Y - lastY) <= 1)
            {
                hits++;
                if (hits >= 2) return (w.X, w.Y, w.W, w.H);
            }
            else
            {
                hits = 0;
            }
            lastX = w.X;
            lastY = w.Y;
            Thread.Sleep(10);
        }
        var finalW = GetWindow(hwnd);
        return finalW != null ? (finalW.X, finalW.Y, finalW.W, finalW.H) : null;
    }

    public static WinInfo? FindByOwner(string keyword)
    {
        var all = GetAllWindows(includeAll: false);
        all.Sort((a, b) => (b.W * b.H).CompareTo(a.W * a.H));
        return all.Find(w => w.Owner.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static (Point pt, string note) ResolveCoord(string xs, string ys, int winW, int winH, string? refImg)
    {
        // Check for eN@see.json syntax
        if (xs.StartsWith('e') && xs.Contains('@'))
        {
            var parts = xs.Split('@', 2);
            string elRef = parts[0];
            string jsonPath = parts[1];

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Cannot find JSON metadata: {jsonPath}");

            string json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("elements", out var elements))
            {
                foreach (var el in elements.EnumerateArray())
                {
                    if (el.TryGetProperty("ref", out var r) && r.GetString() == elRef)
                    {
                        double cx = el.GetProperty("cx").GetDouble();
                        double cy = el.GetProperty("cy").GetDouble();
                        string title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        return (new Point((int)cx, (int)cy), $"Element {elRef} \"{title}\" -> WinRel({(int)cx},{(int)cy})");
                    }
                }
            }
            throw new InvalidOperationException($"Element {elRef} not found in {jsonPath}");
        }

        if (!double.TryParse(xs, out double x) || !double.TryParse(ys, out double y))
            throw new ArgumentException($"Invalid coordinates: {xs}, {ys}");

        // If screenshot reference provided: scale from image pixels to window dimensions
        if (!string.IsNullOrEmpty(refImg))
        {
            if (!File.Exists(refImg))
                throw new FileNotFoundException($"Reference screenshot not found: {refImg}");

            using var img = Image.FromFile(refImg);
            int iw = img.Width;
            int ih = img.Height;
            double px = x * winW / iw;
            double py = y * winH / ih;
            return (new Point((int)px, (int)py), $"ImgRel({(int)x},{(int)y})@{iw}x{ih} -> WinRel({(int)px},{(int)py})");
        }

        // Normalized coordinates <= 1.0
        if (x <= 1.0 && y <= 1.0 && winW > 0 && winH > 0)
        {
            int px = (int)(x * winW);
            int py = (int)(y * winH);
            return (new Point(px, py), $"Normalized({x:F4},{y:F4}) -> WinRel({px},{py})");
        }

        // Absolute window-relative points
        return (new Point((int)x, (int)y), $"WinRel({(int)x},{(int)y})");
    }

    public static IntPtr GetTopWindowAtPoint(Point screenPoint)
    {
        var pt = new POINT { X = screenPoint.X, Y = screenPoint.Y };
        IntPtr hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return IntPtr.Zero;
        return GetAncestor(hit, GA_ROOT);
    }
}
