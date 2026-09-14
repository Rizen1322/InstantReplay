using Aura.Core.Logging;

namespace Aura.Core.Encoding;

/// <summary>
/// Пресет качество/скорость, который подстраивается сам.
///
/// Было жёстко 25 — быстрый пресет NVENC, выбранный когда энкодер не вытягивал 60 fps
/// под нагрузкой. Причина той просадки оказалась другой (не применялся AVLowLatencyMode),
/// а на быстром пресете при том же битрейте картинка заметно грубее в движении.
/// Поэтому стартуем с высококачественного пресета и уходим на средний сами, только если
/// энкодер действительно перестаёт успевать.
///
/// Ключ есть не у всех, а допустимый диапазон у разных MFT отличается. Там, где
/// ключа нет, адаптация просто выключается; где есть — принятое значение читается
/// обратно, потому что некоторые драйверы молча ограничивают запрос.
/// </summary>
internal sealed class QualityAdapter
{
    private uint _qualityPreset = 75;
    private uint _fallbackPreset = 50;

    /// <summary>Окно оценки. Короче — дёргано, длиннее — поздно реагируем.</summary>
    private const int WindowMs = 5000;
    /// <summary>Сколько спокойных окон подряд нужно, чтобы вернуться к качеству.</summary>
    private const int RecoveryWindows = 6;

    /// <summary>
    /// Сколько спокойных окон требуется после КАЖДОГО неудачного возврата.
    ///
    /// ЗАЧЕМ. В логах пресет прыгал 50 → 25 → 50 → 25 с интервалом в пять секунд:
    /// шесть спокойных окон набирались, качество возвращалось, нагрузка тут же
    /// возвращала его обратно. Каждое переключение перенастраивает энкодер на ходу,
    /// то есть флап стоит дороже, чем выигрыш от качества. Поэтому после неудачной
    /// попытки порог удваивается — до предела, а после долгого спокойствия
    /// возвращается к исходному.
    /// </summary>
    private const int MaxRecoveryWindows = 48;   // около четырёх минут

    private readonly CodecApi? _codecApi;
    private readonly int _fps;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _nextCheckMs = WindowMs;
    private long _lastEncoded, _lastSubmitted, _lastBlocked, _lastDropped;
    private int _calmWindows;
    private int _recoveryNeeded = RecoveryWindows;
    private bool _recoveryAttempted;
    private bool _unavailable;

    // ---------------- Режим низкой задержки ----------------
    //
    // AVLowLatencyMode включается при старте, потому что без него NVENC под игровой
    // нагрузкой просил лишь 39–44 кадра/с вместо 60. Но у NVENC этот режим стоит
    // качества: он запрещает просмотр вперёд и B-кадры и сажает буфер VBV, то есть
    // при том же битрейте картинка грубее. Именно здесь проходит часть разрыва
    // с ShadowPlay, который пишет через NVENC напрямую и ничего этого не теряет.
    //
    // Поэтому после длительного спокойствия пробуем выключить его ОДИН раз за сеанс.
    // Если энкодер после этого перестаёт успевать — возвращаем и больше не трогаем.
    // Одна попытка вместо повторов выбрана намеренно: цена неудачи это одно окно
    // в пять секунд с возможными потерями кадров, и платить её многократно незачем.

    /// <summary>Сколько спокойных окон подряд нужно, чтобы попробовать без низкой задержки.</summary>
    private const int LowLatencyTrialWindows = 12;   // около минуты

    private readonly bool _lowLatencyStartsOn;
    private bool _lowLatencyOn;
    private bool _lowLatencyTrialDone;
    private int _lowLatencyCalmWindows;

    /// <summary>Включён ли сейчас режим низкой задержки — показывается в статистике.</summary>
    public bool LowLatency => _lowLatencyOn;

    /// <summary>Текущий пресет — показывается в статистике конвейера.</summary>
    public uint Preset { get; private set; }

    public QualityAdapter(CodecApi? codecApi, int fps, bool lowLatencyOn)
    {
        _codecApi = codecApi;
        _fps = fps;
        _lowLatencyStartsOn = lowLatencyOn;
        _lowLatencyOn = lowLatencyOn;

        if (codecApi is null || !codecApi.IsSupported(CodecApiGuids.AVEncCommonQualityVsSpeed))
        {
            _unavailable = true;
            Log.Info("Encoder", "Пресет качество/скорость этот энкодер не поддерживает — " +
                                "адаптация под нагрузку выключена, качество определяется битрейтом");
            return;
        }
        // Диапазон зависит от MFT. Стандартный энкодер допускает 0–100, а
        // NVIDIA HEVC на RTX 3070 сообщает 0–33 и молча зажимает 50/75 до 33.
        // Берём реальный максимум вместо фиктивного универсального числа.
        if (codecApi.TryGetUIntRange(CodecApiGuids.AVEncCommonQualityVsSpeed, out uint min, out uint max))
        {
            _qualityPreset = max >= 75 ? 75 : max;
            _fallbackPreset = Math.Clamp((uint)Math.Round(_qualityPreset * 0.75), min, _qualityPreset);
        }
        codecApi.Set(CodecApiGuids.AVEncCommonQualityVsSpeed, _qualityPreset);
        if (codecApi.TryReadUInt(CodecApiGuids.AVEncCommonQualityVsSpeed, out uint accepted))
            Preset = _qualityPreset = accepted;
        else
            Preset = _qualityPreset;
        if (_fallbackPreset == 0 || _fallbackPreset >= _qualityPreset)
            _fallbackPreset = (uint)Math.Round(_qualityPreset * 0.75);

        Log.Info("Encoder", $"Пресет качества: {Preset} (аварийный {_fallbackPreset})");
    }

    /// <summary>
    /// Раз в 5 секунд смотрим, справляется ли энкодер, и переключаем пресет.
    ///
    /// Признак «упирается ЭНКОДЕР» — именно пара условий: закодировано заметно меньше
    /// целевого fps И при этом очередь переполнялась (пейсер молчал или кадры дропались).
    /// Одного отставания по fps мало: когда голодает захват (WGC отдаёт 18 кадров в
    /// секунду), кадров просто нет, и ронять качество тут незачем — быстрее не станет.
    ///
    /// Слабая видеокарта попадает сюда сама собой: у неё окно «не справляется»
    /// наступает в первые же секунды записи.
    /// </summary>
    public void Tick(long encoded, long submitted, long pacerBlocked, long dropped)
    {
        if (_clock.ElapsedMilliseconds < _nextCheckMs) return;
        _nextCheckMs = _clock.ElapsedMilliseconds + WindowMs;

        long dEncoded = encoded - _lastEncoded;
        long dSubmitted = submitted - _lastSubmitted;
        long dBlocked = pacerBlocked - _lastBlocked;
        long dDropped = dropped - _lastDropped;
        _lastEncoded = encoded; _lastSubmitted = submitted;
        _lastBlocked = pacerBlocked; _lastDropped = dropped;

        double target = _fps * (WindowMs / 1000.0);

        // Условие «упирается ЭНКОДЕР» требует, чтобы кадры до энкодера вообще ДОШЛИ.
        //
        // ЗАЧЕМ. В логе 14 сентября окно с отставанием было засчитано энкодеру, хотя
        // виноват был захват: WGC перестала присылать кадры («поток кадров
        // остановился»), подача упала до 35.8 из 60 кадров в секунду, и счётчик
        // молчания пейсера вырос вместе с ней. Итогом стали два неверных решения
        // разом — пресет упал до аварийного и попытка отдать режим низкой задержки
        // была потрачена впустую. Если пейсер не подал целевое число кадров, энкодер
        // этой работы и не получал, и спрашивать с него нечего.
        bool fedEnough = dSubmitted >= target * 0.9;
        bool encoderBound = fedEnough && dEncoded < target * 0.9 && (dBlocked > 0 || dDropped > 0);

        TickLowLatency(encoderBound, dEncoded);
        if (_unavailable) return;

        if (encoderBound)
        {
            _calmWindows = 0;

            if (Preset != _fallbackPreset)
            {
                // Возврат к качеству не сработал — в следующий раз ждём дольше.
                // Иначе получается флап 50 → 25 → 50 → 25 каждые пять секунд, и
                // энкодер перенастраивается чаще, чем успевает дать выигрыш.
                if (_recoveryAttempted)
                {
                    _recoveryNeeded = Math.Min(_recoveryNeeded * 2, MaxRecoveryWindows);
                    Log.Info("Encoder", $"Возврат к качеству не удержался — следующая попытка " +
                                        $"через {_recoveryNeeded * WindowMs / 1000} с");
                }
                Apply(_fallbackPreset, $"энкодер не успевает ({dEncoded / (WindowMs / 1000.0):F0} из {_fps} fps)");
                _recoveryAttempted = false;
            }
        }
        else if (Preset != _qualityPreset && ++_calmWindows >= _recoveryNeeded)
        {
            _calmWindows = 0;
            _recoveryAttempted = true;   // если качество не удержится, порог вырастет
            Apply(_qualityPreset, "нагрузка спала");
        }
        else if (Preset == _qualityPreset && ++_calmWindows >= MaxRecoveryWindows)
        {
            // Давно спокойно и качество держится — снимаем накопленный штраф,
            // иначе один тяжёлый эпизод навсегда оставлял бы долгий порог
            _calmWindows = 0;
            _recoveryNeeded = RecoveryWindows;
        }
    }

    /// <summary>
    /// Одна попытка за сеанс отдать качество вместо низкой задержки, и немедленный
    /// возврат, если энкодер после этого не справляется.
    /// </summary>
    private void TickLowLatency(bool encoderBound, long encodedInWindow)
    {
        if (_codecApi is null || !_lowLatencyStartsOn) return;

        if (encoderBound)
        {
            _lowLatencyCalmWindows = 0;
            if (_lowLatencyOn) return;

            // Выключили и не потянули — возвращаем и больше не пробуем.
            _lowLatencyTrialDone = true;
            if (SetLowLatency(true))
                Log.Info("Encoder", "Без режима низкой задержки энкодер не успевает " +
                                    $"({encodedInWindow / (WindowMs / 1000.0):F0} из {_fps} fps) — " +
                                    "режим возвращён, качество определяется битрейтом");
            return;
        }

        if (_lowLatencyTrialDone || !_lowLatencyOn) return;
        if (++_lowLatencyCalmWindows < LowLatencyTrialWindows) return;

        _lowLatencyTrialDone = true;
        if (SetLowLatency(false))
            Log.Info("Encoder", "Нагрузка спокойная — режим низкой задержки выключен, " +
                                "энкодеру доступны просмотр вперёд и B-кадры");
    }

    private bool SetLowLatency(bool value)
    {
        // Только прямым вызовом и только как VT_BOOL: Tick приходит из потока пейсера,
        // а ключ объявлен булевым (см. CodecApi.SetDirectBool).
        string error = "интерфейс недоступен";
        if (_codecApi is not null && _codecApi.SetDirectBool(CodecApiGuids.AVLowLatencyMode, value, out error))
        {
            _lowLatencyOn = value;
            return true;
        }

        _lowLatencyTrialDone = true;
        Log.Info("Encoder", $"Режим низкой задержки на лету не меняется ({error}) — " +
                            $"остаётся {(_lowLatencyOn ? "включённым" : "выключенным")}");
        return false;
    }

    private void Apply(uint value, string reason)
    {
        // Только прямым вызовом: Tick приходит из потока пейсера, а обёртка .NET
        // с чужого потока отвечает E_NOINTERFACE (см. CodecApi).
        string failure = "интерфейс недоступен";
        if (_codecApi is not null && _codecApi.SetDirect(CodecApiGuids.AVEncCommonQualityVsSpeed, value, out failure))
        {
            Preset = value;
            Log.Info("Encoder", $"Пресет качества → {value} ({reason})");
            return;
        }

        // Энкодер не даёт менять пресет на лету — больше не дёргаем его каждые 5 сек
        _unavailable = true;
        Log.Info("Encoder", $"Пресет качества на лету не меняется ({failure}) — остаётся {Preset}");
    }
}
