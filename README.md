# Instant Replay

Аналог NVIDIA ShadowPlay Instant Replay: постоянная фоновая запись экрана в кольцевой буфер RAM, сохранение последних N минут по хоткею. C# / .NET 9 / WinUI 3, отдельное unpackaged-приложение, без Steam и сторонних сервисов.

## Сборка

Требуется **Windows 10 2004+ / Windows 11**, Visual Studio 2022 (workload «Разработка классических приложений .NET» + Windows App SDK) или .NET 9 SDK.

```
dotnet build -c Release -r win-x64 src/InstantReplay/InstantReplay.csproj
```

Приложение self-contained по Windows App SDK (`WindowsAppSDKSelfContained=true`) — рантайм ставить не нужно.

## Архитектура конвейера

```
Windows.Graphics.Capture (BGRA, VRAM)
        │  frame.SystemRelativeTime (QPC-тики) — общая шкала времени
        ▼
ID3D11VideoProcessor  →  NV12 + масштабирование (VRAM, без hwdownload)
        ▼
HW MFT-энкодер (NVENC / AMF / QuickSync через MFTEnumEx MFT_ENUM_FLAG_HARDWARE)
        ▼
ReplayVideoBuffer — кольцевой RAM-буфер СЖАТЫХ кадров
        │                              WASAPI loopback + mic (NAudio)
        │                                      ▼
        │                              AudioMixerEngine — активный микшер:
        │                              поток по QPC каждые 10 мс, тишина-заполнение,
        │                              обе дорожки раздельно → ReplayAudioBuffer
        ▼
SaveReplay(): мгновенный снимок буферов → ReplaySaver (фон):
SinkWriter, видео passthrough (remux без перекодирования), PCM→AAC
→ Videos/<Игра>/replay_YYYY-MM-DD_HH-mm.mp4
```

Ключевые решения (в т.ч. выстраданные ранее грабли):

- **Кадр не покидает VRAM до энкодера.** hwdownload (GPU→RAM) — главный пожиратель CPU, его нет вообще: WGC-текстура → VideoProcessor → NVENC, всё в видеопамяти. В RAM попадает только сжатый битстрим (десятки МБ/мин вместо гигабайт).
- **Буфер хранит сжатое видео, файл = remux.** Сохранение 5-минутного клипа — доли секунды, без нагрузки на GPU.
- **Аудио — активный микшер, не пассивное чтение WASAPI.** WASAPI loopback молчит в тишине; микшер идёт по системным часам (QPC, та же шкала, что у видеокадров WGC) с шагом 10 мс и добивает недостачу тишиной → непрерывный PCM без рассинхрона, AAC-энкодер получает готовый поток.
- **Кольцевой буфер вытесняет по времени и режется по keyframe** (GOP = 2 сек через ICodecAPI): UI показывает ровно то, что реально сохранится, а клип всегда начинается с ключевого кадра.
- **CsWinRT-интероп** с `IGraphicsCaptureItemInterop` / `IDirect3DDxgiInterfaceAccess` — через `RoGetActivationFactory` + `Marshal.GetObjectForIUnknown` / `FromAbi` (не `.As<T>()`).
- **Хоткеи** — низкоуровневый хук `WH_KEYBOARD_LL` в отдельном потоке: комбинация ловится до игры, обработка уводится в ThreadPool (<1 мс в хуке).
- **Уведомления** — WinUI-окно поверх всех окон, Acrylic, fade+slide-анимации, click-through, не отбирает фокус. **Белая рамка** системного окна убрана через `DwmSetWindowAttribute(DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE)` — это единственный работающий способ для WinUI 3.
- **Статистика хранилища** всегда считается по актуальному `SaveRootPath`; смена папки в настройках триггерит `Settings.Changed("storage")` → мгновенный пересчёт по новой папке.

## Модули

| Модуль | Файлы |
|---|---|
| Capture Engine | `Core/Capture/ScreenCaptureSource.cs`, `VideoProcessorNv12.cs` |
| Encoder | `Core/Encoding/VideoEncoder.cs` |
| Replay Buffer | `Core/Buffering/ReplayBuffers.cs` |
| Audio Engine | `Core/Audio/AudioEngine.cs` |
| Saving/Mux | `Core/Saving/ReplaySaver.cs` |
| Оркестратор | `Core/Engine/ReplayEngine.cs` |
| Hotkeys | `Core/Hotkeys/HotkeyService.cs` |
| Notifications/Overlay | `Core/Notifications/NotificationService.cs` |
| Game Detection | `Core/GameDetection/GameDetector.cs` |
| Storage Manager | `Core/Storage/StorageManager.cs` |
| Settings Manager | `Core/Settings/*` |
| Характеристики | `Core/Hardware/HardwareServices.cs` (WMI + GeForce lookup API + ZenitH-AT/nvidia-data) |
| Система | `Core/System/*` (автозапуск, автообновление) |
| UI | `MainWindow`, `Views/*` (Mica, Fluent, тёмная тема, NavigationView) |

## Выпуск новой версии (автообновление)

Обновления берутся только из официального репозитория проекта — он зашит
константой `UpdateService.Repo` (`Rizen1322/InstantReplay`), настройкой не меняется.

1. **Поднять версию** в `src/InstantReplay/InstantReplay.csproj` — все три поля:
   `AssemblyVersion`, `FileVersion`, `Version`. Без этого обновление не предложится:
   сравнение идёт с версией сборки, и одинаковые версии считаются «уже последней».
   **Первые три числа `AssemblyVersion` обязаны совпадать с тегом релиза**
   (тег `v1.0.1` → `AssemblyVersion` = `1.0.1.0`). При `1.0.0.1` установленная
   версия считается старее собственного релиза, и он предлагается бесконечно.
2. Собрать установщик: `.\build_setup.ps1` → `dist\InstantReplaySetup.exe`.
3. Создать релиз на GitHub с тегом вида `v1.0.1` (префикс `v` необязателен) и
   приложить `InstantReplaySetup.exe` как asset.

Дальше приложение само: находит релиз новее текущей сборки, скачивает asset
(`.exe`/`.msi`/`.zip` — берётся первый подходящий) и запускает его с ключом
`/update <папка установки>`. В этом режиме установщик работает молча: гасит
приложение, обновляет файлы в той же папке и запускает новую версию.
Настройки, записи и выбор автозапуска не трогаются.

## Известные тонкости и что проверить при первом запуске

1. **Vortice API.** Код написан под Vortice 3.6.x. Имена методов/ключей MF в обёртке могут минимально отличаться от использованных (например, `writer.Finalize()` у `IMFSinkWriter`, поля `OutputDataBuffer`, `MediaFactory.MFTEnumEx`-перегрузка). Это первые кандидаты на правку компилятором — семантика вызовов верная, сверяйтесь с сигнатурами пакета.
2. **AV1** доступен только на RTX 40+/RX 7700+/Arc; при отсутствии MFT энкодер честно кидает `NotSupportedException` — UI показывает диалог, выберите H264/HEVC.
3. **Оверлей поверх exclusive fullscreen** ОС не рисует в принципе (ShadowPlay делает это через собственный in-game hook). В borderless/windowed — работает везде; сама **запись** работает в любом режиме, включая exclusive fullscreen.
4. **Смешанные частоты дискретизации**: микшер приводит всё к 48 кГц stereo float (WDL-ресемплер NAudio).
5. Все настройки — `%LocalAppData%\InstantReplay\settings.json`, логи — там же в `logs\`.
