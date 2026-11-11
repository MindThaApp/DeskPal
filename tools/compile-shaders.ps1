# Regenerates the precompiled shader bytecode next to the HLSL sources. Run after editing a .hlsl file.
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter fxc.exe | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName | Select-Object -Last 1
$dir = Join-Path $PSScriptRoot '..\src\DeskPal\Assets\Shaders'
foreach ($name in 'Mesh', 'Shadow') {
    foreach ($stage in @(@('VS', 'vs_4_0'), @('PS', 'ps_4_0'))) {
        & $fxc.FullName /nologo /O3 /E $stage[0] /T $stage[1] /Fo (Join-Path $dir "$name.$($stage[0]).cso") (Join-Path $dir "$name.hlsl")
        if ($LASTEXITCODE -ne 0) { throw "fxc failed for $name $($stage[0])" }
    }
}
