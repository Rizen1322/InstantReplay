using System.Reflection;
using Aura.Core.Capture;

/// <summary>
/// Проверяет требование «выход, удерживаемый незавершившейся сессией DDA, не
/// открывается заново нативным DuplicateOutput». Работает без реального зависания
/// драйвера: реестр выходов помечается напрямую, а Prepare нового источника обязан
/// отказать ДО создания D3D-устройства и дупликации.
/// </summary>
internal static class DdaHeldOutputCheck
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    internal static void Check()
    {
        var assembly = typeof(Aura.Core.Engine.ReplayEngine).Assembly;
        var heldRegistry = assembly.GetType("Aura.Core.Capture.HeldCaptureOutputs")!;
        var heldException = assembly.GetType("Aura.Core.Capture.CaptureOutputHeldException")!;
        var ddaType = assembly.GetType("Aura.Core.Capture.DesktopDuplicationSource")!;

        string identity = MonitorLayout.IdentityFor(0);
        try
        {
            bool marked = (bool)heldRegistry
                .GetMethod("TryHold", AnyStatic)!
                .Invoke(null, [identity, 1L, "selfcheck: незавершившаяся остановка", "фаза Acquire"])!;
            if (!marked) throw new Exception("Удерживаемый выход не удалось пометить");
            if (!(bool)heldRegistry.GetMethod("IsHeld", AnyStatic, [typeof(string)])!
                    .Invoke(null, [identity])!)
                throw new Exception("Пометка удерживаемого выхода не видна реестру");

            using var capture = (IDisposable)Activator.CreateInstance(ddaType, nonPublic: true)!;
            Exception? failure = null;
            try
            {
                ddaType.GetMethod("Prepare")!.Invoke(capture, [0, 30, false, 2L]);
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException;
            }

            if (failure is null || failure.GetType() != heldException)
                throw new Exception("Prepare не отказал для удерживаемого выхода: " +
                                    (failure is null ? "исключения нет" : failure.GetType().Name));
            if (ddaType.GetField("_device", PrivateInstance)!.GetValue(capture) is not null)
                throw new Exception("Prepare создал D3D-устройство для удерживаемого выхода");
            if (ddaType.GetField("_duplication", PrivateInstance)!.GetValue(capture) is not null)
                throw new Exception("Prepare открыл дупликацию для удерживаемого выхода");

            Console.WriteLine("PASS: удерживаемый выход отклоняет новый Desktop Duplication ДО нативных вызовов; " +
                              "повторные попытки не создают устройство");
        }
        finally
        {
            heldRegistry.GetMethod("ResetForTests", AnyStatic)!.Invoke(null, null);
        }
    }
}
