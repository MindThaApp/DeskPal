#requires -Version 7
<#
.SYNOPSIS
    Builds the unsigned Microsoft Store upload for Desk Pal (x64, or x64 + ARM64 in a bundle with -Arch both).

.DESCRIPTION
    dotnet build with -p:DeskPalStore=true produces an MSIX from src/DeskPal/Package.appxmanifest (Partner Center
    identity). The Store signs the package on ingestion, so it is not signed here, and nothing is installed.

    Output in builds/store/<version>/: DeskPal_<version>_<arch>.msix per architecture and the .msixupload to upload
    (with -Arch both, DeskPal_<version>_x64_arm64.msixbundle inside DeskPal_<version>_x64_arm64.msixupload).
    Refuses to run when an output file already exists; move it away first.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'both')][string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = [xml](Get-Content -LiteralPath (Join-Path $root 'src\DeskPal\Package.appxmanifest') -Raw)
$version = $manifest.Package.Identity.Version
$out = Join-Path $root "builds\store\$version"
$archs = if ($Arch -eq 'both') { @('x64', 'ARM64') } else { @('x64') }
$tag = if ($Arch -eq 'both') { 'x64_arm64' } else { 'x64' }
$msixFiles = @($archs | ForEach-Object { Join-Path $out "DeskPal_${version}_$($_.ToLowerInvariant()).msix" })
$bundleFinal = Join-Path $out "DeskPal_${version}_$tag.msixbundle"
$uploadFinal = Join-Path $out "DeskPal_${version}_$tag.msixupload"
foreach ($f in $msixFiles + $bundleFinal + $uploadFinal) { if (Test-Path -LiteralPath $f) { throw "$f already exists; move it away first." } }
New-Item -ItemType Directory -Force $out | Out-Null

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dp-store-' + [guid]::NewGuid().ToString('N'))
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    for ($i = 0; $i -lt $archs.Count; $i++) {
        $plat = $archs[$i]
        $rid = if ($plat -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        & dotnet build (Join-Path $root 'src\DeskPal\DeskPal.csproj') -c $Configuration -r $rid "-p:Platform=$plat" '-p:Platforms=x64%3BARM64' `
            -p:DeskPalStore=true -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$work\$plat\" -v q -nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $plat ($LASTEXITCODE)" }
        $msix = Get-ChildItem -LiteralPath "$work\$plat" -Recurse -Filter *.msix | Select-Object -First 1
        if (-not $msix) { throw "No .msix produced for $plat" }
        Copy-Item -LiteralPath $msix.FullName -Destination $msixFiles[$i]
        $zip = [System.IO.Compression.ZipFile]::OpenRead($msixFiles[$i])
        try {
            $reader = [System.IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
            $xml = [xml]$reader.ReadToEnd(); $reader.Dispose()
        }
        finally { $zip.Dispose() }
        $id = $xml.Package.Identity
        Write-Host "Package: Name=$($id.Name) Version=$($id.Version) Arch=$($id.ProcessorArchitecture)" -ForegroundColor Green
    }

    $upload = $msixFiles
    if ($archs.Count -gt 1) {
        $makeAppx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $makeAppx) { throw 'makeappx.exe (Windows SDK) is needed for the bundle.' }
        $bundleSrc = Join-Path $work 'bundle-src'
        New-Item -ItemType Directory -Force $bundleSrc | Out-Null
        $msixFiles | ForEach-Object { Copy-Item -LiteralPath $_ -Destination $bundleSrc }
        & $makeAppx.FullName bundle /d $bundleSrc /p $bundleFinal /bv $version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed ($LASTEXITCODE)" }
        $upload = @($bundleFinal)
    }

    # The .msixupload is a zip holding the package (or the bundle).
    $tmpZip = Join-Path $work 'upload.zip'
    Compress-Archive -LiteralPath $upload -DestinationPath $tmpZip
    Copy-Item -LiteralPath $tmpZip -Destination $uploadFinal
    Get-ChildItem -LiteralPath $out -File | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
