# Minecraft OpenGL Game Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture Minecraft Java's live OpenGL backbuffer in true F11 fullscreen, switch to the selected monitor immediately on Alt-Tab, and never persist video unless the user explicitly saves or starts continuous recording.

**Architecture:** Add a Minecraft-only x64 OpenGL hook and versioned shared-memory bridge, then combine it with the existing monitor WGC source behind an epoch-gated `MinecraftGameCaptureSource`. The encoder, replay buffer, audio pipeline, and monitor canvas stay alive while focus routing changes. CS2 is permanently excluded from injection and keeps using external capture.

**Tech Stack:** .NET 10/C#, Vortice D3D11, Win32 memory-mapped files and events, native C/OpenGL, MinHook v1.3.4 (`c3fcafd`), Zig 0.16.0 x86_64-windows, xUnit, PowerShell packaging.

**Spec:** `docs/superpowers/specs/2026-09-13-minecraft-opengl-game-capture-design.md`

## Global Constraints

- Apply strict TDD: add one focused failing test, run it and observe the expected failure, implement only enough to pass, then rerun the focused and relevant suites.
- Never inject into `cs2.exe`, a non-Minecraft process, a higher-integrity process, or an unverified/stale process identity.
- Never fall back to synchronous OpenGL readback; missing PBO support is an explicit hook error.
- Keep frame publication synchronous while the D3D texture is valid and allocate no managed full-frame array per captured frame.
- Route changes may alter frame admission only. They must not call replay saving, muxing, file naming, clip registration, or storage code.
- Preserve user changes and keep each task independently reviewable.

---

## Task 1: Freeze the cross-process ABI

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/GameHookProtocol.cs`
- Create: `src/native/Aura.GameCaptureHook/game_hook_protocol.h`
- Create: `tests/InstantReplay.Tests/GameHookProtocolTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [x] Write failing managed layout tests for magic `0x48475541`, protocol version `1`, the 256-byte header, the 64-byte slot header, all atomic 64-bit offsets, three slots, and checked mapping-size calculation.
- [x] Run `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj -c Release --filter FullyQualifiedName~GameHookProtocolTests` and confirm compilation/test failure because the protocol does not exist.
- [x] Implement explicit-layout managed structs and matching C structs. Use these fixed header offsets: mapping size 8, controller PID 16, target PID 20, process-start ticks 24, HWND 32, target revision 40, generation 48, route epoch 56, command 64, state 68, error 72, target FPS 76, width 80, height 84, stride 88, pixel format 92, slot count 96, slot-header size 100, slot stride 104, newest sequence 112, controller heartbeat 120, hook heartbeat 128, issued/published/dropped counters 136/144/152. Reserve the rest through byte 255.
- [x] Define slot offsets: seqlock 0, frame sequence 8, timestamp 16, route epoch 24, width/height/stride/byte count 32/36/40/44, followed by 16 reserved bytes.
- [x] Add C `_Static_assert` checks for size, alignment, and every shared field offset; cap dimensions at 7680×4320 BGRA and reject integer overflow.
- [x] Rerun the focused tests, then commit with `git commit -m "Define Minecraft game hook protocol"`.

## Task 2: Implement deterministic focus routing

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/GameCaptureRoutePolicy.cs`
- Create: `tests/InstantReplay.Tests/GameCaptureRoutePolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [x] Write failing tests for `Monitor -> GamePending -> GameLive`, immediate `GameLive -> Monitor`, re-entry with a new epoch, target-revision changes, old game-frame rejection after Alt-Tab, and old monitor-frame rejection after return.
- [x] Run the focused test filter and confirm the missing policy failure.
- [x] Implement immutable `GameCaptureRouteState(Route, Epoch, TargetRevision)` plus pure transition/admission methods. Every focus or target-identity boundary increments the positive route epoch; only a current-epoch game frame promotes `GamePending` to `GameLive`.
- [x] Ensure `GamePending` admits neither monitor nor stale game frames. `Monitor` admits only current-epoch monitor frames; `GameLive` admits only current-epoch/current-target game frames.
- [x] Run focused tests and the existing `CaptureFrameAdmissionGateTests`; commit with `git commit -m "Add epoch gated game capture routing"`.

## Task 3: Lock injection eligibility to verified Minecraft

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/MinecraftHookEligibility.cs`
- Create: `tests/InstantReplay.Tests/MinecraftHookEligibilityTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [x] Write table-driven failing tests for accepted 64-bit Minecraft and rejection of CS2, arbitrary `javaw.exe`, non-Minecraft classification, stale PID/start time/HWND, wrong monitor, non-foreground target, architecture mismatch, different user, higher integrity, missing `opengl32.dll`, protected process, and known anti-cheat modules.
- [x] Run the focused filter and observe failure.
- [x] Implement `MinecraftHookEligibilitySnapshot`, `MinecraftHookEligibilityReason`, and a pure `Evaluate(GameCaptureTarget, snapshot)` method. Check exact case-insensitive executable basename `javaw.exe`, exact Minecraft classification, and permanent `cs2.exe`/non-Minecraft denial before any permissive check.
- [x] Make accepted output carry the complete immutable target identity needed by IPC; never let window size/fullscreen shape alone grant eligibility.
- [x] Run focused tests plus `GameWindowSelectorTests`; commit with `git commit -m "Restrict game hook to verified Minecraft"`.

## Task 4: Add reproducible native toolchain and MinHook

**Files:**

- Create: `packaging/get_zig.ps1`
- Create: `packaging/build_game_capture_hook.ps1`
- Create: `src/native/Aura.GameCaptureHook/build.zig`
- Create: `src/native/Aura.GameCaptureHook/hook_exports.c`
- Create: `src/native/Aura.GameCaptureHook/hook_exports.h`
- Create: `src/native/Aura.GameCaptureHook/protocol_layout_test.c`
- Vendor: `third_party/minhook/**` from v1.3.4 commit `c3fcafd`
- Modify: `.gitignore`

- [x] Add a build smoke test mode that compiles and runs `protocol_layout_test.exe`; run it first and confirm it fails because the scripts/project are absent.
- [x] Implement `get_zig.ps1` to cache Zig 0.16.0 under ignored `.tools/`, download only from the pinned official URL, and verify SHA-256 `68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e` before extraction.
- [x] Vendor only the MinHook sources/headers required for x64 plus upstream `LICENSE.txt`; record tag and full commit in `third_party/minhook/UPSTREAM.txt`.
- [x] Create a DLL skeleton exporting protocol/version information and a Zig build that produces only x64 Release artifacts with no C runtime dependency surprises.
- [x] Run `powershell -NoProfile -ExecutionPolicy Bypass -File packaging/build_game_capture_hook.ps1 -RunProtocolTests`; inspect PE architecture and exported symbols.
- [x] Commit with `git commit -m "Add reproducible native hook build"`.

## Task 5: Implement safe shared-memory reading

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/GameHookFrameReader.cs`
- Create: `src/Aura/Core/Capture/GameHook/GameHookSessionNames.cs`
- Create: `tests/InstantReplay.Tests/GameHookFrameReaderTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [x] Write failing tests using unmanaged test mappings for valid newest-slot reads, odd seqlock, changed seqlock, wrong magic/version/size, stale identity/generation/epoch, invalid dimensions/stride/byte count/format/timestamp, sequence rollback, and random nonce names.
- [x] Run the focused tests and confirm failure.
- [x] Implement a bounds-checked zero-allocation reader API that copies directly from an acquired mapped view into a caller-provided `Span<byte>` or row-copy callback. Read the seqlock before and after the copy with acquire semantics and preserve the previous accepted sequence on rejection.
- [x] Generate mapping/event names from PID plus 128-bit cryptographic nonce. Put the nonce in a PID-scoped bootstrap mapping whose ACL remains current-session/current-user by default.
- [x] Rerun focused tests and commit with `git commit -m "Read versioned game hook frames safely"`.

## Task 6: Inspect the target and inject with bounded rights

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/MinecraftProcessInspector.cs`
- Create: `src/Aura/Core/Capture/GameHook/MinecraftHookInjector.cs`
- Create: `src/Aura/Core/Capture/GameHook/PortableExecutableInspector.cs`
- Create: `src/Aura/Core/Capture/GameHook/GameHookNativeMethods.cs`
- Create: `tests/InstantReplay.Tests/PortableExecutableInspectorTests.cs`
- Create: `tests/InstantReplay.Tests/MinecraftHookInjectorPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [ ] Write failing PE tests for x64 DLL acceptance, x86/non-PE rejection, and SHA-256 mismatch. Write policy tests proving only `PROCESS_CREATE_THREAD | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ` are requested and every timeout/failure enters a process-start-scoped quarantine.
- [ ] Run focused tests and observe the expected failure.
- [ ] Implement current-user SID, token integrity, architecture, module, protected-process, anti-cheat, PID/start/HWND/foreground inspection and feed only the immutable snapshot into `MinecraftHookEligibility`.
- [ ] Implement absolute-path/hash/PE verification before `OpenProcess`, UTF-16 remote allocation/write, `LoadLibraryW` remote thread, bounded wait, nonzero module result validation, and unconditional handle/allocation cleanup.
- [ ] Load the expected digest from an embedded managed resource created by the native build, not from the adjacent manifest alone.
- [ ] Run focused tests and commit with `git commit -m "Add bounded Minecraft hook injection"`.

## Task 7: Attach presentation hooks and lifecycle control

**Files:**

- Create: `src/native/Aura.GameCaptureHook/dllmain.c`
- Create: `src/native/Aura.GameCaptureHook/ipc.c`
- Create: `src/native/Aura.GameCaptureHook/ipc.h`
- Create: `src/native/Aura.GameCaptureHook/present_hooks.c`
- Create: `src/native/Aura.GameCaptureHook/present_hooks.h`
- Create: `src/native/Aura.GameCaptureHook/hook_lifetime.c`
- Create: `src/native/Aura.GameCaptureHook/hook_lifetime.h`
- Create: `tests/native/OpenGlCaptureFixture/**`
- Create: `tests/native/run_hook_lifecycle_test.ps1`

- [ ] Build a failing native lifecycle test: launch a simple x64 OpenGL fixture, create bootstrap/control mappings, inject the hook, and require heartbeat plus clean stop/unload.
- [ ] Run `tests/native/run_hook_lifecycle_test.ps1` and confirm the missing-heartbeat failure.
- [ ] Implement a loader-safe `DllMain` that starts one control thread and does no mapping/hook work under loader lock. On the control thread open/validate IPC, initialize MinHook, and hook `gdi32!SwapBuffers`, `opengl32!wglSwapBuffers`, and `wglSwapLayerBuffers` when available.
- [ ] Add a per-thread recursion guard, verify intercepted HDC/root HWND/current context, and call the original presentation exactly once on every path.
- [ ] On stop/controller death/protocol mismatch, disable capture, remove hooks, wait boundedly for active callbacks, close IPC, and call `FreeLibraryAndExitThread`.
- [ ] Run the lifecycle test repeatedly (at least 20 attach/detach cycles) and commit with `git commit -m "Hook OpenGL presentation with safe lifetime"`.

## Task 8: Capture asynchronously through a three-PBO ring

**Files:**

- Create: `src/native/Aura.GameCaptureHook/gl_capture.c`
- Create: `src/native/Aura.GameCaptureHook/gl_capture.h`
- Modify: `src/native/Aura.GameCaptureHook/present_hooks.c`
- Modify: `tests/native/OpenGlCaptureFixture/**`
- Create: `tests/native/run_gl_frame_test.ps1`

- [ ] Extend the fixture to present numbered color/checker frames and add a failing integration assertion that shared-memory frame sequences and sampled pixels change monotonically.
- [ ] Add resize, command enable/disable, target-FPS, row-orientation, and torn-slot test cases; run and confirm failure before readback exists.
- [ ] Load PBO functions from the active context and allocate three `GL_PIXEL_PACK_BUFFER` objects. Throttle before issuing `glReadPixels(GL_BGRA, GL_UNSIGNED_BYTE)`; map only an older PBO and never wait on the just-issued readback.
- [ ] Preserve/read back framebuffer binding, pixel-pack buffer binding, pack alignment, and read buffer. Do not consume a game-owned GL error. Reverse rows into an odd/even seqlock slot, then publish newest sequence with release ordering.
- [ ] Report `UnsupportedOpenGlReadback` when required PBO functions are unavailable; add no synchronous fallback.
- [ ] Run the frame test at 30 and 60 FPS, resize repeatedly, and verify throttling bounds issued reads; commit with `git commit -m "Capture Minecraft OpenGL frames through PBOs"`.

## Task 9: Upload hook frames and build the hybrid source

**Files:**

- Create: `src/Aura/Core/Capture/GameHook/OpenGlGameFrameBridge.cs`
- Create: `src/Aura/Core/Capture/GameHook/ForegroundWindowWatcher.cs`
- Create: `src/Aura/Core/Capture/MinecraftGameCaptureSource.cs`
- Create: `src/Aura/Core/Capture/HybridCaptureInput.cs`
- Create: `tests/InstantReplay.Tests/MinecraftGameCaptureSourceTests.cs`
- Modify: `src/Aura/Core/Capture/CapturedSurface.cs`
- Modify: `src/Aura/Core/Capture/CaptureFrameAdmissionGate.cs`
- Modify: `tests/InstantReplay.Tests/CaptureFrameAdmissionGateTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`

- [ ] Write failing tests with fake monitor/hook inputs for immediate monitor admission on Alt-Tab, immediate monitor closure on return, first-fresh-game promotion, route-epoch races, target replacement, hook failure while foreground, and exactly-once synchronous publication.
- [ ] Run focused route/source/admission tests and confirm failure.
- [ ] Add `RouteEpoch` to `CapturedSurface` and extend broker admission to reject lower epochs and wrong scope/revision without weakening generation checks.
- [ ] Implement `ForegroundWindowWatcher` with WinEvent delivery plus 100 ms identity polling. Its callback only updates the route/commands and never performs D3D work.
- [ ] Implement `OpenGlGameFrameBridge` with one reusable dynamic BGRA upload texture on the monitor source's D3D11 device, direct mapped-view row copies, existing `WindowCursorSampler`, synchronous callback, heartbeat/rejection/upload diagnostics, and bounded disposal.
- [ ] Implement `MinecraftGameCaptureSource` around a long-lived `ScreenCaptureSource` and bridge, using one lock for route transitions and publication. Keep the fixed monitor canvas; use existing normalization for game-frame sizing. While foreground/pending, never admit a desktop frame.
- [ ] Run focused tests and the full managed suite; commit with `git commit -m "Add hybrid Minecraft game capture source"`.

## Task 10: Select Minecraft game capture proactively

**Files:**

- Modify: `src/Aura/Core/Capture/CaptureBackendPolicy.cs`
- Modify: `src/Aura/Core/Capture/CaptureSourceRequest.cs`
- Modify: `src/Aura/Core/Capture/IScreenCapture.cs`
- Modify: `src/Aura/Core/Capture/CaptureRecoveryPolicy.cs`
- Modify: `src/Aura/Core/Capture/CaptureFailure.cs`
- Modify: `src/Aura/Core/Capture/GameCaptureRecoveryCoordinator.cs`
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Modify: `src/Aura/Core/Diagnostics/PipelineProbe.cs`
- Modify: `tests/InstantReplay.Tests/CaptureSourceRequestTests.cs`
- Modify: `tests/InstantReplay.Tests/CaptureRecoveryPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/GameCaptureRecoveryScenarioTests.cs`
- Modify: `tests/InstantReplay.Tests/ReplayBufferContinuityTests.cs`

- [ ] Write failing policy/scenario tests: verified Minecraft true fullscreen requests `MinecraftOpenGl` immediately rather than after ten seconds; non-Minecraft/CS2 never does; Alt-Tab does not rebuild capture/encoder; target process restart creates a new episode; fallback never exposes monitor content while old Minecraft is foreground.
- [ ] Add a persistence spy assertion proving provider selection, injection failure, focus switching, and recovery make zero Save/mux/file-name/storage calls.
- [ ] Run the focused tests and observe failure.
- [ ] Add `CaptureBackend.MinecraftOpenGl`, require a matching verified target in `CaptureSourceRequest`, and construct `MinecraftGameCaptureSource` in the factory. Keep `CaptureBackendPolicy.Alternative` limited to monitor providers.
- [ ] Trigger the backend proactively when the existing selector proves Minecraft fullscreen. Keep the hybrid provider alive across focus loss; rebuild only for target process/identity change or device loss.
- [ ] Add probe labels `WGC-monitor`, `OpenGL-game-pending`, and `OpenGL-game-live`, with target/generation/epoch, heartbeat age, issued/mapped/published/rejected/upload counters, and one-shot route transitions.
- [ ] Run focused tests and the full managed suite; commit with `git commit -m "Integrate automatic Minecraft OpenGL capture"`.

## Task 11: Package and verify the native payload

**Files:**

- Modify: `src/Aura/Aura.csproj`
- Modify: `build_setup.ps1`
- Modify: `release.ps1`
- Create: `tests/packaging/game_capture_hook_package_test.ps1`

- [ ] Add a failing packaging test that requires `Aura.GameCaptureHook64.dll`, its SHA-256 manifest, matching embedded digest, x64 PE architecture, and inclusion in `payload.zip`.
- [ ] Run the packaging test and confirm the missing payload failure.
- [ ] Add a pre-publish native build step, copy DLL/manifest to the Aura output, embed the expected digest as an assembly resource, and make publish fail on absence/hash mismatch.
- [ ] Update `build_setup.ps1` step numbering and build the hook before `dotnet publish`; preserve the current recoverable, explicit PowerShell paths and the fixed `packaging/build_identity_package.ps1` call.
- [ ] Ensure `release.ps1` signs/hashes the final payload consistently and cannot package a stale hook binary.
- [ ] Run the packaging test and one complete `./build_setup.ps1`; commit with `git commit -m "Package verified Minecraft capture hook"`.

## Task 12: End-to-end verification and CS2 handoff

**Files:**

- Create: `docs/verification/2026-09-13-minecraft-opengl-game-capture.md`
- Create: `docs/diagnostics/2026-09-13-cs2-encoder-contention.md`
- Modify only if evidence requires: relevant test or implementation files from prior tasks

- [ ] Run `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj -c Release` and both native test scripts from a clean tree; record exact commands/results.
- [ ] Build the installer and verify DLL/manifest hashes from both `app_publish` and extracted payload.
- [ ] On real Minecraft Java, run ten minutes in true F11, verify live useful FPS, no freeze longer than 250 ms, stationary/moving cursor correctness, three Alt-Tabs each way, F11 exit/re-entry, world reload, resize, process restart, and clean Aura/Minecraft shutdown.
- [ ] Verify a run without Save creates no new video file; then explicitly Save once and verify the clip is playable. Check explicit continuous recording remains unchanged.
- [ ] Measure Minecraft frame-time impact with capture disabled/enabled and record P99 plus hook counters. If acceptance fails, add a reproducing test before changing code.
- [ ] Capture a fresh CS2 log without injection, document WGC arrival rate, converter/encoder timings, queue depth, pacer blocking, GPU engine load, and recommended external-only pacing changes. Do not mix the CS2 fix into the Minecraft hook commits.
- [ ] Perform a final diff review, run `git status --short`, and commit verification evidence with `git commit -m "Verify Minecraft OpenGL game capture"`.

## Completion Gate

The implementation is complete only when all managed/native/packaging tests pass, the real Minecraft F11 matrix passes, no desktop frame can enter a foreground Minecraft epoch, no video appears without explicit Save/Record, and CS2 remains provably outside every injector path.
