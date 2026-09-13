param(
    [int]$Iterations = 20
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$zigExe = & (Join-Path $repoRoot "packaging\get_zig.ps1")
$hookOutput = Join-Path $repoRoot "artifacts\native\win-x64\Release"
$testOutput = Join-Path $repoRoot "artifacts\native-tests"
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null

& (Join-Path $repoRoot "packaging\build_game_capture_hook.ps1") -OutputDir $hookOutput -RunProtocolTests
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "hook build failed" }

$fixture = Join-Path $testOutput "OpenGlCaptureFixture.exe"
$nativeRoot = Join-Path $repoRoot "src\native\Aura.GameCaptureHook"
$fixtureSource = Join-Path $repoRoot "tests\native\OpenGlCaptureFixture\main.c"
& $zigExe cc -target x86_64-windows-gnu -std=c11 -O2 -municode `
    "-I$nativeRoot" $fixtureSource -lopengl32 -lgdi32 -luser32 -lkernel32 `
    -o $fixture
if ($LASTEXITCODE -ne 0) { throw "OpenGL fixture build failed" }

$hookDll = Join-Path $hookOutput "Aura.GameCaptureHook64.dll"
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    & $fixture $hookDll
    if ($LASTEXITCODE -ne 0) {
        throw "hook lifecycle failed at iteration $iteration with exit code $LASTEXITCODE"
    }
}

Write-Host "Hook lifecycle passed: $Iterations/$Iterations"
