# Сборка sparse-пакета identity для Aura.
#
# Пакет не содержит файлов приложения — только манифест с объявленной
# возможностью graphicsCaptureWithoutBorder. Без него Windows не отдаёт право на
# захват без жёлтой рамки: IsBorderRequired = false принимается и игнорируется.
#
# Результат: dist\Aura.Identity.msix (подписанный). Дальше build_setup.ps1 кладёт
# его в поставку, а установщик регистрирует с -ExternalLocation = папка app\.
#
# Сертификат: ищется в личном хранилище пользователя по Subject из манифеста.
# Если его нет — создаётся самоподписанный. ДОВЕРИЕ к нему не настраивается
# автоматически: импорт в TrustedPeople — сознательное решение владельца машины,
# скрипт лишь печатает нужную команду.
[CmdletBinding()]
param(
    [string]$Version,           # x.y.z; по умолчанию — версия из Aura.csproj
    [switch]$SkipSign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$manifestSource = Join-Path $PSScriptRoot 'AppxManifest.xml'
$output = Join-Path $dist 'Aura.Identity.msix'

function Fail($text) { Write-Host "ОШИБКА: $text" -ForegroundColor Red; exit 1 }

# ---------- Версия ----------
if (-not $Version) {
    $csproj = [IO.File]::ReadAllText((Join-Path $root 'src\Aura\Aura.csproj'), [Text.UTF8Encoding]::new($false))
    if ($csproj -notmatch '<Version>([\d]+\.[\d]+\.[\d]+)</Version>') { Fail 'не удалось прочитать <Version> из Aura.csproj' }
    $Version = $Matches[1]
}
# Версия пакета — четырёхчастная. Повторно зарегистрировать ту же версию нельзя
# (0x80073CF9), поэтому она следует за версией приложения.
$packageVersion = "$Version.0"

# ---------- Инструменты ----------
# MakeAppx и SignTool живут в Windows SDK, но тот же набор приезжает пакетом
# Microsoft.Windows.SDK.BuildTools — на машине без SDK берём оттуда.
function Find-SdkTool([string]$name) {
    $candidates = @()
    foreach ($kit in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (Test-Path $kit) {
            $candidates += Get-ChildItem $kit -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' }
        }
    }
    $nuget = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (Test-Path $nuget) {
        $candidates += Get-ChildItem $nuget -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' }
    }
    $tool = $candidates | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) {
        Fail "$name не найден. Поставь Windows SDK или выполни: dotnet restore на проекте с пакетом Microsoft.Windows.SDK.BuildTools"
    }
    return $tool.FullName
}

$makeappx = Find-SdkTool 'makeappx.exe'
Write-Host "MakeAppx: $makeappx"

# ---------- Манифест с подставленной версией ----------
$staging = Join-Path $dist 'identity_pkg'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

$utf8 = [Text.UTF8Encoding]::new($false)
$manifest = [IO.File]::ReadAllText($manifestSource, $utf8)
$manifest = $manifest -replace '(<Identity[^>]*?Version=")[\d\.]+(")', "`${1}$packageVersion`${2}"
[IO.File]::WriteAllText((Join-Path $staging 'AppxManifest.xml'), $manifest, $utf8)

if ($manifest -notmatch 'Publisher="([^"]+)"') { Fail 'в манифесте нет Identity/@Publisher' }
$publisher = $Matches[1]
Write-Host "Пакет: версия $packageVersion, издатель $publisher"

# ---------- Упаковка ----------
# /nv — не проверять пути файлов: их в пакете нет, они лежат во внешней папке
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $output) { Remove-Item $output -Force }
& $makeappx pack /o /d $staging /nv /p $output | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "MakeAppx вернул $LASTEXITCODE" }
if (-not (Test-Path $output)) { Fail 'пакет не собрался' }

if ($SkipSign) {
    Write-Host "Готово (без подписи): $output" -ForegroundColor Yellow
    Write-Host 'Неподписанный пакет зарегистрировать нельзя — только для проверки сборки.'
    exit 0
}

# ---------- Сертификат ----------
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "Сертификат $publisher не найден — создаю самоподписанный" -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
        -KeyUsage DigitalSignature -FriendlyName 'Aura package signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(5)
}
Write-Host "Сертификат: $($cert.Subject), отпечаток $($cert.Thumbprint)"

# ---------- Подпись ----------
$signtool = Find-SdkTool 'signtool.exe'
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $output
if ($LASTEXITCODE -ne 0) { Fail "SignTool вернул $LASTEXITCODE" }

$size = (Get-Item $output).Length / 1KB
Write-Host ("Готово: {0} ({1:0} КБ)" -f $output, $size) -ForegroundColor Green

# ---------- Доверие ----------
# Пакет с самоподписанным сертификатом регистрируется только там, где этот
# сертификат лежит в доверенных. На чужих машинах для этого нужен сертификат
# от настоящего центра сертификации.
$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $trusted) {
    $cer = Join-Path $dist 'Aura.Identity.cer'
    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
    Write-Host ''
    Write-Host 'Сертификат ещё не в доверенных — регистрация пакета упадёт с 0x800B0109.' -ForegroundColor Yellow
    Write-Host 'Чтобы доверять ему на ЭТОЙ машине, выполни:' -ForegroundColor Yellow
    Write-Host "    Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
}
