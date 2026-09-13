# Aura Quality And Size Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut Aura's installed footprint to roughly 300 MB while preserving full x64 playback support and making recording presets codec-aware.

**Architecture:** The existing x64 application remains self-contained. Publish-time architecture pruning removes unreachable native binaries; a separate trimmed single-file uninstaller replaces the embedded copy of the full installer; bitrate recommendations move from WPF code to tested pure policy.

**Tech Stack:** .NET 10, WPF, PowerShell packaging, xUnit, Windows registry and Win32 MessageBox interop.

**Spec:** `docs/superpowers/specs/2026-09-13-quality-size-optimization-design.md`

## Global Constraints

- Windows 10 19041 and Windows 11 remain supported.
- The application remains self-contained and x64-only.
- Full x64 LibVLC stays packaged so H.264, HEVC, and AV1 clips remain previewable without optional Windows codecs.
- Capture backend, encoder rate control, color conversion, and save-on-hotkey behavior do not change.

---

### Task 1: Codec-aware bitrate policy

**Files:**
- Create: `src/Aura/Core/Encoding/RecordingQualityPolicy.cs`
- Create: `tests/InstantReplay.Tests/RecordingQualityPolicyTests.cs`
- Modify: `tests/InstantReplay.Tests/InstantReplay.Tests.csproj`
- Modify: `src/Aura/Views/CapturePage.xaml.cs`
- Modify: `src/Aura/Core/Settings/AppSettings.cs`

**Interfaces:**
- Produces: `RecordingQualityPolicy.BitrateMbps(RecordingQualityTier tier, int height, int fps, VideoCodec codec)`.

- [x] Write table-driven xUnit tests with literal expected bitrates for HEVC, H.264, AV1, 30/60/120 fps, and the 4..80 bounds.
- [x] Run the focused tests and verify compilation fails because the policy does not exist.
- [x] Implement the pure policy and link it into the test project.
- [x] Make preset buttons request the normal tier for the selected codec and refresh their displayed bitrate after codec changes.
- [x] Set the new-install 1080p60 HEVC default to the policy's high tier value, 18 Mbps.
- [x] Run focused tests and the full managed suite.

### Task 2: x64-only publish

**Files:**
- Modify: `tests/packaging/game_capture_hook_package_test.ps1`
- Modify: `src/Aura/Aura.csproj`
- Modify: `build_setup.ps1`

**Interfaces:**
- Produces: an x64 publish containing `libvlc/win-x64` and no `win-x86` or `win-arm64` directories.

- [x] Extend the package integration test to reject foreign LibVLC directories and run it to observe failure on the current package.
- [x] Add an after-publish MSBuild target that removes only foreign LibVLC architecture directories.
- [x] Add release-build assertions for x64 LibVLC presence, foreign-directory absence, and a 300 MB app-payload budget.
- [x] Re-run the package integration test.

### Task 3: Lightweight uninstaller

**Files:**
- Create: `src/AuraUninstall/AuraUninstall.csproj`
- Create: `src/AuraUninstall/Program.cs`
- Modify: `src/InstantReplaySetup/MainWindow.xaml.cs`
- Modify: `build_setup.ps1`
- Modify: `InstantReplay.sln`
- Create: `tests/packaging/distribution_size_test.ps1`

**Interfaces:**
- Produces: self-contained `AuraUninstall.exe`, invoked from Add/Remove Programs without installer payload.

- [x] Add the distribution integration test requiring an x64 uninstaller below 30 MB, x64-only LibVLC, and an app payload below 300 MB; verify it fails against the current publish.
- [x] Implement the WinExe uninstaller with Win32 confirmation and delayed self-removal.
- [x] Publish it into `app_publish`, fail the build if it exceeds 30 MB, and register it from the installer.
- [x] Remove legacy root `Uninstall.exe` after a successful update.
- [x] Run distribution and managed tests.

### Task 4: Fresh desktop screenshots

**Files:**
- Modify: `src/Aura/Core/Capture/CaptureFrameBroker.cs`
- Modify: `src/Aura/Core/Capture/GpuCaptureFrameBroker.cs`
- Create: `src/Aura/Core/Capture/ScreenshotFramePolicy.cs`
- Modify: `src/Aura/Core/Engine/ReplayEngine.cs`
- Modify: `src/Aura/Core/Capture/ScreenshotService.cs`
- Modify: `tests/InstantReplay.Tests/CaptureFrameBrokerTests.cs`
- Create: `tests/InstantReplay.Tests/ScreenshotFramePolicyTests.cs`

**Interfaces:**
- Produces: bounded next-frame waiting with cached monitor fallback.

- [x] Reproduce the missing wait with focused tests.
- [x] Wake screenshot readers when a new broker frame is published.
- [x] Store frame scope with the GPU slot and reject game-window frames for desktop screenshots.
- [x] Keep all synchronous GPU readback off the UI thread.
- [x] Run focused tests and a release application build.

### Task 5: Release verification

**Files:**
- Modify only files required by verification findings.

**Interfaces:**
- Produces: `dist/InstantReplaySetup.exe` and measured before/after sizes.

- [x] Build the complete setup.
- [x] Verify managed, native lifecycle, OpenGL frame, and packaging tests.
- [x] Inspect the final payload architecture, PE machine types, hashes, and byte counts.
- [ ] Commit the verified implementation on `codex/quality-size-optimization`.
