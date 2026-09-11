# Adaptive Capture Backend Design

## Goal

Aura automatically chooses and, when necessary, changes the screen-capture backend without exposing a WGC/DDA setting to the user. A backend failure or sustained, evidenced degradation must not leave the video frozen, create cursor artifacts indefinitely, or save a clip without an explicit user action.

## User-visible behavior

- Windows 11 starts with Windows Graphics Capture (WGC).
- Windows 10 starts with Desktop Duplication API (DDA), avoiding WGC's capture border where borderless permission is unavailable.
- Aura changes backend automatically after a confirmed failure or sustained degradation.
- A transient one-off stall does not trigger a switch.
- Aura never calls `SaveReplay` and never writes the RAM replay buffer to disk merely because a backend changed.
- If the user explicitly started continuous recording, a required pipeline rebuild closes the current valid file and resumes recording in a new segment. This remains part of the user's explicit recording action.
- Switching is reported in the application log and through the existing warning notification, with no backend selector added to settings.
- `INSTANTREPLAY_CAPTURE=wgc|dda` remains an undocumented diagnostic override. When present, automatic cross-backend switching is disabled so a forced diagnostic run stays deterministic; recovery may restart only the forced backend.

## Why WGC is preferred on Windows 11

WGC lets Windows compose the cursor and is designed to survive ordinary fullscreen/borderless transitions without the manual cursor-shape path required by DDA. It also supports the existing borderless permission mechanism on Windows 11. DDA remains valuable as a fallback under GPU saturation because prior measurements on this hardware showed WGC delivering only 19–24 new frames per second in one uncapped borderless-game workload.

DDA is not a universally safe default: secure desktop, mode transitions, unsupported desktop modes, and disconnected sessions can invalidate duplication or temporarily prevent `DuplicateOutput`. Those cases must be treated as backend availability failures rather than a permanently frozen capture.

## Architecture

### Backend identity and factory

Introduce a `CaptureBackend` enum (`Wgc`, `DesktopDuplication`) and make `ScreenCaptureFactory` accept an explicit backend. A pure selection policy chooses the initial backend from the Windows build and diagnostic override:

- forced override, if valid;
- otherwise WGC on build 22000 or newer;
- otherwise DDA.

The active backend is stored by `ReplayEngine`; it is not persisted to user settings.

### Typed capture failures

Replace the unclassified `Failed(Exception)` signal with a typed failure containing:

- the original exception;
- `DeviceLost` for failures that invalidate every D3D object;
- `BackendUnavailable` for failures specific to the current capture API;
- `BackendStalled` for a source that remained alive but stopped producing useful frames.

Device loss first rebuilds the pipeline using the preferred backend because changing API cannot repair a removed D3D device. Backend-specific failures quarantine the active backend and choose the alternative.

### Health policy

A pure `CaptureHealthPolicy` consumes one-second samples:

- target FPS;
- new frames received;
- frames encoded;
- duplicated frames;
- whether a non-desktop/game window is foreground;
- active backend;
- consecutive unhealthy sample count;
- backend cooldown state.

Hard failure rules:

- failure to construct or start a backend: immediately try the alternative;
- DDA device loss: rebuild the D3D pipeline, then retry the preferred backend;
- DDA transient duplication errors: retry locally for up to 5 seconds; if access has not returned, report `BackendUnavailable` and switch to WGC;
- WGC device loss: rebuild the D3D pipeline; other repeated session-start failures switch to DDA.

Soft WGC degradation requires all of the following for 10 consecutive one-second samples:

- a non-desktop/game foreground window;
- fewer than 60% of target FPS received as new frames;
- at least 35% of encoded frames supplied as duplicates;
- the encoder itself remains healthy, producing at least 75% of target FPS.

This combination distinguishes capture starvation from encoder starvation. Desktop/idle samples reset the soft-degradation streak, so a static desktop or paused browsing session does not cause a switch. The first ten seconds after pipeline start are a warm-up period and are excluded.

DDA is not switched merely because it receives few new frames: a static DDA desktop normally emits no frames. DDA switches only on explicit API/recovery failure or device loss.

### Anti-flapping and fallback

- A backend that caused a backend-specific failure is quarantined for 10 minutes.
- A backend rejected for soft degradation is quarantined until the current Aura process exits.
- No more than two cross-backend switches are allowed in a rolling 10-minute window.
- If the alternative cannot start, Aura retries the most recently working backend with the existing bounded pipeline-recovery backoff.
- A successful uninterrupted 10-minute period clears transient failure history, but not a session-long soft-degradation quarantine.

These rules prevent WGC/DDA ping-pong while still allowing recovery after a temporary OS state.

### Pipeline rebuild and buffer ownership

Split pipeline teardown into two intents:

1. `UserStop`: current behavior; stop capture, encoder and audio, finalize explicitly active recording, and release replay buffers.
2. `CaptureRestart`: stop and replace capture, video processor and encoder while retaining the encoded replay ring and audio ring in RAM when codec, output resolution and FPS are unchanged.

During `CaptureRestart`:

- close the frame gate before destroying GPU objects;
- keep audio capture and both ring buffers alive;
- rebuild capture, processor and encoder with the selected backend;
- reopen the frame gate and restart health timers;
- do not call replay save/remux code;
- if continuous recording is active, finalize its current segment and start a new segment only after the new encoder is ready.

The new encoder must force an initial keyframe. Replay saving already begins from a usable keyframe; preserving the old encoded ring therefore remains safe across a same-format restart. If format changes unexpectedly, clear only the incompatible video ring and log the reason; never write it to disk automatically.

## Concurrency and error handling

- All switches run through `_lifecycle`; `_recovering` continues to coalesce concurrent recovery requests.
- Timer callbacks only submit a switch request. They never tear down capture directly.
- `_frameGate` prevents capture callbacks and the encoder pacer from using disposed GPU resources.
- A stale callback includes the backend generation that produced it; requests from an older generation are ignored.
- Manual `Stop` sets `_stopRequested` before acquiring `_lifecycle`, cancelling pending retries and preventing automatic restart.
- Every switch log includes generation, previous backend, next backend, trigger, sample values, and quarantine expiry.

## Testing

Pure unit tests cover:

- Windows 11 defaults to WGC and Windows 10 to DDA;
- diagnostic override is deterministic;
- one bad sample does not switch;
- ten qualifying WGC samples switch to DDA;
- desktop/idle or encoder starvation does not count as WGC degradation;
- DDA transient recovery succeeds within the grace period without switching;
- DDA exceeding the grace period requests WGC;
- device loss rebuilds without blaming or quarantining the backend;
- quarantine and rolling switch limit prevent ping-pong;
- manual stop cancels pending recovery;
- capture restart never invokes replay-save behavior and retains compatible in-memory buffers;
- explicitly active continuous recording resumes as a new segment.

Integration verification builds the complete solution, runs all tests, and checks logs from controlled forced-backend runs. Hardware validation on Windows 11 exercises static desktop, cursor movement, borderless game, exclusive fullscreen/Alt+Tab, UAC secure desktop, display mode change, monitor sleep/wake, and sustained GPU load.

## Non-goals

- No visible backend selector or persistent backend preference.
- No simultaneous full-resolution WGC and DDA capture probe.
- No automatic clip saving, remuxing, or creation of a recovery video file.
- No promise that a single MP4 file can span a D3D-device replacement.
