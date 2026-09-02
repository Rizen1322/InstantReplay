using Aura.Core.Logging;

namespace Aura.Core.Encoding;

/// <summary>
/// Пресет качество/скорость, который подстраивается сам.
///
/// Было жёстко 25 — быстрый пресет NVENC, выбранный когда энкодер не вытягивал 60 fps
/// под нагрузкой. Причина той просадки оказалась другой (не применялся AVLowLatencyMode),
/// а на быстром пресете при том же битрейте картинка заметно грубее в движении.
/// Поэтому стартуем с качественного пресета и уходим на быстрый сами, только если
/// энкодер действительно перестаёт успевать.
///
/// Ключ есть не у всех: NVIDIA на IsSupported отвечает «нет» (при этом SetValue молча
/// проглатывает значение — то есть прежняя жёсткая 25 у неё никогда ни на что не
/// влияла). Там, где ключа нет, адаптация просто выключается.
/// </summary>
internal sealed class QualityAdapter
{
    /// <summary>Качественный пресет — режим по умолчанию.</summary>
    public const uint Balanced = 50;
    /// <summary>Быстрый пресет: включается сам, когда энкодер не успевает.</summary>
    public const uint Fast = 25;

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
    private long _lastEncoded, _lastBlocked, _lastDropped;
    private int _calmWindows;
    private int _recoveryNeeded = RecoveryWindows;
    private bool _recoveryAttempted;
    private bool _unavailable;

    /// <summary>Текущий пресет — показывается в статистике конвейера.</summary>
    public uint Preset { get; private set; } = Balanced;

    public QualityAdapter(CodecApi? codecApi, int fps)
    {
        _codecApi = codecApi;
        _fps = fps;

        if (codecApi is null || !codecApi.IsSupported(CodecApiGuids.AVEncCommonQualityVsSpeed))
        {
            _unavailable = true;
            Log.Info("Encoder", "Пресет качество/скорость этот энкодер не поддерживает — " +
                                "адаптация под нагрузку выключена, качество определяется битрейтом");
            return;
        }
        codecApi.Set(CodecApiGuids.AVEncCommonQualityVsSpeed, Balanced);
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
    public void Tick(long encoded, long pacerBlocked, long dropped)
    {
        if (_unavailable || _clock.ElapsedMilliseconds < _nextCheckMs) return;
        _nextCheckMs = _clock.ElapsedMilliseconds + WindowMs;

        long dEncoded = encoded - _lastEncoded;
        long dBlocked = pacerBlocked - _lastBlocked;
        long dDropped = dropped - _lastDropped;
        _lastEncoded = encoded; _lastBlocked = pacerBlocked; _lastDropped = dropped;

        double target = _fps * (WindowMs / 1000.0);
        bool encoderBound = dEncoded < target * 0.9 && (dBlocked > 0 || dDropped > 0);

        if (encoderBound)
        {
            _calmWindows = 0;

            if (Preset != Fast)
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
                Apply(Fast, $"энкодер не успевает ({dEncoded / (WindowMs / 1000.0):F0} из {_fps} fps)");
                _recoveryAttempted = false;
            }
        }
        else if (Preset != Balanced && ++_calmWindows >= _recoveryNeeded)
        {
            _calmWindows = 0;
            _recoveryAttempted = true;   // если качество не удержится, порог вырастет
            Apply(Balanced, "нагрузка спала");
        }
        else if (Preset == Balanced && ++_calmWindows >= MaxRecoveryWindows)
        {
            // Давно спокойно и качество держится — снимаем накопленный штраф,
            // иначе один тяжёлый эпизод навсегда оставлял бы долгий порог
            _calmWindows = 0;
            _recoveryNeeded = RecoveryWindows;
        }
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
