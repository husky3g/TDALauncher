<#
    TDA Launcher - build
      src\*.cs -> build\TDALauncher.exe   (uses the C# compiler that ships with Windows / .NET Framework 4)

    Usage: powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

# ---- icon (generated: sun setting behind a palm island, in the banner's colours)
$ico = Join-Path $root 'resources\icon.ico'
if (!(Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force (Split-Path $ico) | Out-Null
    $pngs = @()
    foreach ($size in 256, 48, 32, 16) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.ScaleTransform($size / 256.0, $size / 256.0)
        $rect = New-Object System.Drawing.RectangleF 8, 8, 240, 240
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 48; $d = $r * 2
        $path.AddArc(8, 8, $d, $d, 180, 90); $path.AddArc(248 - $d, 8, $d, $d, 270, 90)
        $path.AddArc(248 - $d, 248 - $d, $d, $d, 0, 90); $path.AddArc(8, 248 - $d, $d, $d, 90, 90); $path.CloseFigure()
        $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(24, 32, 36)), ([System.Drawing.Color]::FromArgb(176, 98, 50)), 90
        $g.FillPath($sky, $path)
        $g.SetClip($path)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 192, 128))), 78, 118, 100, 100)
        $land = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(27, 33, 35))
        $g.FillRectangle($land, 0, 172, 256, 90)
        $g.FillEllipse($land, -40, 150, 200, 60)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(27, 33, 35)), 9
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawBezier($pen, 70, 170, 74, 130, 82, 100, 92, 78)
        foreach ($f in @(@(40, 92), @(52, 66), @(92, 50), @(132, 70), @(140, 100))) { $g.DrawBezier($pen, 92, 78, (92 + $f[0]) / 2, 62, $f[0], $f[1] - 10, $f[0], $f[1]) }
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , @($size, $ms.ToArray())
        $bmp.Dispose()
    }
    $fs = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $s = if ($p[0] -ge 256) { 0 } else { $p[0] }
        $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$p[1].Length); $bw.Write([uint32]$offset)
        $offset += $p[1].Length
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
    [System.IO.File]::WriteAllBytes($ico, $fs.ToArray())
}

# ---- launcher
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (!(Test-Path $csc)) { throw ".NET Framework 4 C# compiler not found: $csc" }
$exe = Join-Path $out 'TDALauncher.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:" + (Join-Path $fw $_) }
$cs = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', "/out:$exe", "/win32icon:$ico") + $refs + $cs
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compiling the launcher failed ($LASTEXITCODE)" }
Write-Host ("Built {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)
