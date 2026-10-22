using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HuashuWinUse;

public static class InputSimulator
{
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint KEYEVENTF_KEYDOWN = 0x0000;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_CHAR = 0x0102;

    private static readonly string TrailPath = Path.Combine(Path.GetTempPath(), "huashu-win-synthetic.trail");

    public static void MarkSynthetic()
    {
        try
        {
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllText(TrailPath, now.ToString("F3"));
        }
        catch { }
    }

    public static double? GetSyntheticAgo()
    {
        try
        {
            if (!File.Exists(TrailPath)) return null;
            string text = File.ReadAllText(TrailPath).Trim();
            if (double.TryParse(text, out double ts))
            {
                double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                double diff = now - ts;
                return diff >= 0 ? diff : null;
            }
        }
        catch { }
        return null;
    }

    public static void ClickAt(Point screenPoint, bool bg = false, IntPtr targetHwnd = default, Point? relPoint = null)
    {
        if (bg && targetHwnd != IntPtr.Zero && relPoint.HasValue)
        {
            // PostMessage background click directly to target window
            IntPtr lParam = (IntPtr)((relPoint.Value.Y << 16) | (relPoint.Value.X & 0xFFFF));
            PostMessage(targetHwnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
            Thread.Sleep(50);
            PostMessage(targetHwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
            Thread.Sleep(120);
            return;
        }

        // Global synthetic input
        SetCursorPos(screenPoint.X, screenPoint.Y);
        Thread.Sleep(30);

        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } }
        };

        SendInput(1, new[] { inputs[0] }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(50);
        SendInput(1, new[] { inputs[1] }, Marshal.SizeOf<INPUT>());

        MarkSynthetic();
        Thread.Sleep(50);
    }

    public static void MoveMouse(Point screenPoint)
    {
        SetCursorPos(screenPoint.X, screenPoint.Y);
        MarkSynthetic();
    }

    public static void ScrollMouse(Point screenPoint, int dy, int dx, int steps)
    {
        SetCursorPos(screenPoint.X, screenPoint.Y);
        Thread.Sleep(100);

        for (int i = 0; i < steps; i++)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = MOUSEEVENTF_WHEEL,
                        mouseData = (uint)dy
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            MarkSynthetic();
            Thread.Sleep(40);
        }
    }

    public static void TypeUnicode(string text, IntPtr targetHwnd, bool global)
    {
        if (!global && targetHwnd != IntPtr.Zero)
        {
            // Background PostMessage WM_CHAR
            foreach (char c in text)
            {
                PostMessage(targetHwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(10);
            }
            Thread.Sleep(120);
            return;
        }

        // Global SendInput KEYEVENTF_UNICODE
        foreach (char c in text)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            };

            SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5);
            SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
        }

        MarkSynthetic();
    }

    public static void SendKey(ushort vkCode, bool ctrl, bool alt, bool shift, bool win)
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_MENU = 0x12;
        const ushort VK_SHIFT = 0x10;
        const ushort VK_LWIN = 0x5B;

        void Key(ushort vk, bool down)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = down ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP
                    }
                }
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
        }

        if (ctrl) Key(VK_CONTROL, true);
        if (alt) Key(VK_MENU, true);
        if (shift) Key(VK_SHIFT, true);
        if (win) Key(VK_LWIN, true);

        Key(vkCode, true);
        Thread.Sleep(20);
        Key(vkCode, false);

        if (win) Key(VK_LWIN, false);
        if (shift) Key(VK_SHIFT, false);
        if (alt) Key(VK_MENU, false);
        if (ctrl) Key(VK_CONTROL, false);

        MarkSynthetic();
    }
}
