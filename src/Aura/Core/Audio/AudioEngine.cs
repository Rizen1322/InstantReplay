using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Aura.Core.Buffering;
using Aura.Core.Logging;

namespace Aura.Core.Audio;

/// <summary>Кадр AAC одной дорожки. Байты валидны только на время вызова.</summary>
public delegate void EncodedAudioHandler(AudioTrackKind kind, ReadOnlySpan<byte> frame, long ptsTicks);

/// <summary>
/// Звук повтора: два источника (звук системы и микрофон) на общей шкале времени
/// QPC, сведение, громкость, шумовой гейт, лимитер и кодирование в AAC трёх
/// дорожек — «игра», «микрофон» и «смесь».
///
/// УСТРОЙСТВО.
/// • <see cref="WasapiSource"/> кладёт пакеты устройства на свою
///   <see cref="AudioTimeline"/> по времени, которое сообщило само устройство.
/// • Микшер раз в 10 мс берёт с обеих шкал один и тот же отрезок времени — с
///   отставанием <see cref="MixLagTicks"/>, чтобы запоздавшие пакеты успели лечь
///   на место. Время блока — ровно время этого отрезка, поэтому отставание на
///   синхронизацию с видео не влияет вовсе.
/// • Каждая дорожка кодируется в AAC сразу; наружу уходят готовые кадры.
///
/// Какие дорожки положить в файл, решается при сохранении — все три уже готовы.
/// Смесь кодируется только когда есть оба источника.
/// </summary>
public sealed class AudioMixerEngine : IDisposable
{
    /// <summary>Блок микшера — 10 мс.</summary>
    public const int BlockFrames = AudioTimeline.Rate / 100;

    /// <summary>
    /// Отставание микшера от реального времени. Пакет loopback приходит примерно
    /// через 10–20 мс после того, как прозвучал, микрофон — через 10–30 мс, у
    /// Bluetooth и USB бывает заметно больше. 200 мс покрывают всё это с запасом;
    /// на сохранение это влияет только тем, что последние 0.2 с звука дописываются
    /// чуть позже (сохранение их дожидается).
    /// </summary>
    public const long MixLagTicks = 2_000_000;

    public const int GameBitrate = 192_000;
    public const int MicBitrate = 128_000;

    private readonly object _sync = new();
    private WasapiSource? _game;
    private WasapiSource? _mic;
    private AudioTimeline? _gameLine;
    private AudioTimeline? _micLine;
    private AacEncoder? _gameEncoder, _micEncoder, _mixEncoder;
    private Thread? _thread;
    private volatile bool _running;

    public bool MicNoiseGate { get; set; }

    /// <summary>Шумоподавление микрофона нейросетью (RNNoise) — до гейта и громкости.</summary>
    public bool MicNeuralDenoise { get; set; } = true;
    public float MicGateThresholdDb { get; set; } = -44f;

    /// <summary>Push-to-talk включён: микрофон пишется, только пока клавиша зажата.</summary>
    public bool PushToTalkEnabled { get; set; }

    /// <summary>Нажатия push-to-talk со временем (см. <see cref="PushToTalkGate"/>).</summary>
    public PushToTalkGate PushToTalk { get; } = new();

    /// <summary>Громкость звука игры, 1 — без изменений (0..2).</summary>
    public float GameVolume { get; set; } = 1f;

    /// <summary>Громкость микрофона, 1 — без изменений (0..2).</summary>
    public float MicVolume { get; set; } = 1f;

    public float GamePeak { get; private set; }
    public float MicPeak { get; private set; }

    /// <summary>Готовый кадр AAC какой-либо дорожки.</summary>
    public event EncodedAudioHandler? FrameEncoded;

    public event Action<string>? Warning;

    /// <summary>
    /// До какого времени (тики QPC) звук уже сведён и закодирован. Сохранение ждёт,
    /// пока микшер дойдёт до конца клипа, чтобы последние доли секунды не
    /// оказались без звука.
    /// </summary>
    public long MixedUpToTicks => Interlocked.Read(ref _mixedUpTo);
    private long _mixedUpTo;

    /// <summary>Идёт ли сейчас сведение.</summary>
    public bool IsRunning => _running;

    // Настройки последнего запуска — по ним пересоздаём источник, если система
    // сменила устройство по умолчанию.
    private bool _wantGame, _wantMic;
    private string? _renderDeviceId, _captureDeviceId;

    /// <summary>
    /// Программы, чей звук пишется в дорожку игры; пусто — весь звук устройства
    /// вывода. У каждой своя шкала и свой loopback процесса, микшер их складывает:
    /// loopback Windows берёт только одно дерево процессов, поэтому «игра плюс
    /// Discord» это два источника, а не один.
    /// </summary>
    private string[] _gameProcesses = [];
    private ProcessSlot[] _slots = [];
    private System.Threading.Timer? _processWatch;
    private const long ProcessRetryMs = 30_000;

    private bool ProcessMode => _gameProcesses.Length > 0;

    /// <summary>Источник звука одной программы. Поля меняются под _gameSwitch, Source ещё и под _sync.</summary>
    private sealed class ProcessSlot(string name, AudioTimeline line)
    {
        public string Name { get; } = name;
        public AudioTimeline Line { get; } = line;
        public WasapiSource? Source;
        public long RetryAfter;
        public int Failures;
        public bool MissingLogged;
        public bool FailureWarned;
    }

    /// <summary>Разделитель программ в настройке <c>GameAudioProcess</c>.</summary>
    public const char ProcessSeparator = ';';

    /// <summary>Имена программ из настройки: «cs2;Discord» — две программы.</summary>
    public static string[] ParseProcesses(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(ProcessSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Строка настройки из списка программ; null — весь звук.</summary>
    public static string? JoinProcesses(IEnumerable<string> names)
    {
        string joined = string.Join(ProcessSeparator, names.Where(n => !string.IsNullOrWhiteSpace(n)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// Смена источника звука игры идёт по одному: слежение за процессом (раз в
    /// 2 с) и пересоздание после смены устройства раньше могли идти разом, и на
    /// миг на шкалу писали два источника, звук удваивался. А открытие loopback
    /// процесса ждёт систему до 5 с, дольше периода таймера, и вызовы наслаивались.
    /// </summary>
    private readonly object _gameSwitch = new();
    private MMDeviceEnumerator? _deviceWatchEnumerator;
    private DefaultDeviceWatcher? _deviceWatcher;
    private int _generation;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Channels, int Bitrate), byte[]?> Silence = new();

    /// <summary>Кадр AAC тишины для дорожки — заполнять разрывы при сохранении.</summary>
    public static byte[]? SilentFrameOf(AudioTrackKind kind) => kind == AudioTrackKind.Mic
        ? Silence.GetOrAdd((1, MicBitrate), key => AacEncoder.CreateSilentFrame(key.Channels, key.Bitrate))
        : Silence.GetOrAdd((2, GameBitrate), key => AacEncoder.CreateSilentFrame(key.Channels, key.Bitrate));

    /// <summary>Описание дорожки для контейнера; null — такой дорожки нет.</summary>
    public Saving.Mp4.Mp4AudioFormat? FormatOf(AudioTrackKind kind)
    {
        lock (_sync)
        {
            AacEncoder? encoder = kind switch
            {
                AudioTrackKind.Game => _gameEncoder,
                AudioTrackKind.Mic => _micEncoder,
                _ => _mixEncoder
            };
            if (encoder is null) return null;
            string name = kind switch
            {
                AudioTrackKind.Game => "Game audio",
                AudioTrackKind.Mic => "Microphone",
                _ => "Game + microphone"
            };
            return new Saving.Mp4.Mp4AudioFormat(AacEncoder.SampleRate, encoder.Channels,
                                                 encoder.AudioSpecificConfig, encoder.Bitrate, name)
            {
                SilentFrame = SilentFrameOf(kind)
            };
        }
    }

    public void Start(bool captureGame, bool captureMic, string? renderDeviceId, string? captureDeviceId,
                      string? gameProcess = null)
    {
        Stop();

        _wantGame = captureGame; _wantMic = captureMic;
        _renderDeviceId = renderDeviceId; _captureDeviceId = captureDeviceId;
        _gameProcesses = ParseProcesses(gameProcess);
        if (!captureGame && !captureMic) return;

        long origin = NowTicks();
        lock (_sync)
        {
            if (captureGame)
            {
                if (ProcessMode)
                    _slots = _gameProcesses.Select(name => new ProcessSlot(name, new AudioTimeline(origin, 2))).ToArray();
                else
                    _gameLine = new AudioTimeline(origin, 2);
                _gameEncoder = new AacEncoder(2, GameBitrate);
                _gameEncoder.FrameReady += (frame, pts) => Emit(AudioTrackKind.Game, frame, pts);
            }
            if (captureMic)
            {
                // Микрофон почти всегда моно; хранить его стерео значило удваивать
                // дорожку одинаковыми каналами.
                _micLine = new AudioTimeline(origin, 1);
                _micEncoder = new AacEncoder(1, MicBitrate);
                _micEncoder.FrameReady += (frame, pts) => Emit(AudioTrackKind.Mic, frame, pts);
            }
            if (captureGame && captureMic)
            {
                _mixEncoder = new AacEncoder(2, GameBitrate);
                _mixEncoder.FrameReady += (frame, pts) => Emit(AudioTrackKind.Mixed, frame, pts);
            }
        }

        // Кадры тишины понадобятся при сохранении — готовим заранее, в фоне
        _ = Task.Run(() => { SilentFrameOf(AudioTrackKind.Game); SilentFrameOf(AudioTrackKind.Mic); });

        if (captureGame && ProcessMode)
        {
            lock (_gameSwitch)
                foreach (var slot in _slots)
                {
                    var source = OpenSlot(slot);
                    lock (_sync) slot.Source = source;
                }
        }
        else if (captureGame) _game = OpenSource(loopback: true);
        if (captureMic) _mic = OpenSource(loopback: false);

        WatchDefaultDevices();

        // Звук отдельных программ: программа может запуститься позже Aura,
        // перезапуститься с новым номером процесса или закрыться. Раз в две
        // секунды сверяемся.
        if (captureGame && ProcessMode)
        {
            int watchGeneration = Volatile.Read(ref _generation);
            _processWatch = new System.Threading.Timer(_ => SyncGameProcess(watchGeneration), null,
                                                       TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        Interlocked.Exchange(ref _mixedUpTo, origin);
        _running = true;
        int generation = Volatile.Read(ref _generation);
        _thread = new Thread(() => MixLoop(origin, generation))
        {
            IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    private WasapiSource? OpenSource(bool loopback)
    {
        var line = loopback ? _gameLine! : _micLine!;
        try
        {
            var source = new WasapiSource(loopback, loopback ? _renderDeviceId : _captureDeviceId, line);
            source.Failed += _ => RestartSource(loopback ? DataFlow.Render : DataFlow.Capture);
            if (source.FellBackTo is { } name)
                Warning?.Invoke(loopback
                    ? $"Выбранное устройство вывода не найдено — пишу звук с «{name}»"
                    : $"Выбранный микрофон не найден — пишу с «{name}»");
            return source;
        }
        catch (Exception ex)
        {
            Log.Error("Audio", $"{(loopback ? "Loopback" : "Микрофон")} недоступен: {ex.Message}");
            Warning?.Invoke(loopback
                ? "Звук игры записать не удалось — проверьте устройство вывода в настройках"
                : "Микрофон записать не удалось — проверьте устройство в настройках");
            return null;
        }
    }

    /// <summary>
    /// Открыть loopback одной программы. Не запущена или не открылась: null, и
    /// весь звук компьютера вместо неё НЕ пишем. Человек выбрал программы, а в
    /// клип попали бы звонок, браузер и уведомления. Раньше так и было.
    /// </summary>
    private WasapiSource? OpenSlot(ProcessSlot slot)
    {
        int? pid = ProcessLoopback.FindProcess(slot.Name);
        if (pid is null)
        {
            if (!slot.MissingLogged)
                Log.Info("Audio", $"Звук игры: {slot.Name} не запущена, жду её");
            slot.MissingLogged = true;
            return null;
        }
        try
        {
            var source = new WasapiSource(pid.Value, slot.Name, slot.Line);
            source.Failed += _ => DropSlotSource(slot, source);
            slot.MissingLogged = false;
            return source;
        }
        catch (Exception ex)
        {
            slot.RetryAfter = Environment.TickCount64 + ProcessRetryMs;
            if (++slot.Failures is 1 or 2 or 10 or 100)
                Log.Warn("Audio", $"Звук только из {slot.Name} не открылся ({ex.Message}), " +
                                  $"пока пишу тишину, повторю через 30 с ({slot.Failures}-й раз)");
            if (!slot.FailureWarned)
                Warning?.Invoke($"Звук {slot.Name} не открылся. Пока он не пишется, Aura повторит попытку сама");
            slot.FailureWarned = true;
            return null;
        }
    }

    /// <summary>
    /// Источник программы отказал: убираем его, слежение за процессами откроет
    /// заново на следующей сверке. Освобождаем не в потоке источника: его
    /// Dispose ждёт этот самый поток.
    /// </summary>
    private void DropSlotSource(ProcessSlot slot, WasapiSource source)
    {
        lock (_sync)
        {
            if (slot.Source != source) return;
            slot.Source = null;
        }
        Log.Warn("Audio", $"Звук игры: источник {slot.Name} отказал, открою заново");
        Task.Run(source.Dispose);
    }

    /// <summary>Подхватить программы, которые запустились, перезапустились или закрылись.</summary>
    private void SyncGameProcess(int generation)
    {
        if (!Monitor.TryEnter(_gameSwitch)) return;   // прошлая сверка ещё идёт
        try
        {
            foreach (var slot in _slots)
            {
                if (!_running || generation != Volatile.Read(ref _generation)) return;
                int? wanted = ProcessLoopback.FindProcess(slot.Name);
                int? current;
                lock (_sync) current = slot.Source?.ProcessId;
                if (wanted == current) continue;
                // После неудачи новую попытку не раньше срока
                if (current is null && wanted is not null && Environment.TickCount64 < slot.RetryAfter) continue;

                WasapiSource? replacement = wanted is null ? null : OpenSlot(slot);
                WasapiSource? old;
                lock (_sync)
                {
                    if (!_running || generation != _generation) { replacement?.Dispose(); return; }
                    old = slot.Source;
                    slot.Source = replacement;
                }
                if (replacement is not null)
                {
                    Log.Info("Audio", $"Звук игры: подключился к {slot.Name} ({wanted})");
                    slot.FailureWarned = false;
                }
                else if (old is not null)
                    Log.Info("Audio", $"Звук игры: {slot.Name} закрылась, пишу тишину до её запуска");
                old?.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"Слежение за процессом игры: {ex.Message}"); }
        finally { Monitor.Exit(_gameSwitch); }
    }

    private void Emit(AudioTrackKind kind, ReadOnlySpan<byte> frame, long pts)
    {
        // Подписчиков двое (кольцевой буфер и запись в файл), и исключение любого
        // из них не должно убить поток микшера — звук важнее одного кадра.
        try { FrameEncoded?.Invoke(kind, frame, pts); }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _emitFailures) is 1 or 100 or 10_000)
                Log.Error("Audio", $"Подписчик уронил кадр звука ({_emitFailures}-й раз): {ex.Message}");
        }
    }
    private long _emitFailures;

    private void WatchDefaultDevices()
    {
        try
        {
            _deviceWatchEnumerator = new MMDeviceEnumerator();
            _deviceWatcher = new DefaultDeviceWatcher(flow =>
            {
                // Звук отдельной игры от устройства по умолчанию не зависит
                bool affectsGame = flow == DataFlow.Render && _wantGame && _renderDeviceId is null && !ProcessMode;
                bool affectsMic = flow == DataFlow.Capture && _wantMic && _captureDeviceId is null;
                if (affectsGame || affectsMic) RestartSource(flow);
            },
            // Появилось или включилось устройство: если источник ждёт своё
            // устройство, пробуем сразу, а не через минуту
            () => _deviceArrived.Set());
            _deviceWatchEnumerator.RegisterEndpointNotificationCallback(_deviceWatcher);
        }
        catch (Exception ex) { Log.Warn("Audio", $"Слежение за устройствами недоступно: {ex.Message}"); }
    }

    /// <summary>
    /// Пересоздать источник после смены устройства или его отказа. Шкала остаётся
    /// та же: новый источник кладёт звук на неё по времени, и в клипе на месте
    /// переключения будет короткая тишина, а не сдвиг всего звука.
    /// </summary>
    private void RestartSource(DataFlow flow)
    {
        int generation = Volatile.Read(ref _generation);
        bool loopback = flow == DataFlow.Render;
        // Звук отдельных программ от устройства не зависит: их переподключает
        // слежение за процессами
        if (loopback && ProcessMode) return;
        Task.Run(() =>
        {
            // Windows шлёт уведомление до того, как новое устройство готово. А
            // выдернутые наушники или засыпающий USB-микрофон бывают недоступны
            // дольше: раньше одна неудачная попытка оставляла запись без звука до
            // перезапуска. Теперь пробуем с растущей паузой, а потом раз в минуту,
            // пока устройство не вернётся: выбранный USB-микрофон, воткнутый через
            // пять минут, тоже должен подхватиться. Появление любого устройства
            // будит попытку сразу.
            int attempt = 0;
            foreach (int delay in RecoveryDelays())
            {
                if (attempt++ == InitialRecoveryAttempts)
                    Log.Warn("Audio", $"{(loopback ? "Звук игры" : "Микрофон")}: устройство пока недоступно, " +
                                      "жду его появления");
                if (delay <= 1000) Thread.Sleep(delay);
                else _deviceArrived.WaitOne(delay);
                if (!_running || generation != Volatile.Read(ref _generation)) return;

                WasapiSource? replacement;
                WasapiSource? old;
                // Источник игры меняем под тем же замком, что и слежение за процессом
                if (loopback) Monitor.Enter(_gameSwitch);
                try
                {
                    replacement = OpenSource(loopback);
                    if (replacement is null) continue;
                    lock (_sync)
                    {
                        if (!_running || generation != _generation)
                        {
                            replacement.Dispose();
                            return;
                        }
                        if (loopback) { old = _game; _game = replacement; }
                        else { old = _mic; _mic = replacement; }
                    }
                }
                finally { if (loopback) Monitor.Exit(_gameSwitch); }
                Log.Info("Audio", loopback
                    ? "Устройство вывода сменилось — источник звука игры пересоздан"
                    : "Устройство ввода сменилось — микрофон пересоздан");
                old?.Dispose();
                return;
            }
        });
    }

    private const int InitialRecoveryAttempts = 7;

    /// <summary>Паузы между попытками: сначала быстро, потом раз в минуту без конца.</summary>
    private static IEnumerable<int> RecoveryDelays()
    {
        foreach (int delay in new[] { 300, 1000, 2000, 4000, 8000, 15000, 30000 }) yield return delay;
        while (true) yield return 60_000;
    }

    /// <summary>Будит ожидающее восстановление источника: в системе появилось устройство.</summary>
    private readonly AutoResetEvent _deviceArrived = new(false);

    private sealed class DefaultDeviceWatcher(Action<DataFlow> onDefaultChanged, Action onDeviceArrived) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) onDefaultChanged(flow);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active) onDeviceArrived();
        }
        public void OnDeviceAdded(string pwstrDeviceId) => onDeviceArrived();
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private static long NowTicks() => (long)(System.Diagnostics.Stopwatch.GetTimestamp()
        * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    private void MixLoop(long origin, int generation)
    {
        // Поток микшера фоновый, и необработанное исключение в нём завершает процесс
        // без единой строки в логе.
        try { MixLoopCore(origin, generation); }
        catch (Exception ex) { Log.Error("Audio", $"Микшер остановлен: {ex}"); }
    }

    private void MixLoopCore(long origin, int generation)
    {
        using var timer = new Interop.PreciseTimer();
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.ProAudio, "AudioMixer");

        var game = new float[BlockFrames * 2];
        var programme = new float[BlockFrames * 2];
        var mic = new float[BlockFrames];
        var mix = new float[BlockFrames * 2];
        var gameLimiter = new Limiter(2);
        var micLimiter = new Limiter(1);
        var mixLimiter = new Limiter(2);
        var gate = new NoiseGate();
        using var denoiser = RnnoiseDenoiser.TryCreate();
        // Одна строка при каждом запуске микшера: по логу сразу видно, был ли шумодав
        // в записи. Без неё «шумодав не работает» проверить было не по чему.
        Log.Info("Audio", denoiser is null
            ? "Шумодав RNNoise: библиотека не загрузилась, микрофон пишется без него"
            : $"Шумодав RNNoise: готов, {(MicNeuralDenoise ? "включён" : "выключен в настройках")}");
        bool denoiseWas = MicNeuralDenoise;
        float micGain = 1f;
        long block = 0;
        int report = 0;

        while (_running && generation == Volatile.Read(ref _generation))
        {
            long blockStart = origin + block * 100_000;        // ровно 10 мс на блок
            long due = blockStart + 100_000 + MixLagTicks;
            long wait = due - NowTicks();
            if (wait > 5_000) { timer.Wait(wait); continue; }

            // Сильно отстали (система засыпала, поток не получал процессор): не
            // догоняем секунды звука залпом, а перескакиваем к настоящему. В AAC это
            // станет разрывом, после которого кодер начнёт отсчёт заново.
            if (wait < -10_000_000)
            {
                long skipTo = (NowTicks() - origin - MixLagTicks) / 100_000 - 1;
                Log.Warn("Audio", $"Микшер отстал на {-wait / 10_000} мс — пропускаю вперёд");
                block = Math.Max(block + 1, skipTo);
                continue;
            }

            long position = block * BlockFrames;
            AudioTimeline? gameLine, micLine;
            ProcessSlot[] slots;
            AacEncoder? gameEncoder, micEncoder, mixEncoder;
            lock (_sync)
            {
                gameLine = _gameLine; micLine = _micLine; slots = _slots;
                gameEncoder = _gameEncoder; micEncoder = _micEncoder; mixEncoder = _mixEncoder;
            }

            float gv = Math.Clamp(GameVolume, 0f, 2f);
            float mv = Math.Clamp(MicVolume, 0f, 2f);

            bool haveGame = gameLine is not null || slots.Length > 0;
            if (gameLine is not null) gameLine.Read(position, game, BlockFrames);
            else if (slots.Length > 0)
            {
                // Программы складываются; перегруз после сложения снимет лимитер
                slots[0].Line.Read(position, game, BlockFrames);
                for (int k = 1; k < slots.Length; k++)
                {
                    slots[k].Line.Read(position, programme, BlockFrames);
                    for (int i = 0; i < game.Length; i++) game[i] += programme[i];
                }
            }
            if (haveGame && gv != 1f) for (int i = 0; i < game.Length; i++) game[i] *= gv;
            if (micLine is not null)
            {
                micLine.Read(position, mic, BlockFrames);
                bool denoise = MicNeuralDenoise;
                if (denoise != denoiseWas)
                {
                    denoiseWas = denoise;
                    Log.Info("Audio", $"Шумодав RNNoise {(denoise ? "включён" : "выключен")}");
                }
                if (denoise) denoiser?.ProcessBlock(mic);
                float g = MicNoiseGate ? gate.Process(mic, MicGateThresholdDb) : 1f;
                float k = g * mv;
                if (PushToTalkEnabled && !PushToTalk.IsOpenAt(blockStart)) k = 0f;
                // Усиление плавно ведём от прошлого блока к новому внутри блока.
                // Раньше оно менялось ступенькой раз в 10 мс, и гейт на открытии и
                // закрытии давал щелчки.
                if (k != 1f || micGain != 1f)
                {
                    float step = (k - micGain) / mic.Length;
                    for (int i = 0; i < mic.Length; i++) mic[i] *= micGain + step * (i + 1);
                }
                micGain = k;
            }

            // Смесь считается до лимитеров отдельных дорожек: у неё свой лимитер
            if (mixEncoder is not null)
            {
                for (int f = 0; f < BlockFrames; f++)
                {
                    mix[2 * f] = game[2 * f] + mic[f];
                    mix[2 * f + 1] = game[2 * f + 1] + mic[f];
                }
                mixLimiter.Process(mix, BlockFrames);
            }
            if (haveGame) gameLimiter.Process(game, BlockFrames);
            if (micLine is not null) micLimiter.Process(mic, BlockFrames);

            GamePeak = haveGame ? Peak(game) : 0;
            MicPeak = micLine is null ? 0 : Peak(mic);

            // Время блока — начало отрезка на шкале; лимитер сдвигает звук на
            // свои 2 мс одинаково для всех дорожек.
            long pts = blockStart - Limiter.LookaheadFrames * 10_000_000L / AudioTimeline.Rate;
            gameEncoder?.Encode(game, BlockFrames, pts);
            micEncoder?.Encode(mic, BlockFrames, pts);
            mixEncoder?.Encode(mix, BlockFrames, pts);
            Interlocked.Exchange(ref _mixedUpTo, blockStart + 100_000);

            if (++report >= 6000) // раз в минуту
            {
                report = 0;
                string gameText = slots.Length == 0 ? Describe(gameLine)
                    : string.Join(", ", slots.Select(slot => $"{slot.Name} {Describe(slot.Line)}"));
                Log.Info("Audio", $"Шкала звука: игра {gameText}, микрофон {Describe(micLine)}; " +
                                  $"лимитер: игра {Db(gameLimiter)}, микрофон {Db(micLimiter)}, смесь {Db(mixLimiter)}");
            }
            block++;
        }
    }

    private static string Describe(AudioTimeline? line) => line is null ? "нет"
        : $"поправка темпа {line.Correction * 1e6:+0;-0;0} ppm, разрывов {line.Resyncs}";

    private static string Db(Limiter limiter)
    {
        float min = limiter.MinGain;
        limiter.MinGain = 1f;
        return min >= 0.999f ? "не срабатывал" : $"до {20 * MathF.Log10(min):F1} дБ";
    }

    private static float Peak(ReadOnlySpan<float> block)
    {
        float peak = 0;
        foreach (float v in block) { float a = MathF.Abs(v); if (a > peak) peak = a; }
        return Math.Min(1f, peak);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _running = false;
        _processWatch?.Dispose();
        _processWatch = null;
        try
        {
            if (_deviceWatcher is not null && _deviceWatchEnumerator is not null)
                _deviceWatchEnumerator.UnregisterEndpointNotificationCallback(_deviceWatcher);
        }
        catch { }
        _deviceWatcher = null;
        _deviceWatchEnumerator?.Dispose();
        _deviceWatchEnumerator = null;

        bool mixerExited = _thread?.Join(1000) ?? true;
        _thread = null;
        lock (_sync)
        {
            _game?.Dispose(); _game = null;
            foreach (var slot in _slots) slot.Source?.Dispose();
            _slots = [];
            _mic?.Dispose(); _mic = null;
            if (mixerExited)
            {
                _gameEncoder?.Dispose();
                _micEncoder?.Dispose();
                _mixEncoder?.Dispose();
            }
            else
            {
                // Микшер ещё внутри кодера — освобождать MFT под ним нельзя
                Log.Warn("Audio", "Микшер не завершился за секунду — кодеры AAC оставлены сборщику");
            }
            _gameEncoder = null; _micEncoder = null; _mixEncoder = null;
            _gameLine = null; _micLine = null;
        }
        GamePeak = MicPeak = 0;
    }

    public void Dispose() => Stop();
}
