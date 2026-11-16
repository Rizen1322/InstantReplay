using System.Reflection;
using Aura.Core.Engine;

internal static class DdaLifetimeCheck
{
    internal static void CheckLifetime()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(ReplayEngine).Assembly.GetType("Aura.Core.Capture.DesktopDuplicationSource")!;
        using var capture = (IDisposable)Activator.CreateInstance(type)!;
        type.GetMethod("Prepare")!.Invoke(capture, [0, 30, false, 1L]);
        string[] fields = ["_device", "_context", "_output", "_duplication"];
        var resources = fields.Select(name => type.GetField(name, Private)!.GetValue(capture)).ToArray();
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() => release.Wait()) { IsBackground = true };
        thread.Start();
        type.GetField("_thread", Private)!.SetValue(capture, thread);
        try
        {
            type.GetMethod("Stop")!.Invoke(capture, null);
            if ((bool)type.GetProperty("StopCompleted")!.GetValue(capture)!)
                throw new Exception("Unstopped DDA reported a completed stop");

            // Незавершившаяся остановка обязана пометить выход занятым: без этого
            // следующая попытка снова дёрнула бы нативный DuplicateOutput и
            // получила E_INVALIDARG — ровно та петля, от которой защищает этап.
            var heldRegistry = type.Assembly.GetType("Aura.Core.Capture.HeldCaptureOutputs")!;
            var identity = (string)type.GetField("_monitorIdentity", Private)!.GetValue(capture)!;
            if (!(bool)heldRegistry.GetMethod("IsHeld", BindingFlags.Public | BindingFlags.Static |
                                                        BindingFlags.NonPublic, [typeof(string)])!
                    .Invoke(null, [identity])!)
                throw new Exception("Hung DDA stop did not mark its output as held");

            type.GetMethod("Stop")!.Invoke(capture, null);
            capture.Dispose();
            for (int i = 0; i < fields.Length; i++)
                if (!ReferenceEquals(resources[i], type.GetField(fields[i], Private)!.GetValue(capture)))
                    throw new Exception("Hung DDA lost ownership of " + fields[i]);
            try
            {
                type.GetMethod("Prepare")!.Invoke(capture, [0, 30, false, 2L]);
                throw new Exception("Hung DDA allowed Prepare to overwrite its resources");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            Console.WriteLine("PASS: hung DDA retains all COM resources across repeated Stop/Dispose, " +
                              "rejects reuse and marks its output as held");
        }
        finally
        {
            type.GetField("_threadStuck", Private)!.SetValue(capture, false);
            var heldRegistry = type.Assembly.GetType("Aura.Core.Capture.HeldCaptureOutputs")!;
            heldRegistry.GetMethod("ResetForTests", BindingFlags.Public | BindingFlags.Static |
                                                    BindingFlags.NonPublic)!.Invoke(null, null);
            release.Set();
            thread.Join();
        }
    }

}
