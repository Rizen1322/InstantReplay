param(
    [string]$OutputDir
)

# Сборка Aura.Media64.dll (src\native\Aura.Media): прослойка прямого NVENC и
# шумоподавление микрофона RNNoise. Тем же Zig, что и хук захвата.
# Библиотека грузится в процесс Aura, а не внедряется в чужой, поэтому хэш-манифест
# ей не нужен: подменить её можно только вместе с самим Aura.exe.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repoRoot "src\native\Aura.Media"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\native\win-x64\Release"
} elseif (-not [IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$zigExe = & (Join-Path $PSScriptRoot "get_zig.ps1")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $zigExe -PathType Leaf)) {
    throw "Не удалось подготовить Zig"
}

foreach ($required in @(
    (Join-Path $nativeRoot "nvenc_shim.c"),
    (Join-Path $nativeRoot "build.zig"),
    (Join-Path $repoRoot "third_party\nvenc\nvEncodeAPI.h"),
    (Join-Path $repoRoot "third_party\rnnoise\src\denoise.c"))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Не найден native source: $required" }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Push-Location $nativeRoot
try {
    & $zigExe build --prefix $OutputDir --release=fast
    if ($LASTEXITCODE -ne 0) { throw "Aura.Media64.dll не собралась" }
} finally {
    Pop-Location
}

$installed = Join-Path $OutputDir "bin\Aura.Media64.dll"
if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Zig не создал $installed" }
$dllPath = Join-Path $OutputDir "Aura.Media64.dll"
Copy-Item -LiteralPath $installed -Destination $dllPath -Force
Write-Host "Aura.Media64: $dllPath"
