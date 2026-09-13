# CS2 external capture real-frame priority plan

**Goal:** Prevent encoder duplicate bursts from filling the queue and evicting useful
WGC/DDA frames during transient GPU/MFT contention, while keeping CS2 strictly external-only.

**Architecture:** Add a pure queue-admission policy, tag queued encoder entries as real or
duplicate, reject duplicates early under pressure, and preferentially remove an obsolete
duplicate when a real frame arrives to a full queue. Preserve monotonic timestamps and the
existing bounded texture pool.

## Task 1: Specify pressure behavior

- Add focused tests for duplicate headroom, encoder-behind rejection, and real-frame eviction.
- Run them red before implementation.

## Task 2: Integrate information-aware admission

- Add `EncoderQueueAdmissionPolicy`.
- Tag encoder queue entries.
- Make duplicate enqueue non-destructive and make real enqueue prefer evicting a duplicate.
- Limit pacer catch-up to one duplicate per pass and skip backfill when pressure rejects it.
- Expose separate dropped-real and suppressed-duplicate counters in diagnostics.

## Task 3: Verify

- Run focused tests, the full managed suite, and an x64 Release application build.
- Record that CS2 remains outside all hook eligibility and backend selection paths.
- Keep the change in this separate branch/commit.
