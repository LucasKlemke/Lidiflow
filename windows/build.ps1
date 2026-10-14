# Builds Lidiflow for Windows into windows\dist. Pass -Install to install it for the current user
# (%LOCALAPPDATA%\Programs\Lidiflow, with a Start menu shortcut) and launch it.
param([switch]$Install)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = Join-Path $PSScriptRoot 'Lidiflow\Lidiflow.csproj'
$png = Join-Path $PSScriptRoot '..\Resources\AppIcon-1024.png'
$icon = Join-Path $PSScriptRoot 'Lidiflow\Assets\AppIcon.ico'

# The .exe icon: a multi-size .ico with PNG frames, generated from the macOS app icon.
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force (Split-Path $icon) | Out-Null
    $source = [System.Drawing.Image]::FromFile((Resolve-Path $png))
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $frames = foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        , $stream.ToArray()
    }
    $source.Dispose()

    $writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($icon))
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $side = [byte]($sizes[$i] % 256)   # 0 means 256
        $writer.Write($side); $writer.Write($side); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Close()
}

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained false -o $dist
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }
Write-Host "Built $dist\Lidiflow.exe"

if ($Install) {
    Get-Process Lidiflow -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $target = Join-Path $env:LOCALAPPDATA 'Programs\Lidiflow'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $dist $target -Recurse

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Lidiflow.lnk'))
    $shortcut.TargetPath = Join-Path $target 'Lidiflow.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Save()

    Write-Host "Installed $target"
    Start-Process (Join-Path $target 'Lidiflow.exe')
}
