using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace HuashuWinUse;

public static class CaptureEngine
{
    private const int PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                     IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    private const uint SRCCOPY = 0x00CC0020;

    public static Bitmap? CaptureWindowBackground(IntPtr hwnd, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        IntPtr hdcWindow = GetDC(hwnd);
        if (hdcWindow == IntPtr.Zero) hdcWindow = GetDC(IntPtr.Zero);

        IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcWindow, w, h);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        bool success = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT);
        if (!success)
        {
            // Fallback without full content flag
            success = PrintWindow(hwnd, hdcMem, 0);
        }

        Bitmap? bmp = null;
        if (success)
        {
            bmp = Image.FromHbitmap(hBitmap);
        }

        SelectObject(hdcMem, hOld);
        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(hwnd, hdcWindow);

        return bmp;
    }

    public static Bitmap? CaptureScreenRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, w, h);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SRCCOPY);

        Bitmap bmp = Image.FromHbitmap(hBitmap);

        SelectObject(hdcMem, hOld);
        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScreen);

        return bmp;
    }

    public static bool LooksBlank(string imagePath)
    {
        if (!File.Exists(imagePath)) return true;

        using var src = Image.FromFile(imagePath);
        int targetW = 64;
        int targetH = Math.Max(1, src.Height * targetW / src.Width);

        using var small = new Bitmap(targetW, targetH);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, targetW, targetH);
        }

        int x0 = targetW * 8 / 100;
        int x1 = targetW - x0;
        int y0 = targetH * 18 / 100;
        int y1 = targetH - targetH * 8 / 100;

        if (x1 <= x0 || y1 <= y0) return true;

        var seenColors = new HashSet<int>();
        for (int cy = y0; cy < y1; cy++)
        {
            for (int cx = x0; cx < x1; cx++)
            {
                Color c = small.GetPixel(cx, cy);
                int hash = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                seenColors.Add(hash);
            }
        }

        return seenColors.Count < 6;
    }

    public static (int width, int height)? Downsample(string srcPath, string dstPath, int maxWidth)
    {
        if (!File.Exists(srcPath)) return null;

        using var src = Image.FromFile(srcPath);
        int outW = Math.Min(maxWidth, src.Width);
        int outH = Math.Max(1, src.Height * outW / src.Width);

        using var bmp = new Bitmap(outW, outH);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, outW, outH);
        }

        bmp.Save(dstPath, ImageFormat.Png);
        return (outW, outH);
    }

    public static string DiffReport(string beforeImg, string afterImg, double hitNormX, double hitNormY)
    {
        if (!File.Exists(beforeImg) || !File.Exists(afterImg))
            return "  差分不可用（前后有一张没截成）";

        const int W = 160;
        using var a = Image.FromFile(beforeImg);
        using var b = Image.FromFile(afterImg);

        int H = Math.Max(1, a.Height * W / a.Width);

        using var ba = new Bitmap(W, H);
        using var bb = new Bitmap(W, H);

        using (var g = Graphics.FromImage(ba)) g.DrawImage(a, 0, 0, W, H);
        using (var g = Graphics.FromImage(bb)) g.DrawImage(b, 0, 0, W, H);

        double r = 0.12;
        int lx0 = (int)(Math.Max(0, hitNormX - r) * W);
        int lx1 = (int)(Math.Min(1, hitNormX + r) * W);
        int ly0 = (int)(Math.Max(0, hitNormY - r) * H);
        int ly1 = (int)(Math.Min(1, hitNormY + r) * H);

        int changed = 0;
        int localChanged = 0;
        int localTotal = 0;
        int minX = W, maxX = -1, minY = H, maxY = -1;

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Color ca = ba.GetPixel(x, y);
                Color cb = bb.GetPixel(x, y);
                int delta = Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);

                bool inLocal = (x >= lx0 && x < lx1 && y >= ly0 && y < ly1);
                if (inLocal) localTotal++;

                if (delta > 24)
                {
                    changed++;
                    if (inLocal) localChanged++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        double pct = (double)changed * 100.0 / (W * H);
        double lpct = localTotal > 0 ? (double)localChanged * 100.0 / localTotal : 0.0;

        if (changed == 0)
        {
            return "  effect=suspected_noop 全窗 0% 变化。按可能性：①坐标没落在控件上 ②窗口没真正激活 ③控件不响应合成事件 ④截图早于刷新";
        }

        string report = $"  落点邻域(±12%) 变化 {lpct:F1}%  |  全窗 {pct:F1}%";
        if (lpct < 1.0)
        {
            double cx = maxX >= 0 ? (double)(minX + maxX) / 2.0 / W : 0;
            double cy = maxY >= 0 ? (double)(minY + maxY) / 2.0 / H : 0;
            report += $"\n  effect=suspected_noop 落点几乎没变，全窗变化集中在 ({cx:F2},{cy:F2})，多半是 app 自己的动画";
        }
        else if (lpct < 8.0)
        {
            report += "\n  effect=partial 落点变化很小，可能只是焦点高亮/光标。看截图确认";
        }
        else
        {
            report += "\n  effect=confirmed 落点确实变了。仍需确认变的是「文字进去」不是「弹出了别的东西」";
        }

        return report;
    }
}
