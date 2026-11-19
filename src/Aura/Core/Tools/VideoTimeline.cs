namespace Aura.Core.Tools;

public static class VideoTimeline
{
    public sealed record MovePlan(VideoSegment[] Parts, int TargetIndex, double? SnapGuide);

    public static (int Index, double SourceSeconds) Locate(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        seconds = Math.Max(0, seconds);
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < offset + length || i == parts.Count - 1)
                return (i, parts[i].Start.TotalSeconds + Math.Clamp(seconds - offset, 0, length));
            offset += length;
        }
        return (-1, 0);
    }

    public static double Offset(IReadOnlyList<VideoSegment> parts, int index)
    {
        double offset = 0;
        for (int i = 0; i < Math.Clamp(index, 0, parts.Count); i++)
            offset += parts[i].Gap.TotalSeconds + parts[i].Duration.TotalSeconds;
        if (index >= 0 && index < parts.Count) offset += parts[index].Gap.TotalSeconds;
        return offset;
    }

    public static int Insertion(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds < offset + length / 2) return i;
            offset += length;
        }
        return parts.Count;
    }

    /// <summary>Индекс клипа под курсором; -1 в пустом промежутке перед клипом.</summary>
    public static int ClipAt(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            double length = parts[i].Duration.TotalSeconds;
            if (seconds >= offset && seconds < offset + length) return i;
            offset += length;
        }
        return -1;
    }

    public static (int Index, double Start, double End) GapAt(IReadOnlyList<VideoSegment> parts, double seconds)
    {
        seconds = Math.Max(0, seconds);
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            double gap = parts[i].Gap.TotalSeconds;
            if (gap > 0 && seconds >= offset - 1e-6 && seconds < offset + gap - 1e-4)
                return (i, offset, offset + gap);
            offset += gap + parts[i].Duration.TotalSeconds;
        }
        return (-1, 0, 0);
    }

    public static (double Value, double? Guide) SnapEdge(double value, IReadOnlyList<double> targets, double tolerance, double min = 0, double max = double.PositiveInfinity)
    {
        if (tolerance <= 0 || targets.Count == 0) return (Math.Clamp(value, min, max), null);
        double bestValue = Math.Clamp(value, min, max);
        double? guide = null;
        double bestDist = tolerance + 1e-9;
        for (int i = 0; i < targets.Count; i++)
        {
            double target = targets[i];
            if (target < min - 1e-6 || target > max + 1e-6) continue;
            double dist = Math.Abs(value - target);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestValue = Math.Clamp(target, min, max);
                guide = target;
            }
        }
        return (bestValue, guide);
    }

    public static (double Start, double? Guide) SnapRange(double start, double duration, IReadOnlyList<double> targets, double tolerance, double minStart = 0, double maxStart = double.PositiveInfinity)
    {
        if (tolerance <= 0 || targets.Count == 0) return (Math.Clamp(start, minStart, maxStart), null);
        double bestStart = Math.Clamp(start, minStart, maxStart);
        double? guide = null;
        double bestDist = tolerance + 1e-9;
        for (int i = 0; i < targets.Count; i++)
        {
            double target = targets[i];
            double leftDist = Math.Abs(start - target);
            if (leftDist < bestDist && target >= minStart - 1e-6 && target <= maxStart + 1e-6)
            {
                bestDist = leftDist;
                bestStart = Math.Clamp(target, minStart, maxStart);
                guide = target;
            }
            double rightStart = target - duration;
            double rightDist = Math.Abs(start + duration - target);
            if (rightDist < bestDist && rightStart >= minStart - 1e-6 && rightStart <= maxStart + 1e-6)
            {
                bestDist = rightDist;
                bestStart = Math.Clamp(rightStart, minStart, maxStart);
                guide = target;
            }
        }
        return (bestStart, guide);
    }

    public static MovePlan PlanMove(IReadOnlyList<VideoSegment> parts, int dragIndex, double deltaSeconds, double pointerSeconds,
        double fps, bool snapToFrames, IReadOnlyList<double>? snapTargets, double snapTolerance)
    {
        if (dragIndex < 0 || dragIndex >= parts.Count) return new(parts.ToArray(), dragIndex, null);
        fps = double.IsFinite(fps) && fps > 0 ? fps : 30;
        var origStarts = new double[parts.Count];
        var origEnds = new double[parts.Count];
        double offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            offset += parts[i].Gap.TotalSeconds;
            origStarts[i] = offset;
            offset += parts[i].Duration.TotalSeconds;
            origEnds[i] = offset;
        }
        var moving = parts[dragIndex];
        double origStart = origStarts[dragIndex];
        double dragDur = moving.Duration.TotalSeconds;
        int targetIndex = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i != dragIndex && (origStarts[i] + origEnds[i]) * 0.5 < pointerSeconds) targetIndex++;
        }
        double rawStart = Math.Max(0, origStart + deltaSeconds);

        if (targetIndex == dragIndex)
        {
            double minStart = dragIndex > 0 ? origEnds[dragIndex - 1] : 0;
            double maxStart = dragIndex + 1 < parts.Count ? Math.Max(minStart, origStarts[dragIndex + 1] - dragDur) : double.PositiveInfinity;
            var localTargets = new List<double>((snapTargets?.Count ?? 0) + 2);
            if (snapTargets is not null) localTargets.AddRange(snapTargets);
            localTargets.Add(minStart);
            if (dragIndex + 1 < parts.Count) localTargets.Add(origStarts[dragIndex + 1]);
            double finalStart = ApplySnapAndFrame(rawStart, origStart, dragDur, minStart, maxStart, fps, snapToFrames, localTargets, snapTolerance, out double? guide);
            var result = parts.ToArray();
            double newGap = finalStart - minStart;
            if (newGap < 1e-6) newGap = 0;
            result[dragIndex] = ShiftRelinkedAudio(moving with { Gap = TimeSpan.FromSeconds(newGap) }, finalStart - origStart);
            if (dragIndex + 1 < parts.Count)
            {
                double nextGap = origStarts[dragIndex + 1] - (finalStart + dragDur);
                if (nextGap < 1e-6) nextGap = 0;
                result[dragIndex + 1] = result[dragIndex + 1] with { Gap = TimeSpan.FromSeconds(nextGap) };
            }
            return new(result, targetIndex, guide);
        }

        var others = new List<VideoSegment>(parts.Count - 1);
        var otherOldStarts = new List<double>(parts.Count - 1);
        for (int i = 0; i < parts.Count; i++)
        {
            if (i == dragIndex) continue;
            others.Add(parts[i]);
            otherOldStarts.Add(origStarts[i]);
        }
        double prevEnd = 0;
        for (int i = 0; i < targetIndex; i++)
            prevEnd += others[i].Gap.TotalSeconds + others[i].Duration.TotalSeconds;
        double slotMin = prevEnd;
        double slotMax = double.PositiveInfinity;
        bool preserveNextGap = false;
        double existingGap = 0;
        if (targetIndex < others.Count)
        {
            existingGap = others[targetIndex].Gap.TotalSeconds;
            if (existingGap >= dragDur - 1e-6)
            {
                slotMax = prevEnd + Math.Max(0, existingGap - dragDur);
                preserveNextGap = true;
            }
            else
            {
                slotMax = prevEnd;
            }
        }
        var reorderTargets = new List<double>((snapTargets?.Count ?? 0) + 2);
        if (snapTargets is not null) reorderTargets.AddRange(snapTargets);
        reorderTargets.Add(slotMin);
        if (preserveNextGap) reorderTargets.Add(prevEnd + existingGap);
        double placedStart = ApplySnapAndFrame(rawStart, origStart, dragDur, slotMin, slotMax, fps, snapToFrames, reorderTargets, snapTolerance, out double? reorderGuide);
        double movingGap = placedStart - slotMin;
        if (movingGap < 1e-6) movingGap = 0;
        var assembled = new VideoSegment[parts.Count];
        int src = 0;
        for (int i = 0; i < assembled.Length; i++)
        {
            if (i == targetIndex) assembled[i] = moving with { Gap = TimeSpan.FromSeconds(movingGap) };
            else assembled[i] = others[src++];
        }
        if (targetIndex + 1 < assembled.Length)
        {
            double nextGap = preserveNextGap ? Math.Max(0, prevEnd + existingGap - (placedStart + dragDur)) : 0;
            if (nextGap < 1e-6) nextGap = 0;
            assembled[targetIndex + 1] = assembled[targetIndex + 1] with { Gap = TimeSpan.FromSeconds(nextGap) };
        }
        double runOffset = 0;
        int otherIdx = 0;
        for (int i = 0; i < assembled.Length; i++)
        {
            double oldStart = i == targetIndex ? origStart : otherOldStarts[otherIdx++];
            runOffset += assembled[i].Gap.TotalSeconds;
            assembled[i] = ShiftRelinkedAudio(assembled[i], runOffset - oldStart);
            runOffset += assembled[i].Duration.TotalSeconds;
        }
        return new(assembled, targetIndex, reorderGuide);
    }

    private static double ApplySnapAndFrame(double rawStart, double origStart, double duration, double minStart, double maxStart,
        double fps, bool snapToFrames, IReadOnlyList<double>? targets, double snapTolerance, out double? guide)
    {
        if (targets is { Count: > 0 } && snapTolerance > 0)
        {
            var (snapped, snappedGuide) = SnapRange(rawStart, duration, targets, snapTolerance, minStart, maxStart);
            if (snappedGuide is not null) { guide = snappedGuide; return snapped; }
        }
        guide = null;
        double value = snapToFrames ? origStart + Math.Round((rawStart - origStart) * fps) / fps : rawStart;
        return Math.Clamp(value, minStart, maxStart);
    }

    private static VideoSegment ShiftRelinkedAudio(VideoSegment part, double deltaSeconds)
    {
        if (!part.AudioLinked || !part.AudioDetached || Math.Abs(deltaSeconds) <= 1e-6 || part.AudioEvents.Count == 0)
            return part;
        bool anyChanged = false;
        var events = new AudioEvent[part.AudioEvents.Count];
        for (int i = 0; i < part.AudioEvents.Count; i++)
        {
            var ev = part.AudioEvents[i];
            if (ev.Position is TimeSpan at && (ev.SourcePath is null || string.Equals(ev.SourcePath, part.Path, StringComparison.OrdinalIgnoreCase)))
            {
                events[i] = ev with { Position = TimeSpan.FromSeconds(Math.Max(0, at.TotalSeconds + deltaSeconds)) };
                anyChanged = true;
            }
            else events[i] = ev;
        }
        return anyChanged ? part with { AudioEvents = events } : part;
    }

    public static VideoSegment Trim(VideoSegment part, bool left, double delta, double sourceDuration, double fps, bool snap = true)
    {
        fps = double.IsFinite(fps) && fps > 0 ? fps : 30;
        double minimum = Math.Max(0.05, 1 / fps);
        double Snap(double value) => snap ? Math.Round(value * fps) / fps : value;
        double maxLeft = Math.Max(0, part.End.TotalSeconds - minimum);
        double maxRight = Math.Max(part.Start.TotalSeconds + minimum, Math.Max(sourceDuration, part.End.TotalSeconds));
        return left
            ? part with { Start = TimeSpan.FromSeconds(Math.Clamp(Snap(part.Start.TotalSeconds + delta), 0, maxLeft)) }
            : part with { End = TimeSpan.FromSeconds(Math.Clamp(Snap(part.End.TotalSeconds + delta), part.Start.TotalSeconds + minimum, maxRight)) };
    }
}
