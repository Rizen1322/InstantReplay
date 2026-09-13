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

$actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "SHA-256 архива Zig не совпал: $actualSha256"
}

if (-not (Test-Path -LiteralPath $expandedRoot -PathType Container)) {
    Expand-Archive -LiteralPath $archive -DestinationPath $versionRoot
}

if (-not (Test-Path -LiteralPath $zigExe -PathType Leaf)) {
    throw "После распаковки не найден $zigExe"
}

Write-Output $zigExe
