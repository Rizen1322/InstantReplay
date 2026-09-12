# Aura Capture Engine v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Aura's mutable backend-owned capture frame path with a generation-safe GPU frame broker and validated cursor plane so capture recovers automatically without frozen video, cursor corruption, or implicit file creation.

**Architecture:** WGC and DDA remain the Windows pixel providers, but publish short-lived `CapturedSurface` values into an Aura-owned three-slot broker. DDA cursor metadata is validated and kept as an immutable per-generation snapshot; the GPU compositor always starts from a clean desktop texture. `ReplayEngine` owns provider generation, broker lifetime, encoding leases, screenshots, and recovery while the existing adaptive policy retains backend selection and anti-flapping.

**Tech Stack:** .NET 10, C# 14, WPF, Vortice.Direct3D11/DXGI 3.8.3, Windows.Graphics.Capture, xUnit 2.9.3, PowerShell packaging scripts.

**Spec:** `docs/superpowers/specs/2026-09-12-aura-capture-engine-v2-design.md`

## Global Constraints

- Target `net10.0-windows10.0.22621.0`, minimum Windows platform `10.0.19041.0`, x64.
- Windows 11 build 22000+ starts WGC; older Windows starts DDA.
- Keep WGC and DDA as the only shipping providers; no driver, virtual display, or game injection.
- Keep frames in GPU memory. Only DDA's small pointer payload may enter CPU memory.
- Broker capacity is exactly three output slots and retains the newest unleased frame.
- Invalid cursor data is skipped and rate-limited; it is never interpreted as color pixels.
- Frames, failures, leases, and cursor snapshots are generation-bound.
- Keep `INSTANTREPLAY_CAPTURE=wgc|dda` diagnostic-only; add no UI selector.
- Recovery must never call `SaveReplay`, reserve a replay path, remux a replay, or write the replay ring.
- Same-format restart retains compatible replay/audio rings in RAM; incompatible video is discarded only from RAM.
- Explicit continuous recording may resume in a new segment after the new encoder is ready.
- `DuplicateOutput1` requests BGRA8 only. HDR and tone mapping are outside this plan.

---

### Task 1: Validated generation-bound DDA cursor state

**Files:**
- Create: `src/Aura/Core/Capture/DdaCursorState.cs`
- Modify: `src/Aura/Core/Capture/CursorShapePixels.cs`
- Create: `tests/InstantReplay.Tests/DdaCursorStateTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Consumes: `CursorShapePixels.ExpandMonochrome` and `ExpandMaskedColor`.
- Produces: `DdaCursorShape.TryCreate(int type, int width, int reportedHeight, int pitch, ReadOnlySpan<byte> payload, out DdaCursorShape? shape, out string reason)`.
- Produces: `CaptureCursorMode`, `CaptureCursorUpdate`, `DdaCursorSnapshot`, `DdaCursorState.Reset(long generation)`, and `Apply(long generation, in CaptureCursorUpdate update)`.

- [ ] **Step 1: Link the new pure file into the test project**

```xml
<Compile Include="..\..\src\Aura\Core\Capture\DdaCursorState.cs" Link="App\DdaCursorState.cs" />
```

- [ ] **Step 2: Write failing validation and generation tests**

Create `DdaCursorStateTests.cs` with these cases:

```csharp
[Fact]
public void RejectsUnknownShapeType()
{
    Assert.False(DdaCursorShape.TryCreate(99, 32, 32, 128, new byte[4096],
        out var shape, out string reason));
    Assert.Null(shape);
    Assert.Contains("тип", reason, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void RejectsTruncatedColorPayload()
{
    Assert.False(DdaCursorShape.TryCreate(2, 32, 32, 128, new byte[4095],
        out _, out string reason));
    Assert.Contains("буфер", reason, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ResetPreventsShapeCrossingProviderGeneration()
{
    Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4, [1, 2, 3, 255],
        out var shape, out _));
    var state = new DdaCursorState();
    state.Reset(7);
    Assert.True(state.Apply(7, new CaptureCursorUpdate(
        CaptureCursorMode.Separate, true, true, 40, 50, shape)));
    state.Reset(8);
    Assert.False(state.Current.Visible);
    Assert.Null(state.Current.Shape);
    Assert.False(state.Apply(7, new CaptureCursorUpdate(
        CaptureCursorMode.Separate, true, true, 1, 2, shape)));
}

[Theory]
[InlineData(0, 32)]
[InlineData(-1, 32)]
[InlineData(1025, 32)]
[InlineData(32, 0)]
[InlineData(32, 1025)]
public void RejectsImpossibleDimensions(int width, int height) =>
    Assert.False(DdaCursorShape.TryCreate(2, width, height, 4096,
        new byte[4096], out _, out _));

[Fact]
public void RejectsPitchAndPayloadOverflow()
{
    Assert.False(DdaCursorShape.TryCreate(2, 32, 32, 127,
        new byte[4096], out _, out _));
    Assert.False(DdaCursorShape.TryCreate(2, 32, 32, int.MaxValue,
        Array.Empty<byte>(), out _, out _));
}

[Fact]
public void AcceptsTwoMaskMonochromeShape()
{
    Assert.True(DdaCursorShape.TryCreate(1, 2, 2, 1,
        [0b1000_0000, 0b0100_0000], out var shape, out _));
    Assert.Equal(1, shape!.Height);
    Assert.Equal(8, shape.Pixels.Length);
}

[Fact]
public void PositionOnlyUpdateKeepsValidShape()
{
    Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4,
        [1, 2, 3, 255], out var shape, out _));
    var state = new DdaCursorState();
    state.Reset(4);
    state.Apply(4, new CaptureCursorUpdate(
        CaptureCursorMode.Separate, true, true, 10, 20, shape));
    state.Apply(4, new CaptureCursorUpdate(
        CaptureCursorMode.Separate, true, true, 30, 40, null));
    Assert.Same(shape, state.Current.Shape);
    Assert.Equal((30, 40), (state.Current.X, state.Current.Y));
}
```

- [ ] **Step 3: Verify the tests fail because the types do not exist**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter DdaCursorStateTests --no-restore
```

Expected: compilation FAIL naming `DdaCursorShape` and `DdaCursorState`.

- [ ] **Step 4: Implement exact cursor validation**

Create these types:

```csharp
internal enum DdaCursorShapeKind { Monochrome = 1, Color = 2, MaskedColor = 4 }

internal sealed record DdaCursorShape(
    DdaCursorShapeKind Kind, int Width, int Height, byte[] Pixels)
{
    public const int MaximumDimension = 1024;
    public static bool TryCreate(
        int type, int width, int reportedHeight, int pitch, ReadOnlySpan<byte> payload,
        out DdaCursorShape? shape, out string reason);
}

internal enum CaptureCursorMode { SystemComposed, Separate }

internal readonly record struct CaptureCursorUpdate(
    CaptureCursorMode Mode, bool HasPosition, bool Visible,
    int X, int Y, DdaCursorShape? Shape, bool ResetState = false)
{
    public static CaptureCursorUpdate SystemComposed =>
        new(CaptureCursorMode.SystemComposed, false, false, 0, 0, null);
}

internal readonly record struct DdaCursorSnapshot(
    long Generation, long Revision, bool Visible, int X, int Y, DdaCursorShape? Shape);
```

`TryCreate` accepts only types 1/2/4. For monochrome, require even `reportedHeight`, expose half as visible height, and require `pitch >= (width + 7) / 8`; for color/masked require `pitch >= width * 4`. Use checked arithmetic for `pitch * reportedHeight`, reject short payloads, width/visible-height outside 1..1024, and convert only the required payload. Move `CopyColor` from `CursorOverlay` to `CursorShapePixels.CopyColor`. Normalize masked alpha to 0 or 255.

Add a test that first applies a valid shape and then applies `CaptureCursorUpdate(CaptureCursorMode.Separate, false, false, 0, 0, null, ResetState: true)` in the same generation; visibility and shape must be cleared. `DdaCursorState.Reset` clears visibility, position, and shape and increments revision. `Apply` rejects a different generation or `SystemComposed` update without mutation; otherwise it performs `ResetState` first, updates position only when `HasPosition`, replaces shape only when non-null, and increments revision once for the complete accepted update.

- [ ] **Step 5: Run focused and complete tests**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "DdaCursorStateTests|CaptureRegressionTests" --no-restore
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
```

Expected: PASS, including existing hotspot and masked-color regressions.

- [ ] **Step 6: Commit**

```powershell
git add src/Aura/Core/Capture/DdaCursorState.cs src/Aura/Core/Capture/CursorShapePixels.cs tests/InstantReplay.Tests/DdaCursorStateTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Validate DDA cursor state by capture generation"
```

---

### Task 2: Bounded three-slot frame broker

**Files:**
- Create: `src/Aura/Core/Capture/CaptureFrameBroker.cs`
- Create: `tests/InstantReplay.Tests/CaptureFrameBrokerTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Consumes: `Func<TSlot>` slot factory and `Action<TSlot>` disposer.
- Produces: `Publish(long generation, long timestamp, Action<TSlot> write)`, `TryLeaseLatest(long generation, out CaptureFrameLease<TSlot>? lease)`, and `Reset(long generation)`.
- Produces: `FramesPublished`, `FramesDroppedNoSlot`, `LatestTimestamp`, and `Generation`.

- [ ] **Step 1: Link the broker into the test project**

```xml
<Compile Include="..\..\src\Aura\Core\Capture\CaptureFrameBroker.cs" Link="App\CaptureFrameBroker.cs" />
```

- [ ] **Step 2: Write failing ownership tests**

Use a disposable `FakeSlot` with integer `Value`. Cover newest-frame leasing, exact capacity three, replacement of the oldest unleased ready slot, rejection of stale generation, reset clearing ready frames, safe old-lease return after reset, write-exception rollback, and dropped counter when all slots are leased. Core examples:

```csharp
[Fact]
public void PublishesAndLeasesNewestFrame()
{
    using var broker = CreateBroker();
    broker.Reset(3);
    Assert.True(broker.Publish(3, 100, s => s.Value = 10));
    Assert.True(broker.Publish(3, 200, s => s.Value = 20));
    Assert.True(broker.TryLeaseLatest(3, out var lease));
    using (lease!)
    {
        Assert.Equal(20, lease.Slot.Value);
        Assert.Equal(200, lease.Timestamp);
    }
}

[Fact]
public void NeverOverwritesLeasedSlot()
{
    using var broker = CreateBroker();
    broker.Reset(1);
    broker.Publish(1, 1, s => s.Value = 1);
    broker.TryLeaseLatest(1, out var held);
    using (held!)
    {
        broker.Publish(1, 2, s => s.Value = 2);
        broker.Publish(1, 3, s => s.Value = 3);
        broker.Publish(1, 4, s => s.Value = 4);
        Assert.Equal(1, held.Slot.Value);
    }
}
```

- [ ] **Step 3: Verify the broker tests fail**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureFrameBrokerTests --no-restore
```

Expected: compilation FAIL naming the missing broker types.

- [ ] **Step 4: Implement the broker state machine**

```csharp
internal sealed class CaptureFrameBroker<TSlot> : IDisposable where TSlot : class
{
    public CaptureFrameBroker(Func<TSlot> createSlot, Action<TSlot> disposeSlot);
    public long FramesPublished { get; }
    public long FramesDroppedNoSlot { get; }
    public long LatestTimestamp { get; }
    public long Generation { get; }
    public void Reset(long generation);
    public bool Publish(long generation, long timestamp, Action<TSlot> write);
    public bool TryLeaseLatest(long generation, out CaptureFrameLease<TSlot>? lease);
    public void Dispose();
}
```

Use private states `Free`, `Writing`, `Ready`, and `Reading`, one lock, and exactly three slots. `Publish` chooses `Free`, otherwise the oldest `Ready`, never `Writing/Reading`; it marks `Writing` under lock, calls `write` outside the lock, and publishes only if generation and write token still match. Exceptions restore that token's slot to `Free`. Leasing chooses the newest `Ready`. Lease return is idempotent and checks slot index, generation, and lease token. `Reset` clears `Ready`, leaves `Reading` unavailable until returned, and never resets lifetime counters.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureFrameBrokerTests --no-restore
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
git add src/Aura/Core/Capture/CaptureFrameBroker.cs tests/InstantReplay.Tests/CaptureFrameBrokerTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Add bounded capture frame broker"
```

Expected: all tests PASS and all fake slots are disposed once.

---

### Task 3: GPU broker and clean cursor compositor

**Files:**
- Create: `src/Aura/Core/Capture/CapturedSurface.cs`
- Create: `src/Aura/Core/Capture/GpuCaptureFrameBroker.cs`
- Modify: `src/Aura/Core/Capture/CursorOverlay.cs`
- Modify: `tests/InstantReplay.Tests/CaptureRegressionTests.cs`

**Interfaces:**
- Consumes: `CaptureFrameBroker<GpuCaptureFrameSlot>`, `DdaCursorState`, and validated shapes.
- Produces: `CapturedSurface(ID3D11Texture2D Texture, long Timestamp, long Generation, CaptureCursorUpdate Cursor)`.
- Produces: `GpuCaptureFrameBroker.Publish`, `TryLeaseLatest`, `TryUseLatest`, and `CursorOverlay.Compose`.

- [ ] **Step 1: Add failing pure compositor regressions**

```csharp
[Fact]
public void ColorShapeSkipsPitchPadding()
{
    byte[] source = [1, 2, 3, 4, 9, 9, 9, 9, 5, 6, 7, 8, 9, 9, 9, 9];
    Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
        CursorShapePixels.CopyColor(source, width: 1, height: 2, pitch: 8));
}

[Theory]
[InlineData(0, 10, 20, 10)]
[InlineData(255, 10, 20, 30)]
public void MaskedColorUsesReplaceOrXor(byte mask, byte shape, byte background, byte expected) =>
    Assert.Equal(expected, CursorShapePixels.ComposeMaskedChannel(mask, shape, background));
```

Run:

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "ColorShapeSkipsPitchPadding|MaskedColorUsesReplaceOrXor" --no-restore
```

Expected: FAIL until `CopyColor` is exposed and `ComposeMaskedChannel` exists. Implement the helper as `mask == 0 ? shape : (byte)(shape ^ background)`.

- [ ] **Step 2: Define the scoped surface and unified cursor update**

Create `CapturedSurface.cs`:

```csharp
using Vortice.Direct3D11;

namespace Aura.Core.Capture;

internal readonly record struct CapturedSurface(
    ID3D11Texture2D Texture, long Timestamp, long Generation,
    CaptureCursorUpdate Cursor);
```

Reuse `CaptureCursorUpdate` from Task 1; do not introduce a second provider-specific update contract.

- [ ] **Step 3: Refactor cursor rendering to consume validated snapshots**

Remove `IDXGIOutputDuplication`, raw-shape parsing, and mutable position fields from `CursorOverlay`. Implement:

```csharp
public void Compose(
    ID3D11Texture2D clean, ID3D11Texture2D output,
    in DdaCursorSnapshot cursor)
{
    _context.CopyResource(output, clean);
    if (!_ready || !cursor.Visible || cursor.Shape is null) return;
    EnsureShapeTexture(cursor.Revision, cursor.Shape);
    DrawValidated(output, cursor.X, cursor.Y, cursor.Shape);
}
```

Upload `shape.Pixels` only when revision changes. Keep the existing color alpha, monochrome AND/XOR, and masked-color shader operations. Use `(cursor.X, cursor.Y)` directly without subtracting hotspot. Always remove render-target/shader bindings before returning.

- [ ] **Step 4: Implement the D3D wrapper**

`GpuCaptureFrameBroker` wraps the generic broker. A slot owns `Output` and, only for DDA, `Clean`. Use BGRA8 texture dimensions from the prepared provider and:

```csharp
Usage = ResourceUsage.Default,
CPUAccessFlags = CpuAccessFlags.None,
MiscFlags = ResourceOptionFlags.None,
BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
```

For WGC, copy source directly to `Output`. For DDA, apply `CaptureCursorUpdate` to the generation-bound state, copy source to `Clean`, then compose `Clean` to `Output`. Expose:

```csharp
public bool Publish(in CapturedSurface surface);
public bool TryLeaseLatest(long generation, out GpuCaptureFrameLease? lease);
public bool TryUseLatest(long generation, Action<ID3D11Texture2D> use);
```

`TryUseLatest` must dispose its ordinary broker lease in `finally`. `GpuCaptureFrameLease` exposes only `Texture`, `Timestamp`, `Generation`, and idempotent `Dispose`.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
dotnet build src/Aura/Aura.csproj -c Release --no-restore
git add src/Aura/Core/Capture/CapturedSurface.cs src/Aura/Core/Capture/GpuCaptureFrameBroker.cs src/Aura/Core/Capture/CursorOverlay.cs src/Aura/Core/Capture/CursorShapePixels.cs src/Aura/Core/Capture/DdaCursorState.cs tests/InstantReplay.Tests/CaptureRegressionTests.cs
git commit -m "Separate clean frames from DDA cursor composition"
```

Expected: tests PASS and Aura builds with zero errors.

---

### Task 4: Prepare/start providers and generation-tagged events

**Files:**
- Modify: `src/Aura/Core/Capture/IScreenCapture.cs`
- Modify: `src/Aura/Core/Capture/CaptureFailure.cs`
- Modify: `src/Aura/Core/Capture/DesktopDuplicationSource.cs`
- Modify: `src/Aura/Core/Capture/ScreenCaptureSource.cs`
- Modify: `tests/InstantReplay.Tests/CaptureRegressionTests.cs`

**Interfaces:**
- Consumes: `CapturedSurface`, `CaptureCursorUpdate`, and `DdaCursorShape.TryCreate`.
- Produces: `Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)` then parameterless `Start()`.
- Produces: `event Action<CapturedSurface>? FrameArrived` and generation-bearing `CaptureFailure`.
- Removes: `IScreenCapture.TryUseLatestFrame`.

- [ ] **Step 1: Add a failing failure-generation regression**

```csharp
[Fact]
public void CaptureFailureRetainsProviderGeneration()
{
    var error = new InvalidOperationException("lost");
    var failure = new CaptureFailure(
        CaptureFailureKind.BackendUnavailable, error, "DDA unavailable", 12);
    Assert.Equal(12, failure.Generation);
    Assert.Same(error, failure.Error);
}
```

- [ ] **Step 2: Change the provider contract first**

```csharp
internal interface IScreenCapture : IDisposable
{
    ID3D11Device D3DDevice { get; }
    ID3D11DeviceContext D3DContext { get; }
    int Width { get; }
    int Height { get; }
    long FramesReceived { get; }
    long FramesAccepted { get; }
    event Action<CapturedSurface>? FrameArrived;
    event Action<CaptureFailure>? Failed;
    void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation);
    void Start();
    void Stop();
}
```

Add `long Generation` as the fourth positional member of `CaptureFailure`. Build once and retain the compiler's old-call-site list for Task 5:

```powershell
dotnet build src/Aura/Aura.csproj -c Release --no-restore
```

Expected: FAIL only at old `Start(...)`, old two-argument frame handlers, old failure constructors, and `TryUseLatestFrame`.

- [ ] **Step 3: Convert DDA**

`Prepare` performs stop/reset, monitor/FPS/generation setup, device/output/duplication creation, but does not start a thread. `Start` requires prepared duplication and starts one run-token thread. Remove `_frameCopy`, `_frameLock`, `_cursor`, and provider screenshot code.

While the DXGI frame is held, call `GetFramePointerShape` only when size is positive, validate all metadata with `DdaCursorShape.TryCreate`, and create a separate cursor update. Log one warning per distinct validation reason per generation. A rejected shape becomes null. Set `_resetCursorOnNextFrame = true` in `Prepare` and after every successful internal `RecreateDuplication`; atomically consume it into `CaptureCursorUpdate.ResetState`. This prevents a cached pointer shape from crossing a DDA duplication session even when the outer engine generation is unchanged.

Emit before `ReleaseFrame`:

```csharp
var cursor = ReadCursorUpdate(dup, frameInfo);
bool cursorChanged = cursor.HasPosition || cursor.Shape is not null;
if (!DesktopFramePolicy.ShouldCapture(
        frameInfo.AccumulatedFrames, _firstFrameSinceStart, cursorChanged))
    continue;
using var texture = resource.QueryInterface<ID3D11Texture2D>();
FrameArrived?.Invoke(new CapturedSurface(texture, ticks, _generation, cursor));
```

Preserve exactly one `ReleaseFrame` for every successful acquire even when the handler throws.

- [ ] **Step 4: Convert WGC**

`Prepare` creates the capture item, frame pool, borderless access, session, and stores generation, but does not call `StartCapture`. `Start` calls it exactly once. Remove WGC's `_frameCopy`, request event, wait handle, and provider screenshot code. Emit each scoped WinRT texture as:

```csharp
FrameArrived?.Invoke(new CapturedSurface(
    texture, ticks, _generation, CaptureCursorUpdate.SystemComposed));
```

Every provider failure passes `_generation`.

- [ ] **Step 5: Verify provider compilation and commit boundary**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
dotnet build src/Aura/Aura.csproj -c Release --no-restore
```

Pure tests must PASS. Application errors may remain only in `ReplayEngine` and `ScreenshotService`. Do not commit this deliberately broken interface boundary; continue directly through Task 5 and commit the provider and engine migration together after the application builds.

---

### Task 5: ReplayEngine ownership, screenshots, and no-save recovery

**Files:**
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Modify: `src/Aura/Core/Capture/ScreenshotService.cs`
- Create: `src/Aura/Core/Engine/CaptureRecoveryBackoff.cs`
- Modify: `tests/InstantReplay.Tests/PipelineRestartPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/ReplayBufferContinuityTests.cs`
- Create: `tests/InstantReplay.Tests/CaptureRecoveryBackoffTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Consumes: prepared providers, `GpuCaptureFrameBroker`, generation-tagged frames/failures, `CaptureHealthPolicy`, and `PipelineRestartPolicy`.
- Produces: one broker and one generation-bound frame delegate per pipeline generation.
- Produces: live screenshots exclusively through broker leases.

- [ ] **Step 1: Strengthen the no-disk-write test matrix**

```csharp
[Theory]
[InlineData(PipelineStopIntent.UserStop, false, false)]
[InlineData(PipelineStopIntent.UserStop, true, true)]
[InlineData(PipelineStopIntent.CaptureRestart, false, false)]
[InlineData(PipelineStopIntent.CaptureRestart, false, true)]
[InlineData(PipelineStopIntent.CaptureRestart, true, false)]
[InlineData(PipelineStopIntent.CaptureRestart, true, true)]
public void LifecyclePolicyNeverWritesReplayAutomatically(
    PipelineStopIntent intent, bool recording, bool compatible) =>
    Assert.False(PipelineRestartPolicy.For(intent, recording, compatible).SaveReplay);
```

Retain tests proving only explicit continuous recording resumes and incompatible video is cleared in RAM.

Link `CaptureRecoveryBackoff.cs` into the test project and create `CaptureRecoveryBackoffTests.cs`:

```csharp
[Theory]
[InlineData(1, false, 250)]
[InlineData(1, true, 1500)]
[InlineData(2, false, 3000)]
[InlineData(3, false, 5000)]
[InlineData(4, false, 10000)]
[InlineData(5, false, 15000)]
[InlineData(50, false, 15000)]
public void RecoveryBackoffIsBounded(
    int attempt, bool deviceLost, int expected) =>
    Assert.Equal(expected, CaptureRecoveryBackoff.DelayMilliseconds(attempt, deviceLost));
```

Implement `CaptureRecoveryBackoff.DelayMilliseconds` with that exact schedule and a 15-second cap.

- [ ] **Step 2: Add broker/delegate fields and move screenshot reuse**

```csharp
private GpuCaptureFrameBroker? _frameBroker;
private Action<CapturedSurface>? _captureFrameHandler;
private Action<CaptureFailure>? _captureFailureHandler;
```

Remove the old duplicate failure field. `TryUseCaptureFrame` acquires `_frameGate`, reads `_captureGeneration` once, and calls `_frameBroker.TryUseLatest`; it no longer calls a provider screenshot method.

- [ ] **Step 3: Reorder startup to prevent losing the first static frame**

Inside `StartLocked`:

```csharp
long generation = Interlocked.Increment(ref _captureGeneration);
_capture = ScreenCaptureFactory.Create(_captureBackend, s.MonitorIndex);
_capture.Prepare(s.MonitorIndex, s.Fps, s.RecordCursor, generation);

_processor = new VideoProcessorNv12(_capture.D3DDevice, _capture.D3DContext);
_processor.Configure(_capture.Width, _capture.Height, s.VerticalResolution, s.Fps);

_frameBroker = new GpuCaptureFrameBroker(
    _capture.D3DDevice, _capture.D3DContext,
    _capture.Width, _capture.Height,
    separateCursor: _captureBackend == CaptureBackend.DesktopDuplication,
    generation);
```

Keep the existing `PrepareForCaptureRestart` compatibility check, sequence-header comparison on the first new keyframe, and `_videoBuffer.Add` subscription unchanged. Then subscribe and start in this exact order:

```csharp
_captureFrameHandler = frame => OnCapturedSurface(frame, generation);
_captureFailureHandler = failure => OnCaptureFailed(failure, generation);
_capture.FrameArrived += _captureFrameHandler;
_capture.Failed += _captureFailureHandler;
_pipelineOpen = true;
_capture.Start();
```

Any failure in prepare, broker/encoder creation, subscription, or start must enter existing transactional cleanup.

- [ ] **Step 4: Publish and lease before encoding**

Replace `OnFrame` with generation checks, frame gate, publication, and a lease:

```csharp
private void OnCapturedSurface(in CapturedSurface surface, long observedGeneration)
{
    if (surface.Generation != observedGeneration ||
        observedGeneration != Interlocked.Read(ref _captureGeneration)) return;
    if (!_frameGate.TryEnterReadLock(0)) return;
    try
    {
        if (!_pipelineOpen || _frameBroker is null) return;
        if (!_frameBroker.Publish(surface)) return;
        if (!_frameBroker.TryLeaseLatest(observedGeneration, out var lease)) return;
        using (lease!)
        {
            long t0 = Diagnostics.PipelineProbe.Now();
            var nv12 = _processor!.Convert(lease.Texture);
            long t1 = Diagnostics.PipelineProbe.Now();
            _encoder!.SubmitFrame(nv12, lease.Timestamp, _capture!.D3DContext);
            Diagnostics.PipelineProbe.Convert.Add(t0, t1);
            Diagnostics.PipelineProbe.Submit.Add(t1, Diagnostics.PipelineProbe.Now());
        }
    }
    catch (Exception ex)
    {
        if (DeviceLoss.IsDeviceLost(ex))
            OnCaptureFailed(new CaptureFailure(
                CaptureFailureKind.DeviceLost, ex, $"ошибка кадра: {ex.Message}",
                observedGeneration), observedGeneration);
        else Log.Error("Engine", $"Кадр пропущен: {ex.Message}");
    }
    finally { _frameGate.ExitReadLock(); }
}
```

- [ ] **Step 5: Update one-shot screenshots**

In `ScreenshotService`, prepare, subscribe, then start:

```csharp
using var source = ScreenCaptureFactory.Create(
    ScreenCaptureFactory.Selection.Backend, monitorIndex);
const long generation = 1;
source.Prepare(monitorIndex, 0, cursor, generation);
source.FrameArrived += frame =>
{
    if (tcs.Task.IsCompleted) return;
    try { tcs.TrySetResult(ReadPixels(source.D3DDevice, source.D3DContext, frame.Texture)); }
    catch (Exception ex) { tcs.TrySetException(ex); }
};
source.Start();
```

This subscription order is required for a static DDA desktop.

- [ ] **Step 6: Enforce teardown order**

In `StopLocked`: increment generation and close pipeline; unsubscribe stored delegates; stop provider; drain frame-gate readers; finish only an explicit recorder; dispose encoder, processor, broker, then provider/device; apply `PipelineRestartPolicy`. Null the delegates/broker. Do not call `SaveReplayLocked`.

Replace the fixed ten-attempt recovery loop with an unbounded cancellation-aware loop. Create a `CancellationTokenSource` for the active recovery, wait with `token.WaitHandle.WaitOne(CaptureRecoveryBackoff.DelayMilliseconds(attempt, deviceLost))`, alternate/quarantine providers through the existing health policy, and retry until capture starts or the user stops Aura. `StopLocked(UserStop)` cancels the token before teardown; cancellation must neither restart capture nor create a recording segment.

- [ ] **Step 7: Verify tests, build, and static no-save invariant**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "CaptureFrameBroker|DdaCursor|CaptureBackend|CaptureHealth|PipelineRestart|ReplayBufferContinuity|DuplicationRecovery" --no-restore
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
dotnet build src/Aura/Aura.csproj -c Release --no-restore
rg -n "SaveReplayLocked|ReserveFilePath|ReplaySaver\.Save" src/Aura/Core/Engine/ReplayEngine.cs
```

Expected: tests PASS, build has zero new warnings/errors, and save/remux/path reservation appears only in explicit save/recording methods—not recovery, failure, or stop paths.

- [ ] **Step 8: Commit the complete provider/engine boundary**

```powershell
git add src/Aura/Core/Engine/ReplayEngine.cs src/Aura/Core/Engine/CaptureRecoveryBackoff.cs src/Aura/Core/Capture/ScreenshotService.cs src/Aura/Core/Capture/IScreenCapture.cs src/Aura/Core/Capture/CaptureFailure.cs src/Aura/Core/Capture/DesktopDuplicationSource.cs src/Aura/Core/Capture/ScreenCaptureSource.cs tests/InstantReplay.Tests/PipelineRestartPolicyTests.cs tests/InstantReplay.Tests/ReplayBufferContinuityTests.cs tests/InstantReplay.Tests/CaptureRecoveryBackoffTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Integrate Aura-owned capture frame pipeline"
```

---

### Task 6: Safe `DuplicateOutput1` capability path

**Files:**
- Create: `src/Aura/Core/Capture/DdaDuplicationPolicy.cs`
- Modify: `src/Aura/Core/Capture/DesktopDuplicationSource.cs`
- Create: `tests/InstantReplay.Tests/DdaDuplicationPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Consumes: target `IDXGIOutput`, same-adapter D3D device, and `IDXGIOutput5` when available.
- Produces: `DdaDuplicationPolicy.ShouldFallBackToLegacy(int hresult)` and a BGRA8 `DuplicateOutput1` attempt.

- [ ] **Step 1: Write fallback tests and link the policy**

```csharp
[Theory]
[InlineData(unchecked((int)0x80004002))] // E_NOINTERFACE
[InlineData(unchecked((int)0x80004001))] // E_NOTIMPL
[InlineData(unchecked((int)0x887A0004))] // DXGI_ERROR_UNSUPPORTED
public void CapabilityFailuresMayFallBack(int hresult) =>
    Assert.True(DdaDuplicationPolicy.ShouldFallBackToLegacy(hresult));

[Theory]
[InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED
[InlineData(unchecked((int)0x887A0022))] // NOT_CURRENTLY_AVAILABLE
[InlineData(unchecked((int)0x887A0028))] // SESSION_DISCONNECTED
public void AccessFailuresMustReachRecovery(int hresult) =>
    Assert.False(DdaDuplicationPolicy.ShouldFallBackToLegacy(hresult));
```

Add its `<Compile Include>` beside the other pure capture policies and verify the tests initially fail.

- [ ] **Step 2: Implement the policy**

```csharp
internal static class DdaDuplicationPolicy
{
    public static bool ShouldFallBackToLegacy(int hresult) => hresult is
        unchecked((int)0x80004002) or
        unchecked((int)0x80004001) or
        unchecked((int)0x887A0004);
}
```

- [ ] **Step 3: Implement transactional `DuplicateOutput1` creation**

Query `targetOutput` for `IDXGIOutput5`, then call the Vortice overload `output5.DuplicateOutput1(_device!, [Format.B8G8R8A8_UNorm])`. Log success and `duplication.Description.ModeDescription.Format`, and publish the duplication field only after success. Fall back to `IDXGIOutput1.DuplicateOutput` only for the three policy HRESULTs. Propagate access/session/mode-in-progress failures into existing bounded recovery. Dispose every temporary COM interface exactly once; add no custom COM declaration and no HDR format.

- [ ] **Step 4: Verify and commit**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "DdaDuplicationPolicyTests|CaptureRegressionTests" --no-restore
dotnet build src/Aura/Aura.csproj -c Release --no-restore
git add src/Aura/Core/Capture/DdaDuplicationPolicy.cs src/Aura/Core/Capture/DesktopDuplicationSource.cs tests/InstantReplay.Tests/DdaDuplicationPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Use safe Desktop Duplication capability path"
```

Expected: tests PASS and build has zero errors.

---

### Task 7: Diagnostics, packaging, and acceptance evidence

**Files:**
- Modify: `src/Aura/Core/Diagnostics/PipelineProbe.cs`
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-09-12-aura-capture-engine-v2-design.md` only to correct facts discovered during implementation

**Interfaces:**
- Consumes: broker counters, provider generation/backend, existing capture/encoder/probe metrics, and VRAM budget.
- Produces: diagnostics separating provider starvation, broker pressure, cursor rejection, encoder starvation, and device loss.

- [ ] **Step 1: Add an allocation-free broker diagnostic snapshot**

```csharp
internal readonly record struct CaptureBrokerDiagnostics(
    long Generation,
    long FramesPublished,
    long FramesDroppedNoSlot,
    long LatestTimestamp,
    long CursorRevision,
    long InvalidCursorShapes);
```

Read fields through `Interlocked.Read`; do not build per-frame strings or arrays.

- [ ] **Step 2: Extend the existing probe line**

Log `backend/gen | broker published/drop/age-ms | cursor revision/invalid` beside received/encoded/duplicated/MFT/queue/VRAM values. Compute age only in the QPC 100-ns domain; never subtract from wall-clock time. Rate-limit repeated invalid-shape reasons and the existing per-second warning header.

- [ ] **Step 3: Update README architecture facts**

Document in Russian: WGC on Windows 11, DDA on Windows 10, three GPU slots, clean-frame cursor composition, generation-safe restart, compatible RAM-ring retention, no recovery save, and explicit recording segmentation. Remove statements saying DDA is always selected.

- [ ] **Step 4: Run complete automated and packaging verification**

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
dotnet build InstantReplay.sln -c Release --no-restore
git diff --check
powershell -NoProfile -ExecutionPolicy Bypass -File .\build_setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\release.ps1
```

Expected: all tests PASS, solution builds without new warnings/errors, installer and signed identity package are produced, and no missing `_identity/_package.ps1` error appears.

- [ ] **Step 5: Run hardware acceptance and preserve logs**

1. Windows 10/DDA: 30-minute GPU-heavy game; 10-minute stationary cursor; then arrow, text, resize, and custom color/masked cursors.
2. Windows 10/DDA: ten Alt+Tab/fullscreen transitions; refresh-rate change; monitor sleep/wake; lock/unlock; enter and leave UAC secure desktop.
3. Windows 11/WGC: repeat load and transitions; verify borderless permission.
4. Forced runs: `INSTANTREPLAY_CAPTURE=dda` and `=wgc`; forced backend must not cross-switch.
5. Do not press Save for 15 minutes and verify no replay file appears. Press Save once and verify exactly one playable replay.
6. Explicitly start continuous recording, trigger recovery, stop it, and verify playable segments exist only for that explicit session.

Acceptance requires no persistent freeze, colored square, stale/offset cursor, crash, backend ping-pong, unbounded VRAM growth, or implicit video file. Secure-desktop denial is acceptable only if capture resumes after returning.

- [ ] **Step 6: Commit diagnostics/docs and record final evidence**

```powershell
git add src/Aura/Core/Diagnostics/PipelineProbe.cs src/Aura/Core/Engine/ReplayEngine.cs README.md docs/superpowers/specs/2026-09-12-aura-capture-engine-v2-design.md
git commit -m "Document and diagnose Aura capture engine v2"
git status --short --branch
git log --oneline --decorate -10
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore
dotnet build InstantReplay.sln -c Release --no-restore
```

Report exact test count, warning/error count, installer paths, active branch, commit range, and hardware cases actually performed. Never report unperformed Windows 10/11 hardware acceptance as passed.
