# Сборка установщика Aura.
# Результат: dist\InstantReplaySetup.exe (один файл: приложение + музыка внутри).
#
# Имя файла установщика оставлено прежним НАМЕРЕННО: установленные версии 1.0.x
# (ещё под именем Instant Replay) ищут в GitHub-релизе именно InstantReplaySetup.exe
# и обновляются по нему. Переименуем — старые копии перестанут видеть обновления.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$publish = Join-Path $dist "app_publish"

Write-Host "== 1/6 Публикация приложения =="
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\Aura\Aura.csproj") `
    -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish приложения не удался" }

Write-Host "== 2/6 Чистка поставки =="
# Языковые папки сторонних пакетов — оставляем только английские.
$keep = @("en-US", "en", "Assets")
$removed = 0
Get-ChildItem $publish -Directory | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    # удаляем только языковые папки (имя вида xx-XX / xx-Xxxx-XX), не трогая служебные
    if ($_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,10}){1,2}$') { Remove-Item $_.FullName -Recurse -Force; $removed++ }
}
# Символы и XML-документация в поставке не нужны
Get-ChildItem $publish -Include *.pdb, *.xml -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue
$files = (Get-ChildItem $publish -Recurse -File).Count
$sizeMb = ((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB
Write-Host ("   удалено языковых папок: {0}; итог: {1} файлов, {2:0} МБ" -f $removed, $files, $sizeMb)

if (-not (Test-Path (Join-Path $publish "Aura.exe"))) { throw "в поставке нет Aura.exe" }

Write-Host "== 3/6 Пакет identity =="
# Sparse-пакет кладём в поставку: установщик зарегистрирует его с внешним
# расположением = папка app\. Без него приложение не получит право
# graphicsCaptureWithoutBorder и запись пойдёт с жёлтой рамкой.
& (Join-Path $root "packaging\build_identity_package.ps1")
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "пакет identity не собрался" }
$identity = Join-Path $dist "Aura.Identity.msix"
if (-not (Test-Path $identity)) { throw "нет $identity" }
Copy-Item $identity (Join-Path $publish "Aura.Identity.msix") -Force

Write-Host "== 4/6 Упаковка payload.zip =="
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist "payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Optimal: установщик скачивают, размер важнее скорости упаковки
# (SmallestSize есть только в .NET 5+, а скрипт запускается на Windows PowerShell 5.1)
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("   payload.zip: {0:0} МБ" -f ((Get-Item $zip).Length / 1MB))

Write-Host "== 5/6 Публикация установщика =="
dotnet publish (Join-Path $root "src\InstantReplaySetup\InstantReplaySetup.csproj") `
    -c Release -o (Join-Path $dist "setup_publish")
if ($LASTEXITCODE -ne 0) { throw "publish установщика не удался" }

Write-Host "== 6/6 Финал =="
Copy-Item (Join-Path $dist "setup_publish\InstantReplaySetup.exe") (Join-Path $dist "InstantReplaySetup.exe") -Force
$size = (Get-Item (Join-Path $dist "InstantReplaySetup.exe")).Length / 1MB
Write-Host ("Готово: dist\InstantReplaySetup.exe ({0:0} МБ)" -f $size)
