# CS2 external-capture encoder contention — 2026-09-13

## Scope and safety boundary

CS2 remains external-only. The new selection policy accepts only `javaw.exe` plus the
Minecraft game identity, and the final injector eligibility gate explicitly denies
`cs2.exe`. No DLL is loaded into CS2 and no anti-cheat-facing behavior was added.

## Evidence from the current machine

Source: `C:\Users\Rizen\AppData\Local\Aura\logs\app-2026-09-13.log`, the CS2 WGC
session around 04:45–05:01. The log identifies `target cs2` and `WGC-monitor`.

A representative degraded minute at 04:54:42 reports:

- WGC arrival: 46.8 FPS (2809 received, 2784 accepted).
- Encoder output and MFT requests: 57.3 FPS.
- Duplicates: 744 per minute; queue drops: 76 per minute.
- Pacer blocked by a full queue: 201 times; queue peak: 33/33.
- Stage average/peak: capture copy 0.0/0.6 ms, NV12 0.2/6.8 ms,
  enqueue copy 0.0/1.1 ms, MFT input 0.1/18.1 ms, MFT output 0.0/0.6 ms.

Individual seconds show the visible failure mode more clearly: queue depth reaches
33/33 while output falls to 18–33 FPS, with 10–28 drops and 19–41 duplicates per
second. VRAM remains far below budget (roughly 478/7249 MiB). This points to transient
GPU/MFT scheduling contention and duplicate pressure, not a persistent VRAM leak and
not a reason to inject into CS2.

GPU engine utilization was not logged, so no utilization percentage is invented here.
A fresh post-install CS2 run should capture Task Manager or GPUView/PresentMon engine
load alongside the Aura log before tuning thresholds.

## Recommended separate CS2 change

Keep WGC/DDA external capture and make encoder admission explicitly information-aware:

1. Tag queued entries as real or duplicate; a duplicate must never evict a real frame.
2. Stop backfill and live pacing earlier when queue latency/occupancy rises, rather
   than waiting for 33/33 or a one-second MFT-rate window.
3. Collapse obsolete duplicates while retaining the newest real frame and monotonic
   CFR timestamps.
4. Use the existing adaptive quality preset only after real-frame priority has been
   enforced; otherwise lowering quality can hide a bad queue policy.
5. Validate with a dedicated replay test and a fresh CS2 run. Keep this work in a
   separate commit/branch from Minecraft capture.

Acceptance for that follow-up: no injection, queue does not remain full, real-frame
drops approach zero, encoded cadence remains playable, and CS2 frame-time P99 does not
regress.
