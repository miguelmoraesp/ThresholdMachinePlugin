using System.Collections.Generic;
using System.Text.Json.Nodes;
using ThresholdMachine;

namespace ThresholdMachine.Threshold;

public sealed class ResolvedPhase
{
    public required int Index { get; init; }
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public bool StartObserved { get; init; }
    public bool EndObserved { get; init; }
}

public sealed class PhaseResolution
{
    public required IReadOnlyList<ResolvedPhase> Phases { get; init; }
    public int? CurrentIndex { get; init; }
}

public static class PhaseResolver
{
    /// <summary>
    /// Resolves per-phase windows. <paramref name="observedStarts"/> keys are 0-based absolute
    /// phase indexes (converted from the API's 1-based ids by <see cref="ParseTransitions"/>);
    /// values are report-relative start ms. Observed starts always beat configured fallbacks.
    /// </summary>
    public static PhaseResolution Resolve(
        IReadOnlyList<FightPhase> phases,
        IReadOnlyDictionary<int, long> observedStarts,
        long fightStartMs,
        long nowMs,
        long? fightEndMs = null)
    {
        var resolved = new ResolvedPhase[phases.Count];
        for (var i = 0; i < phases.Count; i++)
        {
            var startObserved = observedStarts.TryGetValue(i, out var observedStart);
            long? start = startObserved ? observedStart : ParseOrNull(phases[i].FallbackStart, fightStartMs);
            start ??= i == 0 ? fightStartMs : null;

            // End = next phase's boundary, observed beating fallback. The final phase's end
            // only becomes known through fightEndMs (delayed pull-end pass).
            var endObserved = observedStarts.TryGetValue(i + 1, out var observedEnd);
            long? end = endObserved
                ? observedEnd
                : i + 1 < phases.Count ? ParseOrNull(phases[i + 1].FallbackStart, fightStartMs) : null;
            end ??= i == phases.Count - 1 ? fightEndMs : null;

            resolved[i] = new ResolvedPhase
            {
                Index = i,
                StartMs = start,
                EndMs = end,
                StartObserved = startObserved,
                EndObserved = endObserved,
            };
        }

        int? current = null;
        for (var i = 0; i < resolved.Length; i++)
        {
            if (resolved[i].StartMs is { } startMs && startMs <= nowMs)
            {
                current = i;
            }
        }

        return new PhaseResolution { Phases = resolved, CurrentIndex = current };
    }

    /// <summary>Converts a fight's phaseTransitions JSON into a 0-based index → start ms map.</summary>
    public static IReadOnlyDictionary<int, long> ParseTransitions(JsonNode? phaseTransitions)
    {
        var observed = new Dictionary<int, long>();
        if (phaseTransitions is not JsonArray array)
        {
            return observed;
        }

        foreach (var transition in array)
        {
            var id = transition?["id"]?.GetValue<int>();
            var startTime = transition?["startTime"]?.GetValue<long>();
            if (id == null || startTime == null)
            {
                continue;
            }

            // API transition ids are 1-indexed absolute; everything internal is 0-based.
            observed[id.Value - 1] = startTime.Value;
        }

        return observed;
    }

    public static long ParseMs(string time)
    {
        var parts = time.Split(':');
        var minutes = int.Parse(parts[0]);
        var seconds = int.Parse(parts[1]);
        return (minutes * 60 + seconds) * 1000L;
    }

    public static string FormatMs(long ms)
    {
        var totalSeconds = ms / 1000;
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    private static long? ParseOrNull(string fallback, long fightStartMs)
    {
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return null;
        }

        return fightStartMs + ParseMs(fallback);
    }
}
