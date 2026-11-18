$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtimeDir = Join-Path $repoRoot 'Runtime'
$artifactDir = Join-Path $PSScriptRoot 'artifacts'
if (-not (Test-Path $artifactDir)) { New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null }
$outDll = Join-Path $artifactDir 'env-culler-tests.dll'

$sources = @(
    (Join-Path $runtimeDir 'ICullResettable.cs'),
    (Join-Path $runtimeDir 'CullCategoryConfig.cs'),
    (Join-Path $runtimeDir 'EnvironmentDistanceCuller.cs'),
    (Join-Path $PSScriptRoot 'EnvironmentCullingTests.cs')
)

$compiler = $null
$compilerType = $null
$vsPaths = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
)
foreach ($cand in $vsPaths) {
    if (Test-Path $cand) { $compiler = $cand; $compilerType = 'csc'; break }
}
if (-not $compiler) {
    $vsRoslyn = Get-ChildItem -Path "${env:ProgramFiles}\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio" -Recurse -Filter "csc.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\Roslyn\csc.exe" } | Select-Object -First 1 -ExpandProperty FullName
    if ($vsRoslyn -and (Test-Path $vsRoslyn)) { $compiler = $vsRoslyn; $compilerType = 'csc' }
}
if (-not $compiler -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $sdk = (& dotnet --list-sdks 2>$null | Select-Object -Last 1)
    if ($sdk -match '^([^ ]+) \[(.+)\]') {
        $cscDll = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
        if (Test-Path $cscDll) { $compiler = $cscDll; $compilerType = 'dotnet' }
    }
}
if (-not $compiler) { throw "No Roslyn C# compiler (VS Roslyn csc.exe or .NET SDK csc.dll) located." }

if ($compilerType -eq 'csc') {
    & $compiler /nologo /optimize+ /target:library /warn:0 /out:$outDll $sources
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
} else {
    $fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
    if (-not (Test-Path $fx)) { $fx = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
    & dotnet $compiler /nologo /optimize+ /target:library /nostdlib /warn:0 /r:"$fx\mscorlib.dll" /r:"$fx\System.dll" /r:"$fx\System.Core.dll" /out:$outDll $sources
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
}

$bytes = [IO.File]::ReadAllBytes($outDll)
[Reflection.Assembly]::Load($bytes) | Out-Null
[CullingTestAndBenchmarkRunner]::Run()
