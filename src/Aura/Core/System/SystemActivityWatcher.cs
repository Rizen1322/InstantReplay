using System.Runtime.InteropServices;
using System.Windows.Interop;
using Aura.Core.Interop;
using Aura.Core.Logging;
using Microsoft.Win32;

namespace Aura.Core.SystemIntegration;

/// <summary>
/// Следит за тем, смотрит ли кто-нибудь на экран, и сообщает об этом движку.
///
/// ЗАЧЕМ. Повтор писался круглосуточно: при запертом сеансе, при погашенном экране
/// и даже пока машина уходила в сон. Пользы от такой записи нет никакой, а цена
/// вполне ощутимая: занятая видеопамять, работающий энкодер, около гигабайта
/// оперативной памяти под кольцевым буфером и разряд батареи на ноутбуке.
///
/// Ловим три независимых сигнала:
///
/// • блокировка и разблокировка сеанса — <see cref="SystemEvents.SessionSwitch"/>;
/// • погасший и включённый экран — уведомление питания GUID_CONSOLE_DISPLAY_STATE;
/// • уход в сон и пробуждение — <see cref="SystemEvents.PowerModeChanged"/>.
///
/// Сигналы приходят вперемешку и не парами: система гасит экран и запирает сеанс
/// почти одновременно, а после пробуждения порядок событий вообще не определён.
/// Поэтому здесь только приход и уход причины, а решение принимает движок по
/// множеству причин (см. ReplayEngine.SuspendForSystem).
///
/// Окно нужно потому, что уведомления питания доставляются оконным сообщением.
/// Берём message-only окно: оно не появляется на экране, не участвует в
/// переключении задач и живёт ровно столько, сколько живёт эта служба.
/// </summary>
public sealed class SystemActivityWatcher : IDisposable
{
    private const string ReasonLocked = "Сеанс заперт";
    private const string ReasonDisplayOff = "Экран погашен";
    private const string ReasonSleep = "Машина уходит в сон";

    private readonly Action<string> _suspend;
    private readonly Action<string> _resume;
    private readonly Action<string> _displayChanged;

    /// <summary>
    /// Смена набора дисплеев приходит пачкой: система шлёт несколько уведомлений
    /// подряд, пока раскладка не устоится. Пересобирать конвейер на каждое из них
    /// бессмысленно, поэтому берём последнее за короткое окно.
    /// </summary>
    private static readonly TimeSpan DisplaySettle = TimeSpan.FromMilliseconds(700);
    private int _displayEpoch;

    private HwndSource? _window;
    private IntPtr _displayNotification;
    private bool _disposed;

    public SystemActivityWatcher(
        Action<string> suspend,
        Action<string> resume,
        Action<string> displayChanged)
    {
        _suspend = suspend;
        _resume = resume;
        _displayChanged = displayChanged;
    }

    /// <summary>
    /// Подписаться на события. Звать из потока интерфейса: message-only окну нужен
    /// поток с насосом сообщений, а SystemEvents доставляет свои события в него же.
    /// </summary>
    public void Start()
    {
        if (_window is not null) return;

        try
        {
            var parameters = new HwndSourceParameters("Aura.SystemActivityWatcher")
            {
                // HWND_MESSAGE: окно существует только ради сообщений
                ParentWindow = new IntPtr(-3),
                Width = 0,
                Height = 0
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);

            _displayNotification = NativeMethods.RegisterPowerSettingNotification(
                _window.Handle,
                NativeMethods.GuidConsoleDisplayState,
                NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (_displayNotification == IntPtr.Zero)
                Log.Warn("System", $"Состояние экрана не отслеживается: ошибка {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Warn("System", $"Окно для событий питания не создано: {ex.Message}");
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Log.Info("System", "Слежение за блокировкой, экраном, сном и дисплеями включено");
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WM_POWERBROADCAST ||
            (int)wParam != NativeMethods.PBT_POWERSETTINGCHANGE ||
            lParam == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var setting = Marshal.PtrToStructure<NativeMethods.PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting != NativeMethods.GuidConsoleDisplayState) return IntPtr.Zero;

            // Приглушённый экран (DisplayStateDimmed) паузой не считаем: человек
            // всё ещё перед монитором, просто ничего не трогает.
            if (setting.Data == NativeMethods.DisplayStateOff) Suspend(ReasonDisplayOff);
            else if (setting.Data == NativeMethods.DisplayStateOn) Resume(ReasonDisplayOff);
        }
        catch (Exception ex)
        {
            Log.Warn("System", $"Событие состояния экрана не разобрано: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            // Запирание сеанса и переключение пользователя равнозначны: наш рабочий
            // стол в обоих случаях больше не на экране.
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Suspend(ReasonLocked);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                Resume(ReasonLocked);
                // Разблокировать сеанс с погашенным экраном нельзя: человек видит
                // поле ввода пароля. Снимаем и причину «экран погашен» — иначе
                // драйвер, который не прислал событие включения, оставил бы повтор
                // выключенным до перезапуска приложения.
                Resume(ReasonDisplayOff);
                break;
        }
    }

    /// <summary>
    /// Набор дисплеев изменился: монитор выключили кнопкой, выдернули кабель,
    /// поменяли разрешение или переключили основной экран.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ СОСТОЯНИЯ ЭКРАНА. Физическое выключение монитора не даёт
    /// события питания вовсе: для Windows это исчезновение дисплея с шины, а не
    /// погашенный экран. Захват при этом молча перестаёт отдавать кадры, и раньше
    /// это замечал только сторож тишины — через пятнадцать секунд, которые уходили
    /// в буфер пустотой. Здесь мы узнаём об этом сразу.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        int epoch = Interlocked.Increment(ref _displayEpoch);
        _ = SettleAndNotify(epoch);
    }

    private async Task SettleAndNotify(int epoch)
    {
        try
        {
            await Task.Delay(DisplaySettle).ConfigureAwait(false);
            if (_disposed || Volatile.Read(ref _displayEpoch) != epoch) return;
            _displayChanged("Набор дисплеев изменился");
        }
        catch (Exception ex) { Log.Warn("System", $"Смена дисплеев: {ex.Message}"); }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspend(ReasonSleep);
                break;
            case PowerModes.Resume:
                // После пробуждения устройство захвата и энкодер часто ещё не готовы:
                // драйвер видеокарты поднимается позже самой системы. Поднимать
                // конвейер сразу означает поймать ошибку создания устройства и уйти
                // в восстановление. Поэтому даём системе прийти в себя.
                DelayedResume(ReasonSleep, TimeSpan.FromSeconds(5));
                break;
        }
    }

    private async void DelayedResume(string reason, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (!_disposed) Resume(reason);
        }
        catch (Exception ex) { Log.Warn("System", $"Возобновление после сна: {ex.Message}"); }
    }

    private void Suspend(string reason)
    {
        if (_disposed) return;
        try { _suspend(reason); }
        catch (Exception ex) { Log.Error("System", ex); }
    }

    private void Resume(string reason)
    {
        if (_disposed) return;
        try { _resume(reason); }
        catch (Exception ex) { Log.Error("System", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_displayNotification != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = IntPtr.Zero;
        }

        if (_window is not null)
        {
            _window.RemoveHook(WndProc);
            _window.Dispose();
            _window = null;
        }
    }
}
