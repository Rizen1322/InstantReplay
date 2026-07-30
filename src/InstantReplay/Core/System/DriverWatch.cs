using InstantReplay.Core.Hardware;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.SystemIntegration;

/// <summary>
/// Тихая проверка драйвера NVIDIA раз в неделю.
///
/// Правила, из которых сделано поведение:
/// • сеть дёргается не чаще раза в 7 дней, дата последней проверки живёт в настройках,
///   поэтому перезапуски приложения ничего не сбрасывают;
/// • про одну и ту же версию сообщаем ОДИН раз (см. NotifiedDriverVersion) — иначе
///   уведомление всплывало бы каждую неделю, пока драйвер не обновят;
/// • проверка идёт в фоне и с задержкой после старта: на запуске приложение и так
///   поднимает захват, энкодер и аудио, лезть в сеть в этот момент незачем;
/// • любая ошибка (нет сети, эндпоинт молчит) просто откладывает попытку — дату
///   последней проверки при неудаче не двигаем.
/// </summary>
public sealed class DriverWatch(SettingsManager settings, NvidiaDriverService nvidia)
{
    /// <summary>Как часто проверять.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    /// <summary>Пауза после запуска приложения, чтобы не мешать старту записи.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    /// <summary>Как часто перепроверять условие, если ПК не выключают неделями.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    /// <summary>Нашли новый драйвер: версия для уведомления.</summary>
    public event Action<string>? UpdateFound;

    private int _running;

    /// <summary>Запустить фоновый цикл проверок на весь сеанс работы приложения.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(StartupDelay);
            while (true)
            {
                await CheckIfDueAsync();
                await Task.Delay(PollInterval);
            }
        });
    }

    /// <summary>Проверить, если со прошлого раза прошла неделя. Иначе — ничего не делать.</summary>
    public async Task CheckIfDueAsync()
    {
        var s = settings.Current;
        if (!s.CheckNvidiaDriver) return;
        if (DateTime.UtcNow - s.LastDriverCheckUtc < Interval) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            var hardware = await HardwareInfoService.CollectAsync();
            var gpu = hardware.Gpus.FirstOrDefault(g =>
                g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            if (gpu is null) return; // не NVIDIA — проверять нечего, и дату не двигаем

            var status = await nvidia.CheckAsync(gpu);
            if (status is null)
            {
                // Сеть/эндпоинт подвели — попробуем в следующий цикл, дату не трогаем
                Log.Info("DriverWatch", "Проверка драйвера не удалась, повторим позже");
                return;
            }

            s.LastDriverCheckUtc = DateTime.UtcNow;

            if (!status.UpdateAvailable)
            {
                Log.Info("DriverWatch", $"Драйвер актуален ({status.InstalledVersion})");
                settings.Save("system");
                return;
            }

            bool alreadyTold = string.Equals(s.NotifiedDriverVersion, status.LatestVersion,
                                             StringComparison.OrdinalIgnoreCase);
            s.NotifiedDriverVersion = status.LatestVersion;
            settings.Save("system");

            Log.Info("DriverWatch", $"Драйвер {status.LatestVersion} доступен " +
                                    $"(установлен {status.InstalledVersion})" +
                                    (alreadyTold ? ", о нём уже сообщали" : ""));
            if (!alreadyTold) UpdateFound?.Invoke(status.LatestVersion);
        }
        catch (Exception ex)
        {
            Log.Warn("DriverWatch", ex.Message);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
