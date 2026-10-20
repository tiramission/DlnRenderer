param(
    [long]$Hwnd,
    [string]$OutFile = "$env:TEMP\dln-media\printwindow.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class PwCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    // PW_RENDERFULLCONTENT = 2 also captures DirectComposition/D3D content.
    public static string Capture(IntPtr hwnd, string path)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return "GetWindowRect failed";
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "empty rect";

        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) return "PrintWindow failed";
            }

            bmp.Save(path, ImageFormat.Png);

            var colors = new System.Collections.Generic.HashSet<string>();
            int saturated = 0, total = 0;
            for (int x = 0; x < w; x += 11)
            {
                for (int y = 0; y < h; y += 11)
                {
                    var c = bmp.GetPixel(x, y);
                    colors.Add(c.R + "," + c.G + "," + c.B);
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                    int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mx - mn > 40) saturated++;
                    total++;
                }
            }

            return string.Format("size={0}x{1} distinct={2} saturated={3}/{4} file={5}", w, h, colors.Count, saturated, total, path);
        }
    }
}
"@

Write-Output ([PwCapture]::Capture([IntPtr]$Hwnd, $OutFile))
