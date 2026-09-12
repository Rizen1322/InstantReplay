# Aura Capture Engine v2 Design

## Goal

Aura must capture a complete monitor smoothly on Windows 10 and Windows 11 without exposing a backend choice to the user. A mode switch, temporary secure desktop, capture-API failure, or malformed cursor update must not leave a permanently frozen video or a corrupted colored rectangle in place of the pointer. Capture recovery must never save or write the replay buffer to disk unless the user explicitly pressed Save; an explicitly started continuous recording remains an explicit write operation and may continue in a new segment after recovery.

This design extends the adaptive WGC/DDA work already present on `codex/adaptive-capture`. It replaces the mutable, backend-owned frame passed directly to the encoder with a small capture engine that owns frame publication, cursor composition, and provider recovery.

## Constraints Established by Windows

- Aura cannot obtain monitor pixels without a Windows or application-provided surface. The supported general-purpose providers remain Windows Graphics Capture (WGC) and DXGI Desktop Duplication (DDA).
- DDA can become invalid on desktop switches, display-mode changes, fullscreen transitions, session disconnects, and secure desktop. `DXGI_ERROR_ACCESS_LOST` requires recreation. `E_ACCESSDENIED` on secure desktop is expected for a normal user process and cannot be bypassed safely.
- WGC is the preferred initial provider on Windows 11. DDA is the preferred initial provider on Windows 10 because WGC border suppression is unavailable on mainstream Windows 10 builds.
- A graphics hook can capture an individual game's swap chain efficiently, but injection is incompatible with some anti-cheat and security products and cannot serve as a reliable whole-desktop provider.
- An Indirect Display Driver represents an indirect or virtual monitor. It is not a replacement API for capturing the user's existing physical output and would add installation, signing, elevation, and support costs.

## Approaches Considered

### 1. Keep the current backend-owned frame path

Continue patching `DesktopDuplicationSource`, `ScreenCaptureSource`, and `CursorOverlay` independently. This has the smallest short-term diff, but capture ownership, cursor composition, pacing, and recovery remain coupled. It does not remove the class of stale-frame and stale-cursor-generation failures.

### 2. Aura-owned frame broker over WGC and DDA — selected

Keep WGC and DDA as interchangeable pixel providers, but publish their output through a common GPU-resident frame broker. Keep the clean desktop image separate from cursor state and compose into a leased output slot. Centralize provider health, generations, and recovery in the engine.

This keeps the supported Windows capture paths, avoids injection, preserves the existing GPU-only BGRA → NV12 → hardware-encoder pipeline, and directly addresses the observed freezes and cursor corruption.

### 3. Game hook or display driver

Hook each game's graphics API or ship a display driver. This could be useful for a future opt-in game-only capture mode, but it is not suitable as Aura's default: it expands the compatibility matrix across graphics APIs, process architectures, anti-cheat systems, driver signing, and Windows updates while losing reliable desktop capture.

## Architecture

### Capture providers

`WgcCaptureProvider` and `DdaCaptureProvider` implement a narrow internal provider contract. A provider is responsible only for:

- creating the correct D3D device on the monitor's adapter;
- acquiring an OS-owned surface and its QPC timestamp;
- reporting format or size changes;
- reporting typed failures;
- returning DDA pointer metadata when the pointer is not already in the desktop surface;
- releasing the OS-owned frame promptly.

Providers do not call the encoder and do not draw into the broker's clean desktop texture. WGC marks cursor state as `SystemComposed`; DDA supplies a separate immutable pointer update.

Each provider instance has a monotonically increasing generation. Callbacks, failures, frames, and cursor updates carry that generation. The engine ignores events from an older generation after a restart.

### GPU frame broker

`CaptureFrameBroker` owns a bounded ring of three frame slots on the capture D3D device. Each slot contains:

- a clean desktop texture;
- a composited output texture;
- frame timestamp and provider generation;
- immutable cursor revision used for composition;
- an explicit `Free`, `Writing`, `Ready`, or `Reading` ownership state.

The provider copies the acquired surface into a `Writing` slot, releases the Windows frame, and atomically publishes the slot as the newest `Ready` frame. The encoder leases only a completed `Ready` slot. A slot cannot be reused until its lease is returned. When the producer outruns the consumer, the broker discards the oldest unleased `Ready` slot and retains the newest image; it never blocks capture behind stale video.

GPU commands use the existing multithread-protected immediate context. Command ordering on that context guarantees that the copy and cursor composition precede video processing. CPU ownership states prevent resource reuse or disposal while a frame callback is active. If a later implementation introduces deferred contexts or cross-device textures, it must add an explicit D3D synchronization primitive rather than relying on the current ordering guarantee.

No frame is copied to system RAM. The only CPU-side image data is the small DDA pointer-shape buffer.

### Cursor plane

`DdaCursorState` replaces mutable cursor state embedded in `CursorOverlay`. It retains an immutable validated cursor snapshot for the current provider generation:

- visibility and top-left position from DDA frame metadata;
- shape type: monochrome, color, or masked color;
- width, height, pitch, and exact copied payload;
- monotonically increasing cursor revision.

Before accepting a shape, Aura validates its type, dimensions, pitch, arithmetic overflow, and required payload size. Unknown or invalid shapes are not rendered; Aura logs one rate-limited warning and waits for a new valid shape. It never interprets arbitrary bytes as a color cursor.

On DDA recreation, device loss, monitor change, or backend switch, cursor visibility and cached shape are cleared before the new generation can publish a frame. An old shape is never paired with a new duplication session.

The compositor always starts from the clean desktop texture and writes the cursor to the separate output texture. It supports the three documented operations:

- color: alpha composition;
- monochrome: AND followed by XOR;
- masked color: replace when alpha is zero, otherwise XOR with the corresponding clean desktop pixel.

The clean texture is never modified by cursor rendering. A static desktop can therefore be recomposed with a new cursor state without accumulating old cursor pixels, and a bad cursor update cannot poison the frame retained for pacing.

### Scheduling and timestamps

Acquisition and encoder pacing remain separate:

- providers acquire and publish the latest meaningful OS frame;
- the encoder's QPC-based cadence requests the newest completed broker frame at the configured output FPS;
- if no newer desktop frame exists, the encoder duplicates the last valid encoded image as it does today;
- a cursor-only DDA update publishes a recomposited frame even when the desktop pixels did not change.

The broker retains the original Windows QPC timestamp. The scheduler does not manufacture capture timestamps or use wall-clock time. Large discontinuities after sleep, mode change, or restart start a new pacing epoch rather than attempting to encode a backlog.

### Recovery coordinator

The existing adaptive policy remains the authority for initial backend selection, quarantine, and anti-flapping. `CaptureRecoveryCoordinator` owns the mechanics:

1. Close the frame gate and invalidate the current provider generation.
2. Stop the provider and wait for active leases/callbacks to leave the old generation.
3. For a DDA `DXGI_ERROR_ACCESS_LOST`, recreate duplication for up to five seconds with bounded delay.
4. Treat `E_ACCESSDENIED`, unsupported mode, or an exhausted DDA retry window as `BackendUnavailable` and try WGC when policy permits.
5. Rebuild the complete D3D/video pipeline for device removal or reset; changing only the capture API cannot repair a lost device.
6. Clear cursor state and incompatible frame slots, start the selected provider, force the new encoder's first frame to be a keyframe, and reopen the frame gate.

Health decisions combine typed failures with frame age and existing encoder diagnostics. Low DDA frame count on a static desktop is normal. WGC degradation is actionable only when a foreground game is active, the encoder can accept frames, and capture starvation persists for the configured consecutive samples.

If neither provider can access the desktop, Aura remains alive and keeps retrying with bounded backoff. It displays a warning and resumes automatically after access returns. It does not publish a stale frame as a new frame indefinitely.

### Capture format and HDR

DDA first attempts `IDXGIOutput5.DuplicateOutput1` with BGRA8 and falls back to `DuplicateOutput`. The returned format and capability are logged. The first implementation does not advertise a 10-bit or floating-point format; those formats require an explicit tested conversion and color-space policy in the video processor.

Native HDR capture and tone mapping are intentionally a separate follow-up. `DuplicateOutput1` capability and returned format are logged now so hardware tests can establish whether adding the HDR formats will avoid a conversion on the target machines without silently changing output colors.

## Data Flow

```text
WGC or DDA
    │ OS surface + QPC + optional cursor update
    ▼
Capture provider (generation N)
    │ copy clean surface, release OS frame
    ▼
CaptureFrameBroker ── DdaCursorState
    │ newest leased clean/composited GPU texture
    ▼
QPC frame scheduler → VideoProcessor → hardware encoder → RAM replay rings
                                                   │
                                                   └─ disk only after explicit Save
```

## Threading and Lifetime Rules

- Provider callbacks never perform pipeline teardown directly; they submit a typed recovery request.
- Pipeline start, stop, and provider switching remain serialized by the engine lifecycle lock.
- Broker slot leases are generation-bound and disposable. A stale return cannot free a slot belonging to a newer generation.
- The frame gate prevents capture callbacks, cursor composition, screenshots, and encoder pacing from using resources during teardown.
- Stop requested by the user cancels pending automatic retries and prevents recovery from restarting capture.
- Screenshots lease the newest broker frame. They never open a second DDA or WGC session for the same monitor.
- Logging is rate-limited for repeated acquisition and cursor-shape failures.

## Disk-Write Invariant

Capture failure, backend switching, provider recreation, frame-broker replacement, and cursor recovery must not invoke replay saving, remuxing, or file reservation.

The RAM replay video/audio rings survive a same-format capture restart. If format compatibility changes, Aura discards only incompatible in-memory video data and records the reason in the log. It does not write those packets to disk.

If continuous recording was explicitly started, recovery may finalize its current playable segment and resume into a new segment. This is part of the user's explicit recording action, not an automatic replay save.

## Testing

### Pure unit tests

- cursor payload validation for all three documented shape types;
- rejection of unknown type, impossible dimensions, undersized pitch, truncated payload, and overflow;
- cursor state is cleared across provider generations;
- top-left position is used without subtracting hotspot;
- masked-color replace/XOR behavior matches the documented truth table;
- broker never publishes a `Writing` slot and never reuses a leased slot;
- newest-frame replacement remains bounded at three slots;
- stale generation callbacks, failures, and lease returns are ignored;
- capture restart cannot invoke any replay-save collaborator;
- manual stop cancels recovery;
- a static DDA desktop is not diagnosed as stalled.

### Integration tests

- simulated provider repeatedly publishes, stalls, fails, switches, and restarts while the fake encoder leases frames;
- screenshot and capture teardown race without use-after-dispose;
- format change clears incompatible video packets but performs no disk write;
- explicitly active continuous recording resumes as a new segment only after the new encoder is ready.

### Hardware acceptance

On supported Windows 10 and Windows 11 machines:

- record at least 30 minutes under sustained game/GPU load without a permanently frozen output;
- leave the cursor stationary for at least 10 minutes, then move and change cursor shapes; no colored rectangle, stale cursor, or offset cursor may appear;
- exercise borderless/fullscreen transitions, Alt+Tab, resolution/refresh-rate changes, monitor sleep/wake, lock/unlock, and entry/exit from UAC secure desktop;
- after inaccessible secure desktop ends, useful capture must resume automatically within the bounded recovery cycle;
- verify frame age, received/encoded/duplicated rates, provider generation, switch reason, D3D device state, and VRAM budget in logs;
- verify that no video file appears unless Save or continuous recording was explicitly invoked;
- verify WGC borderless behavior on Windows 11 and absence of a DDA capture border on Windows 10.

## Rollout

The v2 engine replaces the internal path without a settings toggle. Existing `INSTANTREPLAY_CAPTURE=wgc|dda` remains an undocumented deterministic diagnostic override and disables cross-backend switching for controlled tests. Logs identify whether the legacy or v2 path is active only during development; the shipping build uses v2 after hardware acceptance passes.

Implementation is split into reviewable stages:

1. cursor validation/state reset and tests;
2. frame broker and slot ownership tests;
3. provider adapters and screenshot integration;
4. recovery coordinator integration and no-save tests;
5. `DuplicateOutput1` capability logging/fallback;
6. full build, automated tests, and hardware validation package.

## Non-Goals

- No kernel driver, Indirect Display Driver, or DLL injection into games.
- No capture of protected video or secure desktop from a normal user process.
- No visible backend selector or persistent backend preference.
- No simultaneous full-resolution WGC and DDA probe.
- No automatic creation of a recovery video file.
- No HDR output or tone mapping until an explicit end-to-end color design is implemented and tested.

## Success Criteria

- Capture either remains useful or reports a typed degraded/unavailable state; it never silently freezes forever.
- Cursor corruption cannot persist: invalid shapes are skipped and cursor state cannot cross provider generations.
- Capture, cursor, encoder, and screenshot ownership are explicit and bounded.
- Backend recovery is automatic and anti-flapping remains enforced.
- The hot path stays GPU-resident and memory use is bounded.
- Only an explicit user save or explicit continuous-recording session writes video to disk.
