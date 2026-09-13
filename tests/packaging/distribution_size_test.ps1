[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

& (Join-Path $repoRoot 'build_setup.ps1')
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw 'release build failed' }

$publish = Join-Path $repoRoot 'dist\app_publish'
$setup = Join-Path $repoRoot 'dist\InstantReplaySetup.exe'
$uninstaller = Join-Path $publish 'AuraUninstall.exe'

if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
    throw 'release payload missing AuraUninstall.exe'
}

$bytes = [IO.File]::ReadAllBytes($uninstaller)
if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
    throw 'AuraUninstall.exe is not a PE image'
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw ('uninstaller must be x64, machine=0x{0:X4}' -f $machine) }

$uninstallerMb = (Get-Item -LiteralPath $uninstaller).Length / 1MB
if ($uninstallerMb -gt 30) {
    throw ('uninstaller is {0:0.0} MB; expected at most 30 MB' -f $uninstallerMb)
}

foreach ($foreignArchitecture in @('win-x86', 'win-arm64')) {
    if (Test-Path -LiteralPath (Join-Path $publish "libvlc\$foreignArchitecture")) {
        throw "release payload contains foreign LibVLC runtime: $foreignArchitecture"
    }
}

$payloadBytes = (Get-ChildItem -LiteralPath $publish -Recurse -File | Measure-Object Length -Sum).Sum
$payloadMb = $payloadBytes / 1MB
if ($payloadMb -gt 300) { throw ('application payload is {0:0.0} MB; expected at most 300 MB' -f $payloadMb) }

$setupMb = (Get-Item -LiteralPath $setup).Length / 1MB
if ($setupMb -gt 200) { throw ('installer is {0:0.0} MB; expected at most 200 MB' -f $setupMb) }

$setupAssembly = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\InstantReplaySetup\bin\Release') `
    -Filter 'InstantReplaySetup.dll' -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $setupAssembly) { throw 'compiled installer assembly is missing' }
$assembly = [Reflection.Assembly]::LoadFrom($setupAssembly.FullName)
$audioDependencies = $assembly.GetReferencedAssemblies().Name
foreach ($requiredAudioDependency in @('NAudio.Core', 'NAudio.WinMM', 'NLayer.NAudioSupport')) {
    if ($audioDependencies -notcontains $requiredAudioDependency) {
        throw "installer is missing standalone audio dependency: $requiredAudioDependency"
    }
}
$musicResourceName = $assembly.GetManifestResourceNames() |
    Where-Object { $_.EndsWith('setup_music.mp3', [StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($musicResourceName)) {
    throw 'installer does not contain embedded setup_music.mp3'
}
$musicStream = $assembly.GetManifestResourceStream($musicResourceName)
try {
    if ($null -eq $musicStream -or $musicStream.Length -lt 100KB) {
        throw 'embedded setup music is unexpectedly empty'
    }
    if ($musicStream.Length -gt 5MB) {
        throw ('embedded setup music is too large: {0:0.0} MB' -f ($musicStream.Length / 1MB))
    }
}
finally {
    if ($null -ne $musicStream) { $musicStream.Dispose() }
}

Write-Host ('Distribution size passed: app {0:0.0} MB, uninstall {1:0.0} MB, setup {2:0.0} MB' -f `
    $payloadMb, $uninstallerMb, $setupMb)
