# Сборка установщика Instant Replay.
# Результат: dist\InstantReplaySetup.exe (один файл: приложение + музыка внутри).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$publish = Join-Path $dist "app_publish"

Write-Host "== 1/5 Публикация приложения =="
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\InstantReplay\InstantReplay.csproj") `
    -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish приложения не удался" }

Write-Host "== 2/5 Чистка поставки =="
# Языковые .mui-папки WindowsAppSDK (~100 штук, 11 МБ) — оставляем только английские.
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

Write-Host "== 3/5 Упаковка payload.zip =="
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist "payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Optimal: установщик скачивают, размер важнее скорости упаковки
# (SmallestSize есть только в .NET 5+, а скрипт запускается на Windows PowerShell 5.1)
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("   payload.zip: {0:0} МБ" -f ((Get-Item $zip).Length / 1MB))

Write-Host "== 4/5 Публикация установщика =="
dotnet publish (Join-Path $root "src\InstantReplaySetup\InstantReplaySetup.csproj") `
    -c Release -o (Join-Path $dist "setup_publish")
if ($LASTEXITCODE -ne 0) { throw "publish установщика не удался" }

Write-Host "== 5/5 Финал =="
Copy-Item (Join-Path $dist "setup_publish\InstantReplaySetup.exe") (Join-Path $dist "InstantReplaySetup.exe") -Force
$size = (Get-Item (Join-Path $dist "InstantReplaySetup.exe")).Length / 1MB
Write-Host ("Готово: dist\InstantReplaySetup.exe ({0:0} МБ)" -f $size)
