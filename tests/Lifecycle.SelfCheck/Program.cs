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
