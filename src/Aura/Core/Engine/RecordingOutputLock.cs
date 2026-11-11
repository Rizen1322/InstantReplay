namespace Aura.Core.Engine;

/// <summary>
/// Размер, от которого считается выход записи (OutputResolution), закреплённый на
/// весь сеанс повтора.
///
/// ЗАЧЕМ. Раньше размер выхода читался заново на каждой пересборке конвейера из
/// режима рабочего стола в реестре. Пересборка как раз случается при смене режима
/// экрана: пробуждение монитора, игра на своём разрешении. В этот момент Windows
/// может ещё (или уже) показывать временный режим — у пользователя после
/// пробуждения монитора это был 1920x1080 вместо 2560x1440, и запись осталась в
/// 1080p до конца сеанса. Здесь размер выхода — свойство сеанса, как холст в OBS:
/// источник любого размера масштабируется в него, а сам он меняется только по
/// воле человека (другой монитор, другое разрешение в настройках, выключил и
/// включил повтор).
/// </summary>
internal sealed class RecordingOutputLock
{
    private (int Monitor, int Vertical, int Width, int Height)? _locked;

    /// <summary>
    /// База выхода для сеанса. Первый вызов (или после <see cref="Reset"/>, или с
    /// другим монитором/разрешением в настройках) закрепляет <paramref name="desktopMode"/>,
    /// а если его прочитать не удалось — <paramref name="fallback"/>. Дальше
    /// возвращается закреплённое значение, что бы ни показывала система.
    /// </summary>
    public (int Width, int Height) Resolve(int monitor, int vertical, (int Width, int Height)? desktopMode,
                                           (int Width, int Height) fallback, out bool reused)
    {
        if (_locked is { } l && l.Monitor == monitor && l.Vertical == vertical)
        {
            reused = true;
            return (l.Width, l.Height);
        }
        var (w, h) = desktopMode ?? fallback;
        _locked = (monitor, vertical, w, h);
        reused = false;
        return (w, h);
    }

    /// <summary>
    /// Размер кадра энкодера: вертикаль из настроек, но не больше базы (вверх не
    /// растягиваем), ширина по пропорции базы, обе чётные (требование NV12/P010).
    /// </summary>
    public static (int Width, int Height) OutputFor((int Width, int Height) outputBase, int vertical)
    {
        int height = Math.Min(vertical, outputBase.Height) & ~1;
        int width = (int)Math.Round((double)outputBase.Width / outputBase.Height * height) & ~1;
        return (width, height);
    }

    /// <summary>Повтор выключил человек: следующий старт снова читает рабочий стол.</summary>
    public void Reset() => _locked = null;
}
