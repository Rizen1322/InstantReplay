# Adaptive Capture Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Aura choose WGC/DDA automatically, switch away from a failed or demonstrably starved backend without flapping, and never save the replay buffer to disk unless the user explicitly requests it.

**Architecture:** Pure policy types own initial selection, failure classification, health streaks, quarantine, and switch limits. `ReplayEngine` owns the active backend generation and performs serialized video-pipeline restarts while retaining compatible in-memory replay/audio buffers. Capture implementations report typed failures; they do not select the alternative themselves.

**Tech Stack:** C# 14, .NET 10, WPF, Windows.Graphics.Capture, DXGI Desktop Duplication, Vortice.Direct3D11/DXGI, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-11-adaptive-capture-backend-design.md`

## Global Constraints

- Windows 11 build 22000+ starts with WGC; older Windows starts with DDA.
- No visible or persisted backend choice is added.
- `INSTANTREPLAY_CAPTURE=wgc|dda` remains a deterministic diagnostic override and disables cross-backend switching.
- Automatic recovery must never call `SaveReplay`, reserve a replay filename, or write the in-memory replay buffer to disk.
- A user-started continuous recording may be finalized and resumed as a new segment.
- Soft degradation applies to WGC only and requires ten consecutive qualifying one-second samples after a ten-second warm-up.
- Backend switching is serialized through the existing lifecycle/frame gates and must honor manual Stop.

---

### Task 1: Backend identity and initial selection

**Files:**
- Create: `src/Aura/Core/Capture/CaptureBackendPolicy.cs`
- Modify: `src/Aura/Core/Capture/IScreenCapture.cs:62-106`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`
- Create: `tests/InstantReplay.Tests/CaptureBackendPolicyTests.cs`

**Interfaces:**
- Produces: `enum CaptureBackend { Wgc, DesktopDuplication }`
- Produces: `CaptureBackendSelection(CaptureBackend Backend, bool Forced)`
- Produces: `CaptureBackendPolicy.SelectInitial(int windowsBuild, string? diagnosticOverride)`
- Produces: `CaptureBackendPolicy.Alternative(CaptureBackend backend)`
- Changes: `ScreenCaptureFactory.Create(CaptureBackend backend, int monitorIndex)`

- [ ] **Step 1: Write failing selection tests**

```csharp
[Theory]
[InlineData(22000)]
[InlineData(22621)]
public void Windows11DefaultsToWgc(int build) =>
    Assert.Equal(new CaptureBackendSelection(CaptureBackend.Wgc, false), CaptureBackendPolicy.SelectInitial(build, null));

[Fact]
public void Windows10DefaultsToDesktopDuplication() =>
    Assert.Equal(new CaptureBackendSelection(CaptureBackend.DesktopDuplication, false), CaptureBackendPolicy.SelectInitial(19045, null));

[Theory]
[InlineData("wgc", CaptureBackend.Wgc)]
[InlineData(" DDA ", CaptureBackend.DesktopDuplication)]
public void DiagnosticOverrideForcesBackend(string value, CaptureBackend expected) =>
    Assert.Equal(new CaptureBackendSelection(expected, true), CaptureBackendPolicy.SelectInitial(22621, value));
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureBackendPolicyTests --no-restore`

Expected: compilation fails because `CaptureBackendPolicy` and related types do not exist.

- [ ] **Step 3: Implement the pure selection policy and explicit factory**

```csharp
internal enum CaptureBackend { Wgc, DesktopDuplication }
internal readonly record struct CaptureBackendSelection(CaptureBackend Backend, bool Forced);

internal static class CaptureBackendPolicy
{
    public static CaptureBackendSelection SelectInitial(int windowsBuild, string? diagnosticOverride)
    {
        string forced = diagnosticOverride?.Trim().ToLowerInvariant() ?? "";
        if (forced == "wgc") return new(CaptureBackend.Wgc, true);
        if (forced == "dda") return new(CaptureBackend.DesktopDuplication, true);
        return new(windowsBuild >= 22000 ? CaptureBackend.Wgc : CaptureBackend.DesktopDuplication, false);
    }

    public static CaptureBackend Alternative(CaptureBackend backend) => backend == CaptureBackend.Wgc
        ? CaptureBackend.DesktopDuplication
        : CaptureBackend.Wgc;
}
```

Update `ScreenCaptureFactory.Create` to switch on the explicit backend and keep backend-specific logging there. Remove `UsesWgc`; update `App.AskBorderlessPermission` to query the selected default/forced backend through the pure policy rather than global mutable state.

- [ ] **Step 4: Run focused tests and the solution build**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureBackendPolicyTests --no-restore`

Run: `dotnet build InstantReplay.sln --no-restore`

Expected: all focused tests pass; build has zero warnings/errors.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/Aura/Core/Capture/CaptureBackendPolicy.cs src/Aura/Core/Capture/IScreenCapture.cs src/Aura/App.xaml.cs tests/InstantReplay.Tests/CaptureBackendPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Select capture backend by Windows version"
```

### Task 2: Typed capture failures and bounded DDA recovery

**Files:**
- Create: `src/Aura/Core/Capture/CaptureFailure.cs`
- Modify: `src/Aura/Core/Capture/IScreenCapture.cs:24-45`
- Modify: `src/Aura/Core/Capture/DesktopDuplicationSource.cs:227-405`
- Modify: `src/Aura/Core/Capture/ScreenCaptureSource.cs:247-267`
- Modify: `src/Aura/Core/Capture/DuplicationRecovery.cs`
- Modify: `tests/InstantReplay.Tests/CaptureRegressionTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Produces: `enum CaptureFailureKind { DeviceLost, BackendUnavailable, BackendStalled }`
- Produces: `CaptureFailure(CaptureFailureKind Kind, Exception Error, string Reason)`
- Changes: `IScreenCapture.Failed` to `event Action<CaptureFailure>? Failed`
- Produces: `CaptureFailureClassifier.IsDeviceLossHResult(int hresult)`; `DeviceLoss.IsDeviceLost` delegates HRESULT recognition to it.
- Changes: `DuplicationRecovery.Run(Func<bool> isRunning, Action resetCurrent, Action create, Action<int> delay, Func<Exception, bool> isTemporary, int maxTemporaryMilliseconds, Func<long> elapsedMilliseconds, Action<Exception>? temporaryFailure)` returns `TimedOut` with the last error after the five-second grace period.

- [ ] **Step 1: Write failing bounded-recovery and classification tests**

Add tests proving:

```csharp
[Fact]
public void TemporaryDdaFailureWithinGracePeriodRestoresWithoutEscalation()
{
    long elapsed = 0;
    int attempts = 0;
    var result = DuplicationRecovery.Run(
        () => true, () => { },
        () => { if (++attempts < 6) throw new UnauthorizedAccessException(); },
        milliseconds => elapsed += milliseconds,
        ex => ex is UnauthorizedAccessException,
        maxTemporaryMilliseconds: 5_000,
        elapsedMilliseconds: () => elapsed,
        temporaryFailure: null);

    Assert.Equal(DuplicationRecoveryStatus.Restored, result.Status);
    Assert.Equal(6, attempts);
}

[Fact]
public void TemporaryDdaFailurePastFiveSecondsTimesOutWithLastError()
{
    long elapsed = 0;
    var denied = new UnauthorizedAccessException();
    var result = DuplicationRecovery.Run(
        () => true, () => { }, () => throw denied,
        milliseconds => elapsed += milliseconds,
        ex => ex is UnauthorizedAccessException,
        maxTemporaryMilliseconds: 5_000,
        elapsedMilliseconds: () => elapsed,
        temporaryFailure: null);

    Assert.Equal(DuplicationRecoveryStatus.TimedOut, result.Status);
    Assert.Same(denied, result.Error);
}

[Theory]
[InlineData(unchecked((int)0x887A0005))]
[InlineData(unchecked((int)0x887A0006))]
[InlineData(unchecked((int)0x887A0007))]
public void DeviceLossHResultsAreClassified(int hresult) =>
    Assert.True(CaptureFailureClassifier.IsDeviceLossHResult(hresult));
```

The injected delay advances a fake elapsed counter; tests do not sleep.

- [ ] **Step 2: Run focused recovery tests and verify RED**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "DuplicationRecovery|CaptureFailure" --no-restore`

Expected: failures because timeout status and typed failure do not exist.

- [ ] **Step 3: Implement typed failures and five-second DDA grace**

Add `DuplicationRecoveryStatus.TimedOut`, retain the last transient exception, and stop retrying when elapsed time reaches `5_000` ms. `DesktopDuplicationSource` maps timeout to `BackendUnavailable`, permanent device HRESULTs to `DeviceLost`, and other permanent creation errors to `BackendUnavailable`. `ScreenCaptureSource` maps `DeviceLoss.IsDeviceLost` to `DeviceLost`; repeated non-device callback exceptions remain logged locally until the health controller detects a stall.

- [ ] **Step 4: Run focused tests and build**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "DuplicationRecovery|CaptureFailure" --no-restore`

Run: `dotnet build InstantReplay.sln --no-restore`

Expected: focused tests pass; zero build warnings/errors.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/Aura/Core/Capture/CaptureFailure.cs src/Aura/Core/Capture/IScreenCapture.cs src/Aura/Core/Capture/DesktopDuplicationSource.cs src/Aura/Core/Capture/ScreenCaptureSource.cs src/Aura/Core/Capture/DuplicationRecovery.cs tests/InstantReplay.Tests/CaptureRegressionTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Classify capture failures and bound DDA recovery"
```

### Task 3: Health, quarantine, and anti-flapping policy

**Files:**
- Create: `src/Aura/Core/Capture/CaptureHealthPolicy.cs`
- Create: `tests/InstantReplay.Tests/CaptureHealthPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Produces: `CaptureHealthSample(CaptureBackend Backend, int TargetFps, int FramesReceived, int FramesEncoded, int FramesDuplicated, bool GameForeground, TimeSpan Uptime)`
- Produces: `CaptureHealthDecision(bool SwitchBackend, string Reason)`
- Produces: stateful `CaptureHealthPolicy.Observe(CaptureHealthSample sample, DateTimeOffset now)`
- Produces: `CaptureHealthPolicy.RecordFailure`, `CanUse`, `RecordSwitch`, and `ResetHealthyHistory`.

- [ ] **Step 1: Write failing health-policy tests**

Cover these concrete behaviors:

```csharp
[Fact] public void OneBadWgcSecondDoesNotSwitch();
[Fact] public void TenStarvedWgcSecondsInGameSwitchToDda();
[Fact] public void StaticDesktopResetsStarvationStreak();
[Fact] public void EncoderStarvationDoesNotBlameCapture();
[Fact] public void WarmupSamplesDoNotCount();
[Fact] public void DdaLowFrameRateDoesNotTriggerSoftSwitch();
[Fact] public void BackendFailureQuarantinesForTenMinutes();
[Fact] public void SoftWgcFailureQuarantinesForProcessSession();
[Fact] public void ThirdSwitchWithinTenMinutesIsRejected();
[Fact] public void HealthyTenMinutesClearsTransientFailureHistory();
```

Use target 60 FPS. A qualifying WGC sample is `received: 30`, `encoded: 55`, `duplicated: 25`, `gameForeground: true`, `uptime: 20s`.

- [ ] **Step 2: Run the focused health tests and verify RED**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureHealthPolicyTests --no-restore`

Expected: compilation failure because the health policy types do not exist.

- [ ] **Step 3: Implement the minimal state machine**

Implement constants exactly from the spec:

```csharp
private const int RequiredBadSamples = 10;
private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
private static readonly TimeSpan FailureQuarantine = TimeSpan.FromMinutes(10);
private static readonly TimeSpan SwitchWindow = TimeSpan.FromMinutes(10);
private const int MaxSwitchesPerWindow = 2;
```

The qualifying expression is WGC, game foreground, received below `target * 0.60`, duplicates at least `encoded * 0.35`, and encoded at least `target * 0.75`. Any nonqualifying sample resets the bad streak. Keep all clocks injected through `now`; do not call `DateTime.UtcNow` inside the policy.

- [ ] **Step 4: Run focused and full unit tests**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter CaptureHealthPolicyTests --no-restore`

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore`

Expected: all tests pass.

- [ ] **Step 5: Commit Task 3**

```powershell
git add src/Aura/Core/Capture/CaptureHealthPolicy.cs tests/InstantReplay.Tests/CaptureHealthPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Detect capture degradation without backend flapping"
```

### Task 4: Restart-intent policy that forbids automatic replay saving

**Files:**
- Create: `src/Aura/Core/Engine/PipelineRestartPolicy.cs`
- Create: `tests/InstantReplay.Tests/PipelineRestartPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

**Interfaces:**
- Produces: `enum PipelineStopIntent { UserStop, CaptureRestart }`
- Produces: `PipelineRestartActions(bool KeepReplayBuffer, bool KeepAudioBuffer, bool SaveReplay, bool ResumeContinuousRecording)`
- Produces: `PipelineRestartPolicy.For(PipelineStopIntent intent, bool continuousRecordingActive, bool formatCompatible)`

- [ ] **Step 1: Write failing no-auto-save tests**

```csharp
[Fact]
public void CaptureRestartKeepsCompatibleRamBuffersAndNeverSavesReplay()
{
    var actions = PipelineRestartPolicy.For(PipelineStopIntent.CaptureRestart, false, true);
    Assert.True(actions.KeepReplayBuffer);
    Assert.True(actions.KeepAudioBuffer);
    Assert.False(actions.SaveReplay);
    Assert.False(actions.ResumeContinuousRecording);
}

[Fact]
public void CaptureRestartResumesOnlyExplicitContinuousRecording()
{
    var actions = PipelineRestartPolicy.For(PipelineStopIntent.CaptureRestart, true, true);
    Assert.True(actions.ResumeContinuousRecording);
    Assert.False(actions.SaveReplay);
}

[Fact]
public void IncompatibleVideoFormatClearsRamVideoWithoutSavingIt()
{
    var actions = PipelineRestartPolicy.For(PipelineStopIntent.CaptureRestart, false, false);
    Assert.False(actions.KeepReplayBuffer);
    Assert.False(actions.SaveReplay);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter PipelineRestartPolicyTests --no-restore`

Expected: compilation failure because restart policy types do not exist.

- [ ] **Step 3: Implement the pure restart-intent policy**

`SaveReplay` is hard-coded `false` for every intent; replay saving remains reachable only from the existing explicit `SaveReplay` public command. `CaptureRestart` retains audio and compatible video RAM buffers, and resumes continuous recording only if it was already active.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter PipelineRestartPolicyTests --no-restore`

Expected: all restart-policy tests pass.

- [ ] **Step 5: Commit Task 4**

```powershell
git add src/Aura/Core/Engine/PipelineRestartPolicy.cs tests/InstantReplay.Tests/PipelineRestartPolicyTests.cs tests/InstantReplay.Tests/InstantReplay.Tests.csproj
git commit -m "Define buffer-safe capture restart intent"
```

### Task 5: Integrate automatic switching into ReplayEngine

**Files:**
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs:24-70,182-258,295-357,581-707,716-776,1005-1090`
- Modify: `src/Aura/Core/Buffering/ReplayBuffers.cs`
- Create: `tests/InstantReplay.Tests/ReplayBufferContinuityTests.cs`

**Interfaces:**
- Consumes: `CaptureBackendPolicy`, `CaptureHealthPolicy`, typed `CaptureFailure`, `PipelineRestartPolicy`.
- Produces: `ReplayVideoBuffer.IsCompatible(long bitrateBps, int seconds)` or equivalent capacity check that does not mutate/clear the arena.
- Produces: private `RequestCaptureRestart(CaptureBackend next, string reason, CaptureFailureKind kind, long observedGeneration)` and `RestartVideoPipelineLocked(CaptureBackend next, bool resumeContinuousRecording, long observedGeneration)`.

- [ ] **Step 1: Write failing replay-buffer continuity tests**

Build a `ReplayVideoBuffer`, allocate it, add keyframe/non-keyframe encoded data, record `BufferedDurationTicks`/`TotalBytes`, apply the new non-mutating compatibility API, and assert the entries remain. Add a negative format/capacity case proving incompatible restart requests clearing rather than disk output; no saving class is referenced by the policy or test.

- [ ] **Step 2: Run continuity tests and verify RED**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter ReplayBufferContinuityTests --no-restore`

Expected: failure because the non-mutating compatibility API is absent.

- [ ] **Step 3: Track selected backend and generation in ReplayEngine**

At first start, call:

```csharp
var selection = CaptureBackendPolicy.SelectInitial(
    Environment.OSVersion.Version.Build,
    Environment.GetEnvironmentVariable("INSTANTREPLAY_CAPTURE"));
_captureBackend = selection.Backend;
_captureBackendForced = selection.Forced;
```

Increment `_captureGeneration` for every newly built source. Capture failure and timer requests include the observed generation; ignore them unless it still equals the active generation.

- [ ] **Step 4: Feed one-second health samples from the existing probe**

Preserve the current encoder diagnostics, then pass the same counter deltas plus `GameDetector.DetectForegroundGame() != "Desktop"` and pipeline uptime to `CaptureHealthPolicy.Observe`. On `SwitchBackend`, request the alternative only when the diagnostic override is not forced and `CanUse`/switch-rate policy allow it.

- [ ] **Step 5: Implement capture-only restart under lifecycle gates**

The restart worker must:

1. coalesce through `_recovering`;
2. verify generation and `_stopRequested`;
3. capture `wasRecording` and format (`codec`, output width/height, FPS);
4. stop health timers and close `_pipelineOpen`;
5. unsubscribe/stop capture, acquire the frame write gate, detach and dispose encoder/processor/capture in existing safe order;
6. leave `_videoBuffer`, `_audioBuffer`, and `_audio` alive when compatible;
7. create/start the selected capture, processor, and encoder; resubscribe the buffer and callbacks;
8. reopen gates/timers and increment generation;
9. never call `SaveReplay`, construct `ReplaySaver`, call `ReserveFilePath` with preset `"replay"`, or call `_videoBuffer.Snapshot`;
10. if `wasRecording`, finalize the explicit recorder and call `StartRecordingLocked` only after the new encoder is ready.

If startup of the alternative fails, quarantine it and use the existing bounded recovery loop to retry the most recently working allowed backend. Manual `Stop` wins at every retry boundary.

- [ ] **Step 6: Add structured switch logs**

Log exactly one episode header containing generation, old/new backend, reason, received/encoded/duplicated sample, and quarantine. Repeated failed attempts use existing log de-duplication. Notify the user only when an actual switch begins and when capture is restored.

- [ ] **Step 7: Run focused tests, full tests, and build**

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --filter "CaptureBackend|CaptureHealth|PipelineRestart|ReplayBufferContinuity|DuplicationRecovery" --no-restore`

Run: `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-restore`

Run: `dotnet build InstantReplay.sln --no-restore`

Expected: all tests pass; build has zero warnings/errors.

- [ ] **Step 8: Commit Task 5**

```powershell
git add src/Aura/Core/Engine/ReplayEngine.cs src/Aura/Core/Buffering/ReplayBuffers.cs tests/InstantReplay.Tests/ReplayBufferContinuityTests.cs
git commit -m "Switch capture backends while retaining replay memory"
```

### Task 6: Final audit and hardware-ready diagnostics

**Files:**
- Modify if necessary: files changed in Tasks 1–5 only

**Interfaces:** None.

- [ ] **Step 1: Audit the no-auto-save invariant**

Run:

```powershell
rg -n "RequestCaptureRestart|RestartVideoPipelineLocked|SaveReplay|ReplaySaver|ReserveFilePath|Snapshot" src/Aura/Core/Engine/ReplayEngine.cs
```

Expected: recovery methods contain no call path to replay saving or replay filename reservation.

- [ ] **Step 2: Run final clean verification**

Run:

```powershell
dotnet build InstantReplay.sln --no-restore
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj --no-build --no-restore
git diff --check
git status --short
```

Expected: zero warnings/errors, all tests pass, no whitespace errors, only intentional changes remain.

- [ ] **Step 3: Inspect the final branch diff**

Run: `git diff d0b6c4e..HEAD --stat` and `git diff d0b6c4e..HEAD -- src/Aura/Core/Capture src/Aura/Core/Engine src/Aura/Core/Buffering tests/InstantReplay.Tests`

Verify initial selection, typed failures, five-second DDA grace, soft-WGC thresholds, anti-flapping, generation checks, manual-stop cancellation, preserved RAM buffers, and absence of automatic replay saving.

- [ ] **Step 4: Commit any review corrections and report hardware scenarios**

If review changes are required, repeat the relevant focused RED/GREEN cycle and commit only those corrections. Report that automated verification cannot simulate OS secure desktop/GPU saturation, and give the exact Windows 11 scenarios from the spec for local validation.
