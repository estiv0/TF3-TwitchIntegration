# Ikona powiadomienia w grze z logo (infra\twitch_logo.png) -> lua_mod\content\tf3export\icons\twitch*.tga.
# Ikony powiadomień gry to maski: TGA typ 3 (8-bit grayscale, wiersze od dołu), biały glif na czarnym,
# kolor nadaje gra według guiType. Dwa rozmiary jak w grze: nazwa@2x.tga 48×48 i nazwa_32@2x.tga 64×64.
# Glif = piksele logo wyraźnie ciemniejsze od białego tła (fiolet), oczy i wnętrze dymka zostają dziurami.
param(
    [string]$Source = (Join-Path $PSScriptRoot "twitch_logo.png"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\lua_mod\content\tf3export\icons"),
    [string]$Name = "twitch"
)

$ErrorActionPreference = "Stop"

# System.Drawing kompiluje się w Add-Type bez kłopotów tylko w Windows PowerShell 5.1 (.NET Framework);
# w PowerShell 7 typy GDI+ są rozrzucone po zestawach zależnych od wersji .NET.
if ($PSVersionTable.PSEdition -eq "Core") {
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
        -Source $Source -OutDir $OutDir -Name $Name
    if ($LASTEXITCODE) { throw "make_icon.ps1 failed in Windows PowerShell" }
    return
}

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class Tf3Icon
{
    // Maska logo (255 = glif) przycięta do glifu.
    public static Bitmap Mask(string path)
    {
        int w, h;
        byte[] px;
        using (var src = new Bitmap(path)) { w = src.Width; h = src.Height; px = Read(src); }
        int minX = w, minY = h, maxX = -1, maxY = -1;
        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            // fiolet: kanał zielony dużo niższy niż czerwony/niebieski; tło i środek są jasne
            bool glyph = a > 128 && g < 160 && (r + b) / 2 - g > 40;
            if (!glyph) continue;
            mask[y * w + x] = 255;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) throw new InvalidDataException("No logo pixels found in " + path);
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var outBmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        var buf = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            byte v = mask[(y + minY) * w + x + minX];
            int o = (y * cw + x) * 4;
            buf[o] = buf[o + 1] = buf[o + 2] = v; buf[o + 3] = 255;
        }
        Write(outBmp, buf);
        return outBmp;
    }

    // Glif wpisany w kwadrat size×size z marginesem, wygładzony; TGA typ 3, wiersze od dołu.
    public static void SaveTga(Bitmap mask, int size, int margin, string path)
    {
        using (var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            float box = size - 2 * margin;
            float scale = Math.Min(box / mask.Width, box / mask.Height);
            float dw = mask.Width * scale, dh = mask.Height * scale;
            using (var attrs = new ImageAttributes())
            {
                attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // bez ciemnej obwódki na brzegach
                g.DrawImage(mask, new Rectangle((int)Math.Round((size - dw) / 2), (int)Math.Round((size - dh) / 2),
                    (int)Math.Round(dw), (int)Math.Round(dh)), 0, 0, mask.Width, mask.Height, GraphicsUnit.Pixel, attrs);
            }
        }
        var px = Read(dst);
        using (var fs = File.Create(path))
        {
        var header = new byte[18];
        header[2] = 3; // grayscale, bez kompresji
        header[12] = (byte)size; header[13] = (byte)(size >> 8);
        header[14] = (byte)size; header[15] = (byte)(size >> 8);
        header[16] = 8;
        fs.Write(header, 0, header.Length);
        for (int y = size - 1; y >= 0; y--)
        for (int x = 0; x < size; x++)
            fs.WriteByte(px[(y * size + x) * 4 + 1]);
        }
        }
    }

    private static byte[] Read(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buf, y * bmp.Width * 4, bmp.Width * 4);
            return buf;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void Write(Bitmap bmp, byte[] buf)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(buf, y * bmp.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), bmp.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
    }
}
"@

New-Item -ItemType Directory -Force $OutDir | Out-Null
$mask = [Tf3Icon]::Mask((Resolve-Path $Source).Path)
try {
    # marginesy jak w ikonach gry (building_town_arrow_up: ~6 px przy 48, ~7 px przy 64)
    [Tf3Icon]::SaveTga($mask, 48, 6, (Join-Path $OutDir "$Name@2x.tga"))
    [Tf3Icon]::SaveTga($mask, 64, 8, (Join-Path $OutDir "${Name}_32@2x.tga"))
}
finally { $mask.Dispose() }
Write-Host "Gotowe: $OutDir\$Name@2x.tga, ${Name}_32@2x.tga"
