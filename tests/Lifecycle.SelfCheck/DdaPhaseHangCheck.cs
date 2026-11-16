using System.Reflection;
using Aura.Core.Capture;

/// <summary>
/// Имитирует невозврат нативного вызова DDA: поток захвата замораживается в фазе
/// Acquire диагностическим стендом. Проверяет, что снимок показывает именно эту
/// фазу, что незавершившаяся остановка честно сообщает о себе и помечает выход
/// занятым. Это имитация незавершившегося потока, а не доказательство
/// драйверного зависания.
/// </summary>
internal static class DdaPhaseHangCheck
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    internal static void Check()
    {
        var assembly = typeof(Aura.Core.Engine.ReplayEngine).Assembly;
        var ddaType = assembly.GetType("Aura.Core.Capture.DesktopDuplicationSource")!;
        var heldRegistry = assembly.GetType("Aura.Core.Capture.HeldCaptureOutputs")!;
        string identity = MonitorLayout.IdentityFor(0);
        var hangField = ddaType.GetField("s_testHangPhase", AnyStatic)!;
        hangField.SetValue(null, "Acquire");
        try
        {
            using var capture = (IDisposable)Activator.CreateInstance(ddaType, nonPublic: true)!;
            ddaType.GetMethod("Prepare")!.Invoke(capture, [0, 30, false, 1L]);
            ddaType.GetMethod("Start")!.Invoke(capture, null);

            // Ждём публикации фазы Acquire: поток сразу замирает на нативном вызове.
            object? snapshot = null;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                object?[] diagnosticsArgs = [null];
                if ((bool)ddaType.GetMethod("TryGetDiagnostics")!.Invoke(capture, diagnosticsArgs)!)
                {
                    snapshot = diagnosticsArgs[0];
                    string phase = snapshot!.GetType().GetProperty("Phase")!.GetValue(snapshot)!.ToString()!;
                    if (phase == "Acquire") break;
                }
                Thread.Sleep(50);
            }

            if (snapshot is null)
                throw new Exception("DDA не опубликовал диагностический снимок");
            string hungPhase = snapshot.GetType().GetProperty("Phase")!.GetValue(snapshot)!.ToString()!;
            if (hungPhase != "Acquire")
                throw new Exception($"Снимок показывает фазу {hungPhase}, а не Acquire");

            // Публикация фазы и последний возврат цикла могли разойтись на доли
            // секунды: даём потоку время доказать, что прогресса больше нет.
            Thread.Sleep(1500);
            object?[] laterArgs = [null];
            if (!(bool)ddaType.GetMethod("TryGetDiagnostics")!.Invoke(capture, laterArgs)!)
                throw new Exception("Диагностический снимок пропал после зависания");
            object later = laterArgs[0]!;
            if (later.GetType().GetProperty("Phase")!.GetValue(later)!.ToString() != "Acquire")
                throw new Exception("Фаза зависания изменилась сама по себе");
            long lastProgress = (long)later.GetType().GetProperty("LastLoopProgressQpc")!.GetValue(later)!;
            long nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            double stalledSeconds = (nowQpc - lastProgress) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (stalledSeconds < 1)
                throw new Exception($"Прогресс цикла не остановился: {stalledSeconds:F2} с");

            // Остановка обязана вернуться по таймауту ожидания потока (5 с) и
            // честно сказать, что поток не вышел.
            var stop = Task.Run(() => ddaType.GetMethod("Stop")!.Invoke(capture, null));
            if (!stop.Wait(TimeSpan.FromSeconds(12)))
                throw new Exception("Stop завис дольше своего ограниченного ожидания потока");
            if ((bool)ddaType.GetProperty("StopCompleted")!.GetValue(capture)!)
                throw new Exception("Зависший DDA отчитался о завершённой остановке");
            if (!(bool)heldRegistry.GetMethod("IsHeld", AnyStatic, [typeof(string)])!.Invoke(null, [identity])!)
                throw new Exception("Незавершившаяся остановка не пометила выход занятым");

            Console.WriteLine($"PASS: фаза Acquire видна сторожу ({stalledSeconds:F1} с без прогресса), " +
                              "остановка не завершилась и выход помечен занятым");
        }
        finally
        {
            hangField.SetValue(null, null);
            heldRegistry.GetMethod("ResetForTests", AnyStatic)!.Invoke(null, null);
        }
    }
}
