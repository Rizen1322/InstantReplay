[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$nativeOutput = Join-Path $repoRoot 'artifacts\native\win-x64\Release'
$testRoot = Join-Path $repoRoot 'artifacts\tests\game-capture-package'
$publish = Join-Path $testRoot 'publish'
$payload = Join-Path $testRoot 'payload.zip'
$hookName = 'Aura.GameCaptureHook64.dll'
$manifestName = 'Aura.GameCaptureHook64.sha256'

& (Join-Path $repoRoot 'packaging\build_game_capture_hook.ps1') -OutputDir $nativeOutput
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw 'native hook build failed' }

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publish -Force | Out-Null

dotnet publish (Join-Path $repoRoot 'src\Aura\Aura.csproj') `
    -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Aura publish failed' }

$hook = Join-Path $publish $hookName
$manifest = Join-Path $publish $manifestName
if (-not (Test-Path -LiteralPath $hook -PathType Leaf)) { throw "publish missing $hookName" }
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "publish missing $manifestName" }

$bytes = [IO.File]::ReadAllBytes($hook)
if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
    throw 'hook is not a PE image'
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length) { throw 'invalid hook PE header' }
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
$characteristics = [BitConverter]::ToUInt16($bytes, $peOffset + 22)
if ($machine -ne 0x8664 -or ($characteristics -band 0x2000) -eq 0) {
    throw ('hook must be an x64 DLL, machine=0x{0:X4}' -f $machine)
}

$expected = (([IO.File]::ReadAllText($manifest) -split '\s+')[0]).ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $hook -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) { throw "hook hash mismatch: expected $expected, actual $actual" }

$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $publish 'Aura.dll'))
$resourceName = 'Aura.GameCaptureHook64.sha256'
if ($assembly.GetManifestResourceNames() -notcontains $resourceName) {
    throw "Aura.dll missing embedded resource $resourceName"
}
$stream = $assembly.GetManifestResourceStream($resourceName)
try {
    $reader = [IO.StreamReader]::new($stream)
    try { $embedded = (($reader.ReadLine() -split '\s+')[0]).ToLowerInvariant() }
    finally { $reader.Dispose() }
} finally {
    if ($null -ne $stream) { $stream.Dispose() }
}
if ($embedded -ne $actual) { throw "embedded hook hash mismatch: $embedded != $actual" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $payload)
$zip = [IO.Compression.ZipFile]::OpenRead($payload)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    if ($names -notcontains $hookName -or $names -notcontains $manifestName) {
        throw 'payload.zip missing hook DLL or hash manifest'
    }
} finally {
    $zip.Dispose()
}

Write-Host "Game capture hook package passed: $actual"
