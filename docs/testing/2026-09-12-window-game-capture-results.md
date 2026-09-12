# Window game capture verification — 2026-09-13

## Build under test

- Commit: `b03b2e7f1f17ee6fe2fb0cfd47d84eb8b670bc5b`
- Branch: `main`
- Application: `1.1.79.0` (`1.1.79+b03b2e7f1f17ee6fe2fb0cfd47d84eb8b670bc5b`)
- OS: Windows 11 IoT Enterprise LTSC, `10.0.26100` (build 26100)
- GPUs: AMD Radeon Graphics, driver `32.0.11024.2`; NVIDIA GeForce RTX 3070, driver `32.0.16.1664`
- Installer built: `2026-09-13 02:10:25 +03:00`

## Automated verification

Executed from `E:\вин\ПРОЕКТЫ\InstantReplay`:

```text
dotnet test tests/InstantReplay.Tests/InstantReplay.Tests.csproj
Passed: 342, Failed: 0, Skipped: 0

dotnet build src/Aura/Aura.csproj -c Release
Warnings: 0, Errors: 0

git diff --check
No whitespace errors.

.\build_setup.ps1
Succeeded: app publish, identity package, payload, setup publish, final installer.
```

The focused recovery, capture-health, and persistence-boundary group passed 35/35 tests. The recovery policy never sets `SaveReplay`; a capture restart only resumes continuous recording when that recording was explicitly active before the restart.

## Artifacts and signatures

- Installer: `E:\вин\ПРОЕКТЫ\InstantReplay\dist\InstantReplaySetup.exe`
- Size: `307475086` bytes
- SHA-256: `17653D5F6411ACE195CC844D885AD6B36A181B794407C234FC489E3240DACBCD`
- Hash manifest: `E:\вин\ПРОЕКТЫ\InstantReplay\dist\InstantReplaySetup.exe.sha256`
- Detached ECDSA signature: `E:\вин\ПРОЕКТЫ\InstantReplay\dist\InstantReplaySetup.exe.sig`
- Detached signature verification against the public update key embedded in `UpdateVerification.cs`: **valid**
- Installer Authenticode status: `NotSigned` (the updater trusts the detached ECDSA signature above)
- Identity package: `E:\вин\ПРОЕКТЫ\InstantReplay\dist\Aura.Identity.msix`
- Identity package Authenticode status: **Valid**, signer `CN=Rizen1322`
- Identity package SHA-256: `985DA25ED6CF5C326C0C1EFF8702299FEC43530E6A1C08395D45E7C1793C023B`

## Hardware matrix

Status: **pending interactive Minecraft run**. Automated verification and packaging do not establish that Minecraft Java F11 fullscreen is fixed.

Run against the installer above and attach the relevant application-log interval:

- [ ] Desktop capture for 5 minutes.
- [ ] Minecraft Java F11 fullscreen for 10 minutes.
- [ ] Stationary cursor, moving cursor, inventory and pause menu: no corrupted square, correct hotspot and position.
- [ ] Three alt-tabs: the target episode closes and restores without desktop frames leaking into an active fullscreen episode.
- [ ] Exit and re-enter F11 fullscreen.
- [ ] Press Save around transitions: the resulting clip is playable and has stable geometry/cadence.
- [ ] Repeat without pressing Save: no video file is created.

Acceptance requires capture cadence near the configured FPS instead of duplicate-filled 5 FPS, no desktop frame inside an active fullscreen target episode, correct cursor output, and no DDA recreation storm. If `WGC-window` itself stalls or returns unusable Minecraft content, record `GameHookRequired` with the log evidence; do not fall back to monitor capture that flashes the desktop.
