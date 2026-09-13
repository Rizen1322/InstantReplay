# Minecraft OpenGL game capture verification — 2026-09-13

## Automated verification

Executed from the clean `codex/minecraft-opengl-game-capture` worktree at commit
`88efd4c` before this evidence file was added.

| Check | Command | Result |
|---|---|---|
| Managed suite | `dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj -c Release` | 426 passed, 0 failed, 0 skipped (combined Minecraft + external pacing tree) |
| Hook lifetime | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/run_hook_lifecycle_test.ps1` | 20/20 passed |
| OpenGL PBO frames | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/run_gl_frame_test.ps1` | lifecycle 1/1; numbered/oriented frames passed at 30 and 60 FPS |
| Package integrity | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/packaging/game_capture_hook_package_test.ps1` | x64 DLL, manifest, embedded digest, publish and ZIP checks passed |
| Complete installer | `powershell -NoProfile -ExecutionPolicy Bypass -File ./build_setup.ps1` | all 7 stages passed; setup produced |

Native hook SHA-256 in the native output, `dist/app_publish`, and the extracted
`dist/payload.zip` is identical:

`bc7e9cdc48d4d6ab830357be5054d68626636aae9f12e97042a3263efe45d008`

Installer:

- Path: `dist/InstantReplaySetup.exe`
- Size: approximately 252 MiB
- SHA-256: `88334f9296a26975f34d99725effb4233948d95736b3caf664bb123050e9731f`

## Proved invariants

- Only a verified `javaw.exe` target named Minecraft can select or pass the hook
  eligibility gate. `cs2.exe` has an explicit permanent denial.
- Foreground Minecraft moves the hybrid route to `GamePending`; monitor frames are
  rejected until the first frame carrying the current target revision and route epoch.
- Alt-Tab immediately advances the route to monitor without rebuilding the capture
  provider or encoder. Returning advances to a new game epoch, so an in-flight desktop
  frame cannot enter the foreground Minecraft episode.
- Missing native heartbeat, native failure, or three seconds without OpenGL frame
  progress triggers bounded recovery. Monitor WGC activity cannot hide a dead hook.
- Every automatic provider/focus/injection/recovery transition uses capture-restart
  policy with `SaveReplay = false`. No mux, file-name, or storage call is authorized.

## Real-game matrix still required

No `javaw` process was running during this verification, so the real Minecraft Java
F11 acceptance matrix was not claimed as passed. The fixture validates the protocol,
hook lifecycle, PBO readback, orientation, resize and 30/60 FPS sequencing, but it is
not a substitute for the NVIDIA driver plus a real Minecraft render loop.

Run the built installer, then verify one ten-minute session:

1. Enter true F11 fullscreen and confirm logs transition from `OpenGL-game-pending`
   to `OpenGL-game-live`; useful FPS should remain near the configured target and no
   freeze should exceed 250 ms.
2. Leave the cursor stationary, then move it across the screen; verify no colored
   square, stale cursor, double cursor, or offset hotspot.
3. Alt-Tab away and back three times. The clip must show the other window/desktop
   while away and Minecraft immediately after return, with no stale desktop frame.
4. Exit and re-enter F11, reload the world, resize once, restart Minecraft, and then
   close Minecraft and Aura. Confirm route epochs/process revisions advance and both
   processes shut down cleanly.
5. Before pressing Save or Record, compare the video folder: no new video file may
   exist. Press Save once and verify the resulting clip plays. Explicit continuous
   recording must behave exactly as before.
6. Compare Minecraft frame-time P99 with capture disabled and enabled and preserve
   the matching hook counter lines (`issued/mapped/published/rejected/uploaded`).

The completion gate remains open until this real-game matrix and frame-time P99 are
recorded.
