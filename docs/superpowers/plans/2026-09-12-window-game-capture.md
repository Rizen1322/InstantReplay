# Window Game Capture Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an automatic WGC game-window provider that keeps Minecraft fullscreen capture fluid, never admits transient desktop frames, preserves cursor correctness, and never writes a clip unless the user explicitly presses Save.

**Architecture:** Keep the existing monitor WGC/DDA providers, add a target-scoped WGC window provider, and route failures through a pure recovery policy. A verified foreground game-window identity and episode revision travel with every frame. DDA lifecycle storms quarantine DDA for that episode, while a frame-admission gate holds the last accepted game frame until the new provider is stable. Window frames are fitted into the monitor-shaped output surface with black bars, and the cursor is sampled separately.

**Tech Stack:** C# 14 / .NET 10, WPF, Windows.Graphics.Capture, CsWinRT COM interop, Direct3D 11, Media Foundation, xUnit, PowerShell packaging.

**Spec:** `docs/superpowers/specs/2026-09-12-window-game-capture-design.md`

## Global Constraints

- Preserve the default backend policy: Windows 11 starts with monitor WGC; Windows 10 starts with DDA unless diagnostics explicitly force a backend.
- The new provider is automatic. Do not add a user-facing backend selector.
- Never save, export, or finalize a clip from recovery code. Only the existing explicit Save action may write a clip.
- Do not inject code into games in this change. If WGC window capture fails the hardware test, record the evidence and design a game hook separately.
- Add pure policy tests before production changes. Run the named focused test after each red/green step and the entire test suite before every task commit.
- Keep provider-owned COM/D3D objects on their capture thread and release every native handle according to the API ownership contract.
- Never publish a monitor frame while a verified fullscreen game-window episode is active and monitor capture is quarantined.

---

## Task 1: Model and verify the foreground game-window target

**Files:**

- Create: `src/Aura/Core/Capture/GameCaptureTarget.cs`
- Create: `src/Aura/Core/Capture/GameWindowSelector.cs`
- Create: `src/Aura/Core/Capture/ForegroundGameWindowProbe.cs`
- Modify: `src/Aura/Core/Interop/NativeMethods.cs`
- Create: `tests/InstantReplay.Tests/GameWindowSelectorTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** foreground HWND/PID, process start identity, client rectangle, monitor rectangle, visibility, minimized/cloaked state, and `GameDetector` classification.

**Produces:** immutable `GameCaptureTarget` with HWND, PID, process start time, monitor index, client size, and monotonically increasing target revision.

- [x] Add failing pure selector tests for a valid Minecraft foreground window, a desktop-classified process, minimized/cloaked windows, an empty client rectangle, an undersized window, and reuse of the same HWND by a different process identity.

```csharp
[Fact]
public void Select_accepts_verified_fullscreen_game_window()
{
    var snapshot = WindowCaptureSnapshot.ValidMinecraft(
        client: new PixelRect(0, 0, 1920, 1080),
        monitor: new PixelRect(0, 0, 1920, 1080));

    GameCaptureTarget? target = GameWindowSelector.Select(snapshot, previous: null);

    Assert.NotNull(target);
    Assert.Equal((nint)42, target.Value.Hwnd);
    Assert.Equal(1, target.Value.Revision);
}

[Fact]
public void Select_rejects_window_below_fullscreen_coverage_threshold()
{
    var snapshot = WindowCaptureSnapshot.ValidMinecraft(
        client: new PixelRect(100, 100, 1280, 720),
        monitor: new PixelRect(0, 0, 1920, 1080));

    Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
}
```

- [x] Link only the pure target/selector files into the test project, then run the focused test and confirm it fails because the types do not exist.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~GameWindowSelectorTests
```

- [x] Implement `WindowCaptureSnapshot`, `GameCaptureTarget`, and `GameWindowSelector.Select`. Require foreground status, root owner, visible state, non-minimized/non-cloaked state, recognized non-Desktop game, non-empty client area, same selected monitor, and at least 90% monitor-area coverage. Preserve the revision only while HWND, PID, and process-start identity all match; increment it for a new identity.

```csharp
internal readonly record struct GameCaptureTarget(
    nint Hwnd,
    int ProcessId,
    long ProcessStartTicks,
    string ExecutableName,
    string GameName,
    int MonitorIndex,
    int ClientWidth,
    int ClientHeight,
    long Revision);

internal static class GameWindowSelector
{
    private const double MinimumMonitorCoverage = 0.90;
    public static GameCaptureTarget? Select(
        in WindowCaptureSnapshot snapshot,
        GameCaptureTarget? previous);
}
```

- [x] Implement `ForegroundGameWindowProbe` as the Win32 adapter. Resolve `GetForegroundWindow`, `GetAncestor(GA_ROOT)`, `GetWindowThreadProcessId`, `GetClientRect`, `ClientToScreen`, `IsWindowVisible`, `IsIconic`, `DwmGetWindowAttribute(DWMWA_CLOAKED)`, process start time, and monitor identity. Revalidate HWND/PID/start time immediately before returning the target.

- [x] Run focused and full tests.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~GameWindowSelectorTests
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Capture/GameCaptureTarget.cs src/Aura/Core/Capture/GameWindowSelector.cs src/Aura/Core/Capture/ForegroundGameWindowProbe.cs src/Aura/Core/Interop/NativeMethods.cs tests/InstantReplay.Tests/GameWindowSelectorTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Verify fullscreen game capture targets"
```

---

## Task 2: Detect DDA lifecycle storms and suppress transition frames

**Files:**

- Create: `src/Aura/Core/Capture/DdaLifecycleMonitor.cs`
- Modify: `src/Aura/Core/Capture/DesktopDuplicationSource.cs`
- Create: `tests/InstantReplay.Tests/DdaLifecycleMonitorTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** timestamps and HRESULT-classified DDA invalidations (`DXGI_ERROR_ACCESS_LOST`, `DXGI_ERROR_INVALID_CALL`, release-frame failure).

**Produces:** `Stable`, `TransitionHold`, or `Storm` state plus a typed `BackendTransitionStorm` failure.

- [x] Add failing tests proving that one invalidation enters a two-second transition hold, three invalidations inside two seconds become a storm, old invalidations expire, and accepted frames resume only after two uninterrupted seconds.

```csharp
[Fact]
public void Three_invalidations_within_two_seconds_form_a_storm()
{
    var monitor = new DdaLifecycleMonitor(
        stormWindow: TimeSpan.FromSeconds(2),
        stableWindow: TimeSpan.FromSeconds(2),
        stormThreshold: 3);

    monitor.RecordInvalidation(TimeSpan.Zero);
    monitor.RecordInvalidation(TimeSpan.FromMilliseconds(700));
    Assert.Equal(DdaLifecycleState.Storm,
        monitor.RecordInvalidation(TimeSpan.FromMilliseconds(1400)));
}
```

- [x] Run the focused test and confirm the missing-type failure.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~DdaLifecycleMonitorTests
```

- [x] Implement the bounded timestamp queue and explicit state transitions. Use a caller-supplied monotonic timestamp so the pure class has no clock dependency.

- [x] Add `BackendTransitionStorm` to `CaptureFailureKind`. In `DesktopDuplicationSource`, record each lifecycle invalidation, do not emit frames during `TransitionHold`, and raise one storm failure per storm entry. Rate-limit repeated recovery warnings while preserving total counters in diagnostics.

- [x] Run focused and full tests.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~DdaLifecycleMonitorTests
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Capture/DdaLifecycleMonitor.cs src/Aura/Core/Capture/DesktopDuplicationSource.cs src/Aura/Core/Capture/CaptureFailure.cs tests/InstantReplay.Tests/DdaLifecycleMonitorTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Detect DDA transition storms"
```

---

## Task 3: Route recovery across three providers without ping-pong

**Files:**

- Create: `src/Aura/Core/Capture/CaptureRecoveryPolicy.cs`
- Modify: `src/Aura/Core/Capture/CaptureBackendPolicy.cs`
- Create: `tests/InstantReplay.Tests/CaptureRecoveryPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/CaptureBackendPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** active backend, failure kind, forced-backend flag, verified target, target revision, and provider quarantine state for the active episode.

**Produces:** `Restart(provider, target?)` or `HoldForGameWindow(target, retryDelay)`.

- [x] Add failing tests for direct monitor-WGC-to-window-WGC recovery, DDA-storm-to-window-WGC recovery, holding after window WGC fails while monitor providers are quarantined, target revision reset, forced diagnostic behavior, and unchanged Windows 10/11 defaults.

```csharp
[Fact]
public void Monitor_wgc_stall_routes_directly_to_verified_window()
{
    var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
        CaptureBackend.Wgc,
        CaptureFailureKind.BackendStalled,
        ForcedBackend: false,
        Target: Targets.Minecraft(revision: 7),
        Episode: CaptureEpisode.Empty));

    Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
    Assert.Equal(CaptureBackend.WgcWindow, decision.Backend);
    Assert.Equal(7, decision.TargetRevision);
}
```

- [x] Run the focused tests and confirm they fail before production changes.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "FullyQualifiedName~CaptureRecoveryPolicyTests|FullyQualifiedName~CaptureBackendPolicyTests"
```

- [x] Add `WgcWindow` to `CaptureBackend` without changing `SelectInitial`. Replace the pairwise `Alternative` assumption in recovery paths with the explicit policy below.

```csharp
internal enum CaptureRecoveryAction { Restart, HoldForGameWindow }

internal readonly record struct CaptureRecoveryDecision(
    CaptureRecoveryAction Action,
    CaptureBackend Backend,
    long TargetRevision,
    TimeSpan RetryDelay,
    CaptureEpisode Episode);
```

- [x] Implement episode-scoped quarantines. A monitor WGC stall with a target and a DDA transition storm with a target route to `WgcWindow`. If window WGC fails while both monitor providers are unhealthy in the same target revision, hold the last frame and retry that same target with bounded backoff. A changed target identity starts a new episode and clears old quarantines.

- [x] Run focused and full tests.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "FullyQualifiedName~CaptureRecoveryPolicyTests|FullyQualifiedName~CaptureBackendPolicyTests"
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Capture/CaptureRecoveryPolicy.cs src/Aura/Core/Capture/CaptureBackendPolicy.cs tests/InstantReplay.Tests/CaptureRecoveryPolicyTests.cs tests/InstantReplay.Tests/CaptureBackendPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Route capture recovery by game episode"
```

---

## Task 4: Add the WGC window provider and target metadata

**Files:**

- Modify: `src/Aura/Core/Interop/CaptureInterop.cs`
- Refactor: `src/Aura/Core/Capture/ScreenCaptureSource.cs`
- Create: `src/Aura/Core/Capture/WgcCaptureSession.cs`
- Create: `src/Aura/Core/Capture/WindowGraphicsCaptureSource.cs`
- Create: `src/Aura/Core/Capture/CaptureSourceRequest.cs`
- Modify: `src/Aura/Core/Capture/IScreenCapture.cs`
- Modify: `src/Aura/Core/Capture/CapturedSurface.cs`
- Modify: `src/Aura/Core/Capture/CaptureFailure.cs`
- Modify: `src/Aura/Core/Capture/ScreenshotService.cs`
- Create: `tests/InstantReplay.Tests/CaptureSourceRequestTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** a revalidated `GameCaptureTarget` and shared WGC device/frame-pool/session mechanics.

**Produces:** WGC frames tagged with provider scope and target revision; typed failure when the capture item closes or changes size.

- [x] Add a factory contract test in `CaptureBackendPolicyTests` or a new pure `CaptureSourceRequestTests` file proving that `WgcWindow` requires a target and monitor providers reject accidental target scope.

```csharp
[Fact]
public void Window_request_requires_a_verified_target()
{
    Assert.Throws<ArgumentException>(() =>
        CaptureSourceRequest.Create(CaptureBackend.WgcWindow, monitorIndex: 0, target: null));
}
```

- [x] Run the focused test and confirm it is red.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~CaptureSourceRequestTests
```

- [x] Expose `CaptureInterop.CreateItemForWindow(nint hwnd)` through the existing `IGraphicsCaptureItemInterop.CreateForWindow`. Before changing pointer release behavior, characterize the existing `CreateForMonitor` wrapper and CsWinRT `GraphicsCaptureItem.FromAbi` ownership contract; apply the same verified ownership rule to both wrappers.

- [x] Extract the common WGC device, frame-pool, resize, border, cursor, close-event, and disposal mechanics from `ScreenCaptureSource` into `WgcCaptureSession`. Keep `ScreenCaptureSource` as monitor selection and create `WindowGraphicsCaptureSource` as target selection; do not duplicate the full WGC session loop.

- [x] Extend surface metadata explicitly.

```csharp
internal enum CaptureSurfaceScope { Monitor, GameWindow }

internal readonly record struct CapturedSurface(
    ID3D11Texture2D Texture,
    long Timestamp,
    long Generation,
    CaptureCursorUpdate Cursor,
    CaptureSurfaceScope Scope,
    long TargetRevision);
```

- [x] Make `ScreenCaptureFactory.Create(CaptureSourceRequest request)` instantiate the correct provider. Keep `ScreenshotService` monitor-only. Subscribe to `GraphicsCaptureItem.Closed` in the window provider and raise a new typed `CaptureTargetClosed` failure for the same generation/target revision.

- [x] Build and run the full suite to catch WinRT signature and lifetime errors.

```powershell
dotnet build src/Aura/Aura.csproj -c Debug
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Interop/CaptureInterop.cs src/Aura/Core/Capture/ScreenCaptureSource.cs src/Aura/Core/Capture/WgcCaptureSession.cs src/Aura/Core/Capture/WindowGraphicsCaptureSource.cs src/Aura/Core/Capture/CaptureSourceRequest.cs src/Aura/Core/Capture/IScreenCapture.cs src/Aura/Core/Capture/CapturedSurface.cs src/Aura/Core/Capture/CaptureFailure.cs src/Aura/Core/Capture/ScreenshotService.cs tests/InstantReplay.Tests/CaptureSourceRequestTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Add target-scoped WGC window capture"
```

---

## Task 5: Preserve output geometry and reject desktop-transition frames

**Files:**

- Create: `src/Aura/Core/Capture/VideoOutputGeometry.cs`
- Create: `src/Aura/Core/Capture/WindowFrameNormalizer.cs`
- Create: `src/Aura/Core/Capture/CaptureFrameAdmissionGate.cs`
- Modify: `src/Aura/Core/Capture/GpuCaptureFrameBroker.cs`
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Create: `tests/InstantReplay.Tests/VideoOutputGeometryTests.cs`
- Create: `tests/InstantReplay.Tests/CaptureFrameAdmissionGateTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** monitor dimensions, source-window dimensions, provider scope, generation, target revision, and active episode.

**Produces:** fixed monitor-aspect output with centered black bars and an admission decision before any GPU copy.

- [x] Add failing geometry tests. For a 1280×1024 source fitted into 1920×1080, assert a 1350×1080 destination at x=285/y=0. Cover exact aspect, pillarbox, letterbox, even dimensions, and zero-size rejection.

```csharp
[Fact]
public void Four_by_threeish_window_is_centered_in_sixteen_by_nine_output()
{
    var result = VideoOutputGeometry.Fit(1280, 1024, 1920, 1080);
    Assert.Equal(new VideoRect(285, 0, 1350, 1080), result);
}
```

- [x] Add failing gate tests for stale generation, stale target revision, monitor scope during a quarantined fullscreen episode, and valid window scope.

```csharp
[Fact]
public void Monitor_frame_is_rejected_during_window_episode()
{
    var gate = new CaptureFrameAdmissionGate(generation: 12, targetRevision: 4, windowEpisode: true);
    Assert.False(gate.Accept(generation: 12, targetRevision: 0, CaptureSurfaceScope.Monitor));
    Assert.True(gate.Accept(generation: 12, targetRevision: 4, CaptureSurfaceScope.GameWindow));
}
```

- [x] Run focused tests and confirm they fail.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "FullyQualifiedName~VideoOutputGeometryTests|FullyQualifiedName~CaptureFrameAdmissionGateTests"
```

- [x] Implement `VideoOutputGeometry.Fit` with integer arithmetic and even dimensions. Implement `WindowFrameNormalizer` with a D3D11 video-processor blit: clear the fixed BGRA destination to opaque black, then scale the window texture into the fitted rectangle without a CPU readback.

- [x] Apply `CaptureFrameAdmissionGate` in `GpuCaptureFrameBroker.Publish` before any GPU copy. Construct broker slots at the selected monitor canvas dimensions. Exact-size monitor frames retain the direct-copy path; game-window frames pass through `WindowFrameNormalizer` into the slot. A rejected frame may increment diagnostics but must not mutate the current GPU frame.

- [x] In `ReplayEngine`, configure `VideoProcessorNv12` and the encoder from the fixed monitor canvas dimensions rather than the current provider dimensions. The encoder therefore repeats the last accepted game canvas during hold and retains one format across provider switches.

- [x] Run focused tests, full tests, and a Release build.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "FullyQualifiedName~VideoOutputGeometryTests|FullyQualifiedName~CaptureFrameAdmissionGateTests"
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
dotnet build src/Aura/Aura.csproj -c Release
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Capture/VideoOutputGeometry.cs src/Aura/Core/Capture/WindowFrameNormalizer.cs src/Aura/Core/Capture/CaptureFrameAdmissionGate.cs src/Aura/Core/Capture/GpuCaptureFrameBroker.cs src/Aura/Core/Engine/ReplayEngine.cs tests/InstantReplay.Tests/VideoOutputGeometryTests.cs tests/InstantReplay.Tests/CaptureFrameAdmissionGateTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Gate capture frames and preserve output geometry"
```

---

## Task 6: Sample and composite a correct cursor for window capture

**Files:**

- Create: `src/Aura/Core/Capture/WindowCursorSampler.cs`
- Modify: `src/Aura/Core/Capture/DdaCursorState.cs`
- Modify: `src/Aura/Core/Capture/WindowGraphicsCaptureSource.cs`
- Modify: `src/Aura/Core/Capture/GpuCaptureFrameBroker.cs`
- Modify: `src/Aura/Core/Interop/NativeMethods.cs`
- Create: `tests/InstantReplay.Tests/WindowCursorPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** global cursor info, target client origin/rectangle, cursor hotspot, icon pixels, and target revision.

**Produces:** `CaptureCursorUpdate` in source-client coordinates, or a hidden update when outside the target; the composed source is then scaled together with the game image.

- [x] Add failing pure tests for screen-to-client mapping, hotspot preservation, outside-target hiding, negative desktop coordinates, stationary cursor reuse, and target-revision reset.

```csharp
[Fact]
public void Maps_screen_cursor_to_window_client_coordinates()
{
    var mapped = WindowCursorPolicy.MapPosition(
        screenX: 620, screenY: 340,
        clientLeft: 100, clientTop: 40,
        hotspotX: 5, hotspotY: 7);
    Assert.Equal((515, 293), (mapped.X, mapped.Y));
}
```

- [x] Run the focused test and confirm the missing-policy failure.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~WindowCursorPolicyTests
```

- [x] Implement the pure mapping policy and `WindowCursorSampler`. Treat `GetCursorInfo` position as the hotspot in screen coordinates, subtract the target client origin and the icon hotspot to obtain the source-image draw origin. Use `CopyIcon`, `GetIconInfo`, and `GetDIBits` for shape pixels. Release copied icons, bitmaps, device contexts, and selected GDI objects in `finally` blocks. Cache shape pixels by cursor handle/shape identity; always refresh position and visibility.

- [x] Disable WGC system cursor capture for the window provider and feed sampled updates to the existing separate-cursor compositor. For window scope, compose into a source-sized scratch texture before `WindowFrameNormalizer` scales the combined game image and cursor into the fixed canvas; this keeps position, hotspot, and cursor size under the same transform. Keep the existing fixed-size DDA composition path. Clear cursor state and scratch textures on target revision or source-size changes so a stale colorful square cannot be reused.

- [x] Run focused/full tests and a Debug build.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~WindowCursorPolicyTests
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
dotnet build src/Aura/Aura.csproj -c Debug
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Capture/WindowCursorSampler.cs src/Aura/Core/Capture/DdaCursorState.cs src/Aura/Core/Capture/WindowGraphicsCaptureSource.cs src/Aura/Core/Capture/GpuCaptureFrameBroker.cs src/Aura/Core/Interop/NativeMethods.cs tests/InstantReplay.Tests/WindowCursorPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Composite target-aware window cursor"
```

---

## Task 7: Integrate automatic recovery, degraded hold, and diagnostics

**Files:**

- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Modify: `src/Aura/Core/Diagnostics/PipelineProbe.cs`
- Modify: `src/Aura/Core/Capture/CaptureHealthPolicy.cs`
- Modify: `src/Aura/Core/Engine/CaptureRecoveryBackoff.cs`
- Create: `tests/InstantReplay.Tests/GameCaptureRecoveryScenarioTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Consumes:** verified foreground target, capture failures, recovery policy decisions, provider diagnostics, and user stop/save commands.

**Produces:** automatic monitor→window switching, bounded same-target retry, stable-frame hold, cancellation on stop, and provider/episode diagnostics.

- [x] Add scenario tests around a pure orchestration model: healthy monitor WGC; fullscreen WGC starvation; direct window recovery; DDA storm recovery; window failure with hold/retry; alt-tab ending the episode; stale callback rejection; and stop cancellation.

```csharp
[Fact]
public void Fullscreen_stall_never_admits_monitor_frame_after_window_episode_starts()
{
    var scenario = RecoveryScenario.StartOn(CaptureBackend.Wgc);
    scenario.ObserveTarget(Targets.Minecraft(9));
    scenario.Fail(CaptureFailureKind.BackendStalled);

    Assert.Equal(CaptureBackend.WgcWindow, scenario.ActiveBackend);
    Assert.False(scenario.Admit(CaptureSurfaceScope.Monitor, targetRevision: 0));
}
```

- [x] Run the focused scenario test and confirm it is red.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~GameCaptureRecoveryScenarioTests
```

- [x] In `ReplayEngine`, track current target/episode and pass a `CaptureSourceRequest` into the factory. Execute recovery decisions instead of calling a pairwise alternative. Prefer direct monitor WGC→window WGC when a verified fullscreen target exists; route DDA storms to the same window provider. While holding, keep encoder pacing on the last admitted frame and retry only the same revalidated target with existing bounded backoff.

- [x] Cancel target retry and dispose its provider on user stop. End the window episode after verified focus/target loss, then restore the platform-default monitor provider. Reject every callback whose generation or target revision is stale.

- [x] Extend diagnostics with active provider, target HWND/PID/revision, episode state, quarantine reasons, admitted/rejected frame counts, DDA storm count, window retry count, and last-frame hold duration. Log state transitions once; keep per-second counters in `PipelineProbe`.

- [x] Audit the recovery call graph with `rg`. Assert that recovery paths contain no clip writer, muxer finalization, save command, or storage write. Add a regression test if any pure save-trigger boundary is available.

```powershell
rg -n "Save|WriteClip|Finalize|Mux|Storage" src/Aura/Core/Engine/ReplayEngine.cs src/Aura/Core/Capture
```

- [x] Run focused/full tests and a Release build.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter FullyQualifiedName~GameCaptureRecoveryScenarioTests
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
dotnet build src/Aura/Aura.csproj -c Release
```

- [x] Commit the task.

```powershell
git add src/Aura/Core/Engine/ReplayEngine.cs src/Aura/Core/Diagnostics/PipelineProbe.cs src/Aura/Core/Capture/CaptureHealthPolicy.cs src/Aura/Core/Engine/CaptureRecoveryBackoff.cs tests/InstantReplay.Tests/GameCaptureRecoveryScenarioTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Integrate automatic game-window recovery"
```

---

## Task 8: Verify, sign, package, and run the Minecraft matrix

**Files:**

- Modify only if a packaging defect is found: `release.ps1`
- Modify only if a packaging defect is found: `build_setup.ps1`
- Create: `docs/testing/2026-09-12-window-game-capture-results.md`

**Consumes:** completed implementation, test suite, Release binaries, signing identity, Minecraft Java fullscreen hardware run, and application logs.

**Produces:** signed installer plus a reproducible test record, or a documented `GameHookRequired` result with no claim that the fullscreen defect is fixed.

- [x] Run clean verification and capture exact totals/output in the test record.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
dotnet build src/Aura/Aura.csproj -c Release
git diff --check
```

- [x] Build the installer with the repository script. If packaging fails, diagnose the exact path/signing input and add a targeted packaging regression check before changing scripts.

```powershell
./build_setup.ps1
```

- [x] Verify the produced installer signature and hash, recording the absolute artifact path.

```powershell
Get-AuthenticodeSignature ./dist/* | Format-List Status,StatusMessage,SignerCertificate,Path
Get-FileHash ./dist/* -Algorithm SHA256
```

- [ ] Run the hardware matrix and attach relevant log excerpts/counters to the test record: desktop capture for 5 minutes; Minecraft Java F11 fullscreen for 10 minutes; stationary and moving cursor/menu; three alt-tabs; exit/re-enter fullscreen; explicit Save around transitions; and a full run with no Save press.

- [ ] Acceptance criteria: actual capture cadence stays near configured FPS rather than being filled by duplicates; no desktop frame appears inside the active fullscreen game episode; cursor shape/position remain correct; recovery stabilizes without DDA recreation storms; explicit Save produces a playable clip; the no-Save run creates no video file.

- [ ] If WGC window capture itself stalls or returns unusable game content, record `GameHookRequired` with OS/GPU/game mode/log evidence and stop. Do not silently fall back to desktop-flashing monitor capture and do not add injected hooks in this implementation.

- [ ] Re-run the complete verification after any packaging-only correction, then commit the evidence and any targeted correction.

```powershell
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
dotnet build src/Aura/Aura.csproj -c Release
git diff --check
git add docs/testing/2026-09-12-window-game-capture-results.md release.ps1 build_setup.ps1
git commit -m "Verify window game capture release"
```

## Completion Checklist

- [ ] Every task has its own green focused test and a green full test run.
- [x] Windows 11 still starts on monitor WGC and Windows 10 still starts on DDA by default.
- [x] Target identity is revalidated across HWND reuse, focus changes, and target revisions.
- [x] DDA storms are quarantined and transition desktop frames never reach the broker.
- [x] Window WGC uses shared, ownership-correct WGC lifetime code.
- [x] Output dimensions remain fixed and window content is black-bar fitted.
- [x] Cursor capture is target-relative and stale cursor shapes are cleared.
- [ ] Recovery never invokes clip persistence; no-Save hardware run creates no video.
- [x] Installer signature and SHA256 are recorded.
- [ ] Minecraft hardware evidence supports either acceptance or the explicit `GameHookRequired` outcome.
