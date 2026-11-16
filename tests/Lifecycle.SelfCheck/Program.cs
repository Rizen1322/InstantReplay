using System.Reflection;
using System.Runtime.CompilerServices;
using Aura.Core.Encoding;
using Aura.Core.Engine;

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
var engineType = typeof(ReplayEngine);
if (args.Contains("--dda-lifetime"))
{
    DdaLifetimeCheck.CheckLifetime();
    return;
}
if (args.Contains("--dda-held"))
{
    DdaHeldOutputCheck.Check();
    return;
}
if (args.Contains("--dda-phase-hang"))
{
    DdaPhaseHangCheck.Check();
    return;
}
var encoderType = typeof(VideoEncoder);
var engine = (ReplayEngine)RuntimeHelpers.GetUninitializedObject(engineType);

// Терминальная блокировка: захват не идёт, RAM-повтор сохранён и доступен.
var stateField = engineType.GetField("_state", Private)!;
stateField.SetValue(engine, EngineState.CaptureBlocked);
Require(engine.CaptureBlocked && !engine.ReplayActive && engine.ReplayBufferAvailable,
    "CaptureBlocked must keep the replay buffer available but report no active capture");
stateField.SetValue(engine, EngineState.Running);
Require(!engine.CaptureBlocked && engine.ReplayActive && engine.ReplayBufferAvailable,
    "Running must be active and saveable");
stateField.SetValue(engine, EngineState.Stopped);
Require(!engine.ReplayActive && !engine.ReplayBufferAvailable,
    "Stopped must not offer a replay buffer");
stateField.SetValue(engine, EngineState.Stopped);
var blockedPolicy = Enum.Parse(engineType.Assembly.GetType("Aura.Core.Engine.PipelineStopIntent")!, "CaptureBlocked");
var policyType = engineType.Assembly.GetType("Aura.Core.Engine.PipelineRestartPolicy")!;
var actions = policyType.GetMethod("For")!.Invoke(null, [blockedPolicy, false, false]);
var actionsType = actions!.GetType();
Require((bool)actionsType.GetProperty("KeepReplayBuffer")!.GetValue(actions)! &&
        (bool)actionsType.GetProperty("KeepAudioBuffer")!.GetValue(actions)! &&
        !(bool)actionsType.GetProperty("SaveReplay")!.GetValue(actions)!,
    "CaptureBlocked must keep RAM buffers without writing video to disk");
Console.WriteLine("PASS: terminal capture block keeps the RAM replay saveable, never active, never auto-saved");

using var encoder = new VideoEncoder();
encoderType.GetProperty("Fps")!.SetValue(encoder, 60);
var pipelineType = engineType.Assembly.GetType("Aura.Core.Engine.PipelineSession")!;
var pipeline = Activator.CreateInstance(pipelineType, [1L])!;
pipelineType.GetField("Encoder")!.SetValue(pipeline, encoder);
engineType.GetField("_pipeline", Private)!.SetValue(engine, pipeline);
engineType.GetField("_captureGeneration", Private)!.SetValue(engine, 1L);
using var gate = new ReaderWriterLockSlim();
var gateField = engineType.GetField("_frameGate", Private)!;
gateField.SetValue(engine, gate);
engineType.GetMethod("StartFrameWorker", Private)!.Invoke(engine, [1L]);
var prepareProperty = encoderType.GetProperty("PreparePullFrame", Private)!;
var savedPrepare = (Action)prepareProperty.GetValue(encoder)!;
Require(prepareProperty.GetValue(encoder) is Action, "Pull callback not wired");
Require(engineType.GetField("_frameWorker", Private)!.GetValue(engine) is null, "Independent pull worker still exists");
Require(engineType.GetField("_frameReady", Private)!.GetValue(engine) is null, "Pull still depends on ready/reset signals");
engineType.GetField("_captureGeneration", Private)!.SetValue(engine, 2L);
gateField.SetValue(engine, null);
savedPrepare(); // A stale generation must return before touching the frame gate.
gateField.SetValue(engine, gate);

using var entered = new ManualResetEventSlim(false);
using var release = new ManualResetEventSlim(false);
object cfrLock = encoderType.GetField("_cfrLock", Private)!.GetValue(encoder)!;
int calls = 0;
prepareProperty.SetValue(encoder, (Action)(() =>
{
    Require(!Monitor.IsEntered(cfrLock), "Prepare holds encoder's CFR lock");
    gate.EnterReadLock();
    try { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); }
    finally { gate.ExitReadLock(); }
}));
var tick = encoderType.GetMethod("PullTick", Private)!;
var inFlight = Task.Run(() => tick.Invoke(encoder, null));
Require(entered.Wait(3000), "Output tick did not invoke prepare");
Require(!inFlight.Wait(100), "Output selected a candidate before preparation finished");
Require((bool)engineType.GetMethod("StopFrameWorker", Private)!.Invoke(engine, null)!, "Stopping pull callback failed");
Require(prepareProperty.GetValue(encoder) is null, "Stop did not detach callback");
// The actual engine drain must refuse destruction while a conversion holds its read lease.
var drain = Task.Run(() => (bool)engineType.GetMethod("DrainFramesInFlight", Private)!.Invoke(engine, null)!);
Require(await drain == false, "Hung conversion allowed resource destruction");
release.Set();
await inFlight;
Require((bool)engineType.GetMethod("DrainFramesInFlight", Private)!.Invoke(engine, null)!, "Released conversion did not drain");
tick.Invoke(encoder, null);
Require(calls == 1, "Stopped output callback started another conversion");
Console.WriteLine("PASS: no second worker/ready timer; stale generation rejected; prepare outside CFR lock; hung conversion blocks drain; stop detaches callback");


var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
using var activity = new Aura.Core.SystemIntegration.SystemActivityWatcher(
    _ => events.Enqueue("suspend"), _ => events.Enqueue("resume"), _ => { });
var activityType = activity.GetType();
var backlog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
activityType.GetField("_queue", Private)!.SetValue(activity, backlog.Task);
activityType.GetMethod("DelayedResume", Private)!.Invoke(activity, ["test", TimeSpan.Zero]);
activityType.GetMethod("OnPowerModeChanged", Private)!.Invoke(activity,
    [null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)]);
var activityDrained = (Task)activityType.GetField("_queue", Private)!.GetValue(activity)!;
backlog.SetResult();
await activityDrained.WaitAsync(TimeSpan.FromSeconds(3));
Require(events.SequenceEqual(new[] { "suspend" }), "Stale resume executed after a new sleep epoch: " + string.Join(", ", events));
Console.WriteLine("PASS: queued resume is invalidated by a newer sleep before queue execution");

var audioLine = new Aura.Core.Audio.AudioTimeline(0, 2);
void RejectMissingDevice(bool loopback)
{
    try
    {
        using var source = new Aura.Core.Audio.WasapiSource(loopback, "Aura-missing-device-selfcheck", audioLine);
        throw new Exception("An explicitly missing device silently fell back to a real endpoint");
    }
    catch (System.Runtime.InteropServices.COMException) { }
    catch (ArgumentException) { }
}
RejectMissingDevice(false);
RejectMissingDevice(true);
GC.Collect();
GC.WaitForPendingFinalizers();
using var self = System.Diagnostics.Process.GetCurrentProcess();
int handlesBefore = self.HandleCount;
for (int i = 0; i < 64; i++)
{
    RejectMissingDevice(false);
    RejectMissingDevice(true);
}
GC.Collect();
GC.WaitForPendingFinalizers();
int leakedHandles = self.HandleCount - handlesBefore;
Require(leakedHandles <= 4, "Failed WASAPI constructors leaked handles: " + leakedHandles);
Console.WriteLine($"PASS: 128 missing-device WASAPI failures, no fallback, handle delta {leakedHandles}");

Exception? dispatchError = null;
var dispatchThread = new Thread(() =>
{
    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    try
    {
        var ui = new Aura.Core.Notifications.UiDispatcher(dispatcher);
        var frame = new System.Windows.Threading.DispatcherFrame();
        bool invoked = false;
        ui.Enqueue(() => { invoked = true; frame.Continue = false; });
        Require(!invoked, "UI callback ran inline while the caller might hold a lifecycle lock");
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Require(invoked, "UI callback was lost");
    }
    catch (Exception ex) { dispatchError = ex; }
    finally { dispatcher.InvokeShutdown(); }
});
dispatchThread.SetApartmentState(ApartmentState.STA);
dispatchThread.Start();
Require(dispatchThread.Join(3000), "UI dispatch did not complete");
Require(dispatchError is null, "UI dispatch failed: " + dispatchError);
Console.WriteLine("PASS: same-thread UI events queue asynchronously and are delivered");

var appType = typeof(Aura.App);
var devFlag = appType.GetField("_devInstance", BindingFlags.Static | BindingFlags.NonPublic)!;
devFlag.SetValue(null, true);
appType.GetMethod("StartShowWindowListener", Private)!.Invoke(RuntimeHelpers.GetUninitializedObject(appType), null);
Require(EventWaitHandle.TryOpenExisting(@"Global\Aura.Quit.dev", out var devQuit), "Dev instance did not register its quit signal");
devQuit!.Dispose();
Require(appType.GetField("_showWindowSignal", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
    "Dev instance registered the production show-window signal");
devFlag.SetValue(null, false);
Console.WriteLine("PASS: dev instance registers its own quit signal without the production show-window listener");

// Никакого захвата экрана: проверяем гонки восстановления на пустом движке.
var recoveryEngine = (ReplayEngine)RuntimeHelpers.GetUninitializedObject(engineType);
engineType.GetField("_recoverySync", Private)!.SetValue(recoveryEngine, new object());
engineType.GetField("_captureGeneration", Private)!.SetValue(recoveryEngine, 7L);
var beginRecovery = engineType.GetMethod("TryBeginCaptureRecovery", Private)!;
var finishRecovery = engineType.GetMethod("FinishCaptureRecovery", Private)!;
var cancelRecovery = engineType.GetMethod("CancelCaptureRecovery", Private)!;
var failureHandler = engineType.GetMethod("OnCaptureFailed", Private)!;
var pendingFailure = engineType.GetField("_pendingCaptureFailure", Private)!;
Require((bool)beginRecovery.Invoke(recoveryEngine, [null])!, "Recovery did not acquire ownership");
var failure = new Aura.Core.Capture.CaptureFailure(Aura.Core.Capture.CaptureFailureKind.CaptureTargetClosed,
    new InvalidOperationException("test source closed"), "test", 7);
failureHandler.Invoke(recoveryEngine, [failure, 7L]);
Require(Equals(pendingFailure.GetValue(recoveryEngine), failure), "Failure during recovery was lost");
failureHandler.Invoke(recoveryEngine, [failure with { Generation = 6 }, 6L]);
Require(Equals(pendingFailure.GetValue(recoveryEngine), failure), "Stale source replaced the pending failure");
engineType.GetField("_stopRequested", Private)!.SetValue(recoveryEngine, true);
finishRecovery.Invoke(recoveryEngine, [null]);
Require(pendingFailure.GetValue(recoveryEngine) is null &&
    (int)engineType.GetField("_recovering", Private)!.GetValue(recoveryEngine)! == 0,
    "User stop did not discard the queued recovery");
var cancellationField = engineType.GetField("_recoveryCancellation", Private)!;
for (int i = 0; i < 500; i++)
{
    var cancellation = new CancellationTokenSource();
    cancellationField.SetValue(recoveryEngine, cancellation);
    using var startRace = new ManualResetEventSlim(false);
    Task cancel = Task.Run(() => { startRace.Wait(); cancelRecovery.Invoke(recoveryEngine, null); });
    Task dispose = Task.Run(() => { startRace.Wait(); finishRecovery.Invoke(recoveryEngine, [cancellation]); });
    startRace.Set();
    await Task.WhenAll(cancel, dispose);
    Require(cancellationField.GetValue(recoveryEngine) is null, "Completed recovery retained a disposed CTS");
    try { _ = cancellation.Token; throw new Exception("Recovery CTS leaked"); }
    catch (ObjectDisposedException) { }
}
Console.WriteLine("PASS: recovery retains new failures, rejects stale sources, respects user stop; 500 concurrent cancel/dispose races");

var wgcType = engineType.Assembly.GetType("Aura.Core.Capture.WgcCaptureSession")!;
object wgc = RuntimeHelpers.GetUninitializedObject(wgcType);
wgcType.GetField("_prepared", Private)!.SetValue(wgc, true);
wgcType.GetField("_generation", Private)!.SetValue(wgc, 9L);
wgcType.GetField("_sourceName", Private)!.SetValue(wgc, "WGC monitor");
int closures = 0;
wgcType.GetField("Failed", Private)!.SetValue(wgc, (Action<Aura.Core.Capture.CaptureFailure>)(closed =>
{
    Require(closed.Generation == 9 && closed.Kind == Aura.Core.Capture.CaptureFailureKind.CaptureTargetClosed,
        "Monitor close lost source identity");
    closures++;
}));
var onClosed = wgcType.GetMethod("OnItemClosed", Private)!;
onClosed.Invoke(wgc, [null, null]);
onClosed.Invoke(wgc, [null, null]);
Require(closures == 1, "Monitor close did not report exactly one failure");
Console.WriteLine("PASS: closed WGC monitor reports recovery once without starting desktop capture");

// HRESULT ошибки выходного MFT не должны выглядеть как законное ожидание входа.
var acceptOutput = encoderType.GetMethod("AcceptMftOutputResult", Private)!;
foreach (var error in new[] { Vortice.MediaFoundation.ResultCode.TransformStreamChange,
    new SharpGen.Runtime.Result(unchecked((int)0x80004005)) })
{
    using var checkedEncoder = new VideoEncoder();
    encoderType.GetField("_running", Private)!.SetValue(checkedEncoder, true);
    encoderType.GetField("_inFlight", Private)!.SetValue(checkedEncoder, 7);
    int errors = 0;
    var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    checkedEncoder.Failed += message => { Interlocked.Increment(ref errors); reported.TrySetResult(message); };
    Require((bool)acceptOutput.Invoke(checkedEncoder, [SharpGen.Runtime.Result.Ok])!, "Successful MFT output was rejected");
    Require(!(bool)acceptOutput.Invoke(checkedEncoder, [Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput])!,
        "Need-more-input was treated as output");
    Require((bool)encoderType.GetField("_running", Private)!.GetValue(checkedEncoder)! && errors == 0,
        "Need-more-input killed a healthy encoder");
    acceptOutput.Invoke(checkedEncoder, [error]);
    acceptOutput.Invoke(checkedEncoder, [error]);
    string message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Require(errors == 1 && !(bool)encoderType.GetField("_running", Private)!.GetValue(checkedEncoder)!,
        "MFT failure was silent or restarted recovery twice");
    Require((int)encoderType.GetField("_inFlight", Private)!.GetValue(checkedEncoder)! == 7,
        "MFT failure released an input frame that has not been output");
    Require(!string.IsNullOrWhiteSpace(message), "MFT failure omitted its reason");
}
Console.WriteLine("PASS: MFT need-more-input preserves the session; stream-change and real errors request recovery exactly once");

Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
    Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,
    [Vortice.Direct3D.FeatureLevel.Level_11_0],
    out Vortice.Direct3D11.ID3D11Device rotationDevice, out _, out Vortice.Direct3D11.ID3D11DeviceContext rotationContext).CheckError();
using (rotationDevice)
using (rotationContext)
{
    using var inputTexture = rotationDevice.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
    {
        Width = 4, Height = 2, MipLevels = 1, ArraySize = 1,
        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
        Usage = Vortice.Direct3D11.ResourceUsage.Default
    });
    byte[] inputPixels = Enumerable.Range(1, 8).SelectMany(i => new byte[] { (byte)i, 0, 0, 255 }).ToArray();
    IntPtr upload = System.Runtime.InteropServices.Marshal.AllocHGlobal(inputPixels.Length);
    try
    {
        System.Runtime.InteropServices.Marshal.Copy(inputPixels, 0, upload, inputPixels.Length);
        rotationContext.UpdateSubresource(inputTexture, 0, null, upload, 16, 0);
    }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(upload); }
    var rotatorType = engineType.Assembly.GetType("Aura.Core.Capture.DdaFrameRotator")!;
    foreach (var (rotation, widthOut, expected) in new[]
    {
        (Vortice.DXGI.ModeRotation.Identity, 4, new byte[] {1,2,3,4,5,6,7,8}),
        (Vortice.DXGI.ModeRotation.Rotate90, 2, new byte[] {5,1,6,2,7,3,8,4}),
        (Vortice.DXGI.ModeRotation.Rotate180, 4, new byte[] {8,7,6,5,4,3,2,1}),
        (Vortice.DXGI.ModeRotation.Rotate270, 2, new byte[] {4,8,3,7,2,6,1,5})
    })
    {
        using var rotator = (IDisposable)Activator.CreateInstance(rotatorType, [rotationDevice, rotationContext, 4, 2, rotation])!;
        var outputTexture = (Vortice.Direct3D11.ID3D11Texture2D)rotatorType.GetMethod("Rotate")!.Invoke(rotator, [inputTexture])!;
        var (pixels, pixelWidth, pixelHeight) = Aura.Core.Capture.ScreenshotService.ReadPixels(rotationDevice, rotationContext, outputTexture);
        Require(pixelWidth == widthOut && pixelWidth * pixelHeight == 8, "DDA rotation produced the wrong desktop dimensions");
        Require(expected.SequenceEqual(Enumerable.Range(0, 8).Select(i => pixels[i * 4])),
            "DDA GPU rotation mirrored or misplaced pixels: " + rotation);
    }
}
Console.WriteLine("PASS: actual GPU pixels at identity,90,180,270 degrees; desktop capture never started");

var monitorList = Aura.Core.Capture.MonitorEnumerator.Enumerate();
foreach (var monitor in monitorList.Where(m => m.Width > 0))
{
    string identity = Aura.Core.Capture.MonitorLayout.IdentityFor(monitor.Index);
    Require(!string.IsNullOrEmpty(identity) && monitor.DeviceId == identity, "Native monitor identity is inconsistent");
    Require(Aura.Core.Capture.MonitorLayout.Resolve(999, identity) == monitor.Index, "Identity resolution used the stale index");
}
try
{
    Aura.Core.Capture.MonitorLayout.Resolve(0, "nonexistent-monitor-self-check");
    throw new Exception("Missing monitor silently selected another screen");
}
catch (InvalidOperationException) { }
Console.WriteLine("PASS: native monitor identities resolve independently of index; disconnected monitor refuses fallback");

var ddaType = engineType.Assembly.GetType("Aura.Core.Capture.DesktopDuplicationSource")!;
using (var stoppedDda = (IDisposable)Activator.CreateInstance(ddaType, nonPublic: true)!)
{
    var stopBounded = engineType.GetMethod("StopCaptureBounded", BindingFlags.Static | BindingFlags.NonPublic)!;
    // Необязательный контекст диагностики передаётся явно: reflection не
    // подставляет значения по умолчанию для пропущенных аргументов.
    Require((bool)stopBounded.Invoke(null, [stoppedDda, null])!, "Idle DDA failed to stop");
    ddaType.GetField("_threadStuck", Private)!.SetValue(stoppedDda, true);
    Require(!(bool)stopBounded.Invoke(null, [stoppedDda, null])!, "Stuck DDA reported a safe pipeline teardown");
}
Console.WriteLine("PASS: stop distinguishes completed teardown from a timed-out capture thread");

using (var releasePacer = new ManualResetEventSlim())
{
    var blockedEncoder = new VideoEncoder();
    var blockedPacer = new Thread(releasePacer.Wait) { IsBackground = true };
    blockedPacer.Start();
    encoderType.GetField("_pacerThread", Private)!.SetValue(blockedEncoder, blockedPacer);
    var heldPipeline = Activator.CreateInstance(pipelineType, [9876L])!;
    pipelineType.GetField("Encoder")!.SetValue(heldPipeline, blockedEncoder);
    try
    {
        pipelineType.GetMethod("DisposeOrdered")!.Invoke(heldPipeline, null);
        Require((bool)encoderType.GetProperty("ResourcesAbandoned", Private)!.GetValue(blockedEncoder)!,
            "Hung pacer did not prohibit device destruction");
        Require(ReferenceEquals(pipelineType.GetField("Encoder")!.GetValue(heldPipeline), blockedEncoder),
            "Pipeline disposed resources still used by a hung pacer");
    }
    finally
    {
        releasePacer.Set();
        blockedPacer.Join();
        blockedEncoder.Dispose();
    }
}
Console.WriteLine("PASS: hung encoder pacer prevents destruction of the rest of its pipeline");

using (var releaseOldEncoder = new ManualResetEventSlim())
{
    var recoveringEngine = (ReplayEngine)RuntimeHelpers.GetUninitializedObject(engineType);
    var oldPipeline = Activator.CreateInstance(pipelineType, [9877L])!;
    var oldEncoder = new VideoEncoder();
    var oldEventThread = new Thread(releaseOldEncoder.Wait) { IsBackground = true };
    oldEventThread.Start();
    encoderType.GetField("_eventThread", Private)!.SetValue(oldEncoder, oldEventThread);
    pipelineType.GetField("Encoder")!.SetValue(oldPipeline, oldEncoder);
    void SetRecoveryField(string name, object value) => engineType.GetField(name, Private)!.SetValue(recoveringEngine, value);
    SetRecoveryField("_pipeline", oldPipeline);
    SetRecoveryField("_rescueSync", new object());
    SetRecoveryField("_writerTasksSync", new object());
    SetRecoveryField("_writerTasks", new HashSet<Task>());
    SetRecoveryField("_captureTargetSync", new object());
    SetRecoveryField("_stateSync", new object());
    using var recoveryFrameGate = new ReaderWriterLockSlim();
    SetRecoveryField("_frameGate", recoveryFrameGate);
    SetRecoveryField("_videoBuffer", new Aura.Core.Buffering.ReplayVideoBuffer());
    SetRecoveryField("_audioBuffer", new Aura.Core.Buffering.ReplayAudioBuffer());
    SetRecoveryField("_continuousRecordingRequested", true);
    string recorderRoot = System.IO.Path.Combine("artifacts", "tests", "lifecycle-recorder-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(recorderRoot);
    using var recorder = new Aura.Core.Saving.ManualRecorder(System.IO.Path.Combine(recorderRoot, "late-frame.mp4"),
        _ => throw new Exception("Late frame reached a detached recorder"), [], 10);
    typeof(Aura.Core.Saving.ManualRecorder).GetField("_baseTicks", Private)!.SetValue(recorder, 0L);
    SetRecoveryField("_recorder", recorder);
    Action<Aura.Core.Buffering.EncodedFrame> recorderHandler = recorder.OnFrame;
    SetRecoveryField("_recorderFrameHandler", recorderHandler);
    oldEncoder.FrameEncoded += recorderHandler;
    try
    {
        var restartIntent = Enum.Parse(engineType.Assembly.GetType("Aura.Core.Engine.PipelineStopIntent")!, "CaptureRestart");
        engineType.GetMethod("StopLocked", Private)!.Invoke(recoveringEngine, [restartIntent]);
        Require((bool)encoderType.GetProperty("ResourcesAbandoned", Private)!.GetValue(oldEncoder)!,
            "StopLocked did not exercise the abandoned encoder path");
        Require(ReferenceEquals(pipelineType.GetField("Encoder")!.GetValue(oldPipeline), oldEncoder),
            "StopLocked lost its abandoned encoder before detaching the recorder");
        var lateHandlers = (Action<Aura.Core.Buffering.EncodedFrame>?)encoderType.GetField("FrameEncoded", Private)!.GetValue(oldEncoder);
        Require(lateHandlers?.GetInvocationList().Any(h => ReferenceEquals(h.Target, recorder)) != true,
            "Abandoned encoder still subscribes the continuing recorder");
        long beforeLateFrame = recorder.LastVideoEndTicks;
        lateHandlers?.Invoke(new Aura.Core.Buffering.EncodedFrame([1], 0, 1, 100, 10, false));
        Require(recorder.LastVideoEndTicks == beforeLateFrame, "Old encoder appended a late frame after capture recovery");
    }
    finally
    {
        recorder.Discard();
        releaseOldEncoder.Set();
        oldEventThread.Join();
        oldEncoder.Dispose();
    }
}
Console.WriteLine("PASS: real StopLocked detaches a continuing recorder from an abandoned encoder before recovery");

var runCommand = typeof(Aura.App).GetMethod("RunEngineCommandAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
using (var commandEntered = new ManualResetEventSlim())
using (var releaseCommand = new ManualResetEventSlim())
{
    int duplicateCommands = 0;
    var pendingCommand = (Task)runCommand.Invoke(null, [(Action)(() => { commandEntered.Set(); releaseCommand.Wait(); }), false])!;
    Require(commandEntered.Wait(3000) && !pendingCommand.IsCompleted, "Engine command blocked its caller");
    await (Task)runCommand.Invoke(null, [(Action)(() => duplicateCommands++), false])!;
    releaseCommand.Set();
    await pendingCommand;
    Require(duplicateCommands == 0, "Repeated clicks started overlapping lifecycle commands");
}
Console.WriteLine("PASS: lifecycle commands return without blocking UI and reject overlapping clicks");

using (var startEntered = new ManualResetEventSlim())
using (var releaseStart = new ManualResetEventSlim())
{
    bool recording = false;
    var start = (Task)runCommand.Invoke(null, [(Action)(() =>
    {
        startEntered.Set();
        releaseStart.Wait();
        recording = true;
    }), false])!;
    try
    {
        Require(startEntered.Wait(3000), "Start command did not run");
        var stop = (Task)runCommand.Invoke(null, [(Action)(() => recording = false), true])!;
        Require(!stop.IsCompleted, "Stop was discarded while start was pending");
        releaseStart.Set();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(3));
        Require(!recording, "Stop requested during startup was lost");
    }
    finally { releaseStart.Set(); await start; }
}
Console.WriteLine("PASS: stop requested during startup waits its turn and stops the resulting recording");

Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
    Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport,
    [Vortice.Direct3D.FeatureLevel.Level_11_0], out var colorDevice, out _, out var colorContext).CheckError();
using (colorDevice)
using (colorContext)
foreach (var format in new[] { Vortice.DXGI.Format.NV12, Vortice.DXGI.Format.P010 })
{
    const int size = 16;
    using var rgbTexture = colorDevice.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
    {
        Width = size, Height = size, MipLevels = 1, ArraySize = 1,
        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
        BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource
    });
    byte[] rgbPixels = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
    {
        int at = (y * size + x) * 4;
        rgbPixels[at + 0] = (byte)(x % 4 == 1 ? 255 : 0);
        rgbPixels[at + 1] = (byte)(x % 4 == 2 ? 255 : 0);
        rgbPixels[at + 2] = (byte)(x % 4 == 0 ? 255 : 0);
        rgbPixels[at + 3] = 255;
    }
    IntPtr colorUpload = System.Runtime.InteropServices.Marshal.AllocHGlobal(rgbPixels.Length);
    try
    {
        System.Runtime.InteropServices.Marshal.Copy(rgbPixels, 0, colorUpload, rgbPixels.Length);
        colorContext.UpdateSubresource(rgbTexture, 0, null, colorUpload, size * 4, 0);
    }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(colorUpload); }
    var planarDesc = new Vortice.Direct3D11.Texture2DDescription
    {
        Width = size, Height = size, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new(1, 0), BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget
    };
    using var planarTexture = colorDevice.CreateTexture2D(planarDesc);
    var shaderType = typeof(Aura.Core.Capture.VideoProcessorNv12).GetNestedType("ShaderConverter", BindingFlags.NonPublic)!;
    using var shader = (IDisposable)Activator.CreateInstance(shaderType,
        [colorDevice, colorContext, new Vortice.Direct3D11.ID3D11Texture2D?[] { planarTexture }, format, size, size])!;
    shaderType.GetMethod("Convert")!.Invoke(shader, [rgbTexture, 0]);
    planarDesc.BindFlags = Vortice.Direct3D11.BindFlags.None;
    planarDesc.Usage = Vortice.Direct3D11.ResourceUsage.Staging;
    planarDesc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read;
    using var staging = colorDevice.CreateTexture2D(planarDesc);
    colorContext.CopyResource(staging, planarTexture);
    var mappedColor = colorContext.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
    try
    {
        bool ten = format == Vortice.DXGI.Format.P010;
        int bytes = ten ? 2 : 1;
        for (int x = 0; x < size / 2; x++)
        {
            double[] rgb = new double[3];
            foreach (var (sx, weight) in new[] { (Math.Max(0, x * 2 - 1), .25), (x * 2, .5), (x * 2 + 1, .25) })
                for (int c = 0; c < 3; c++) rgb[c] += rgbPixels[sx * 4 + 2 - c] / 255.0 * weight;
            double cb = -.114572 * rgb[0] - .385428 * rgb[1] + .5 * rgb[2];
            double cr = .5 * rgb[0] - .454153 * rgb[1] - .045847 * rgb[2];
            int offset = checked((int)mappedColor.RowPitch * size + x * 2 * bytes);
            foreach (var (channel, value) in new[] { (0, cb), (1, cr) })
            {
                int actual = ten
                    ? (ushort)System.Runtime.InteropServices.Marshal.ReadInt16(mappedColor.DataPointer, offset + channel * bytes) >> 6
                    : System.Runtime.InteropServices.Marshal.ReadByte(mappedColor.DataPointer, offset + channel);
                double expectedColor = (128 + value * 224) * (ten ? 4 : 1);
                Require(Math.Abs(actual - expectedColor) <= 1.1, $"{format}: chroma sample shifted at x={x}: {actual} vs {expectedColor}");
            }
        }
    }
    finally { colorContext.Unmap(staging, 0); }
}
Console.WriteLine("PASS: NV12 and P010 GPU chroma matches left-sited 4:2:0, including clamped screen edge");
