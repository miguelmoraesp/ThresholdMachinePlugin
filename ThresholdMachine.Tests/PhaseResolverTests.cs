using System.Text.Json.Nodes;
using ThresholdMachine;
using ThresholdMachine.Threshold;
using Xunit;

namespace ThresholdMachine.Tests;

public class PhaseResolverTests
{
    // Report-relative fight start used by every test: 100_000 ms.
    private const long FightStart = 100_000;

    private static FightPhase Phase(string fallback = "") => new() { Name = "P", FallbackStart = fallback };

    [Fact]
    public void ParseMs_ParsesBracketStyleTimes()
    {
        Assert.Equal(270_000, PhaseResolver.ParseMs("4:30"));
        Assert.Equal(7_000, PhaseResolver.ParseMs("0:07"));
        Assert.Equal(0, PhaseResolver.ParseMs("0:00"));
        Assert.Equal(663_000, PhaseResolver.ParseMs("11:03"));
    }

    [Fact]
    public void FormatMs_PadsSecondsToTwoDigits()
    {
        Assert.Equal("4:30", PhaseResolver.FormatMs(270_000));
        Assert.Equal("0:00", PhaseResolver.FormatMs(0));
        Assert.Equal("1:06", PhaseResolver.FormatMs(66_000));
    }

    [Fact]
    public void ParseThenFormat_RoundTrips()
    {
        foreach (var ms in new[] { 0L, 7_000, 66_000, 270_000, 663_000 })
        {
            Assert.Equal(ms, PhaseResolver.ParseMs(PhaseResolver.FormatMs(ms)));
        }
    }

    [Fact]
    public void ParseTransitions_ConvertsOneBasedApiIdsToZeroBasedIndexes()
    {
        var node = JsonNode.Parse("""[{"id":1,"startTime":100000},{"id":3,"startTime":250000}]""");

        var observed = PhaseResolver.ParseTransitions(node);

        Assert.Equal(2, observed.Count);
        Assert.Equal(100_000, observed[0]);
        Assert.Equal(250_000, observed[2]);
    }

    [Fact]
    public void ParseTransitions_EmptyOrNullYieldsNoObservations()
    {
        Assert.Empty(PhaseResolver.ParseTransitions(null));
        Assert.Empty(PhaseResolver.ParseTransitions(JsonNode.Parse("[]")));
    }

    [Fact]
    public void FallbackStarts_DriveLiveWindows_WhenNothingObserved()
    {
        // P2 falls back at 4:30, P3 at 8:00; now is 3:20 into the pull.
        var phases = new List<FightPhase> { Phase(), Phase("4:30"), Phase("8:00") };
        var now = FightStart + 200_000;

        var resolution = PhaseResolver.Resolve(phases, new Dictionary<int, long>(), FightStart, now);

        Assert.False(resolution.Phases[0].StartObserved);
        Assert.Equal(FightStart, resolution.Phases[0].StartMs);
        Assert.Equal(FightStart + 270_000, resolution.Phases[0].EndMs);
        Assert.False(resolution.Phases[0].EndObserved);

        Assert.Equal(FightStart + 270_000, resolution.Phases[1].StartMs);
        Assert.Equal(FightStart + 480_000, resolution.Phases[1].EndMs);

        // Last phase is live: no next boundary, no fight end passed.
        Assert.Equal(FightStart + 480_000, resolution.Phases[2].StartMs);
        Assert.Null(resolution.Phases[2].EndMs);

        Assert.Equal(0, resolution.CurrentIndex);
    }

    [Fact]
    public void ObservedStarts_BeatFallbacks()
    {
        // Party transitioned at 4:22 against a 4:30 fallback.
        var phases = new List<FightPhase> { Phase(), Phase("4:30"), Phase("8:00") };
        var observed = new Dictionary<int, long> { [1] = FightStart + 262_000 };
        var now = FightStart + 300_000;

        var resolution = PhaseResolver.Resolve(phases, observed, FightStart, now);

        Assert.True(resolution.Phases[0].EndObserved);
        Assert.Equal(FightStart + 262_000, resolution.Phases[0].EndMs);
        Assert.Equal(FightStart + 262_000, resolution.Phases[1].StartMs);
        Assert.True(resolution.Phases[1].StartObserved);
        Assert.False(resolution.Phases[1].EndObserved);
        Assert.Equal(FightStart + 480_000, resolution.Phases[1].EndMs);

        Assert.Equal(1, resolution.CurrentIndex);
    }

    [Fact]
    public void CurrentPhase_SelectsHighestStartedPhase()
    {
        var phases = new List<FightPhase> { Phase(), Phase("4:30"), Phase("8:00") };

        var before = PhaseResolver.Resolve(phases, new Dictionary<int, long>(), FightStart, FightStart + 260_000);
        Assert.Equal(0, before.CurrentIndex);

        var after = PhaseResolver.Resolve(phases, new Dictionary<int, long>(), FightStart, FightStart + 290_000);
        Assert.Equal(1, after.CurrentIndex);
    }

    [Fact]
    public void EndObserved_Flips_WhenNextTransitionArrives()
    {
        var phases = new List<FightPhase> { Phase(), Phase("4:30"), Phase("8:00") };
        var now = FightStart + 300_000;

        var before = PhaseResolver.Resolve(phases, new Dictionary<int, long>(), FightStart, now);
        Assert.False(before.Phases[0].EndObserved);

        var after = PhaseResolver.Resolve(
            phases, new Dictionary<int, long> { [1] = FightStart + 262_000 }, FightStart, now);
        Assert.True(after.Phases[0].EndObserved);
    }

    [Fact]
    public void MissingFallbacks_LeaveLaterPhasesUnresolvable()
    {
        var phases = new List<FightPhase> { Phase(), Phase(), Phase("8:00") };
        var now = FightStart + 300_000;

        var resolution = PhaseResolver.Resolve(phases, new Dictionary<int, long>(), FightStart, now);

        // P1 starts at fight start but its end is unknown (P2 has no boundary yet).
        Assert.Equal(FightStart, resolution.Phases[0].StartMs);
        Assert.Null(resolution.Phases[0].EndMs);

        // P2 has neither observation nor fallback and its predecessor's end is unknown.
        Assert.Null(resolution.Phases[1].StartMs);

        // But its end is known: P3's fallback start is the next boundary.
        Assert.Equal(FightStart + 480_000, resolution.Phases[1].EndMs);

        // P3's fallback start is known even though P2 never resolved.
        Assert.Equal(FightStart + 480_000, resolution.Phases[2].StartMs);
        Assert.Null(resolution.Phases[2].EndMs);

        Assert.Equal(0, resolution.CurrentIndex);
    }

    [Fact]
    public void ExtraObservedTransition_EndsLastConfiguredPhase()
    {
        // Party pushed into a transition the config has no phase for (api id 2 -> index 1).
        var phases = new List<FightPhase> { Phase(), Phase() };
        var observed = new Dictionary<int, long> { [1] = FightStart + 262_000 };
        var now = FightStart + 300_000;

        var resolution = PhaseResolver.Resolve(phases, observed, FightStart, now);

        Assert.True(resolution.Phases[0].EndObserved);
        Assert.Equal(FightStart + 262_000, resolution.Phases[0].EndMs);
        Assert.Equal(FightStart + 262_000, resolution.Phases[1].StartMs);
        Assert.Null(resolution.Phases[1].EndMs);
    }

    [Fact]
    public void FightEndMs_ResolvesLastPhase_ForDelayedPass()
    {
        var phases = new List<FightPhase> { Phase(), Phase("4:30"), Phase("8:00") };
        var fightEnd = FightStart + 600_000;

        var resolution = PhaseResolver.Resolve(
            phases, new Dictionary<int, long>(), FightStart, fightEnd, fightEnd);

        // Earlier phases still resolve from fallbacks; the last phase ends at the fight's end.
        Assert.Equal(FightStart + 270_000, resolution.Phases[0].EndMs);
        Assert.Equal(FightStart + 480_000, resolution.Phases[1].EndMs);
        Assert.Equal(fightEnd, resolution.Phases[2].EndMs);
        Assert.False(resolution.Phases[2].EndObserved);
    }

    [Fact]
    public void DelayedPass_FallbackWindowsCarryEverything_WhenNoTransitionWasEverObserved()
    {
        var phases = new List<FightPhase> { Phase("0:00"), Phase("4:30"), Phase("8:00") };
        var fightEnd = FightStart + 600_000;

        var resolution = PhaseResolver.Resolve(
            phases, new Dictionary<int, long>(), FightStart, fightEnd, fightEnd);

        Assert.All(resolution.Phases, p => Assert.NotNull(p.EndMs));
        Assert.All(resolution.Phases, p => Assert.NotNull(p.StartMs));
    }
}
