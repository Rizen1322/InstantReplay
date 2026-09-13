# Minecraft OpenGL Game Capture Design

## Goal

Aura must capture live frames from Minecraft Java in true F11 exclusive fullscreen on the reproduced Windows 11/NVIDIA system, where monitor WGC, window WGC, and DDA all fail to provide useful frames. When Minecraft loses foreground focus, Aura must immediately record the selected monitor so an Alt-Tab shows the new window or desktop. Returning to Minecraft must return to game-only capture without rebuilding the encoder. Capture switching and recovery must never save a replay automatically.

The first version is deliberately limited to verified 64-bit Minecraft Java processes. It must never inject into Counter-Strike 2 or another anti-cheat game.

## Evidence and Root Cause

The 2026-09-13 Minecraft hardware run established:

- monitor WGC starved for ten consecutive seconds in F11;
- Aura successfully selected the verified `javaw.exe` HWND and started window WGC;
- window WGC received one frame in nine seconds while the encoder duplicated it 544 times;
- leaving F11 changed the source from `2560x1440` to `927x562` and restored desktop composition.

The provider selection and encoder pacing behaved as designed; the missing input frames originate below Aura. True exclusive OpenGL presentation can bypass the DWM surfaces used by WGC and DDA. A reliable true-fullscreen provider therefore has to observe Minecraft's OpenGL presentation inside its process.

## Approaches

### Minimal Minecraft-only OpenGL hook — selected

Inject one native x64 DLL into the verified Minecraft `javaw.exe`, intercept the OpenGL buffer presentation boundary, and copy completed game frames through a versioned shared-memory channel. This captures the game backbuffer independently of DWM and keeps the implementation narrow enough to audit and test.

### Embed OBS/libobs game capture

Rejected. OBS has a mature multi-API hook but importing it brings a large native runtime, a broad injector, substantial packaging work, and GPL licensing consequences. Aura needs only Minecraft OpenGL for this first stage.

### Force borderless fullscreen

Rejected by the user. It avoids injection but changes the requested true-F11 behavior.

## System Architecture

`MinecraftGameCaptureSource` is a hybrid provider with one long-lived D3D11 device and two inputs:

1. the existing monitor WGC session supplies desktop/window frames;
2. `OpenGlGameFrameBridge` supplies BGRA frames copied from the injected Minecraft hook.

Both inputs remain attached to the same fixed monitor canvas and existing encoder session. A foreground route gate selects exactly one input. Provider switching never destroys the encoder, replay buffers, audio engine, or monitor WGC session.

When Minecraft is foreground:

- the hook command is enabled;
- all monitor frames are rejected before the broker;
- only fresh frames for the verified PID, process-start identity, HWND target revision, capture generation, and route epoch are admitted;
- while the hook is warming or temporarily stale, the encoder repeats the last admitted game frame, or black if no game frame has ever arrived.

When Minecraft loses foreground focus:

- the hook command is disabled to remove `glReadPixels` overhead;
- the route epoch changes under the same lock used to publish frames;
- every in-flight game callback from the old epoch is rejected;
- the next monitor WGC frame is admitted, showing the foreground window or desktop without a pipeline restart.

When Minecraft becomes foreground again, monitor admission closes immediately. Aura holds the last game frame until a new hook frame from the new route epoch arrives, so a desktop frame cannot leak into the active game episode.

## Native Hook

Create `Aura.GameCaptureHook64.dll` as a small native C component. It uses the permissively licensed MinHook library, retained with its upstream license, to intercept these presentation exports:

- `gdi32!SwapBuffers`;
- `opengl32!wglSwapBuffers`;
- `opengl32!wglSwapLayerBuffers`.

Recursive presentation calls are guarded per thread. Capture runs only when the intercepted HDC belongs to the verified Minecraft top-level window and its OpenGL context is current. The original presentation function is always called exactly once, even if capture initialization or IPC fails.

The hook must preserve every OpenGL state value it changes: read framebuffer binding, pixel-pack buffer binding, pack alignment, and read buffer. It clears and records its own GL errors without consuming errors produced by the game before entry.

Frame readback uses a three-buffer Pixel Buffer Object ring when the required OpenGL functions are available:

1. issue `glReadPixels(GL_BGRA, GL_UNSIGNED_BYTE)` into the current PBO;
2. map the PBO issued on an earlier presentation;
3. copy its rows in reverse order into the next IPC slot so Aura receives top-down BGRA;
4. unmap and rotate without waiting on the just-issued readback.

Capture is throttled to Aura's configured target FPS before issuing readback. If PBO initialization is unavailable, the hook reports `UnsupportedOpenGlReadback`; it does not fall back to synchronous per-frame readback that could stall Minecraft.

## Injection Boundary and Safety

`MinecraftHookEligibility` is a pure policy and requires all of the following:

- executable basename is exactly `javaw.exe`;
- the existing game detector classifies the target as Minecraft;
- process architecture is x64 and matches Aura;
- PID, process start time, root HWND, selected monitor, and foreground identity still match the verified target;
- the process belongs to the current interactive user and is not running at a higher integrity level;
- `opengl32.dll` is loaded;
- no known anti-cheat module or protected-process condition is present.

The policy has an explicit permanent deny rule for `cs2.exe` and all non-Minecraft processes. A large fullscreen window or a process named `javaw.exe` alone is insufficient.

The injector uses an absolute DLL path inside Aura's installation directory, verifies the DLL architecture and expected SHA-256 before opening the target, writes only that UTF-16 path into the target, starts `LoadLibraryW`, waits with a bounded timeout, validates the remote module result, and releases every process/thread/allocation handle on every outcome. It never requests more process rights than thread creation, query, VM operation, VM write, and VM read require.

Injection starts proactively when the selector verifies that Minecraft has entered true fullscreen. Waiting for the existing ten-second starvation threshold would contradict the 250 ms freeze budget and reproduce the defect before recovery. Failures are quarantined for that process-start identity so Aura does not repeatedly inject.

## Shared-Memory Protocol

The hook and Aura share a fixed-layout, little-endian ABI with explicit sizes and offsets. The header contains:

- magic, protocol version, header size, and total mapping size;
- controller PID and heartbeat;
- target PID, process-start identity, HWND, target revision, capture generation, and route epoch;
- enabled/stop commands and hook state/error code;
- width, height, stride, pixel format, target FPS, slot count, and newest completed sequence.

Three frame slots contain a seqlock value, timestamp in 100-nanosecond units, dimensions, byte count, and BGRA bytes. The producer marks a slot odd while writing and even after publishing. Aura copies a slot only when the sequence is even and unchanged before and after the copy. Invalid dimensions, stride, byte count, identity, generation, epoch, or timestamp are rejected without touching the last accepted frame.

Named mapping and event names include target PID plus a cryptographically random session nonce. The nonce is passed through a small bootstrap mapping opened by both sides; global predictable object names are not used. Default object ACLs remain limited to the current user session.

## Managed Frame Bridge

`OpenGlGameFrameBridge` owns injection, shared objects, the reader thread, and a reusable D3D11 upload texture created on the monitor WGC device. It never allocates a managed full-frame byte array per frame.

For each completed slot it:

- validates the complete ABI snapshot;
- copies directly from mapped memory into a mapped dynamic D3D11 BGRA texture row by row;
- samples the target-relative system cursor using the existing `WindowCursorSampler`;
- raises `CapturedSurface` synchronously while the texture is valid;
- records fresh, duplicate, torn, invalid, late-epoch, and upload timing counters.

The existing `WindowFrameNormalizer` fits hook frames into the fixed selected-monitor canvas. Exact-size frames use the direct path. The game-rendered crosshair is already in the backbuffer; a visible Windows cursor is composited exactly once by the existing cursor path.

## Foreground Routing

`CaptureRoutePolicy` is pure and has three states:

- `Monitor`: admit monitor surfaces and disable hook capture;
- `GamePending`: reject monitor surfaces, enable the hook, and hold the prior game frame;
- `GameLive`: admit current-epoch hook surfaces and reject monitor surfaces.

A WinEvent foreground hook supplies low-latency focus changes, with a 100 ms identity poll as a recovery path if an event is missed. Route state changes and frame publication share one lock. The foreground callback performs no D3D work and never waits for injection.

Alt-Tab from Minecraft changes `GameLive` or `GamePending` to `Monitor` immediately. The first fresh monitor frame after that transition becomes visible. Alt-Tab back changes to `GamePending` immediately; it becomes `GameLive` only after a newly published hook frame proves the game backbuffer is current.

## Failure Handling and Lifetime

The native DLL starts a control thread. On stop, controller death, protocol mismatch, or target process shutdown it disables capture, removes hooks, waits for active hook callbacks to leave, releases PBO/GL resources on a valid context when possible, closes IPC, and unloads itself with `FreeLibraryAndExitThread`.

Aura treats these failures separately:

- injection denied or module absent: keep monitor WGC outside Minecraft; show black/last game frame while Minecraft is foreground and log the exact eligibility failure;
- hook heartbeat alive but no frames: re-arm once, then quarantine the hook for that process identity;
- controller or target exits: close mappings and restore monitor routing;
- malformed/torn IPC frame: reject only that frame;
- D3D upload device loss: use the existing generation-safe pipeline recovery.

No failure path silently admits monitor frames while the verified Minecraft target remains foreground.

## Replay and Persistence

Game/monitor route changes alter only which frame reaches the broker. They never call `SaveReplay`, create a muxer, reserve a filename, finalize a clip, or register storage. RAM replay remains continuous. Explicit continuous recording keeps its existing behavior. A replay file is created only after the explicit Save command.

## Diagnostics

Logs and the per-second probe identify:

- `WGC-monitor`, `OpenGL-game-pending`, or `OpenGL-game-live`;
- PID, process-start identity, HWND, target revision, generation, and route epoch;
- injection attempt/result and native hook error code;
- hook heartbeat age and newest OpenGL frame age;
- PBO reads issued/mapped, IPC frames published, torn/rejected frames, D3D uploads, monitor frames admitted/rejected, and duplicates;
- route transitions with the exact foreground HWND and latency to the first accepted frame.

State transitions log once. High-frequency counters remain summarized by `PipelineProbe`.

## Build and Packaging

Native sources live under `src/native/Aura.GameCaptureHook`. MinHook lives under `third_party/minhook` with its upstream license and pinned revision. `packaging/build_game_capture_hook.ps1` builds only x64 Release and emits `Aura.GameCaptureHook64.dll` plus a SHA-256 manifest. The same build passes the digest into the managed build as generated source, so the injector compares against an expected hash embedded in Aura rather than trusting only an adjacent manifest.

The build uses Zig `0.16.0` for `x86_64-windows`, downloaded from `https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip` into a repository-ignored tool cache. The required archive SHA-256 is `68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e`. The script verifies this digest before extraction and never downloads anything during application runtime. `build_setup.ps1` builds the hook before `dotnet publish`, copies the DLL and manifest into `app_publish`, and fails if either is absent. The installer includes both files as ordinary payload content.

## Testing

### Managed unit tests

- eligibility accepts only a fully verified Minecraft x64 target and rejects CS2, arbitrary Java, stale PID/start identity, background HWND, architecture mismatch, higher integrity, and missing OpenGL;
- route policy enters `GamePending` on Minecraft focus, admits only a fresh current-epoch hook frame into `GameLive`, and enters `Monitor` immediately on Alt-Tab;
- old-epoch hook frames are rejected after Alt-Tab and old monitor frames are rejected after returning to Minecraft;
- protocol parser rejects every invalid size, offset, identity, sequence, pixel format, and timestamp case;
- pipeline restart and route changes never request replay persistence.

### Native and IPC tests

- C static assertions and managed offset tests prove the ABI is identical;
- a native OpenGL fixture presents numbered color frames, accepts injection, and proves monotonically changing pixels through the real mapping;
- the fixture exercises resize, focus enable/disable, controller exit, target exit, hook removal, and repeated attach/detach;
- a deliberately torn slot is ignored without corrupting the previous frame;
- target FPS throttling bounds readback work.

### Hardware acceptance

- Minecraft Java F11 runs for ten minutes with useful OpenGL frames near configured FPS and no frozen interval longer than 250 ms;
- stationary and moving cursor/menu output remains correct;
- three Alt-Tabs show the new foreground monitor content within 250 ms and never admit a late game frame afterward;
- returning to Minecraft admits no desktop frame and resumes only on a fresh OpenGL frame;
- F11 exit/re-entry, resize, world reload, and process restart rebind safely;
- Minecraft frame time is measured with capture disabled/enabled; the 99th percentile regression must remain bounded and no synchronous readback fallback is allowed;
- explicit Save produces a playable clip; a run without Save creates no file;
- hook shutdown does not crash or hang Minecraft.

## Counter-Strike 2 Separation

CS2 is not part of the injection scope. Its 2026-09-13 log shows a different failure: WGC continues delivering variable live frames, while the NVENC input queue reaches `33/33`, `MFT ProcessInput` peaks at 141 ms, the pacer is blocked, and the encoder quality preset falls from 33 to 25. That is GPU/encoder contention, not a missing DWM surface.

A separate follow-up will optimize CS2's external capture/convert/encode pacing without loading any Aura DLL into `cs2.exe`. Minecraft hook work must not change CS2 eligibility or introduce a generic injection mode.

## Non-Goals

- no injection into CS2, anti-cheat games, protected processes, or arbitrary executables;
- no DirectX, Vulkan, or universal game hook in this stage;
- no kernel driver, service, elevated helper, capture of protected content, or cross-user capture;
- no synchronous OpenGL readback fallback;
- no visible backend selector;
- no automatic replay save.

## Success Criteria

- true-F11 Minecraft frames come from the live OpenGL backbuffer rather than DWM;
- desktop content is impossible while verified Minecraft is foreground;
- Alt-Tab immediately records the selected monitor and returning immediately gates it out;
- frame routing does not rebuild the encoder or interrupt audio/replay continuity;
- the hook is restricted, versioned, bounded, unloadable, and diagnosable;
- no recovery or route transition writes video without the user's explicit Save or Record action;
- CS2 remains outside the injector and is handled later through safe external-pipeline optimization.
