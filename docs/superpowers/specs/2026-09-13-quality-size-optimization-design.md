# Aura Recording Quality And Distribution Size Design

## Goal

Reduce the installed and downloaded footprint without removing codecs or making capture less reliable, and prevent quality presets from selecting an undersized bitrate for the active codec.

## Evidence

- The installed directory is 735 MB: the application payload is 442 MB and the copied full installer used as `Uninstall.exe` is 293 MB.
- `libvlc` is 279 MB: x64 is 101 MB, x86 is 97 MB, and ARM64 is 81 MB. Aura is published only for `win-x64`, so x86 and ARM64 are unreachable payload.
- The latest local recording log at 2560x1440, 60 fps, HEVC 30 Mbps holds 60.0 encoded fps with zero real-frame drops and the maximum encoder quality/speed setting accepted by the NVIDIA MFT. Capture, color metadata, scaling, and rate control are healthy.
- The four one-click presets always use HEVC bitrates even after H.264 or AV1 is selected. H.264 therefore receives too little bitrate from the same preset.
- Screenshot capture claims to wait for a current live frame, but leases the cached GPU frame immediately. The cached frame also has no monitor/window scope metadata, so a routed game-window frame can be mistaken for a desktop screenshot.

## Design

1. Every `win-x64` publish keeps the complete x64 LibVLC tree and removes only the mutually incompatible `win-x86` and `win-arm64` trees. A build assertion rejects foreign architectures and an unexpectedly large payload.
2. Build a small self-contained, trimmed, single-file `AuraUninstall.exe`. It uses native Windows confirmation UI, removes shortcuts, registry entries, scheduled tasks, and the sparse identity package, then deletes the installation after its process exits. The installer registers this payload executable instead of copying itself and removes the legacy oversized root `Uninstall.exe` during upgrades.
3. Move bitrate recommendations into a pure `RecordingQualityPolicy`. Presets ask the policy for the active codec. Existing custom values and saved settings remain untouched. The default for new installations becomes high-quality 1080p60 HEVC at 18 Mbps instead of the wasteful 35 Mbps.
4. Keep the current CBR, two-second GOP, BT.709 limited-range metadata, GPU scaling, and adaptive quality/speed logic unchanged. The evidence shows those settings are stable; changing them to latency-tolerant VBR/B-frame encoding would risk reintroducing encoder stalls.
5. For screenshots, wait briefly for a frame published after the request. Fall back to the cached frame only on a static desktop, and use a live frame only when its scope is the complete monitor. A game-window frame falls back to a one-shot monitor capture.

## Success Criteria

- Managed tests and native capture tests remain green.
- A real release publish contains `libvlc/win-x64`, contains no foreign LibVLC architecture, and includes `AuraUninstall.exe` below 30 MB.
- The app payload is below 300 MB and the final installer is materially smaller than 252 MB.
- One-click H.264 presets receive 1.4x the HEVC bitrate; AV1 presets receive 0.8x; values stay within 4..80 Mbps.
- Installing an update removes the legacy copied installer and registers the lightweight uninstaller.
- A screenshot taken after moving or covering a window waits for the next desktop frame and never substitutes a game-window frame.
