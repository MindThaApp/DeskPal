#requires -Version 7
<#
.SYNOPSIS
    Renders the Store key art (hero, box art, poster) for Desk Pal 1.2 from the Bloop artwork.

.DESCRIPTION
    Builds an HTML/SVG composition per image and rasterises it with headless Microsoft Edge at the exact pixel size.
    Every PNG is then reduced to its image chunks only (no text or time metadata). Nothing is installed and no network
    is used. The art carries no text: the Store overlays its own title.

    Writes to assets/store/1.2.0/art: hero-1920x1080, hero-dark-1920x1080, tile-1080x1080, tile-dark-1080x1080,
    poster-720x1080, poster-dark-720x1080.
#>
[CmdletBinding()]
param([string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'assets\store\1.2.0\art' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$chars = Join-Path $root 'src\DeskPal\Assets\Characters\soft3d'
$f = [System.Globalization.CultureInfo]::InvariantCulture

$edge = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge (msedge.exe) is needed to rasterise the art.' }

# Bloop spans about x -50..52, y -42..42 inside the artwork's viewBox; a square box around it keeps it uncropped.
function Get-Art([string]$Name, [string]$Box = '-62 -62 124 124') {
    $text = [System.IO.File]::ReadAllText((Join-Path $chars "bloop-$Name.svg"))
    $text = [regex]::Replace($text, 'viewBox="[^"]*"', "viewBox=`"$Box`"", 1)
    $text = [regex]::Replace($text, '\swidth="[^"]*"\s+height="[^"]*"', '', 1)
    'data:image/svg+xml;base64,' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text))
}
$art = @{}
foreach ($n in 'idle', 'wave', 'stretch-side-bend') { $art[$n] = Get-Art $n }
$art['stretch-overhead-reach'] = Get-Art 'stretch-overhead-reach' '-70 -85 140 140'

function Remove-PngMetadata([byte[]]$Bytes) {
    $keep = 'IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND'
    $out = [System.IO.MemoryStream]::new()
    $out.Write($Bytes, 0, 8)
    $i = 8
    while ($i -lt $Bytes.Length) {
        $len = ([int]$Bytes[$i] -shl 24) -bor ([int]$Bytes[$i + 1] -shl 16) -bor ([int]$Bytes[$i + 2] -shl 8) -bor [int]$Bytes[$i + 3]
        $type = [System.Text.Encoding]::ASCII.GetString($Bytes, $i + 4, 4)
        if ($keep -contains $type) { $out.Write($Bytes, $i, 12 + $len) }
        $i += 12 + $len
    }
    $out.ToArray()
}

# Palette: calm sky blue from Bloop's body, warm peach/cream for contrast, soft leaf green as a third note.
$themes = @{
    light = @{ bg1 = '#FFF6EC'; bg2 = '#E4F0FD'; blob1 = '#BDDDFB'; blob2 = '#FFD9BE'; blob3 = '#CDEBC9'; shadow = '#5387C8'; glow = '#FFFFFF'; spark = '#FFFFFF' }
    dark  = @{ bg1 = '#1B2740'; bg2 = '#131A2B'; blob1 = '#2E4C80'; blob2 = '#5A3B3A'; blob3 = '#24493F'; shadow = '#000000'; glow = '#7FB3E8'; spark = '#BDDDFB' }
}

function N([double]$v) { $v.ToString('0.##', $f) }

function New-Html([string]$Kind, [string]$Variant, [int]$W, [int]$H) {
    $t = $themes[$Variant]
    $dark = $Variant -eq 'dark'
    $blobOp = if ($dark) { 0.55 } else { 0.8 }
    $glowOp = if ($dark) { 0.35 } else { 0.9 }
    $m = [math]::Min($W, $H)
    switch ($Kind) {
        'hero' {
            # Bloop large on the right; the left half stays empty for the Store title.
            $big = [int]($H * 0.98); $s2 = [int]($H * 0.30); $s3 = [int]($H * 0.2)
            $blobs = @(
                @{ x = $W * 0.76; y = $H * 0.5; r = $H * 0.62; c = 'blob1' },
                @{ x = $W * 0.97; y = $H * 0.12; r = $H * 0.32; c = 'blob2' },
                @{ x = $W * 0.5; y = $H * 1.0; r = $H * 0.36; c = 'blob3' })
            $imgs = @(
                @{ a = 'stretch-overhead-reach'; size = $big; x = $W * 0.76 - $big / 2; y = $H * 0.5 - $big / 2 + 10; op = 1 },
                @{ a = 'idle'; size = $s2; x = $W * 0.5; y = $H * 0.66; op = 0.9 },
                @{ a = 'wave'; size = $s3; x = $W * 0.97 - $s3; y = $H * 0.78; op = 0.85 })
            $sparks = @(@(($W * 0.60), ($H * 0.2), 14), @(($W * 0.93), ($H * 0.46), 10), @(($W * 0.55), ($H * 0.52), 8))
        }
        'tile' {
            $big = [int]($m * 0.82); $s2 = [int]($m * 0.2)
            $blobs = @(
                @{ x = $W * 0.5; y = $H * 0.52; r = $m * 0.46; c = 'blob1' },
                @{ x = $W * 0.88; y = $H * 0.14; r = $m * 0.2; c = 'blob2' },
                @{ x = $W * 0.1; y = $H * 0.9; r = $m * 0.22; c = 'blob3' })
            $imgs = @(
                @{ a = 'stretch-overhead-reach'; size = $big; x = ($W - $big) / 2; y = ($H - $big) / 2 + 8; op = 1 },
                @{ a = 'idle'; size = $s2; x = $W * 0.08; y = $H * 0.1; op = 0.8 })
            $sparks = @(@(($W * 0.82), ($H * 0.3), 14), @(($W * 0.16), ($H * 0.62), 10), @(($W * 0.86), ($H * 0.78), 8))
        }
        default {
            $big = [int]($W * 0.96); $s2 = [int]($W * 0.3)
            $blobs = @(
                @{ x = $W * 0.5; y = $H * 0.44; r = $W * 0.62; c = 'blob1' },
                @{ x = $W * 0.88; y = $H * 0.1; r = $W * 0.32; c = 'blob2' },
                @{ x = $W * 0.12; y = $H * 0.92; r = $W * 0.4; c = 'blob3' })
            $imgs = @(
                @{ a = 'stretch-side-bend'; size = $big; x = ($W - $big) / 2; y = $H * 0.44 - $big / 2; op = 1 },
                @{ a = 'idle'; size = $s2; x = $W * 0.08; y = $H * 0.76; op = 0.9 },
                @{ a = 'wave'; size = $s2; x = $W * 0.62; y = $H * 0.78; op = 0.9 })
            $sparks = @(@(($W * 0.2), ($H * 0.2), 14), @(($W * 0.84), ($H * 0.3), 10), @(($W * 0.5), ($H * 0.9), 9))
        }
    }
    $sb = [System.Text.StringBuilder]::new()
    foreach ($b in $blobs) {
        [void]$sb.Append("<div style='position:absolute;left:$(N ($b.x - $b.r))px;top:$(N ($b.y - $b.r))px;width:$(N (2 * $b.r))px;height:$(N (2 * $b.r))px;border-radius:50%;background:$($t[$b.c]);opacity:$blobOp;filter:blur($([int]($b.r * 0.18))px)'></div>")
    }
    foreach ($i in $imgs) {
        $sz = $i.size
        [void]$sb.Append("<div style='position:absolute;left:$(N ($i.x + $sz * 0.22))px;top:$(N ($i.y + $sz * $(if ($i.a -eq 'stretch-overhead-reach') { 0.885 } else { 0.82 })))px;width:$(N ($sz * 0.56))px;height:$(N ($sz * 0.07))px;border-radius:50%;background:$($t.shadow);opacity:.22;filter:blur($([int]($sz * 0.03 + 2))px)'></div>")
        [void]$sb.Append("<img src='$($art[$i.a])' style='position:absolute;left:$(N $i.x)px;top:$(N $i.y)px;width:${sz}px;height:${sz}px;opacity:$(N $i.op)'>")
    }
    foreach ($s in $sparks) {
        $r = $s[2]
        [void]$sb.Append("<svg style='position:absolute;left:$(N ($s[0] - $r))px;top:$(N ($s[1] - $r))px;opacity:.9' width='$(2 * $r)' height='$(2 * $r)' viewBox='-10 -10 20 20'><path d='M0-10C1-3 3-1 10 0 3 1 1 3 0 10-1 3-3 1-10 0-3-1-1-3 0-10Z' fill='$($t.spark)'/></svg>")
    }
    "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;overflow:hidden;width:${W}px;height:${H}px}" +
    "body{position:relative;background:linear-gradient(160deg,$($t.bg1),$($t.bg2))}</style></head><body>" +
    "<div style='position:absolute;left:0;top:0;width:${W}px;height:${H}px;background:radial-gradient(circle at 70% 45%,$($t.glow) 0,transparent 55%);opacity:$glowOp'></div>" +
    $sb.ToString() + "</body></html>"
}

$jobs = @(
    @{ n = 'hero-1920x1080.png'; k = 'hero'; v = 'light'; w = 1920; h = 1080 },
    @{ n = 'hero-dark-1920x1080.png'; k = 'hero'; v = 'dark'; w = 1920; h = 1080 },
    @{ n = 'tile-1080x1080.png'; k = 'tile'; v = 'light'; w = 1080; h = 1080 },
    @{ n = 'tile-dark-1080x1080.png'; k = 'tile'; v = 'dark'; w = 1080; h = 1080 },
    @{ n = 'poster-720x1080.png'; k = 'poster'; v = 'light'; w = 720; h = 1080 },
    @{ n = 'poster-dark-720x1080.png'; k = 'poster'; v = 'dark'; w = 720; h = 1080 }
)

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dp-storeart-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    foreach ($job in $jobs) {
        $dir = Join-Path $work ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $dir | Out-Null
        $html = Join-Path $dir 'a.html'
        Set-Content -LiteralPath $html -Encoding utf8 -Value (New-Html $job.k $job.v $job.w $job.h)
        Start-Process -FilePath $edge -Wait -NoNewWindow -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--disable-extensions',
            '--force-device-scale-factor=1', "--window-size=$($job.w),$($job.h)",
            "--user-data-dir=$dir\profile", "--screenshot=$dir\out.png", ([System.Uri]$html).AbsoluteUri)
        $png = Join-Path $dir 'out.png'
        if (-not (Test-Path -LiteralPath $png)) { throw "Edge did not render $($job.n)" }
        $bytes = Remove-PngMetadata ([System.IO.File]::ReadAllBytes($png))
        [System.IO.File]::WriteAllBytes((Join-Path $OutDir $job.n), $bytes)
    }
    Write-Host "Wrote $($jobs.Count) PNGs to $OutDir" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
