param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot ".tools"
$downloads = Join-Path $toolsRoot "downloads"
$versionRoot = Join-Path $toolsRoot "zig-0.16.0"
$archive = Join-Path $downloads "zig-x86_64-windows-0.16.0.zip"
$expandedRoot = Join-Path $versionRoot "zig-x86_64-windows-0.16.0"
$zigExe = Join-Path $expandedRoot "zig.exe"
$expectedSha256 = "68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e"
$downloadUrl = "https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip"

if (Test-Path -LiteralPath $zigExe -PathType Leaf) {
    Write-Output $zigExe
    exit 0
}

New-Item -ItemType Directory -Path $downloads -Force | Out-Null
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Write-Host "Загрузка закреплённого Zig 0.16.0..."
    Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $archive
}

# Хеш и распаковка через .NET, а не Get-FileHash и Expand-Archive: в CI этот
# скрипт запускает powershell.exe из шага pwsh, PSModulePath достаётся от pwsh,
# и командлеты из модулей Windows PowerShell не находятся.
$stream = [System.IO.File]::OpenRead($archive)
try { $hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash($stream) }
finally { $stream.Dispose() }
$actualSha256 = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
if ($actualSha256 -ne $expectedSha256) {
    throw "SHA-256 архива Zig не совпал: $actualSha256"
}

if (-not (Test-Path -LiteralPath $expandedRoot -PathType Container)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $versionRoot)
}

if (-not (Test-Path -LiteralPath $zigExe -PathType Leaf)) {
    throw "После распаковки не найден $zigExe"
}

Write-Output $zigExe
