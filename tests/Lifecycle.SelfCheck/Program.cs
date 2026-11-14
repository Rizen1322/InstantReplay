using System.Reflection;
using System.Runtime.CompilerServices;
using Aura.Core.Encoding;
using Aura.Core.Engine;

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
var engineType = typeof(ReplayEngine);
var encoderType = typeof(VideoEncoder);
var engine = (ReplayEngine)RuntimeHelpers.GetUninitializedObject(engineType);
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
