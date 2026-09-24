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

Write-Host "== 1/8 Нативный захват Minecraft =="
& (Join-Path $root "packaging\build_game_capture_hook.ps1") `
    -OutputDir (Join-Path $root "artifacts\native\win-x64\Release") `
    -RunProtocolTests
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "native hook не собрался" }

Write-Host "== 2/8 Публикация приложения =="
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\Aura\Aura.csproj") `
    -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish приложения не удался" }

Write-Host "== 3/8 Чистка поставки =="
# Языковые папки сторонних пакетов — оставляем только английские.
$keep = @("en-US", "en", "Assets")
$removed = 0
Get-ChildItem $publish -Directory | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    # удаляем только языковые папки (имя вида xx-XX / xx-Xxxx-XX), не трогая служебные
    if ($_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,10}){1,2}$') { Remove-Item $_.FullName -Recurse -Force; $removed++ }
}
# Символы и XML-документация в поставке не нужны
Get-ChildItem $publish -Include *.pdb, *.xml -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue

# Aura публикуется только для win-x64. Полные VLC-копии x86/ARM64 занимали ещё
# ~178 МБ и никогда не могли загрузиться в 64-битный процесс. Основной target в
# Aura.csproj удаляет их для любого publish; здесь оставляем жёсткую проверку,
# чтобы обновление NuGet/MSBuild не вернуло раздутую поставку незаметно.
$requiredVlc = Join-Path $publish 'libvlc\win-x64'
if (-not (Test-Path -LiteralPath $requiredVlc -PathType Container)) {
    throw 'в поставке нет x64 LibVLC'
}
foreach ($foreignVlc in @('win-x86', 'win-arm64')) {
    $foreignPath = Join-Path $publish "libvlc\$foreignVlc"
    if (Test-Path -LiteralPath $foreignPath) {
        throw "в x64-поставке остался чужой LibVLC: $foreignVlc"
    }
}
$files = (Get-ChildItem $publish -Recurse -File).Count
$sizeMb = ((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB
Write-Host ("   удалено языковых папок: {0}; итог: {1} файлов, {2:0} МБ" -f $removed, $files, $sizeMb)
if ($sizeMb -gt 300) { throw ("x64-поставка неожиданно выросла до {0:0} МБ (лимит 300 МБ)" -f $sizeMb) }

Write-Host "== 4/8 Компактный деинсталлятор =="
$uninstallPublish = Join-Path $dist "uninstall_publish"
if (Test-Path -LiteralPath $uninstallPublish) { Remove-Item -LiteralPath $uninstallPublish -Recurse -Force }
dotnet publish (Join-Path $root "src\AuraUninstall\AuraUninstall.csproj") `
    -c Release -r win-x64 --self-contained true -o $uninstallPublish
if ($LASTEXITCODE -ne 0) { throw "publish деинсталлятора не удался" }
$uninstaller = Join-Path $uninstallPublish "AuraUninstall.exe"
if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "деинсталлятор не собрался" }
$uninstallerMb = (Get-Item -LiteralPath $uninstaller).Length / 1MB
if ($uninstallerMb -gt 30) { throw ("деинсталлятор неожиданно вырос до {0:0.0} МБ" -f $uninstallerMb) }
Copy-Item -LiteralPath $uninstaller -Destination (Join-Path $publish "AuraUninstall.exe") -Force
Write-Host ("   AuraUninstall.exe: {0:0.0} МБ" -f $uninstallerMb)

if (-not (Test-Path (Join-Path $publish "Aura.exe"))) { throw "в поставке нет Aura.exe" }
$hook = Join-Path $publish "Aura.GameCaptureHook64.dll"
$hookManifest = Join-Path $publish "Aura.GameCaptureHook64.sha256"
if (-not (Test-Path -LiteralPath $hook -PathType Leaf)) { throw "в поставке нет native hook" }
if (-not (Test-Path -LiteralPath $hookManifest -PathType Leaf)) { throw "в поставке нет hash native hook" }
$expectedHookHash = (([IO.File]::ReadAllText($hookManifest) -split '\s+')[0]).ToLowerInvariant()
$actualHookHash = (Get-FileHash -LiteralPath $hook -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expectedHookHash -ne $actualHookHash) { throw "native hook и manifest не совпадают" }
if (-not (Test-Path -LiteralPath (Join-Path $publish "Aura.Media64.dll") -PathType Leaf)) { throw "в поставке нет Aura.Media64.dll (NVENC и RNNoise)" }

Write-Host "== 5/8 Пакет identity =="
# Sparse-пакет кладём в поставку: установщик зарегистрирует его с внешним
# расположением = папка app\. Без него приложение не получит право
# graphicsCaptureWithoutBorder и запись пойдёт с жёлтой рамкой.
& (Join-Path $root "packaging\build_identity_package.ps1")
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "пакет identity не собрался" }
$identity = Join-Path $dist "Aura.Identity.msix"
if (-not (Test-Path $identity)) { throw "нет $identity" }
Copy-Item $identity (Join-Path $publish "Aura.Identity.msix") -Force

$finalPayloadFiles = Get-ChildItem -LiteralPath $publish -Recurse -File
$finalPayloadMb = ($finalPayloadFiles | Measure-Object Length -Sum).Sum / 1MB
if ($finalPayloadMb -gt 300) {
    throw ("полная поставка неожиданно выросла до {0:0} МБ (лимит 300 МБ)" -f $finalPayloadMb)
}
Write-Host ("   полный x64 payload: {0} файлов, {1:0} МБ" -f $finalPayloadFiles.Count, $finalPayloadMb)

Write-Host "== 6/8 Упаковка payload.zip =="
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist "payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Optimal: установщик скачивают, размер важнее скорости упаковки
# (SmallestSize есть только в .NET 5+, а скрипт запускается на Windows PowerShell 5.1)
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("   payload.zip: {0:0} МБ" -f ((Get-Item $zip).Length / 1MB))

Write-Host "== 7/8 Публикация установщика =="
dotnet publish (Join-Path $root "src\InstantReplaySetup\InstantReplaySetup.csproj") `
    -c Release -o (Join-Path $dist "setup_publish")
if ($LASTEXITCODE -ne 0) { throw "publish установщика не удался" }

Write-Host "== 8/8 Финал =="
Copy-Item (Join-Path $dist "setup_publish\InstantReplaySetup.exe") (Join-Path $dist "InstantReplaySetup.exe") -Force
$size = (Get-Item (Join-Path $dist "InstantReplaySetup.exe")).Length / 1MB
Write-Host ("Готово: dist\InstantReplaySetup.exe ({0:0} МБ)" -f $size)
