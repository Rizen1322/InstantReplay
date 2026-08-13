# Завести ключ подписи релизов Aura.
#
# ЗАЧЕМ. Обновление запускает скачанный установщик с правами администратора.
# Контрольная сумма из релиза защищает от битой закачки и от подмены на раздаче,
# но не от угона аккаунта GitHub: кто выложил файл, тот выложит и его слепок.
# Подпись закрытым ключом закрывает и это — ключ на GitHub не попадает никогда.
#
# ЧТО ДЕЛАЕТ:
#   1. создаёт пару ECDSA P-256 и кладёт закрытую часть в release-key.pem
#      (файл в .gitignore, из репозитория он не уедет);
#   2. печатает открытую часть в base64 — её нужно вписать в константу
#      UpdateVerification.PublicKeyBase64 и закоммитить.
#
# ПОСЛЕ ЭТОГО release.ps1 будет ТРЕБОВАТЬ ключ: релиз без подписи установленные
# копии отклонят. Потеряете release-key.pem — обновляться станет нечем, и всем
# придётся переставлять приложение вручную. Держите копию в надёжном месте.
[CmdletBinding()]
param(
    [string]$KeyPath = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Путь по умолчанию достраиваем ЗДЕСЬ, а не в param(): в Windows PowerShell 5.1
# значения по умолчанию вычисляются раньше, чем $PSScriptRoot получает своё
# значение, и там он пустой. В PowerShell 7 это уже не так — потому ловушка и
# незаметная, пока не запустишь скрипт без -KeyPath.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $KeyPath) { $KeyPath = Join-Path (Split-Path $here -Parent) 'release-key.pem' }

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            "$env:ProgramFiles\Git\usr\bin\openssl.exe",
            "${env:ProgramFiles(x86)}\Git\usr\bin\openssl.exe",
            "$env:ProgramFiles\Git\mingw64\bin\openssl.exe",
            "$env:LOCALAPPDATA\Programs\Git\usr\bin\openssl.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

$openssl = Find-OpenSsl
if (-not $openssl) {
    Write-Host "ОШИБКА: openssl не найден. Он приезжает вместе с Git for Windows." -ForegroundColor Red
    exit 1
}

# Перезапись существующего ключа = потеря возможности обновлять уже установленные
# копии. Отдельный ключ -Force, чтобы это нельзя было сделать случайно.
if ((Test-Path $KeyPath) -and -not $Force) {
    Write-Host "ОШИБКА: ключ уже существует: $KeyPath" -ForegroundColor Red
    Write-Host "Перезапись сделает недействительными все будущие обновления для тех, у кого"
    Write-Host "вшит старый открытый ключ. Если это осознанное решение — добавь -Force."
    exit 1
}

& $openssl ecparam -name prime256v1 -genkey -noout -out $KeyPath
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА: не удалось создать ключ" -ForegroundColor Red; exit 1 }

$pubDer = Join-Path $env:TEMP 'aura-release-pub.der'
# Без перенаправления stderr: openssl пишет туда «read EC key» — обычный отчёт о
# работе, но Windows PowerShell 5.1 при перенаправлении заворачивает КАЖДУЮ строку
# нативного stderr в ошибку и роняет скрипт при $ErrorActionPreference = 'Stop',
# даже когда программа вернула ноль.
& $openssl ec -in $KeyPath -pubout -outform DER -out $pubDer
if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА: не удалось извлечь открытый ключ" -ForegroundColor Red; exit 1 }

$publicKey = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pubDer))
Remove-Item $pubDer -Force

Write-Host ""
Write-Host "Закрытый ключ: $KeyPath" -ForegroundColor Green
Write-Host "  Он в .gitignore. Сделай резервную копию вне этой папки — потеря ключа"
Write-Host "  означает, что установленные копии больше не примут ни одного обновления."
Write-Host ""
Write-Host "Открытый ключ — вписать в src\Aura\Core\System\UpdateVerification.cs:" -ForegroundColor Green
Write-Host ""
Write-Host "    public const string PublicKeyBase64 = `"$publicKey`";" -ForegroundColor Cyan
Write-Host ""
Write-Host "Дальше обычный .\release.ps1 — он сам подпишет установщик и приложит .sig."
