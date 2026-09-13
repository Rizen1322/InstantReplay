param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$lifecycleScript = Join-Path $PSScriptRoot "run_hook_lifecycle_test.ps1"
$hookDll = Join-Path $repoRoot "artifacts\native\win-x64\Release\Aura.GameCaptureHook64.dll"
$fixture = Join-Path $repoRoot "artifacts\native-tests\OpenGlCaptureFixture.exe"

& $lifecycleScript -Iterations 1
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "60 FPS frame test failed" }

& $fixture $hookDll 30
if ($LASTEXITCODE -ne 0) { throw "30 FPS frame test failed with exit code $LASTEXITCODE" }

& $fixture $hookDll 60
if ($LASTEXITCODE -ne 0) { throw "60 FPS frame test failed with exit code $LASTEXITCODE" }

Write-Host "OpenGL PBO frame tests passed at 30 and 60 FPS"
