param(
    [string]$OutputDir,
    [switch]$RunProtocolTests
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repoRoot "src\native\Aura.GameCaptureHook"
$minHookRoot = Join-Path $repoRoot "third_party\minhook"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\native\win-x64\Release"
} elseif (-not [IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$zigExe = & (Join-Path $PSScriptRoot "get_zig.ps1")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $zigExe -PathType Leaf)) {
    throw "Не удалось подготовить Zig"
}

$requiredFiles = @(
    (Join-Path $nativeRoot "game_hook_protocol.h"),
    (Join-Path $nativeRoot "hook_exports.c"),
    (Join-Path $minHookRoot "include\MinHook.h"),
    (Join-Path $minHookRoot "src\buffer.c"),
    (Join-Path $minHookRoot "src\hook.c"),
    (Join-Path $minHookRoot "src\trampoline.c"),
    (Join-Path $minHookRoot "src\hde\hde64.c")
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Не найден native source: $required"
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Push-Location $nativeRoot
try {
    if ($RunProtocolTests) {
        & $zigExe build test --prefix $OutputDir
        if ($LASTEXITCODE -ne 0) { throw "protocol_layout_test не прошёл" }
    }

    & $zigExe build --prefix $OutputDir --release=fast
    if ($LASTEXITCODE -ne 0) { throw "Aura.GameCaptureHook64.dll не собралась" }
} finally {
    Pop-Location
}

$installedDll = Join-Path $OutputDir "bin\Aura.GameCaptureHook64.dll"
if (-not (Test-Path -LiteralPath $installedDll -PathType Leaf)) {
    throw "Zig не создал $installedDll"
}
$dllPath = Join-Path $OutputDir "Aura.GameCaptureHook64.dll"
Copy-Item -LiteralPath $installedDll -Destination $dllPath -Force

$bytes = [IO.File]::ReadAllBytes($dllPath)
if ($bytes.Length -lt 0x40) { throw "Hook DLL слишком мала для PE" }
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) { throw "Повреждён PE header hook DLL" }
if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) { throw "Hook DLL не является PE" }
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw ("Hook DLL не x64: machine=0x{0:X4}" -f $machine) }

$sha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestPath = Join-Path $OutputDir "Aura.GameCaptureHook64.sha256"
[IO.File]::WriteAllText($manifestPath, "$sha256  Aura.GameCaptureHook64.dll`r`n")

Write-Host "Hook DLL: $dllPath"
Write-Host "SHA-256: $sha256"
