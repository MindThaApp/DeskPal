#requires -Version 7
<#
.SYNOPSIS
    Renders the MSIX logo PNGs for the Store package from the Bloop artwork.

.DESCRIPTION
    Master: src/DeskPal/Assets/Characters/soft3d/bloop-idle.svg. Headless Microsoft Edge rasterises it onto a transparent
    canvas at each size (one screenshot per asset). Every PNG is then reduced to its image chunks only, so no text or
    time metadata ships. Nothing is installed and no network is used.

    Writes to src/DeskPal/Assets/Store:
      Square44x44Logo.scale-100/125/150/200/400
      Square44x44Logo.targetsize-16..256 (plain, _altform-unplated, _altform-lightunplated)
      Square71x71Logo (SmallTile), Square150x150Logo, Square310x310Logo (LargeTile), Wide310x150Logo, SplashScreen,
      StoreLogo at scale 100/200/400
#>
[CmdletBinding()]
param([string]$Master)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Master) { $Master = Join-Path $root 'src\DeskPal\Assets\Characters\soft3d\bloop-idle.svg' }
$assets = Join-Path $root 'src\DeskPal\Assets\Store'
New-Item -ItemType Directory -Force $assets | Out-Null

$edge = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge (msedge.exe) is needed to rasterise the SVG master.' }

$jobs = [System.Collections.Generic.List[object]]::new()
function Add-Job([string]$Name, [int]$Width, [int]$Height, [double]$LogoFraction) {
    $logo = [int][math]::Round([math]::Min($Width, $Height) * $LogoFraction)
    $jobs.Add([ordered]@{ name = $Name; w = $Width; h = $Height; logo = $logo })
}

foreach ($pair in @(@(100, 44), @(125, 55), @(150, 66), @(200, 88), @(400, 176))) { Add-Job "Square44x44Logo.scale-$($pair[0]).png" $pair[1] $pair[1] 0.92 }
foreach ($s in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') { Add-Job "Square44x44Logo.targetsize-$s$suffix.png" $s $s 0.96 }
}
foreach ($scale in 100, 200, 400) {
    $k = $scale / 100
    Add-Job "Square71x71Logo.scale-$scale.png" ([int][math]::Round(71 * $k)) ([int][math]::Round(71 * $k)) 0.72
    Add-Job "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.62
    Add-Job "Square310x310Logo.scale-$scale.png" (310 * $k) (310 * $k) 0.55
    Add-Job "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.7
    Add-Job "SplashScreen.scale-$scale.png" (620 * $k) (300 * $k) 0.6
    Add-Job "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 0.9
}

# The artwork's viewBox is 160 x 140 with Bloop Bloop itself spans about x -50..52, y -42..42; a square box around that keeps Bloop centred
# and uncropped, and the per-asset fraction adds the padding.
$text = [System.IO.File]::ReadAllText($Master)
$text = [regex]::Replace($text, 'viewBox="[^"]*"', 'viewBox="-56 -55 112 112"', 1)
$text = [regex]::Replace($text, '\swidth="[^"]*"\s+height="[^"]*"', '', 1)
$art = 'data:image/svg+xml;base64,' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text))

# Keeps only the chunks needed to draw the image; drops tEXt/iTXt/zTXt/tIME/eXIf and anything else.
function Remove-PngMetadata([byte[]]$Bytes) {
    $keep = 'IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND', 'sRGB', 'gAMA', 'cHRM'
    $out = [System.IO.MemoryStream]::new()
    $out.Write($Bytes, 0, 8)
    $i = 8
    while ($i -lt $Bytes.Length) {
        $len = ([int]$Bytes[$i] -shl 24) -bor ([int]$Bytes[$i + 1] -shl 16) -bor ([int]$Bytes[$i + 2] -shl 8) -bor [int]$Bytes[$i + 3]
        $type = [System.Text.Encoding]::ASCII.GetString($Bytes, $i + 4, 4)
        $total = 12 + $len
        if ($keep -contains $type) { $out.Write($Bytes, $i, $total) }
        $i += $total
    }
    $out.ToArray()
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dp-storeassets-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $jobs | ForEach-Object -ThrottleLimit 8 -Parallel {
        $job = $_
        $dir = Join-Path $using:work ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $dir | Out-Null
        $left = [math]::Floor(($job.w - $job.logo) / 2); $top = [math]::Floor(($job.h - $job.logo) / 2)
        $html = Join-Path $dir 'a.html'
        Set-Content -LiteralPath $html -Encoding utf8 -Value ("<!doctype html><html><head><style>html,body{margin:0;background:transparent;overflow:hidden}" +
            "img{position:absolute;display:block;left:${left}px;top:${top}px}</style></head><body>" +
            "<img src='$($using:art)' width='$($job.logo)' height='$($job.logo)'></body></html>")
        Start-Process -FilePath $using:edge -Wait -NoNewWindow -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--disable-extensions',
            '--force-device-scale-factor=1', '--default-background-color=00000000', "--window-size=$($job.w),$($job.h)",
            "--user-data-dir=$dir\profile", "--screenshot=$dir\out.png", ([System.Uri]$html).AbsoluteUri)
        $png = Join-Path $dir 'out.png'
        if (-not (Test-Path -LiteralPath $png)) { throw "Edge did not render $($job.name)" }
        Copy-Item -LiteralPath $png -Destination (Join-Path $using:work $job.name) -Force
    }
    foreach ($job in $jobs) {
        $bytes = Remove-PngMetadata ([System.IO.File]::ReadAllBytes((Join-Path $work $job.name)))
        [System.IO.File]::WriteAllBytes((Join-Path $assets $job.name), $bytes)
    }
    Write-Host "Wrote $($jobs.Count) PNGs to $assets" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
