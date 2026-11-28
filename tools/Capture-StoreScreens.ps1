<#
.SYNOPSIS
    Captures the Desk Pal Store screenshots (light + dark, clean + captioned) for every listing language.

.DESCRIPTION
    Run with Windows PowerShell 5 (powershell.exe): PowerShell 7 has no System.Drawing.
    Each shot launches a Debug x64 copy of the app from a private folder in %TEMP% with its own data folder
    (DESKPAL_DEBUG_DATA_DIR), seeded with sample progress. Only the app's own window is captured, with
    PrintWindow(PW_RENDERFULLCONTENT); nothing reads the screen. Test windows sit on the secondary monitor
    (left of the primary one); a window with its left edge at 0 or more aborts the shot. No input is sent.
    Each capture is composed centred on a gradient backdrop at 1920x1080 and stripped to image chunks only.

    Output: assets\store\1.2.0\screenshots\<locale>\<theme>\clean|captioned\NN-name.png
    Captions come from assets\store\1.2.0\screenshots\captions.json (locale -> one text per shot).

.EXAMPLE
    powershell.exe -NoProfile -File tools\Capture-StoreScreens.ps1 -Locales en-us -Themes light
#>
[CmdletBinding()]
param(
    [string[]]$Locales = @('en-us','de-de','fr-fr','es-es','it-it','pt-br','ja-jp','zh-cn','zh-hant','ko-kr','ru-ru','pl-pl','nl-nl','tr-tr','ar-sa','hi-in','id-id','vi-vn','th-th','sv-se'),
    [string[]]$Themes = @('light','dark'),
    [string[]]$Shots = @(),
    [string]$AppDir,
    [string]$Work,
    [string]$Result,
    [string]$WinPos = '-1880,46',
    [int]$MinX = -1920
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $repo 'assets\store\1.2.0\screenshots'
if (-not $Work) { $Work = Join-Path (Join-Path $env:LOCALAPPDATA 'Temp') 'deskpal-store-shots' }
if (-not $AppDir) { $AppDir = Join-Path $Work 'app' }
if (-not $Result) { $Result = Join-Path $Work 'result.txt' }
New-Item -ItemType Directory -Force $Work | Out-Null
$exe = Join-Path $AppDir 'DeskPal.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Missing $exe (publish a Debug x64 copy there first)." }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class SW {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RC r, int sz);
  [StructLayout(LayoutKind.Sequential)] public struct RC { public int L, T, R, B; }
  public static List<IntPtr> For(uint pid) {
    var l = new List<IntPtr>();
    EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
}
"@
[void][SW]::SetProcessDpiAwarenessContext([IntPtr](-4))

$tags = @{ 'en-us'='en-US'; 'de-de'='de-DE'; 'fr-fr'='fr-FR'; 'es-es'='es-ES'; 'it-it'='it-IT'; 'pt-br'='pt-BR'; 'ja-jp'='ja-JP'; 'zh-cn'='zh-Hans'; 'zh-hant'='zh-Hant'; 'ko-kr'='ko-KR'; 'ru-ru'='ru-RU'; 'pl-pl'='pl-PL'; 'nl-nl'='nl-NL'; 'tr-tr'='tr-TR'; 'ar-sa'='ar-SA'; 'hi-in'='hi-IN'; 'id-id'='id-ID'; 'vi-vn'='vi-VN'; 'th-th'='th-TH'; 'sv-se'='sv-SE' }
$fontFor = @{ 'ja-jp'='Yu Gothic UI'; 'zh-cn'='Microsoft YaHei UI'; 'zh-hant'='Microsoft JhengHei UI'; 'ko-kr'='Malgun Gothic'; 'th-th'='Leelawadee UI'; 'hi-in'='Nirmala UI' }

# n = file name, a = launch flags, wait = seconds after the window shows, pick = which window, scroll = settings scroll percent, idx = caption index
$shotList = @(
    @{ n='01-home';       a='--debug-main --debug-page=home';     wait=9;  pick='largest';  scroll=-1; idx=0; stretch=$false },
    @{ n='04-garden';     a='--debug-main --debug-page=garden';   wait=8;  pick='largest';  scroll=-1; idx=3; stretch=$false },
    @{ n='05-bloop';      a='--debug-main --debug-page=bloop';    wait=9;  pick='largest';  scroll=-1; idx=4; stretch=$false },
    @{ n='06-reminders';  a='--debug-main --debug-page=settings'; wait=6;  pick='largest';  scroll=0;  idx=5; stretch=$false },
    @{ n='07-appearance'; a='--debug-main --debug-page=settings'; wait=6;  pick='largest';  scroll=34; idx=6; stretch=$false }
)
$Shots = @($Shots | ForEach-Object { $_ -split ',' } | Where-Object { $_ }); $Locales = @($Locales | ForEach-Object { $_ -split ',' }); $Themes = @($Themes | ForEach-Object { $_ -split ',' })
if ($Shots.Count -gt 0) { $shotList = @($shotList | Where-Object { $Shots -contains $_.n }) }

$captions = Get-Content -Raw -Encoding UTF8 (Join-Path $outRoot 'captions.json') | ConvertFrom-Json
$installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families | ForEach-Object { $_.Name }

function Log([string]$m) { Add-Content -LiteralPath $Result -Value ((Get-Date -Format 'HH:mm:ss') + ' ' + $m) }

function Write-Seed([string]$dir, [string]$tag, [string]$theme, [bool]$stretch) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $today = Get-Date
    $first = [System.Globalization.CultureInfo]::CurrentCulture.DateTimeFormat.FirstDayOfWeek
    $ws = $today.Date.AddDays(-(([int]$today.DayOfWeek - [int]$first + 7) % 7))
    $hist = @()
    for ($i = 9; $i -ge 1; $i--) {
        $hist += @{ Date = $today.AddDays(-$i).ToString('yyyy-MM-dd'); Stretches = 5 + ($i % 3); EyeBreaks = 4 + ($i % 2); Walks = 2; GoalStretches = 5; GoalEyes = 4; GoalWalks = 2; Relaxed = $false }
    }
    $state = @{
        StretchesCompleted = 63; CountersDate = $today.ToString('yyyy-MM-dd'); StretchesToday = 4; EyeBreaksToday = 3; WalksToday = 1; CarePoints = 420
        Streaks = @{ RecapShownWeek = $ws.ToString("yyyy-MM-dd"); History = $hist; Bests = @(9, 9, 9, 9); Celebrated = @(9, 9, 9, 9) }
        Ui = @{ FirstRunShown = $true; CloseHintShown = $true; X = -1900; Y = 60; Width = 1180; Height = 900 }
    }
    $themeName = 'Light'; if ($theme -eq 'dark') { $themeName = 'Dark' }
    $settings = @{
        SettingsVersion = 99; Language = $tag; SproutStage = 3; AvatarStyle = '3d'; CrashReports = 'Never'
        WorkingHours = @{ Days = @('Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday'); Start = '00:00:00'; End = '23:59:00' }; MovementEnabled = $true; EyesEnabled = $true; Water = 'WithBreaks'; UseNudgeCard = $true
        Appearance = @{ Theme = $themeName }
        StretchWithBloop = @{ Enabled = $stretch }
    }
    [System.IO.File]::WriteAllText((Join-Path $dir 'state.json'), ($state | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding $false))
    [System.IO.File]::WriteAllText((Join-Path $dir 'settings.json'), ($settings | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding $false))
}

function Get-Wins([int]$procId) {
    $list = @()
    foreach ($h in [SW]::For([uint32]$procId)) {
        $r = New-Object SW+RC
        [void][SW]::GetWindowRect($h, [ref]$r)
        $list += [pscustomobject]@{ H = $h; L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
    }
    $list
}

function Test-OwnProcess([int]$procId, [string]$folder) {
    $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue).CommandLine
    if (-not $cl) { return $false }
    $a = $folder.ToLowerInvariant(); $b = $a.Replace('\', '/')
    $c = $cl.ToLowerInvariant()
    return ($c.Contains($a) -or $c.Contains($b))
}

# Stops any DeskPal.exe whose command line is under our own work folder (matches both \ and / separators).
function Stop-OwnInstances {
    foreach ($proc in @(Get-CimInstance Win32_Process -Filter "Name='DeskPal.exe'" -ErrorAction SilentlyContinue)) {
        $cl = ([string]$proc.CommandLine).ToLowerInvariant().Replace('/', '\')
        if ($cl.Contains('\deskpal-store-shots\app\')) {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            Log ('  stopped own instance ' + $proc.ProcessId)
        }
    }
    Start-Sleep -Milliseconds 700
}

function Close-App($p) {
    if ($null -eq $p) { return }
    try {
        if (-not $p.HasExited) {
            [void]$p.CloseMainWindow()
            Start-Sleep -Milliseconds 800
        }
        if (-not $p.HasExited -and (Test-OwnProcess $p.Id $Work)) { Stop-Process -Id $p.Id -Force }
    } catch { }
}

# Returns a Bitmap of the chosen own window, or $null (with a reason in $script:why).
function Capture-Shot($shot, [string]$dataDir) {
    $script:why = ''; Stop-OwnInstances
    $env:DESKPAL_DEBUG_DATA_DIR = $dataDir
    $env:DESKPAL_DEBUG_WINPOS = $WinPos
    $p = $null
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $shot.a -PassThru
        $deadline = (Get-Date).AddSeconds(120)
        $pick = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 700
            if ($p.HasExited) { $script:why = 'app exited early'; return $null }
            $wins = @(Get-Wins $p.Id | Where-Object { $_.W -ge 300 -and $_.Ht -ge 120 })
            if ($wins.Count -gt 0) { $pick = $wins; break }
        }
        if (-not $pick) { $script:why = ('no window; own windows: ' + ((Get-Wins $p.Id | ForEach-Object { "$($_.L),$($_.T) $($_.W)x$($_.Ht)" }) -join ' | ')); return $null }
        Start-Sleep -Seconds $shot.wait
        if ($shot.scroll -ge 0) {
            $main = @(Get-Wins $p.Id | Sort-Object { $_.W * $_.Ht } -Descending)[0]
            if ($main.L -ge 0) { $script:why = 'window not on secondary monitor'; return $null }
            $ae = [System.Windows.Automation.AutomationElement]::FromHandle($main.H)
            $all = $ae.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($e in $all) {
                $pat = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pat)) {
                    if ($pat.Current.VerticallyScrollable) { $pat.SetScrollPercent(-1, $shot.scroll); break }
                }
            }
            Start-Sleep -Seconds 2
        }
        $wins = @(Get-Wins $p.Id)
        foreach ($w in $wins) {
            if ($w.L -ge 0) {
                $script:why = ('visible window left edge ' + $w.L + ' (not on secondary monitor)')
                return $null
            }
        }
        $cand = @($wins | Where-Object { $_.W -ge 300 -and $_.Ht -ge 120 })
        if ($cand.Count -eq 0) { $script:why = ("no window big enough: " + (($wins | ForEach-Object { "$($_.L),$($_.T) $($_.W)x$($_.Ht)" }) -join " | ")); return $null }
        if ($shot.pick -eq 'smallest') { $t = @($cand | Sort-Object { $_.W * $_.Ht })[0] } else { $t = @($cand | Sort-Object { $_.W * $_.Ht } -Descending)[0] }
        $bmp = New-Object System.Drawing.Bitmap $t.W, $t.Ht
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $dc = $g.GetHdc()
        [void][SW]::PrintWindow($t.H, $dc, 2)
        $g.ReleaseHdc($dc); $g.Dispose()
        # Blank/black check on a sample grid.
        $min = 255; $max = 0
        for ($yy = 8; $yy -lt $bmp.Height; $yy += [math]::Max(1, [int]($bmp.Height / 12))) {
            for ($xx = 8; $xx -lt $bmp.Width; $xx += [math]::Max(1, [int]($bmp.Width / 12))) {
                $c = $bmp.GetPixel($xx, $yy); $v = [int](($c.R + $c.G + $c.B) / 3)
                if ($v -lt $min) { $min = $v }; if ($v -gt $max) { $max = $v }
            }
        }
        if (($max - $min) -lt 6) { $bmp.Dispose(); $script:why = ('blank capture (' + $t.W + 'x' + $t.Ht + ')'); return $null }
        # Crop to the visible frame (PrintWindow also returns the invisible resize border as black).
        $fr = New-Object SW+RC
        if ([SW]::DwmGetWindowAttribute($t.H, 9, [ref]$fr, 16) -eq 0) {
            $cx = [math]::Max(0, $fr.L - $t.L); $cy = [math]::Max(0, $fr.T - $t.T)
            $cw = [math]::Min($bmp.Width - $cx, $fr.R - $fr.L); $ch = [math]::Min($bmp.Height - $cy, $fr.B - $fr.T)
            if ($cw -gt 100 -and $ch -gt 60) {
                $crop = $bmp.Clone((New-Object System.Drawing.Rectangle $cx, $cy, $cw, $ch), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $bmp.Dispose(); $bmp = $crop
            }
        }
        Log ('  ' + $shot.n + ' windows: ' + (($wins | ForEach-Object { "$($_.L),$($_.T) $($_.W)x$($_.Ht)" }) -join ' | ') + ' -> ' + $bmp.Width + 'x' + $bmp.Height)
        return $bmp
    } finally {
        Close-App $p; Stop-OwnInstances
    }
}

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $gp.AddArc($x, $y, $d, $d, 180, 90)
    $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    $gp
}

function Compose($win, [string]$theme, [string]$caption, [string]$loc) {
    $W = 1920; $H = 1080
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    if ($theme -eq 'dark') { $c1 = [System.Drawing.Color]::FromArgb(18, 22, 42); $c2 = [System.Drawing.Color]::FromArgb(44, 30, 72); $ink = [System.Drawing.Color]::FromArgb(240, 244, 255) }
    else { $c1 = [System.Drawing.Color]::FromArgb(226, 239, 255); $c2 = [System.Drawing.Color]::FromArgb(248, 234, 255); $ink = [System.Drawing.Color]::FromArgb(30, 41, 70) }
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.Rectangle 0, 0, $W, $H), $c1, $c2, 35.0)
    $g.FillRectangle($br, 0, 0, $W, $H); $br.Dispose()

    $top = 80; $availH = $H - 160
    if ($caption) { $top = 190; $availH = $H - 190 - 70 }
    $availW = $W - 200
    $scale = [math]::Min([math]::Min($availW / $win.Width, $availH / $win.Height), 1.6)
    $dw = [int]($win.Width * $scale); $dh = [int]($win.Height * $scale)
    $dx = [int](($W - $dw) / 2); $dy = [int]($top + ($availH - $dh) / 2)

    for ($i = 14; $i -ge 1; $i--) {
        $sh = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(7, 0, 0, 0))
        $gp = New-RoundedPath ($dx - $i) ($dy - $i + 10) ($dw + 2 * $i) ($dh + 2 * $i) (12 + $i)
        $g.FillPath($sh, $gp); $gp.Dispose(); $sh.Dispose()
    }
    $clip = New-RoundedPath $dx $dy $dw $dh 10
    $g.SetClip($clip)
    $g.DrawImage($win, $dx, $dy, $dw, $dh)
    $g.ResetClip(); $clip.Dispose()

    if ($caption) {
        $family = 'Segoe UI Variable Display'
        if ($fontFor.ContainsKey($loc)) { $family = $fontFor[$loc] }
        if ($installed -notcontains $family) { $family = 'Segoe UI' }
        $size = 64.0
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.LineAlignment = 'Center'; $fmt.Alignment = 'Center'
        $rect = New-Object System.Drawing.RectangleF 100, 30, 1720, 140
        if ($loc -eq 'ar-sa') { $fmt.FormatFlags = [System.Drawing.StringFormatFlags]::DirectionRightToLeft; $fmt.Alignment = 'Near' }
        do {
            $font = New-Object System.Drawing.Font ($family, [single]$size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $m = $g.MeasureString($caption, $font, 1720, $fmt)
            if ($m.Width -le 1700 -or $size -le 28) { break }
            $font.Dispose(); $size -= 2
        } while ($true)
        $tb = New-Object System.Drawing.SolidBrush $ink
        $g.DrawString($caption, $font, $tb, $rect, $fmt)
        $tb.Dispose(); $font.Dispose(); $fmt.Dispose()
    }
    $g.Dispose()
    $bmp
}

# Keeps only the chunks needed to draw the image (same set as tools/New-StoreAssets.ps1).
function Save-CleanPng($bmp, [string]$path) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray(); $ms.Dispose()
    $keep = 'IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND', 'sRGB', 'gAMA', 'cHRM'
    $out = New-Object System.IO.MemoryStream
    $out.Write($bytes, 0, 8)
    $i = 8
    while ($i -lt $bytes.Length) {
        $len = ([int]$bytes[$i] -shl 24) -bor ([int]$bytes[$i + 1] -shl 16) -bor ([int]$bytes[$i + 2] -shl 8) -bor [int]$bytes[$i + 3]
        $type = [System.Text.Encoding]::ASCII.GetString($bytes, $i + 4, 4)
        $total = 12 + $len
        if ($keep -contains $type) { $out.Write($bytes, $i, $total) }
        $i += $total
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    $out.Dispose()
}

Set-Content -LiteralPath $Result -Value ('START ' + (Get-Date -Format 's'))
$ok = 0; $failed = 0; $n = 0
try {
    foreach ($loc in $Locales) {
        if (-not $tags.ContainsKey($loc)) { Log "skip unknown locale $loc"; continue }
        foreach ($theme in $Themes) {
            foreach ($shot in $shotList) {
                $n++
                $data = Join-Path $Work ('data-' + $n)
                $win = $null
                try {
                    Write-Seed $data $tags[$loc] $theme $shot.stretch
                    $win = Capture-Shot $shot $data
                    if ($null -eq $win) { $failed++; Log ("FAIL $loc $theme " + $shot.n + ': ' + $script:why); continue }
                    $capArr = @($captions.$loc); $capText = $capArr[$shot.idx]
                    $dirBase = Join-Path $outRoot "$loc\$theme"
                    $clean = Compose $win $theme '' $loc
                    Save-CleanPng $clean (Join-Path $dirBase ('clean\' + $shot.n + '.png')); $clean.Dispose()
                    $cap = Compose $win $theme $capText $loc
                    Save-CleanPng $cap (Join-Path $dirBase ('captioned\' + $shot.n + '.png')); $cap.Dispose()
                    $ok++; Log ("ok $loc $theme " + $shot.n)
                } catch {
                    $failed++; Log ("FAIL $loc $theme " + $shot.n + ': ' + $_.Exception.Message)
                } finally {
                    if ($win) { $win.Dispose() }
                    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
} finally {
    # Leftover test instances: only processes whose command line is under our own work folder.
    Get-CimInstance Win32_Process -Filter "Name='DeskPal.exe'" | ForEach-Object {
        if (Test-OwnProcess $_.ProcessId $Work) { Log ('left over instance ' + $_.ProcessId + ' stopped'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    Log ("DONE ok=$ok failed=$failed")
}
