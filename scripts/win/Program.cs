using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace HuashuWinUse;

public static class Program
{
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Die(string msg, int code = 1)
    {
        Console.WriteLine(msg);
        Environment.Exit(code);
    }

    private const string USAGE = """
用法（坐标三种写法通用：≤1 归一化 / >1 窗口内点数 / x y @截图.png 按图上像素；加 --dry 只解析不执行）:
  win windows [owner关键词] [--all]           列窗口：id / pid / owner / on(当前屏幕可见) / origin / 尺寸 / 标题
  win shot <hwnd> <路径>                     截单窗口（后台/被遮挡也能截）。失败自诊断
  win shotfg <hwnd> <路径>                   先试后台，确认空图才借焦点并立刻还
  win see <hwnd|owner关键词> [--out 路径]     一次拿到：降采样截图 + 收据 + UIA 元素表（可用时）→ 之后用 eN@<json> 点
  win clickin <hwnd> <x> <y> [@图] [eN@see.json] [--dry]  按窗口内坐标点击（不激活；被遮挡拒绝）
  win hoverin <hwnd> <x> <y> [@图] [holdms]   真实悬停（组件库菜单要它）
  win click <pid> <x> <y> [@全屏图] [bg]      全局坐标点击（加 bg 走 PostMessage）
  win hover <x> <y> [holdms]
  win scroll <x> <y> <dy> [dx] [steps]        真实滚轮（画布/滚动条）
  win type <pid> <文本> [global]              投递 Unicode（中文可用，不带 global 走 PostMessage）
  win key <pid> <keycode> [ctrl|alt|shift|win] 模拟按键（走全局流，回车有终端保护闸）
  win ax <pid>                                UIA 探测（可编辑控件数统计）
  win axset <pid> <文本>                      通过 UIA ValuePattern 给第一个可编辑控件设值并读回
  win axtoggle <hwnd> <eN|关键词>             通过 UIA TogglePattern 零焦点直接翻转开关状态
  win axclick <hwnd> <eN|关键词>              通过 UIA InvokePattern 零焦点直接触发按钮点击
  win op <hwnd> <x> <y> <文本> [@图] [send <sx> <sy>] [shot <路径>] [--bg|--fast] [--force] [--dry]
                                              写操作默认入口。默认后台优先阶梯：
                                                ① PostMessage/UIA 零焦点写入 → 截图差分验证 → 生效即结束
                                                ② 判不出生效才升级借焦点，升级前必过四道闸：终端send/焦点锁/在场闸/遮挡闸
                                              --bg 只走①不升级  --fast 跳过①直接借焦点  --force 拆掉全部闸
  win hud <毫秒> [文案]                        屏幕四角脉冲取景框，提示用户 agent 正在接管（鼠标穿透/不抢焦点/对录屏隐身）
  win idle                                    用户此刻在不在场：键鼠物理空闲秒数 + 前台 app + 借焦点锁归谁
  win open <显示名|路径> [--cdp 端口] [--relaunch] [--dry] 启动应用并支持带 --remote-debugging-port
  win frontmost                               当前前台 app 进程名与标题
""";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WindowManager.AttachCurrentThreadToDefaultDesktop();

        if (args.Length == 0)
        {
            Die(USAGE, 0);
        }

        var argList = new System.Collections.Generic.List<string>(args);
        string cmd = argList[0].ToLowerInvariant();
        argList.RemoveAt(0);

        bool dry = argList.Remove("--dry");
        bool force = argList.Remove("--force");
        bool fastMode = argList.Remove("--fast");
        bool bgOnly = argList.Remove("--bg");

        string? refImg = null;
        for (int i = 0; i < argList.Count; i++)
        {
            if (argList[i].StartsWith('@'))
            {
                refImg = argList[i][1..];
                argList.RemoveAt(i);
                break;
            }
        }

        switch (cmd)
        {
            case "windows":
                {
                    bool all = argList.Remove("--all");
                    string? filter = argList.Count > 0 ? argList[0] : null;
                    var wins = WindowManager.GetAllWindows(includeAll: all);
                    foreach (var w in wins)
                    {
                        if (!string.IsNullOrEmpty(filter))
                        {
                            if (!w.Owner.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                && !w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        Console.WriteLine(w.ToString());
                    }
                    break;
                }

            case "shot":
                {
                    if (argList.Count < 2) Die("用法: win shot <hwnd> <路径>");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");

                    var hwnd = (IntPtr)hwndVal;
                    string outPath = argList[1];
                    var win = WindowManager.GetWindow(hwnd);
                    if (win == null) Die($"窗口 {hwndVal} 不存在或已关闭（句柄可能已过期，请运行 win windows 重取）");

                    if (SafetyGates.IsScreenLocked())
                    {
                        Die("屏幕已锁定：无法后台截取窗口（CDP / UIA 不受影响）。解锁后重试或改走 CDP shot", 2);
                    }

                    var (capturedWin, note, bmp) = CaptureSmart(win);
                    if (bmp == null)
                    {
                        if (IsChromiumProcess(win.Pid, win.Owner))
                        {
                            Die(GetChromiumDiagnostic(win), 2);
                        }
                        Die($"截取窗口 {hwndVal}（{win.Owner}）失败，且无可用兄弟窗口。可能原因：窗口从未渲染或已最小化", 1);
                    }

                    using (bmp)
                    {
                        string? dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        bool blank = CaptureEngine.LooksBlank(outPath);
                        string blankNote = blank ? "是(黑屏或未渲染，试 shotfg 或 CDP)" : "否";
                        Console.WriteLine($"shot id={capturedWin.Hwnd.ToInt64()} -> {outPath} {bmp.Width}x{bmp.Height}px 判空={blankNote} {capturedWin.Receipt}{note}");
                    }
                    break;
                }

            case "shotfg":
                {
                    if (argList.Count < 2) Die("用法: win shotfg <hwnd> <路径>");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");

                    var hwnd = (IntPtr)hwndVal;
                    string outPath = argList[1];
                    var win = WindowManager.GetWindow(hwnd);
                    if (win == null) Die($"窗口 {hwndVal} 不存在或已关闭");

                    // 1. Try background first
                    using (var bgBmp = CaptureEngine.CaptureWindowBackground(hwnd, win.W, win.H))
                    {
                        if (bgBmp != null)
                        {
                            string? dir = Path.GetDirectoryName(outPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            bgBmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                            if (!CaptureEngine.LooksBlank(outPath))
                            {
                                Console.WriteLine($"截图: {outPath}\n后台直接截到，未动焦点 [OK] {win.Receipt}");
                                return;
                            }
                        }
                    }

                    // 2. Degrade to focus borrow
                    if (!SafetyGates.AcquireFocusLock(5.0))
                    {
                        Die("refused: 另一个 win 进程正持有借焦点锁，等 5 秒未释放", 2);
                    }

                    GateUserPresence("为截图不值得打断用户打字。");
                    FlashHUD($"huashu-win-use 正在截图「{win.Owner}」", 1200);

                    var (prevHwnd, _, prevName) = SafetyGates.GetForegroundProcess();
                    var sw = Stopwatch.StartNew();

                    SafetyGates.ShowWindow(hwnd, SafetyGates.SW_RESTORE);
                    SafetyGates.SetForegroundWindow(hwnd);
                    Thread.Sleep(100);

                    var stableBounds = WindowManager.GetStableBounds(hwnd) ?? (win.X, win.Y, win.W, win.H);

                    // Render catch-up loop (matching mac.swift's 12 iterations with 2 consecutive non-blank frames)
                    string tmpA = Path.Combine(Path.GetTempPath(), $"shotfg-{hwndVal}-tmp.png");
                    bool settled = false;
                    for (int it = 0; it < 12; it++)
                    {
                        Thread.Sleep(60);
                        using var testBmp = CaptureEngine.CaptureScreenRegion(stableBounds.x, stableBounds.y, stableBounds.w, stableBounds.h);
                        if (testBmp != null)
                        {
                            testBmp.Save(tmpA, System.Drawing.Imaging.ImageFormat.Png);
                            if (!CaptureEngine.LooksBlank(tmpA))
                            {
                                Thread.Sleep(60);
                                using var testBmp2 = CaptureEngine.CaptureScreenRegion(stableBounds.x, stableBounds.y, stableBounds.w, stableBounds.h);
                                if (testBmp2 != null)
                                {
                                    testBmp2.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                                    if (!CaptureEngine.LooksBlank(outPath))
                                    {
                                        settled = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (!settled)
                    {
                        using var fallback = CaptureEngine.CaptureScreenRegion(stableBounds.x, stableBounds.y, stableBounds.w, stableBounds.h);
                        fallback?.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    }

                    if (File.Exists(tmpA)) { try { File.Delete(tmpA); } catch { } }

                    if (prevHwnd != IntPtr.Zero && prevHwnd != hwnd)
                    {
                        SafetyGates.SetForegroundWindow(prevHwnd);
                    }
                    SafetyGates.ReleaseFocusLock();
                    sw.Stop();

                    bool isBlank = CaptureEngine.LooksBlank(outPath);
                    string wasFront = (prevHwnd == hwnd)
                        ? $"截图: {outPath}\n目标本就在前台，未动焦点"
                        : $"截图: {outPath}\n后台为空图，借焦点 {sw.Elapsed.TotalSeconds:F2} 秒后已还给 {prevName}";

                    Console.WriteLine(wasFront);
                    if (isBlank)
                    {
                        Console.WriteLine("  [WARNING] 借了焦点仍是空图（可能在其它虚拟桌面、最小化或禁止捕获）。Chromium 系建议改用 cdp.js shot");
                    }
                    break;
                }

            case "see":
                {
                    if (argList.Count < 1) Die("用法: win see <hwnd|owner关键词> [--out 路径]");

                    string? outPath = null;
                    int outIdx = argList.IndexOf("--out");
                    if (outIdx >= 0 && outIdx + 1 < argList.Count)
                    {
                        outPath = argList[outIdx + 1];
                        argList.RemoveRange(outIdx, 2);
                    }

                    WinInfo? target = null;
                    if (long.TryParse(argList[0], out long hwndVal))
                    {
                        target = WindowManager.GetWindow((IntPtr)hwndVal);
                    }
                    else
                    {
                        target = WindowManager.FindByOwner(argList[0]);
                    }

                    if (target == null) Die($"未找到匹配的窗口: {argList[0]}");

                    string rawPath = Path.Combine(Path.GetTempPath(), $"see-raw-{target.Hwnd.ToInt64()}.png");
                    string finalPath = outPath ?? Path.Combine(Path.GetTempPath(), $"see-{target.Hwnd.ToInt64()}.png");

                    using (var bmp = CaptureEngine.CaptureWindowBackground(target.Hwnd, target.W, target.H))
                    {
                        if (bmp != null) bmp.Save(rawPath, System.Drawing.Imaging.ImageFormat.Png);
                        else Die("无法截取目标窗口", 1);
                    }

                    var down = CaptureEngine.Downsample(rawPath, finalPath, 1400);
                    if (down == null) Die("降采样失败", 1);

                    Console.WriteLine($"截图: {finalPath} {down.Value.width}x{down.Value.height}px（点它：win clickin {target.Hwnd.ToInt64()} <图上x> <图上y> @{finalPath}）");
                    Console.WriteLine(target.Receipt);

                    // Extract UIA elements
                    var elements = UIAutomationEngine.ExtractElements(target.Hwnd, target.X, target.Y, target.W, target.H);
                    string jsonPath = finalPath + ".json";

                    var meta = new
                    {
                        receipt = target.Receipt,
                        window = new { id = target.Hwnd.ToInt64(), pid = target.Pid, w = target.W, h = target.H },
                        elements
                    };
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                    if (elements.Count == 0)
                    {
                        Console.WriteLine("UIA 元素表: 无（纯自绘或未暴露）。可用图上像素点，Chromium 系走 cdp.js snapshot");
                    }
                    else
                    {
                        Console.WriteLine($"UIA 元素表 {elements.Count} 个（点它：win clickin {target.Hwnd.ToInt64()} e1@{jsonPath}）:");
                        foreach (var el in elements)
                        {
                            string dis = el.Enabled ? "" : " [disabled]";
                            Console.WriteLine($"  {el.Ref} {el.Role} \"{el.Title}\" ({(int)el.Cx},{(int)el.Cy}){dis}");
                        }
                    }
                    break;
                }

            case "clickin":
            case "hoverin":
                {
                    bool isElRef = argList.Count > 1 && argList[1].StartsWith('e') && argList[1].Contains('@');
                    if (!isElRef && argList.Count < 3) Die($"用法: win {cmd} <hwnd> <x> <y> [@图] [--dry]");
                    if (argList.Count < 2) Die($"用法: win {cmd} <hwnd> eN@see.json [--dry]");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");

                    var hwnd = (IntPtr)hwndVal;
                    var win = WindowManager.GetWindow(hwnd);
                    if (win == null) Die($"窗口 {hwndVal} 不存在");

                    string ys = isElRef ? "0" : argList[2];
                    var (pt, note) = WindowManager.ResolveCoord(argList[1], ys, win.W, win.H, refImg);
                    var screenPt = new Point(win.X + pt.X, win.Y + pt.Y);

                    if (dry)
                    {
                        Console.WriteLine($"dry: {cmd} 窗口{hwndVal} {note} on={(win.OnScreen ? 1 : 0)}");
                        var topHwnd = WindowManager.GetTopWindowAtPoint(screenPt);
                        string occl = (topHwnd == hwnd) ? "pass" : $"BLOCK(上层是窗口 {topHwnd.ToInt64()})";
                        Console.WriteLine($"  闸预检: 遮挡={occl}  在场={GetPresencePreview()}");
                        return;
                    }

                    if (!force)
                    {
                        var topHwnd = WindowManager.GetTopWindowAtPoint(screenPt);
                        if (topHwnd != IntPtr.Zero && topHwnd != hwnd)
                        {
                            Die($"refused: occluded 落点 ({screenPt.X},{screenPt.Y}) 上层是窗口 {topHwnd.ToInt64()}，不是目标。加 --force 强制点击", 2);
                        }
                        GateUserPresence($"{cmd} 走全局键鼠指针。");
                    }

                    if (cmd == "clickin")
                    {
                        if (isElRef)
                        {
                            string elRef = argList[1].Split('@')[0];
                            var (l1Ok, l1Msg) = UIAutomationEngine.TriggerElement(hwnd, elRef, win.X, win.Y, win.W, win.H);
                            if (l1Ok)
                            {
                                Console.WriteLine($"clicked (L1 UIA) {l1Msg} -> 零焦点直触发 [OK]");
                                Console.WriteLine($"-> 必须回读：win shot {hwndVal} <路径>");
                                break;
                            }
                            Console.WriteLine($"[L1 fallback] {l1Msg}，降级走 L2 窗口坐标点击");
                        }

                        InputSimulator.GetCursorPos(out POINT saved);
                        InputSimulator.ClickAt(screenPt);
                        InputSimulator.MoveMouse(new Point(saved.X, saved.Y));
                        Console.WriteLine($"clicked {note} -> 全局({screenPt.X},{screenPt.Y}) pid {win.Pid}");
                        Console.WriteLine($"-> 必须回读：win shot {hwndVal} <路径>");
                    }
                    else
                    {
                        int holdMs = 700;
                        if (argList.Count > 3 && int.TryParse(argList[3], out int hm)) holdMs = hm;
                        InputSimulator.MoveMouse(screenPt);
                        Thread.Sleep(holdMs);
                        Console.WriteLine($"hovered {note} held {holdMs}ms");
                    }
                    break;
                }

            case "click":
            case "hover":
                {
                    bool isClick = cmd == "click";
                    int need = isClick ? 3 : 2;
                    if (argList.Count < need)
                    {
                        Die($"用法: win {cmd} {(isClick ? "<pid> " : "")}<x> <y> [@全屏图] [bg|holdms] [--dry]");
                    }

                    int pid = 0;
                    int xi = 0;
                    if (isClick)
                    {
                        if (!int.TryParse(argList[0], out pid)) Die("pid 必须是有效数字");
                        xi = 1;
                    }

                    var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                    var (pt, note) = WindowManager.ResolveCoord(argList[xi], argList[xi + 1], screen.Width, screen.Height, refImg);

                    bool bg = isClick && argList.Count > 3 && argList[3].Equals("bg", StringComparison.OrdinalIgnoreCase);

                    if (dry)
                    {
                        Console.WriteLine($"dry: {cmd} 全局 {note}{(bg ? " postToPid" : "")}");
                        if (isClick && !bg)
                        {
                            var topHwnd = WindowManager.GetTopWindowAtPoint(pt);
                            var topWin = WindowManager.GetWindow(topHwnd);
                            string occl = (topWin != null && topWin.Pid == pid)
                                ? $"pass(就是目标 pid {pid})"
                                : $"BLOCK(「{topWin?.Owner ?? "?"}」pid {topWin?.Pid ?? 0})";
                            Console.WriteLine($"  闸预检: 落点上层={occl}  在场={GetPresencePreview()}");
                        }
                        return;
                    }

                    if (isClick && !bg && !force)
                    {
                        var topHwnd = WindowManager.GetTopWindowAtPoint(pt);
                        var topWin = WindowManager.GetWindow(topHwnd);
                        if (topWin != null && topWin.Pid != pid)
                        {
                            Die($"refused: occluded 落点 ({pt.X},{pt.Y}) 上层是「{topWin.Owner}」(pid {topWin.Pid})，不是目标 pid {pid}。点下去会戳到它。\n改 win clickin（按窗口定位）或 CDP；确认要点加 --force", 2);
                        }
                    }

                    if (!bg)
                    {
                        GateUserPresence("全局坐标点击/悬停跟用户共用同一个鼠标指针。");
                    }

                    if (isClick)
                    {
                        InputSimulator.GetCursorPos(out POINT saved);
                        if (bg)
                        {
                            var wins = WindowManager.GetAllWindows(includeAll: true);
                            var targetWin = wins.Find(w => w.Pid == pid && w.OnScreen) ?? wins.Find(w => w.Pid == pid);
                            if (targetWin != null)
                            {
                                Point rel = new Point(pt.X - targetWin.X, pt.Y - targetWin.Y);
                                InputSimulator.ClickAt(Point.Empty, bg: true, targetHwnd: targetWin.Hwnd, relPoint: rel);
                            }
                            else
                            {
                                Die($"找不到 pid {pid} 对应的窗口", 1);
                            }
                        }
                        else
                        {
                            InputSimulator.ClickAt(pt);
                            InputSimulator.MoveMouse(new Point(saved.X, saved.Y));
                        }
                        Console.WriteLine($"clicked {note} {(bg ? "postToPid" : "global")} -> pid {pid}");
                    }
                    else
                    {
                        int holdMs = 700;
                        if (argList.Count > 2 && int.TryParse(argList[2], out int hm)) holdMs = hm;
                        InputSimulator.GetCursorPos(out POINT cur);
                        Point start = new Point(cur.X, cur.Y);
                        for (int s = 1; s <= 8; s++)
                        {
                            double f = (double)s / 8.0;
                            int nx = (int)(start.X + (pt.X - start.X) * f);
                            int ny = (int)(start.Y + (pt.Y - start.Y) * f);
                            InputSimulator.MoveMouse(new Point(nx, ny));
                            Thread.Sleep(20);
                        }
                        Thread.Sleep(holdMs);
                        Console.WriteLine($"hovered {note} held {holdMs}ms");
                    }
                    break;
                }

            case "scroll":
                {
                    if (argList.Count < 3) Die("用法: win scroll <x> <y> <dy> [dx] [steps]");
                    if (!int.TryParse(argList[0], out int sx)) Die("坐标必须是有效数字");
                    if (!int.TryParse(argList[1], out int sy)) Die("坐标必须是有效数字");
                    if (!int.TryParse(argList[2], out int dy)) Die("滚动量必须是有效数字");
                    int dx = (argList.Count > 3 && int.TryParse(argList[3], out int dxx)) ? dxx : 0;
                    int steps = (argList.Count > 4 && int.TryParse(argList[4], out int st)) ? st : 6;

                    GateUserPresence("滚轮走全局流，会滚到他正在看的窗口上。");
                    InputSimulator.ScrollMouse(new Point(sx, sy), dy, dx, steps);
                    Console.WriteLine($"scrolled at ({sx},{sy}) dy={dy} dx={dx} x{steps}");
                    break;
                }

            case "type":
                {
                    if (argList.Count < 2) Die("用法: win type <pid> <文本> [global] [--dry]");
                    if (!int.TryParse(argList[0], out int pid)) Die("pid 必须是有效数字");

                    string text = argList[1];
                    bool global = argList.Count > 2 && argList[2].Equals("global", StringComparison.OrdinalIgnoreCase);

                    if (dry)
                    {
                        var fg = SafetyGates.GetForegroundProcess();
                        Console.WriteLine($"dry: type {text.Length} 字 -> pid {pid} ({(global ? "global 打给前台" : "PostMessage 直投")})");
                        Console.WriteLine($"  闸预检: frontmost={(fg.pid == pid ? "pass" : $"BLOCK(前台是 {fg.owner} pid={fg.pid})")} 在场={GetPresencePreview()}");
                        return;
                    }

                    if (global)
                    {
                        var fg = SafetyGates.GetForegroundProcess();
                        if (!force && fg.pid != pid)
                        {
                            Die($"refused: frontmost 前台 pid {fg.pid} 不是目标 pid {pid}，global 会把字打进前台应用。先激活目标或去掉 global", 2);
                        }
                        GateUserPresence("global 会把字打进前台窗口。");
                        InputSimulator.TypeUnicode(text, IntPtr.Zero, global: true);
                    }
                    else
                    {
                        // Background PostMessage WM_CHAR
                        var wins = WindowManager.GetAllWindows(includeAll: true);
                        var targetWin = wins.Find(w => w.Pid == pid && w.OnScreen) ?? wins.Find(w => w.Pid == pid);
                        if (targetWin != null)
                        {
                            InputSimulator.TypeUnicode(text, targetWin.Hwnd, global: false);
                        }
                        else
                        {
                            Die($"找不到 pid {pid} 对应的窗口", 1);
                        }
                    }

                    Console.WriteLine($"typed {text.Length} chars -> pid {pid} ({(global ? "global" : "PostMessage")})");
                    break;
                }

            case "key":
                {
                    if (argList.Count < 2) Die("用法: win key <pid> <keycode> [ctrl|alt|shift|win] [--force] [--dry]");
                    if (!int.TryParse(argList[0], out int pid)) Die("pid 必须是有效数字");
                    if (!ushort.TryParse(argList[1], out ushort vk)) Die("按键码必须是有效数字");

                    bool ctrl = argList.Contains("ctrl", StringComparer.OrdinalIgnoreCase);
                    bool alt = argList.Contains("alt", StringComparer.OrdinalIgnoreCase);
                    bool shift = argList.Contains("shift", StringComparer.OrdinalIgnoreCase);
                    bool win = argList.Contains("win", StringComparer.OrdinalIgnoreCase);

                    var fg = SafetyGates.GetForegroundProcess();

                    if (dry)
                    {
                        Console.WriteLine($"dry: key {vk} 会打给前台「{fg.owner}」(pid {fg.pid})");
                        string shellCheck = (vk == 13 && SafetyGates.ShellOwners.Contains(fg.owner)) ? $"BLOCK({fg.owner} 是终端/IDE 类)" : "pass";
                        Console.WriteLine($"  闸预检: frontmost={(fg.pid == pid ? "pass" : $"BLOCK(目标 pid {pid} 不在前台)")} 回车={shellCheck} 在场={GetPresencePreview()}");
                        return;
                    }

                    if (!force && fg.pid != pid)
                    {
                        Die($"refused: frontmost 前台是「{fg.owner}」(pid {fg.pid})，不是目标 pid {pid}。按键会打错地方。\n先激活目标再按，或加 --force", 2);
                    }

                    if (!force && vk == 13 && SafetyGates.ShellOwners.Contains(fg.owner))
                    {
                        Die($"refused: 前台是 {fg.owner}（终端/IDE 类），这一下回车等于执行命令。确认要执行加 --force", 2);
                    }

                    GateUserPresence($"按键会打给前台窗口「{fg.owner}」。");
                    InputSimulator.SendKey(vk, ctrl, alt, shift, win);
                    Console.WriteLine($"key {vk} -> 前台 app (pid {pid} 仅回显)");
                    break;
                }

            case "ax":
                {
                    if (argList.Count < 1) Die("用法: win ax <pid>");
                    if (!int.TryParse(argList[0], out int pid)) Die("pid 必须是有效数字");

                    int count = UIAutomationEngine.CountEditables(pid, printDetails: true);
                    Console.WriteLine($"UIA 可编辑控件统计: {count}");
                    if (count > 0)
                    {
                        Console.WriteLine("-> L1 有希望，可用 win axset 实写并截图验证");
                    }
                    else
                    {
                        Console.WriteLine("-> L1 无望：Chromium 系走 CDP，其它走 L2 窗口坐标");
                    }
                    break;
                }

            case "axset":
                {
                    if (argList.Count < 2) Die("用法: win axset <pid> <文本>");
                    if (!int.TryParse(argList[0], out int pid)) Die("pid 必须是有效数字");

                    string text = argList[1];
                    var (ok, readback) = UIAutomationEngine.SetFirstEditableValue(pid, text);
                    Console.WriteLine($"UIA ValuePattern.SetValue result={(ok ? "OK" : "FAIL")}, 读回=\"{readback}\"");
                    Console.WriteLine("-> 必须回读：截图看发送键或状态指示器是否响应");
                    break;
                }

            case "axclick":
            case "axtoggle":
                {
                    if (argList.Count < 2) Die("用法: win axclick|axtoggle <hwnd> <eN|名称关键词>");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");

                    var hwnd = (IntPtr)hwndVal;
                    var win = WindowManager.GetWindow(hwnd);
                    if (win == null) Die($"窗口 {hwndVal} 不存在");

                    string target = argList[1];
                    (bool ok, string msg) res;
                    if (target.StartsWith('e') && int.TryParse(target[1..], out _))
                    {
                        res = UIAutomationEngine.TriggerElement(hwnd, target, win.X, win.Y, win.W, win.H);
                    }
                    else
                    {
                        res = UIAutomationEngine.TriggerElementByName(hwnd, target);
                    }

                    if (res.ok)
                    {
                        Console.WriteLine($"[L1 UIA] {res.msg} [OK]");
                    }
                    else
                    {
                        Die($"[L1 UIA] 触发失败: {res.msg}", 1);
                    }
                    break;
                }

            case "restore":
            case "show":
                {
                    if (argList.Count < 1) Die("用法: win restore <hwnd>");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");
                    var hwnd = (IntPtr)hwndVal;
                    SafetyGates.ShowWindow(hwnd, SafetyGates.SW_RESTORE);
                    SafetyGates.SetForegroundWindow(hwnd);
                    Console.WriteLine($"窗口 {hwndVal} 已恢复显示");
                    break;
                }

            case "op":
                {
                    bool isElRef = argList.Count > 1 && argList[1].StartsWith('e') && argList[1].Contains('@');
                    int minNeed = isElRef ? 2 : 3;
                    if (argList.Count < minNeed) Die("用法: win op <hwnd> <x> <y> [文本] [@图] [send <sx> <sy>] [shot <路径>] [--bg|--fast] [--force] [--dry]");
                    if (!long.TryParse(argList[0], out long hwndVal)) Die("窗口句柄必须是有效数字");

                    var hwnd = (IntPtr)hwndVal;
                    var win = WindowManager.GetWindow(hwnd);
                    if (win == null) Die($"窗口 {hwndVal} 不存在");

                    string xs = argList[1];
                    string ys = isElRef ? "0" : argList[2];

                    int textIdx = isElRef ? 2 : 3;
                    string text = "";
                    if (argList.Count > textIdx &&
                        !argList[textIdx].Equals("send", StringComparison.OrdinalIgnoreCase) &&
                        !argList[textIdx].Equals("shot", StringComparison.OrdinalIgnoreCase) &&
                        !argList[textIdx].StartsWith('@'))
                    {
                        text = argList[textIdx];
                        textIdx++;
                    }

                    string? sendXs = null, sendYs = null, shotPath = null;
                    int i = textIdx;
                    while (i < argList.Count)
                    {
                        if (argList[i].Equals("send", StringComparison.OrdinalIgnoreCase) && i + 2 < argList.Count)
                        {
                            sendXs = argList[i + 1];
                            sendYs = argList[i + 2];
                            i += 3;
                        }
                        else if (argList[i].Equals("shot", StringComparison.OrdinalIgnoreCase) && i + 1 < argList.Count)
                        {
                            shotPath = argList[i + 1];
                            i += 2;
                        }
                        else { i++; }
                    }

                    var (clickPt, clickNote) = WindowManager.ResolveCoord(xs, ys, win.W, win.H, refImg);
                    (Point pt, string note)? sendPt = null;
                    if (sendXs != null && sendYs != null)
                    {
                        sendPt = WindowManager.ResolveCoord(sendXs, sendYs, win.W, win.H, refImg);
                    }

                    string plan = fastMode ? "--fast 直接借焦点" : (bgOnly ? "--bg 只走后台档，不升级" : "默认阶梯：先后台档，未生效才升级借焦点");

                    if (dry)
                    {
                        Console.WriteLine($"dry: op 窗口{hwndVal}（{win.Owner}）点击 {clickNote}{(sendPt.HasValue ? $"，发送 {sendPt.Value.note}" : "")} 输入{text.Length}字");
                        Console.WriteLine($"  策略: {plan}  用户此刻空闲 {SafetyGates.UserIdleSeconds():F1} 秒");
                        string shellCheck = (SafetyGates.ShellOwners.Contains(win.Owner) && sendPt.HasValue) ? $"BLOCK({win.Owner} 是终端/IDE 类，需 --force)" : "pass";
                        Console.WriteLine($"  闸预检: 终端send={shellCheck} 焦点锁={(SafetyGates.CheckFocusLock() ?? "free")} 在场={GetPresencePreview()}");
                        return;
                    }

                    if (SafetyGates.ShellOwners.Contains(win.Owner) && sendPt.HasValue && !force)
                    {
                        Die($"refused: {win.Owner} 是终端/IDE 类窗口，点发送等于执行命令。只填不发去掉 send；确认执行加 --force", 2);
                    }

                    // --- L1 UIA 语义直投（零焦点/不受坐标高分屏干扰）---
                    if (isElRef && string.IsNullOrEmpty(text))
                    {
                        string elRef = argList[1].Split('@')[0];
                        var (l1Ok, l1Msg) = UIAutomationEngine.TriggerElement(hwnd, elRef, win.X, win.Y, win.W, win.H);
                        if (l1Ok)
                        {
                            Console.WriteLine($"① L1 UIA 语义直触发 -> {l1Msg} [OK]");
                            if (!string.IsNullOrEmpty(shotPath))
                            {
                                Thread.Sleep(600);
                                using var bAfter = CaptureEngine.CaptureWindowBackground(hwnd, win.W, win.H);
                                if (bAfter != null)
                                {
                                    bAfter.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                                    Console.WriteLine($"截图: {shotPath}");
                                }
                            }
                            return;
                        }
                    }

                    // --- 第一档：后台直投 ---
                    if (!fastMode)
                    {
                        string bp = Path.Combine(Path.GetTempPath(), $"opbg-before-{hwndVal}.png");
                        string ap = shotPath ?? Path.Combine(Path.GetTempPath(), $"opbg-after-{hwndVal}.png");

                        using (var b0 = CaptureEngine.CaptureWindowBackground(hwnd, win.W, win.H))
                        {
                            if (b0 != null) b0.Save(bp, System.Drawing.Imaging.ImageFormat.Png);
                        }

                        // Background post click
                        InputSimulator.ClickAt(Point.Empty, bg: true, targetHwnd: hwnd, relPoint: clickPt);
                        Thread.Sleep(100);

                        if (!string.IsNullOrEmpty(text))
                        {
                            InputSimulator.TypeUnicode(text, hwnd, global: false);
                        }

                        if (sendPt.HasValue)
                        {
                            Thread.Sleep(120);
                            InputSimulator.ClickAt(Point.Empty, bg: true, targetHwnd: hwnd, relPoint: sendPt.Value.pt);
                        }

                        Console.WriteLine($"① 后台档 PostMessage（零焦点/不受遮挡）-> pid {win.Pid} 点击 {clickNote}");
                        Thread.Sleep(1500);

                        using (var b1 = CaptureEngine.CaptureWindowBackground(hwnd, win.W, win.H))
                        {
                            if (b1 != null) b1.Save(ap, System.Drawing.Imaging.ImageFormat.Png);
                        }

                        if (!File.Exists(bp) || !File.Exists(ap) || CaptureEngine.LooksBlank(ap))
                        {
                            if (bgOnly) Die("-> --bg 只走后台档，截图不可信无法断言", 2);
                            Die("""
                            -> 🔴 停在这里，不自动升级借焦点。
                            后台档的事件已经投出去了，但验证不可信（截图空/失败），它很可能已经生效——
                            再走一遍借焦点就是重复写入。
                            先换硬判据确认：CDP 读 DOM / UIA 读值 / app 状态指示器 / 任务落盘文件。
                            确认「确实未生效」再运行 win op --fast（跳过后台档直接借焦点）。
                            """, 2);
                        }
                        else
                        {
                            double hitNormX = (double)clickPt.X / Math.Max(win.W, 1);
                            double hitNormY = (double)clickPt.Y / Math.Max(win.H, 1);
                            string diff = CaptureEngine.DiffReport(bp, ap, hitNormX, hitNormY);
                            Console.WriteLine(diff);

                            if (diff.Contains("effect=confirmed") || diff.Contains("effect=partial"))
                            {
                                Console.WriteLine("-> 后台档已生效，全程零焦点，用户完全不受打扰 [OK]");
                                return;
                            }
                        }

                        if (bgOnly) Die("-> --bg 只走后台档，未检测到有效变化，退出", 2);
                        Console.WriteLine("\n[UPGRADE] 后台档判定未生效（落点零变化），升级到借焦点档");
                    }

                    // --- 第二档：借焦点 ---
                    if (!SafetyGates.AcquireFocusLock(5.0))
                    {
                        Die("refused: 另一个 win 进程正持有借焦点锁，等 5 秒未释放", 2);
                    }

                    if (!force)
                    {
                        GateUserPresence("借焦点会抢走前台窗口。");
                    }
                    FlashHUD($"huashu-win-use 正在操作「{win.Owner}」", 1600);

                    var (prevFgHwnd, _, prevFgName) = SafetyGates.GetForegroundProcess();
                    InputSimulator.GetCursorPos(out POINT savedCur);

                    // Watchdog: auto restore focus if stuck > 12s
                    var cts = new CancellationTokenSource();
                    var watchdog = new Thread(() =>
                    {
                        if (cts.Token.WaitHandle.WaitOne(12000)) return;
                        if (prevFgHwnd != IntPtr.Zero) SafetyGates.SetForegroundWindow(prevFgHwnd);
                        SafetyGates.ReleaseFocusLock();
                        Console.WriteLine("refused: op 超过 12 秒未完成，已还焦点。多半是模态对话框阻塞或 app 无响应");
                        Environment.Exit(2);
                    }) { IsBackground = true };
                    watchdog.Start();

                    var tStart = Stopwatch.StartNew();

                    SafetyGates.ShowWindow(hwnd, SafetyGates.SW_RESTORE);
                    SafetyGates.SetForegroundWindow(hwnd);
                    Thread.Sleep(100);

                    var stableBounds = WindowManager.GetStableBounds(hwnd) ?? (win.X, win.Y, win.W, win.H);
                    var screenClick = new Point(stableBounds.x + clickPt.X, stableBounds.y + clickPt.Y);
                    InputSimulator.ClickAt(screenClick);
                    Thread.Sleep(200);

                    if (!string.IsNullOrEmpty(text))
                    {
                        InputSimulator.TypeUnicode(text, IntPtr.Zero, global: true);
                    }

                    if (sendPt.HasValue)
                    {
                        Thread.Sleep(100);
                        var screenSend = new Point(stableBounds.x + sendPt.Value.pt.X, stableBounds.y + sendPt.Value.pt.Y);
                        InputSimulator.ClickAt(screenSend);
                    }

                    // Immediately restore focus
                    if (prevFgHwnd != IntPtr.Zero && prevFgHwnd != hwnd)
                    {
                        SafetyGates.SetForegroundWindow(prevFgHwnd);
                    }
                    InputSimulator.MoveMouse(new Point(savedCur.X, savedCur.Y));
                    SafetyGates.ReleaseFocusLock();
                    cts.Cancel();
                    tStart.Stop();

                    Console.WriteLine($"焦点占用 {tStart.Elapsed.TotalSeconds:F2} 秒，已归还给 {prevFgName}");
                    Console.WriteLine($"点击: {clickNote}{(sendPt.HasValue ? $"，发送 {sendPt.Value.note}" : "")}");

                    if (!string.IsNullOrEmpty(shotPath))
                    {
                        Thread.Sleep(1500);
                        using var bAfter = CaptureEngine.CaptureWindowBackground(hwnd, win.W, win.H);
                        if (bAfter != null)
                        {
                            bAfter.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                            Console.WriteLine($"截图: {shotPath}");
                        }
                    }
                    break;
                }

            case "hud":
                {
                    double ms = 1600;
                    if (argList.Count > 0 && double.TryParse(argList[0], out double m)) ms = m;
                    string text = argList.Count > 1 ? argList[1] : "huashu-win-use 正在接管屏幕";
                    Application.Run(new HudForm(text, ms));
                    break;
                }

            case "idle":
                {
                    double s = SafetyGates.UserIdleSeconds();
                    var fg = SafetyGates.GetForegroundProcess();
                    string verdict = s < SafetyGates.IDLE_NEED ? "[BUSY] 用户在场（借焦点档会先等他停手）" : "[FREE] 用户空闲（借焦点档可直接走）";
                    Console.WriteLine($"键鼠物理空闲 {s:F1} 秒（阈值 {SafetyGates.IDLE_NEED:F0}s）  前台 app: {fg.owner} (pid {fg.pid})\n{verdict}");
                    string? lockInfo = SafetyGates.CheckFocusLock();
                    Console.WriteLine(lockInfo != null ? $"[LOCKED] 借焦点锁状态: {lockInfo}" : "[UNLOCKED] 借焦点锁空闲");
                    Console.WriteLine("注：读操作（windows/shot/see/ax/CDP）不看这个闸，任何时候都能跑");
                    break;
                }

            case "frontmost":
                {
                    var fg = SafetyGates.GetForegroundProcess();
                    Console.WriteLine($"{fg.owner} (pid {fg.pid})");
                    break;
                }

            case "open":
                {
                    if (argList.Count < 1) Die("用法: win open <显示名|路径> [--cdp 端口] [--relaunch] [--dry]");
                    string name = argList[0];
                    string? cdpPort = null;
                    int cdpIdx = argList.IndexOf("--cdp");
                    if (cdpIdx >= 0 && cdpIdx + 1 < argList.Count)
                    {
                        cdpPort = argList[cdpIdx + 1];
                    }
                    bool relaunch = argList.Contains("--relaunch");

                    string exePath = FindAppExecutable(name);
                    if (string.IsNullOrEmpty(exePath))
                    {
                        Die($"未找到应用「{name}」的可执行文件。请提供绝对路径", 1);
                    }

                    string exeName = Path.GetFileNameWithoutExtension(exePath);
                    var runningProcs = Process.GetProcessesByName(exeName);
                    Process? running = runningProcs.Length > 0 ? runningProcs[0] : null;

                    Console.WriteLine($"app: {exePath}  运行中={(running != null ? $"是(pid {running.Id})" : "否")}");

                    if (cdpPort != null)
                    {
                        if (CdpAlive(cdpPort))
                        {
                            Console.WriteLine($"CDP: 端口 {cdpPort} 已通 [OK]  下一步: node cdp.js {cdpPort} list");
                            return;
                        }

                        if (running != null)
                        {
                            if (!relaunch)
                            {
                                Die("CDP 未开而 app 正在运行。带端口必须重启它（会关掉当前窗口）：确认后加 --relaunch 重跑，或让用户自己退出后再跑本命令", 2);
                            }

                            if (dry)
                            {
                                Console.WriteLine($"dry: 将退出 pid {running.Id} 并以 --remote-debugging-port={cdpPort} 重启");
                                return;
                            }

                            try
                            {
                                foreach (var p in runningProcs)
                                {
                                    try { p.CloseMainWindow(); } catch { }
                                }

                                var swExit = Stopwatch.StartNew();
                                bool gone = false;
                                while (swExit.Elapsed.TotalSeconds < 30)
                                {
                                    if (Process.GetProcessesByName(exeName).Length == 0)
                                    {
                                        gone = true;
                                        break;
                                    }
                                    Thread.Sleep(100);
                                }

                                if (!gone)
                                {
                                    foreach (var p in Process.GetProcessesByName(exeName))
                                    {
                                        try { p.Kill(); } catch { }
                                    }
                                }
                                Thread.Sleep(500);
                            }
                            catch (Exception ex)
                            {
                                Die($"退出应用失败: {ex.Message}", 2);
                            }
                        }

                        if (dry)
                        {
                            Console.WriteLine($"dry: {exePath} --remote-debugging-port={cdpPort}");
                            return;
                        }

                        string argsStr = $"--remote-debugging-port={cdpPort}";
                        Process.Start(new ProcessStartInfo { FileName = exePath, Arguments = argsStr, UseShellExecute = true });

                        Console.Write($"等待 CDP 端口 {cdpPort} 就绪");
                        for (int it = 0; it < 40; it++)
                        {
                            Thread.Sleep(500);
                            if (CdpAlive(cdpPort))
                            {
                                Console.WriteLine($"\nCDP: 端口 {cdpPort} 已通 [OK]（启动后等待约 {(it + 1) * 0.5:F1}s）  下一步: node cdp.js {cdpPort} list");
                                return;
                            }
                            Console.Write(".");
                        }
                        Console.WriteLine();
                        Die($"启动了但 20 秒内 {cdpPort}/json/version 没应答。可能不是 Chromium 系应用，或端口被占用", 2);
                    }

                    if (dry)
                    {
                        Console.WriteLine($"dry: 启动 {exePath}");
                        return;
                    }

                    Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                    Console.WriteLine($"已启动: {exePath}");
                    break;
                }

            default:
                Die($"未知命令: {cmd}\n{USAGE}");
                break;
        }
    }

    private static string GetPresencePreview()
    {
        double v = SafetyGates.UserIdleSeconds();
        return v >= SafetyGates.IDLE_NEED
            ? $"pass(空闲{v:F1}s)"
            : $"WAIT(用户{v:F1}s前动过，最多等{SafetyGates.IDLE_WAIT:F0}s)";
    }

    private static void GateUserPresence(string what)
    {
        var (passed, waited, last) = SafetyGates.WaitUserIdle();
        if (!passed)
        {
            Die($"refused: 用户正在用电脑（{last:F1} 秒前还在动键鼠），等了 {waited:F0} 秒未停手。\n{what}；确实要现在做加 --force", 2);
        }
        if (waited > 0.5)
        {
            Console.WriteLine($"[WAIT] 用户刚在动键鼠，等他停手 {waited:F1} 秒后才动手");
        }
    }

    private static void FlashHUD(string text, int ms)
    {
        if (Environment.GetEnvironmentVariable("WIN_HUD") == "0") return;
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"hud {ms} \"{text}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
        }
        catch { }
    }

    private static string FindAppExecutable(string name)
    {
        if (File.Exists(name)) return Path.GetFullPath(name);

        string withExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        if (File.Exists(withExe)) return Path.GetFullPath(withExe);

        // Check PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(p.Trim(), withExe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        // Check App Paths in registry
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{withExe}");
            if (key?.GetValue(null) is string regPath && File.Exists(regPath))
            {
                return regPath;
            }
        }
        catch { }

        // Search Start Menu
        string[] startMenuPaths = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
        };

        foreach (var dir in startMenuPaths)
        {
            if (!Directory.Exists(dir)) continue;
            var lnks = Directory.GetFiles(dir, $"*{name}*.lnk", SearchOption.AllDirectories);
            if (lnks.Length > 0)
            {
                string target = ResolveShortcut(lnks[0]);
                if (File.Exists(target)) return target;
            }
        }

        // Search AppData Local Programs
        string localProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Directory.Exists(localProg))
        {
            var exes = Directory.GetFiles(localProg, $"*{name}*.exe", SearchOption.AllDirectories);
            if (exes.Length > 0) return exes[0];
        }

        return string.Empty;
    }

    private static string ResolveShortcut(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return string.Empty;
            dynamic? shell = Activator.CreateInstance(shellType);
            dynamic? shortcut = shell?.CreateShortcut(lnkPath);
            return (string)(shortcut?.TargetPath ?? "");
        }
        catch { return string.Empty; }
    }

    private static (WinInfo win, string note, Bitmap? bmp) CaptureSmart(WinInfo win)
    {
        var bmp = CaptureEngine.CaptureWindowBackground(win.Hwnd, win.W, win.H);
        if (bmp != null) return (win, "", bmp);

        var siblings = WindowManager.GetSiblings(win);
        foreach (var s in siblings)
        {
            var sbmp = CaptureEngine.CaptureWindowBackground(s.Hwnd, s.W, s.H);
            if (sbmp != null)
            {
                return (s, $"\n  [INFO] 壳窗口截不到，已改截同位置兄弟窗口 id={s.Hwnd.ToInt64()}（{s.Owner}）。以后直接用这个 id", sbmp);
            }
        }

        return (win, "", null);
    }

    private static bool IsChromiumProcess(int pid, string owner)
    {
        if (owner.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            owner.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
            owner.Contains("electron", StringComparison.OrdinalIgnoreCase) ||
            owner.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            owner.Contains("slack", StringComparison.OrdinalIgnoreCase) ||
            owner.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            string? mainModule = proc.MainModule?.FileName;
            if (mainModule != null)
            {
                string dir = Path.GetDirectoryName(mainModule) ?? "";
                if (File.Exists(Path.Combine(dir, "chrome_elf.dll")) ||
                    File.Exists(Path.Combine(dir, "cef.dll")) ||
                    File.Exists(Path.Combine(dir, "resources", "app.asar")))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static string GetChromiumDiagnostic(WinInfo win)
    {
        string cdpjs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cdp.js");
        if (!File.Exists(cdpjs))
        {
            cdpjs = Path.Combine(AppContext.BaseDirectory, "..", "cdp.js");
        }
        string head = $"窗口 {win.Hwnd.ToInt64()}（{win.Owner}）截不到：它是 Chromium 系（Electron/CEF），后台不保留渲染帧 → Win32 PrintWindow 对它无解。改走 CDP（不受遮挡影响）：\n";
        return head + $"  win open \"{win.Owner}\" --cdp 9333 --relaunch   # 注意：会重启应用，未保存内容会丢\n  node \"{cdpjs}\" 9333 shot auto <路径>";
    }

    private static bool CdpAlive(string port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = client.GetStringAsync($"http://127.0.0.1:{port}/json/version").Result;
            return resp.Contains("webSocketDebuggerUrl");
        }
        catch { return false; }
    }
}
