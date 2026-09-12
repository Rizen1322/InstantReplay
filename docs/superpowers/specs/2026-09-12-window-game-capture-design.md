# Window Game Capture Fallback Design

## Goal

Aura must record Minecraft Java smoothly when the game enters true fullscreen on a system where monitor-based Windows Graphics Capture (WGC) stops delivering frames and DXGI Desktop Duplication (DDA) continuously returns `DXGI_ERROR_ACCESS_LOST`. Recovery must be automatic, must not expose a backend selector, must not admit desktop frames while a game-only fallback is active, and must never write a replay to disk unless the user explicitly saves it.

This design extends the existing generation-safe WGC/DDA capture engine with a third provider that captures the foreground game window through `IGraphicsCaptureItemInterop::CreateForWindow`. It does not inject code into the game.

## Evidence and Constraints

The reproduced Minecraft session established the following:

- monitor WGC was healthy at 60 frames per second before fullscreen, then delivered zero new frames;
- the engine correctly switched to DDA;
- DDA recreated its duplication object, but the exclusive fullscreen output immediately invalidated it again;
- the session produced 506 `DXGI_ERROR_ACCESS_LOST` transitions and 29 `DXGI_ERROR_INVALID_CALL` transitions;
- only 390 of 2,931 encoded frames contained a newly captured image; 2,550 were duplicates;
- duplication sessions created between fullscreen transitions occasionally observed the composed desktop, which explains desktop flashes in the saved video.

The monitor APIs therefore cannot be made reliable on this machine with more retry timing. Windows provides a supported Win32 interop API for creating a WGC item from a specific `HWND`. Minecraft Java has a stable top-level `javaw.exe` window even when its monitor capture path is unavailable.

Window WGC can still fail, close, resize, or stop producing frames. The design must treat it as a monitored provider, not as an unconditional cure. A graphics hook like OBS is a possible later fallback, but it is excluded here because DLL injection expands the security, antivirus, anti-cheat, graphics-API, process-architecture, and maintenance surface substantially.

## Approaches Considered

### Continue retrying monitor DDA

Rejected. The recovery loop already recreates the duplication correctly. Continuous `ACCESS_LOST` proves that the output is unavailable while this fullscreen mode is active. Faster retries only increase GPU and log load and expose more desktop transition frames.

### Capture the foreground game window through WGC — selected

Create a `GraphicsCaptureItem` for the verified game `HWND`. This stays on supported Windows APIs, remains GPU-resident, avoids process injection, and targets the game surface instead of the unavailable monitor composition surface.

### Inject an OpenGL/DXGI game-capture hook

Deferred. Hooking `wglSwapBuffers` or `IDXGISwapChain::Present` is the strongest way to capture a true fullscreen backbuffer, but requires native injected components, cross-process synchronization, crash containment, signing, and compatibility policy. It will be considered only if the window-WGC hardware acceptance test also fails.

## Backend Model

`CaptureBackend` becomes a three-provider model:

- `WgcMonitor`: the existing WGC monitor provider and Windows 11 default;
- `WgcWindow`: the new WGC game-window provider;
- `DesktopDuplication`: the existing DDA monitor provider and Windows 10 default.

Initial selection remains unchanged. `WgcWindow` is never an initial desktop provider because it needs a verified foreground game target. It is an automatic game fallback.

The current pairwise `Alternative` decision is replaced by an ordered recovery decision. Given the active backend, failure kind, foreground target, OS support, and session health, the policy returns one of the three providers or a degraded hold state. The state machine records why a provider was rejected and prevents immediate re-entry into the same failed path.

## Game Window Selection

A new `GameWindowSelector` produces an immutable `GameCaptureTarget` containing:

- top-level/root-owner `HWND`;
- process ID and process start identity to guard against PID reuse;
- executable name and detected game name;
- selected monitor handle and monitor pixel bounds;
- current client/window pixel bounds;
- a monotonically increasing target revision.

A target is eligible only when all of these are true:

- it is the foreground root window;
- it is visible, not cloaked, not minimized, and belongs to a live process;
- it is not Aura or a known desktop/system/overlay process;
- `GameDetector` recognizes the process as a game;
- it substantially covers the selected monitor when requested as a fullscreen fallback;
- its client area is non-empty.

Minecraft's `javaw.exe` is resolved through the existing game database. The selector does not choose an arbitrary window merely because it is large.

Window identity is revalidated before provider creation and on each health sample. A destroyed window, changed root owner, exited process, or changed process start identity invalidates the target and closes its provider generation.

## Window WGC Provider

`WindowGraphicsCaptureSource` implements `IScreenCapture` and shares the existing WGC device, frame-pool, callback, and borderless-access patterns where practical. Its differences are:

- the capture item is created with `CreateForWindow(HWND)`;
- `GraphicsCaptureItem.Closed` emits a typed `CaptureTargetClosed` failure;
- every callback carries both capture generation and target revision;
- stale frames from a previous window or generation are ignored;
- the provider reports target size changes instead of silently changing the encoder contract;
- system cursor capture is disabled when the existing separate cursor plane is available, preventing a system-composed and separately composed cursor from appearing together.

The new interop call is isolated in `CaptureInterop.CreateItemForWindow`. The rest of the engine never directly handles COM interop pointers.

## Fixed Output Canvas

The encoder and replay buffer must keep the selected monitor's configured width and height across backend changes. A game window may report slightly different dimensions because of DPI, decorations, or a transition frame.

`WindowFrameNormalizer` copies each accepted window texture on the GPU into a fixed BGRA canvas matching the selected monitor:

- an exact-size fullscreen frame is copied directly;
- a valid differently sized frame is scaled to fit the fixed canvas while preserving aspect ratio;
- uncovered pixels are black, never pixels from a monitor or desktop capture;
- zero-sized, implausibly sized, or transition-only frames are rejected;
- no frame is read back to CPU memory.

The normalized texture enters the existing frame broker, NV12 converter, encoder, and replay rings with unchanged dimensions. A backend switch therefore does not invalidate earlier same-codec replay packets solely because the source became a window.

## Cursor Handling

Window WGC must not reuse DDA pointer metadata. The provider reports whether Windows composed the cursor. The initial implementation disables WGC cursor capture for the game-window provider and uses a generation-bound Win32 cursor snapshot feeding the existing separate cursor compositor.

The snapshot is accepted only while the cursor belongs to the target window and contains a valid shape. Invalid or unavailable cursor data hides only the overlay; it never corrupts the retained game frame. Minecraft's game-rendered crosshair remains part of the captured window image.

Cursor state is cleared on target change, backend switch, item close, or capture generation change.

## Recovery State Machine

The recovery coordinator uses these rules:

1. Start with the OS-preferred monitor provider: WGC on Windows 11, DDA on Windows 10.
2. If monitor WGC starves while a verified fullscreen game is foreground, start `WgcWindow` for that target.
3. If DDA reports an `ACCESS_LOST` storm while a verified fullscreen game is foreground, stop recreating DDA and start `WgcWindow`.
4. An `ACCESS_LOST` storm is three lifecycle invalidations within two seconds without a stable run of useful frames.
5. After a storm, DDA is quarantined for that fullscreen target revision. Leaving fullscreen or changing target revision clears this target-scoped quarantine.
6. If `WgcWindow` closes or starves, enter degraded hold and retry the same verified target with bounded backoff. Do not fall back to a monitor provider already proven to expose desktop or zero frames for this fullscreen episode.
7. When the game leaves fullscreen and monitor capture proves healthy, return to the OS-preferred monitor provider.
8. A user stop cancels every pending retry and cannot restart capture.

The state machine is episode-based rather than a global two-switch counter. It prevents ping-pong while still allowing a new game/fullscreen episode to make fresh decisions.

## No-Desktop Frame Gate

Once a fullscreen game target triggers `WgcWindow`, only frames carrying the active target revision and provider generation may enter the broker. DDA or monitor-WGC callbacks from older generations are rejected even if they arrive during teardown.

If no valid new window frame exists, the encoder may repeat the last accepted game frame to keep the stream playable. It must not promote a newly observed monitor frame during the failed fullscreen episode. If there has never been an accepted game frame, the fixed canvas remains black until capture succeeds.

This gate eliminates desktop flashes without pretending that duplicated frames are fresh gameplay. Diagnostics distinguish captured, normalized, duplicated, rejected-stale, and degraded-hold frames.

## Replay and Disk Invariants

The existing RAM replay audio/video rings survive a same-format provider switch. Backend selection, target changes, degraded hold, recovery, and retries never invoke replay saving, remuxing, file reservation, or continuous-recording startup.

A replay file is created only after the explicit Save command. Explicit continuous recording retains its existing segmented-recovery behavior.

## Diagnostics

Logs include:

- backend plus provider generation;
- target executable, PID, target revision, and redacted window title classification;
- target bounds, monitor bounds, and normalization mode;
- WGC monitor/window received and accepted rates separately;
- DDA lifecycle invalidations within the storm window;
- exact recovery transition and rejected-provider reason;
- age of the newest valid game frame;
- count of frames rejected for stale generation, stale target, invalid size, or desktop-frame gate;
- degraded-hold duration and retry attempt.

Repeated DDA lifecycle messages are rate-limited and summarized. A storm produces one actionable line rather than hundreds of recreation messages.

## Threading and Lifetime

- Provider callbacks publish frames only; they never tear down the pipeline directly.
- Provider start, stop, target replacement, and backend changes remain serialized by the engine lifecycle lock.
- Window target revision and capture generation are checked at both callback entry and broker publication.
- `GraphicsCaptureItem.Closed`, health probes, and DDA storm detection compete through the existing single recovery gate.
- Teardown waits for active WGC callbacks before disposing the frame pool and D3D resources.
- A stale callback cannot free or overwrite a slot owned by a later generation.

## Testing

### Unit tests

- selector accepts the foreground, visible, recognized fullscreen game root window;
- selector rejects Aura, desktop/system processes, cloaked/minimized windows, stale PIDs, and non-game overlays;
- target revision changes when the window identity changes;
- DDA storm detector trips on three invalidations within two seconds and resets after stable useful frames;
- recovery policy selects `WgcWindow` after monitor-WGC starvation or a DDA fullscreen storm;
- target-scoped quarantine clears only after leaving the fullscreen episode;
- stale provider generations and target revisions cannot publish;
- normalizer preserves fixed output dimensions, aspect ratio, and black uncovered regions;
- no-desktop frame gate holds the last game frame and rejects monitor frames during the episode;
- degraded retries and backend changes cannot invoke disk-save collaborators;
- manual stop cancels window capture recovery.

### Integration tests

- fake monitor WGC starves, verified game target appears, window WGC becomes healthy, and replay continuity is preserved;
- DDA emits repeated `ACCESS_LOST`, the storm is rate-limited, and no DDA transition frame enters the broker;
- game window closes and is recreated with a new `HWND`; old callbacks are ignored and capture rebinds;
- a window-size change is normalized without changing the encoder output contract;
- saving during a provider transition writes only the explicitly requested replay and never an automatic recovery file.

### Hardware acceptance

On the target Windows 11/NVIDIA system:

- run Minecraft Java in F11 fullscreen for at least ten minutes;
- verify window-WGC sustains useful gameplay frames near the configured output rate;
- verify no desktop frame appears while entering fullscreen, leaving fullscreen, or using Alt+Tab;
- test Minecraft menus and cursor movement for missing, duplicated, malformed, or offset cursor images;
- destroy and recreate the Minecraft window and verify automatic target rebinding;
- save before, during, and after a backend transition and verify playable timing and audio sync;
- leave replay running without pressing Save and verify no video file is created;
- verify bounded CPU, VRAM, log volume, and recovery retries.

If window WGC also returns no useful frames in this hardware test, implementation stops at a documented `GameHookRequired` result. An injected OpenGL/DXGI capture subsystem requires a separate design and explicit approval.

## Rollout

The feature ships without a user-facing backend option. The existing environment override remains diagnostic and can force monitor WGC or DDA; an additional internal diagnostic value may force window WGC only when a valid game target exists.

Implementation is staged so each boundary is testable:

1. target model, window selector, and tests;
2. three-backend recovery state machine and DDA storm detector;
3. `CreateForWindow` interop and window-WGC provider;
4. fixed output normalizer and no-desktop frame gate;
5. cursor integration and diagnostics;
6. full automated verification and signed hardware-test installer.

## Non-Goals

- no DLL injection, OpenGL/DXGI/Vulkan hooks, kernel driver, or indirect display driver;
- no capture of protected content, secure desktop, or another user session;
- no arbitrary background-window recording when no verified foreground game exists;
- no visible backend selector;
- no simultaneous full-resolution probing of all three providers;
- no automatic video save;
- no promise that Windows window capture can access every exclusive fullscreen game.

## Success Criteria

- Minecraft fullscreen uses useful window frames rather than a monitor `ACCESS_LOST` storm;
- the saved video contains no desktop flashes from failed monitor capture generations;
- output dimensions, encoder format, replay continuity, and audio timing survive automatic provider changes;
- recovery is automatic, target-aware, bounded, and cancellation-safe;
- only explicit Save or explicit continuous recording writes video to disk;
- failure of window WGC is reported honestly as requiring a future game hook rather than hidden behind duplicated or desktop frames.
