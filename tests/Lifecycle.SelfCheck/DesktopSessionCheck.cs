using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using Aura.Core.Capture;
using Aura.Core.Engine;
using Aura.Core.Settings;
using Aura.Core.Storage;
using Aura.Core.SystemIntegration;

/// <summary>
/// Проверка сценариев блокировки сеанса и ARSO без запуска реального захвата видеокарты.
/// </summary>
internal static class DesktopSessionCheck
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly Type SnapshotType = typeof(ReplayEngine).Assembly
        .GetType("Aura.Core.SystemIntegration.DesktopSessionSnapshot", throwOnError: true)!;

    private static readonly ConstructorInfo SnapshotCtor = SnapshotType
        .GetConstructor([typeof(bool), typeof(bool?), typeof(bool)])!;

    private static readonly string ReasonLocked = ReadWatcherReason("ReasonLocked");
    private static readonly string ReasonDisplayOff = ReadWatcherReason("ReasonDisplayOff");
    private static readonly string ReasonSleep = ReadWatcherReason("ReasonSleep");

    private static string ReadWatcherReason(string fieldName) =>
        (string)typeof(SystemActivityWatcher).GetField(fieldName, PrivateStatic)!.GetValue(null)!;

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private sealed class DesktopStateBox(bool defaultInputDesktop, bool? sessionLocked, bool shellWindowPresent)
    {
        private readonly object _sync = new();
        private bool _defaultInputDesktop = defaultInputDesktop;
        private bool? _sessionLocked = sessionLocked;
        private bool _shellWindowPresent = shellWindowPresent;

        public void Set(bool defaultInputDesktop, bool? sessionLocked, bool shellWindowPresent)
        {
            lock (_sync)
            {
                _defaultInputDesktop = defaultInputDesktop;
                _sessionLocked = sessionLocked;
                _shellWindowPresent = shellWindowPresent;
            }
        }

        public bool GetDefaultInputDesktop() { lock (_sync) return _defaultInputDesktop; }
        public bool? GetSessionLocked() { lock (_sync) return _sessionLocked; }
        public bool GetShellWindowPresent() { lock (_sync) return _shellWindowPresent; }
    }

    private static void SetDesktopProbe(ReplayEngine engine, DesktopStateBox state)
    {
        var boxExpr = Expression.Constant(state);
        var newSnapshot = Expression.New(
            SnapshotCtor,
            Expression.Call(boxExpr, nameof(DesktopStateBox.GetDefaultInputDesktop), Type.EmptyTypes),
            Expression.Call(boxExpr, nameof(DesktopStateBox.GetSessionLocked), Type.EmptyTypes),
            Expression.Call(boxExpr, nameof(DesktopStateBox.GetShellWindowPresent), Type.EmptyTypes));
        var probeDelegate = Expression.Lambda(
            typeof(Func<>).MakeGenericType(SnapshotType),
            newSnapshot).Compile();

        typeof(ReplayEngine).GetProperty("DesktopProbeOverride", PrivateInstance)!.SetValue(engine, probeDelegate);
    }

    private static (SettingsManager Settings, StorageManager Storage, ReplayEngine Engine) CreateEngine(string root)
    {
        var settings = new SettingsManager();
        settings.Current.SaveRootPath = root;
        settings.Current.ScreenshotFolder = root;
        settings.Current.MonitorDeviceId = "nonexistent-selfcheck-monitor";
        settings.Current.CaptureGameAudio = false;
        settings.Current.CaptureMicrophone = false;
        var storage = new StorageManager(settings);
        var engine = new ReplayEngine(settings, storage);
        typeof(ReplayEngine).GetProperty("DesktopSettleOverride", PrivateInstance)!
            .SetValue(engine, TimeSpan.FromMilliseconds(40));
        typeof(ReplayEngine).GetProperty("DesktopPollOverride", PrivateInstance)!
            .SetValue(engine, TimeSpan.FromMilliseconds(10));
        return (settings, storage, engine);
    }

    internal static async Task CheckAsync()
    {
        string tempRoot = Path.Combine(
            AppContext.BaseDirectory,
            "artifacts-desktop-session-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await CheckArsoLockedAndDisplayToggleAsync(tempRoot);
            await CheckAutostartOnSecureDesktopUacStartsWhenReturnedAsync(tempRoot);
            await CheckSleepResumeWaitsForUnlockAsync(tempRoot);
            await CheckUnlockWhileWgcRunningRebuildsAfterSettleAsync(tempRoot);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static async Task CheckArsoLockedAndDisplayToggleAsync(string root)
    {
        var (_, storage, engine) = CreateEngine(root);
        using (storage)
        using (engine)
        {
            var desktop = new DesktopStateBox(defaultInputDesktop: false, sessionLocked: true, shellWindowPresent: false);
            SetDesktopProbe(engine, desktop);

            int warningCount = 0;
            engine.Warning += _ => Interlocked.Increment(ref warningCount);

            using var watcher = new SystemActivityWatcher(
                engine.SuspendForSystem,
                engine.ResumeAfterSystem,
                engine.RebuildAfterDisplayChange);
            watcher.SessionUnlocked += engine.NotifySessionUnlocked;

            // Имитируем ARSO: при старте сеанс уже заперт, затем экран гаснет и снова
            // просыпается ещё на экране блокировки (как 14 ноября 12:13:56 -> 12:13:58).
            engine.SuspendForSystem(ReasonLocked);
            engine.SuspendForSystem(ReasonDisplayOff);
            engine.ResumeAfterSystem(ReasonDisplayOff);

            bool started = engine.StartFromAutostart();
            Require(!started, "StartFromAutostart must defer while desktop is locked");
            Require(engine.State == EngineState.Stopped, "Engine must stay Stopped while locked");
            Require(engine.SuspendedBySystem, "Engine must hold _suspendedBySystem until unlock");
            Require(Volatile.Read(ref warningCount) == 0, "Deferred autostart must not touch capture/monitor while locked");

            // Экран снова гаснет и просыпается ДО разблокировки: повтор не должен включаться.
            engine.SuspendForSystem(ReasonDisplayOff);
            engine.ResumeAfterSystem(ReasonDisplayOff);
            await Task.Delay(120);
            Require(Volatile.Read(ref warningCount) == 0 && engine.SuspendedBySystem,
                "DisplayStateOn on lock screen prematurely attempted capture start");

            // Поверх удержанного DisplayOff: StartFromAutostart при открытом рабочем столе,
            // но активном DisplayOff также обязан отложиться.
            desktop.Set(defaultInputDesktop: true, sessionLocked: false, shellWindowPresent: true);
            engine.Stop();
            engine.SuspendForSystem(ReasonDisplayOff);
            bool startedWithDisplayOff = engine.StartFromAutostart();
            Require(!startedWithDisplayOff && engine.SuspendedBySystem,
                "StartFromAutostart must defer while ReasonDisplayOff is active");
            Require(Volatile.Read(ref warningCount) == 0,
                "DisplayOff deferral must not attempt capture start");
        }

        Console.WriteLine("PASS: ARSO locked startup and lock-screen DisplayStateOn never start capture prematurely");
    }

    private static async Task CheckAutostartOnSecureDesktopUacStartsWhenReturnedAsync(string root)
    {
        var (_, storage, engine) = CreateEngine(root);
        using (storage)
        using (engine)
        {
            var desktop = new DesktopStateBox(defaultInputDesktop: false, sessionLocked: false, shellWindowPresent: true);
            SetDesktopProbe(engine, desktop);

            var startAttempted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Warning += msg => startAttempted.TrySetResult(msg);

            // Автозапуск в момент показа защищённого рабочего стола (UAC/Ctrl+Alt+Del без блокировки):
            // ReasonLocked не ставится, а отложенное ожидание само дожидается возврата Default.
            bool started = engine.StartFromAutostart();
            Require(!started, "StartFromAutostart must defer while non-locked secure desktop is active");
            Require(engine.State == EngineState.Stopped, "Engine must stay Stopped while secure desktop is active");
            Require(engine.SuspendedBySystem, "Engine must hold _suspendedBySystem while secure desktop is active");

            await Task.Delay(120);
            Require(!startAttempted.Task.IsCompleted, "Secure-desktop deferral attempted start prematurely");

            // UAC закрыт без WTS_SESSION_UNLOCK — возвращается Default: должен стартовать сам.
            desktop.Set(defaultInputDesktop: true, sessionLocked: false, shellWindowPresent: true);

            string msg = await startAttempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!string.IsNullOrEmpty(msg), "Expected start attempt after returning from UAC secure desktop");
            Require(!engine.SuspendedBySystem, "Engine stayed suspended after returning from UAC secure desktop");
        }

        Console.WriteLine("PASS: autostart deferred on non-locked secure desktop (UAC) resumes automatically when desktop returns");
    }

    private static async Task CheckSleepResumeWaitsForUnlockAsync(string root)
    {
        var (_, storage, engine) = CreateEngine(root);
        using (storage)
        using (engine)
        {
            var desktop = new DesktopStateBox(defaultInputDesktop: false, sessionLocked: true, shellWindowPresent: false);
            SetDesktopProbe(engine, desktop);

            var startAttempted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Warning += msg => startAttempted.TrySetResult(msg);

            var suspendedBySystem = typeof(ReplayEngine).GetField("_suspendedBySystem", PrivateInstance)!;
            suspendedBySystem.SetValue(engine, true);

            // Пробуждение из сна (PowerModes.Resume), пока компьютер всё ещё на экране блокировки.
            engine.ResumeAfterSystem(ReasonSleep);
            await Task.Delay(120);
            Require(!startAttempted.Task.IsCompleted,
                "ResumeAfterSystem(ReasonSleep) attempted start while session was still locked");
            Require(engine.SuspendedBySystem,
                "Engine dropped _suspendedBySystem before desktop became interactive");

            // Пользователь разблокирует ПК.
            desktop.Set(defaultInputDesktop: true, sessionLocked: false, shellWindowPresent: true);
            engine.NotifySessionUnlocked();

            string msg = await startAttempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!string.IsNullOrEmpty(msg), "Expected start attempt after unlock + settle");
            Require(!engine.SuspendedBySystem, "Engine stayed suspended after unlock + settle");
        }

        Console.WriteLine("PASS: ResumeAfterSystem waits through locked sleep wakeup and starts only after unlock + settle");
    }

    private static async Task CheckUnlockWhileWgcRunningRebuildsAfterSettleAsync(string root)
    {
        var (_, storage, engine) = CreateEngine(root);
        using (storage)
        using (engine)
        {
            var desktop = new DesktopStateBox(defaultInputDesktop: false, sessionLocked: true, shellWindowPresent: false);
            SetDesktopProbe(engine, desktop);

            var engineType = typeof(ReplayEngine);
            engineType.GetField("_state", PrivateInstance)!.SetValue(engine, EngineState.Running);
            engineType.GetField("_pipelineOpen", PrivateInstance)!.SetValue(engine, true);
            engineType.GetField("_captureBackend", PrivateInstance)!.SetValue(engine, CaptureBackend.Wgc);
            engineType.GetField("_captureGeneration", PrivateInstance)!.SetValue(engine, 11L);

            var rebuilding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.StateChanged += st =>
            {
                if (st == EngineState.Recovering) rebuilding.TrySetResult();
            };

            // Разблокировка при работающем WGC и _suspendedBySystem == false:
            // пока стол ещё не вернулся на Default, пересборка не начинается.
            engine.ResumeAfterSystem(ReasonLocked);
            await Task.Delay(120);
            Require(!rebuilding.Task.IsCompleted, "Unlock WGC rebuild started before desktop became interactive");

            // Переключаемся на Default — после settle (40 мс) должна запуститься пересборка захвата.
            desktop.Set(defaultInputDesktop: true, sessionLocked: false, shellWindowPresent: true);
            engine.NotifySessionUnlocked();

            await rebuilding.Task.WaitAsync(TimeSpan.FromSeconds(3));
            engine.Stop();
        }

        Console.WriteLine("PASS: unlock while WGC is running waits for desktop settle then triggers capture rebuild");
    }
}
