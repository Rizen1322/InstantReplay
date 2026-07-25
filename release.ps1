# Выпуск новой версии одной командой:
#   .\release.ps1                  — патч (1.0.2 → 1.0.3)
#   .\release.ps1 -Bump minor      — 1.0.2 → 1.1.0
#   .\release.ps1 -Bump major      — 1.0.2 → 2.0.0
#   .\release.ps1 -Notes "текст"   — описание релиза
#   .\release.ps1 -DryRun          — показать план, ничего не менять
#
# Делает всё: поднимает версию в csproj, собирает установщик, коммитит, ставит тег,
# пушит и создаёт GitHub-релиз с приложенным InstantReplaySetup.exe.
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',
    [string]$Notes = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csproj = Join-Path $root 'src\InstantReplay\InstantReplay.csproj'
$setupExe = Join-Path $root 'dist\InstantReplaySetup.exe'

function Step($text) { Write-Host "`n== $text ==" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ОШИБКА: $text" -ForegroundColor Red; exit 1 }

# ---------- gh CLI ----------
# В PATH он есть не всегда (например, сразу после установки), поэтому ищем и по
# стандартным путям — иначе скрипт падал бы на последнем шаге, уже создав тег.
function Find-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            "$env:ProgramFiles\GitHub CLI\gh.exe",
            "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
            "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}
$gh = Find-Gh
if (-not $gh) { Fail "GitHub CLI не найден. Установи: winget install GitHub.cli" }

# ---------- Текущая версия ----------
$content = Get-Content $csproj -Raw
if ($content -notmatch '<Version>([\d]+)\.([\d]+)\.([\d]+)</Version>') {
    Fail "Не удалось прочитать <Version> из $csproj"
}
$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
$current = "$major.$minor.$patch"

switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$new = "$major.$minor.$patch"
# Первые три числа обязаны совпадать с тегом, иначе обновление предлагается по кругу
$assembly = "$new.0"
$tag = "v$new"

Write-Host "Версия: $current -> $new  (тег $tag)" -ForegroundColor Green
if ($DryRun) { Write-Host "DryRun — ничего не изменено."; exit 0 }

# ---------- Проверки до изменений ----------
Step "1/6 Проверки"
Push-Location $root
try {
    git rev-parse --git-dir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Это не git-репозиторий" }

    $existing = git tag --list $tag
    if ($existing) { Fail "Тег $tag уже существует. Укажи другой -Bump или удали тег." }

    # Запущенное приложение держит файлы в dist\app_publish, и публикация не сможет
    # их перезаписать. Молча это приводит к установщику со СТАРОЙ версией внутри.
    if (Get-Process InstantReplay -ErrorAction SilentlyContinue) {
        Fail "Instant Replay запущен — закрой его через трей, иначе соберётся старая версия"
    }
    Write-Host "   git в порядке, тег $tag свободен, приложение закрыто"
}
finally { Pop-Location }

# ---------- Версия в csproj ----------
Step "2/6 Обновление версии в csproj"
$content = $content `
    -replace '<AssemblyVersion>[\d\.]+</AssemblyVersion>', "<AssemblyVersion>$assembly</AssemblyVersion>" `
    -replace '<FileVersion>[\d\.]+</FileVersion>', "<FileVersion>$assembly</FileVersion>" `
    -replace '<Version>[\d\.]+</Version>', "<Version>$new</Version>"
Set-Content $csproj -Value $content -Encoding UTF8 -NoNewline
Write-Host "   AssemblyVersion/FileVersion = $assembly, Version = $new"

# ---------- Сборка ----------
Step "3/6 Сборка установщика"
& (Join-Path $root 'build_setup.ps1')
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { Fail "Сборка не удалась" }
if (-not (Test-Path $setupExe)) { Fail "Установщик не собрался: $setupExe" }

$built = (Get-Item (Join-Path $root 'dist\app_publish\InstantReplay.exe')).VersionInfo.FileVersion
if ($built -ne $assembly) { Fail "В собранном exe версия $built, ожидалась $assembly" }
Write-Host "   Проверено: в exe версия $built"

# ---------- Коммит и тег ----------
Step "4/6 Коммит и тег"
Push-Location $root
try {
    git add -A
    git commit -q -m "Release $tag"
    if ($LASTEXITCODE -ne 0) { Write-Host "   Нечего коммитить (версия уже была поднята)" }
    git tag $tag
    if ($LASTEXITCODE -ne 0) { Fail "Не удалось создать тег $tag" }
    Write-Host "   Коммит и тег $tag созданы"

    # ---------- Пуш ----------
    Step "5/6 Отправка на GitHub"
    git push origin HEAD
    if ($LASTEXITCODE -ne 0) { Fail "git push не удался" }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { Fail "Не удалось отправить тег" }
    Write-Host "   Код и тег отправлены"
}
finally { Pop-Location }

# ---------- Релиз ----------
Step "6/6 Создание релиза"
if (-not $Notes) { $Notes = "Instant Replay $new" }
& $gh release create $tag $setupExe --title $tag --notes $Notes
if ($LASTEXITCODE -ne 0) { Fail "Не удалось создать релиз (тег уже отправлен — можно повторить вручную)" }

$size = (Get-Item $setupExe).Length / 1MB
Write-Host "`nГотово: релиз $tag опубликован ($('{0:0}' -f $size) МБ)" -ForegroundColor Green
Write-Host "https://github.com/Rizen1322/InstantReplay/releases/tag/$tag"
