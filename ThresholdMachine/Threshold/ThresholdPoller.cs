using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace ThresholdMachine.Threshold;

public sealed class ReportSnapshot
{
    public bool InProgress { get; init; }
    public List<PlayerData> Players { get; init; } = new();

    /// <summary>Phase context; null in bracket mode or when the current phase is unresolvable.</summary>
    public PhaseContext? Phase { get; init; }
}

public sealed class PhaseContext
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required long StartMs { get; init; }
    public long? EndMs { get; init; }
    public required long FightStartMs { get; init; }
    public bool StartObserved { get; init; }
    public bool EndObserved { get; init; }
}

public sealed class PlayerData
{
    public string Name { get; init; } = "";
    public string Job { get; init; } = "";

    public double totalRDPS { get; init; }

    public double RDPS { get; init; }
}

public class ThresholdPoller(Configuration configuration, FightThresholdManager manager)
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private string accessToken = "";
    private DateTime tokenExpiration = DateTime.MinValue;

    private HttpClient httpClient = new();
    private DateTime? startTime;
    private Task? pollerTask;
    public KillTimeBracket? LastBracket { get; set; }

    private CancellationTokenSource? cancellationTokenSource;
    private CancellationTokenSource? delayedPassCts;
    private readonly HashSet<int> announcedPhases = new();
    private int? lastFightId;

    public ReportSnapshot? ReportSnapshot { get; set; }
    public ThresholdVerdict? ThresholdVerdict { get; set; }
    public FightPhase? LastPhase { get; set; }
    public PhaseContext? LastPhaseContext { get; set; }

    public PollerState State = PollerState.None;
    public string? TimeInCombat;

    public void Start()
    {
        SetState(PollerState.Polling);
        startTime = DateTime.Now;
        cancellationTokenSource = new CancellationTokenSource();
        delayedPassCts?.Cancel();
        delayedPassCts = null;
        ReportSnapshot = new();
        LastBracket = null;
        LastPhase = null;
        LastPhaseContext = null;
        announcedPhases.Clear();
        lastFightId = null;
        pollerTask = Task.Run(ExecutePollAsync, cancellationTokenSource.Token);
    }

    private async Task ExecutePollAsync()
    {
        while (State == PollerState.Polling && !cancellationTokenSource!.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (startTime == null)
            {
                return;
            }

            var fight = manager.GetCurrentFight();
            if (fight == null)
            {
                return;
            }

            var currentTime = DateTime.Now.Ticks - startTime.Value.Ticks;
            var duration = TimeSpan.FromTicks(currentTime);
            TimeInCombat = duration.ToString("m\\:ss");

            if (fight.UsePhases)
            {
                LastBracket = null;
                await PollData();
                continue;
            }

            var bracket = manager.GetBracket(fight, TimeInCombat);
            if (bracket == null)
            {
                continue;
            }

            LastBracket = bracket;
            await PollData();
        }
    }

    public Task PollData() => Task.Run(async () =>
    {
        try
        {
            Plugin.Log.Debug("Fetching data");
            await EnsureTokenAsync();
            var snapshot = await FetchCurrentPullData();
            ReportSnapshot = snapshot;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "[FFLogsPlugin] Fetch failed");
            ReportSnapshot = null;
        }
    });

    private async Task<ReportSnapshot> FetchCurrentPullData()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(configuration.ReportCode))
            throw new InvalidOperationException("Report code not set. Open ⚙ Config.");

        var current = await FetchCurrentFightNode();

        var fightId = current["id"]!.GetValue<int>();
        var fightStart = current["startTime"]!.GetValue<long>();
        var inProgress = current["inProgress"]!.GetValue<bool>();
        lastFightId = fightId;

        var fight = manager.GetCurrentFight();
        var phases = fight is { UsePhases: true } ? fight.Phases : null;
        if (phases is { Count: > 0 })
        {
            return await FetchPhaseData(phases, current, fightId, fightStart, inProgress);
        }

        var bracket = LastBracket;
        if (LastBracket == null)
        {
            bracket = manager.GetCurrentFight()?.KillTimeBrackets.First();
        }

        if (bracket == null)
        {
            throw new Exception("Bracket not found");
        }

        var snapshot = await FetchTable(fightId, fightStart, fightStart + ParseBracketToMs(bracket.Bracket), inProgress);

        var threshold = new ThresholdVerdict(bracket.Bracket, bracket.Thresholds, snapshot, configuration);
        threshold.GenerateVerdict();

        return snapshot;
    }

    private async Task<ReportSnapshot> FetchPhaseData(
        List<FightPhase> phases, JsonNode fightNode, int fightId, long fightStart, bool inProgress)
    {
        var observed = PhaseResolver.ParseTransitions(fightNode["phaseTransitions"]);
        var now = fightStart + ElapsedPullMs();
        var resolution = PhaseResolver.Resolve(phases, observed, fightStart, now);

        // One verdict per phase, fired exactly once when its end becomes observed.
        // Live polls never announce. Mark announced only after a successful announce so
        // a failed verdict fetch is retried on the next tick.
        foreach (var phaseWindow in resolution.Phases)
        {
            if (!phaseWindow.EndObserved || phaseWindow.StartMs == null || phaseWindow.EndMs == null)
            {
                continue;
            }

            if (announcedPhases.Contains(phaseWindow.Index))
            {
                continue;
            }

            await AnnouncePhase(phases[phaseWindow.Index], phaseWindow, fightId, fightStart);
            announcedPhases.Add(phaseWindow.Index);
        }

        if (resolution.CurrentIndex is not int index || resolution.Phases[index].StartMs is not { } phaseStart)
        {
            // Phase unresolvable: no observation and no fallback yet.
            LastPhase = null;
            LastPhaseContext = null;
            return new ReportSnapshot { InProgress = inProgress };
        }

        var phase = phases[index];
        var window = resolution.Phases[index];
        var context = new PhaseContext
        {
            Index = index,
            Name = phase.Name,
            StartMs = phaseStart,
            EndMs = window.EndMs,
            FightStartMs = fightStart,
            StartObserved = window.StartObserved,
            EndObserved = window.EndObserved,
        };

        LastPhase = phase;
        LastPhaseContext = context;
        return await FetchTable(fightId, phaseStart, now, inProgress, context);
    }

    private async Task AnnouncePhase(FightPhase phase, ResolvedPhase window, int fightId, long fightStart)
    {
        var snapshot = await FetchTable(fightId, window.StartMs!.Value, window.EndMs!.Value, false);
        var verdict = new ThresholdVerdict(
            PhaseLabel(phase, window.StartMs.Value, window.EndMs.Value, fightStart),
            phase.Thresholds, snapshot, configuration);
        verdict.GenerateVerdict();
    }

    /// <summary>
    /// Resolves all phases from the final fights query ~15s after the pull ends (past the
    /// ingestion lag) and announces any phase whose end is now known but was never announced,
    /// including the final one. Cancellable by a fresh Start().
    /// </summary>
    private void ScheduleDelayedPhasePass()
    {
        var fight = manager.GetCurrentFight();
        if (fight is not { UsePhases: true } || fight.Phases.Count == 0 || lastFightId == null)
        {
            return;
        }

        delayedPassCts?.Cancel();
        delayedPassCts = new CancellationTokenSource();
        var cts = delayedPassCts;
        var fightId = lastFightId.Value;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                await RunDelayedPhasePass(fightId, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Plugin.Log.Warning(exception, "[FFLogsPlugin] Delayed phase pass failed");
            }
        });
    }

    private async Task RunDelayedPhasePass(int fightId, CancellationToken ct)
    {
        await EnsureTokenAsync();
        ct.ThrowIfCancellationRequested();

        var fights = await FetchReportFights();
        JsonNode? current = null;
        foreach (var fight in fights)
        {
            if (fight?["id"]?.GetValue<int>() == fightId)
            {
                current = fight;
                break;
            }
        }

        current ??= fights[^1];
        if (current == null)
        {
            return;
        }

        var configFight = manager.GetCurrentFight();
        if (configFight is not { UsePhases: true })
        {
            return;
        }

        var phases = configFight.Phases;
        var fightStart = current["startTime"]!.GetValue<long>();
        var fightEnd = current["endTime"]!.GetValue<long>();
        var observed = PhaseResolver.ParseTransitions(current["phaseTransitions"]);
        var resolution = PhaseResolver.Resolve(phases, observed, fightStart, fightEnd, fightEnd);

        foreach (var phaseWindow in resolution.Phases)
        {
            ct.ThrowIfCancellationRequested();
            if (announcedPhases.Contains(phaseWindow.Index))
            {
                continue;
            }

            if (phaseWindow.StartMs == null || phaseWindow.EndMs == null)
            {
                continue;
            }

            var phase = phases[phaseWindow.Index];
            var snapshot = await FetchTable(fightId, phaseWindow.StartMs.Value, phaseWindow.EndMs.Value, false);
            var verdict = new ThresholdVerdict(
                PhaseLabel(phase, phaseWindow.StartMs.Value, phaseWindow.EndMs.Value, fightStart),
                phase.Thresholds, snapshot, configuration);
            verdict.GenerateVerdict();
            announcedPhases.Add(phaseWindow.Index);
        }
    }

    private async Task<JsonNode> FetchCurrentFightNode()
    {
        var fights = await FetchReportFights();

        foreach (var fight in fights)
        {
            if (fight?["inProgress"]?.GetValue<bool>() == true)
            {
                return fight;
            }
        }

        return fights[^1] ?? throw new Exception("Current fight not found");
    }

    private async Task<JsonArray> FetchReportFights()
    {
        var query = $$"""
                        {
                            reportData {
                                report(code: "{{configuration.ReportCode}}") {
                                    fights {
                                        id startTime endTime inProgress
                                        phaseTransitions { id startTime }
                                    }
                                }
                            }
                        }
                      """;

        var response = await PostGqlAsync(query);
        var fights = response["data"]!["reportData"]!["report"]!["fights"]!.AsArray();
        if (fights.Count == 0)
            throw new Exception($"No fights found in report '{configuration.ReportCode}'. " +
                                "Make sure the report code is correct and the uploader is running.");

        return fights;
    }

    private async Task<ReportSnapshot> FetchTable(
        int fightId, long startMs, long endMs, bool inProgress, PhaseContext? phase = null)
    {
        var tableQuery = $$$"""
                            {
                              reportData {
                                report(code: "{{{configuration.ReportCode}}}") {
                                  table(
                                    fightIDs: [{{{fightId}}}]
                                    dataType: DamageDone
                                    startTime: {{{startMs}}}
                                    endTime: {{{endMs}}}
                                  )
                                }
                              }
                            }
                            """;

        var tableResponse = await PostGqlAsync(tableQuery);

        var tableData = tableResponse["data"]!["reportData"]!["report"]!["table"]!["data"]!;
        var combatTime = tableData["combatTime"]?.GetValue<long>() ?? 0;
        var combatDowntime = tableData["damageDowntime"]?.GetValue<long>() ?? 0;
        var entries = tableData["entries"]?.AsArray() ?? [];

        if (entries.Count == 0)
        {
            throw new Exception("No entries found");
        }

        var divisor = (combatTime - combatDowntime) / 1000;

        var players = new List<PlayerData>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry == null)
            {
                continue;
            }

            var totalRdps = entry["totalRDPS"]?.GetValue<double>() ?? 0;
            players.Add(new PlayerData
            {
                Name = entry["name"]?.GetValue<string>() ?? "Unknown",
                Job = NormalizeJob(entry["type"]?.GetValue<string>() ?? ""),
                totalRDPS = totalRdps,
                RDPS = totalRdps / divisor
            });
        }

        players.Sort((a, b) => b.RDPS.CompareTo(a.RDPS));
        return new ReportSnapshot
        {
            InProgress = inProgress,
            Players = players,
            Phase = phase,
        };
    }

    private async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(accessToken) && DateTime.UtcNow < tokenExpiration)
            return;

        if (string.IsNullOrWhiteSpace(configuration.ClientId) || string.IsNullOrWhiteSpace(configuration.ClientSecret))
            throw new InvalidOperationException("FFLogs Client ID / Secret not configured. Open ⚙ Config.");

        var b64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{configuration.ClientId}:{configuration.ClientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);

        var res = await httpClient.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        accessToken = json["access_token"]!.GetValue<string>();
        var exp = json["expires_in"]?.GetValue<int>() ?? 3600;
        tokenExpiration = DateTime.UtcNow.AddSeconds(exp - 120);

        Plugin.Log.Debug("OAuth token refreshed, expires in {0}s", exp);
    }

    private async Task<JsonNode> PostGqlAsync(string query)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { query }),
            Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var res = await httpClient.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var root = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        if (root["errors"] is JsonArray errs && errs.Count > 0)
            throw new Exception(errs[0]!["message"]?.GetValue<string>() ?? "GraphQL error");

        return root;
    }

    public void Stop()
    {
        startTime = null;
        cancellationTokenSource?.Cancel();
        pollerTask = null;
        TimeInCombat = null;
        LastBracket = null;
        ThresholdVerdict = null;
        cancellationTokenSource = null;
        SetState(PollerState.WaitingForPull);
        ScheduleDelayedPhasePass();
    }

    public void SetState(PollerState newState)
    {
        State = newState;
    }

    public bool CanStart()
    {
        return State == PollerState.None;
    }

    private static readonly Dictionary<string, string> JobMap = new()
    {
        ["Paladin"] = "PLD", ["Warrior"] = "WAR",
        ["DarkKnight"] = "DRK", ["Gunbreaker"] = "GNB",
        ["WhiteMage"] = "WHM", ["Scholar"] = "SCH",
        ["Astrologian"] = "AST", ["Sage"] = "SGE",
        ["Monk"] = "MNK", ["Dragoon"] = "DRG",
        ["Ninja"] = "NIN", ["Samurai"] = "SAM",
        ["Reaper"] = "RPR", ["Viper"] = "VPR",
        ["Bard"] = "BRD", ["Machinist"] = "MCH",
        ["Dancer"] = "DNC", ["BlackMage"] = "BLM",
        ["Summoner"] = "SMN", ["RedMage"] = "RDM",
        ["Pictomancer"] = "PCT",
    };

    private static string NormalizeJob(string raw) =>
        JobMap.TryGetValue(raw, out var abbr) ? abbr : raw.ToUpperInvariant();

    private long ElapsedPullMs() =>
        startTime == null ? 0 : (DateTime.Now - startTime.Value).Ticks / TimeSpan.TicksPerMillisecond;

    private static string PhaseLabel(FightPhase phase, long startMs, long endMs, long fightStart) =>
        $"{phase.Name} · {PhaseResolver.FormatMs(startMs - fightStart)}–{PhaseResolver.FormatMs(endMs - fightStart)}";

    private static long ParseBracketToMs(string bracket)
    {
        var parts = bracket.Split(':');
        var minutes = int.Parse(parts[0]);
        var seconds = int.Parse(parts[1]);
        return (minutes * 60 + seconds) * 1000L;
    }

    private static long CalculateActiveMs(long combatTime, KillTimeBracket bracket)
    {
        var downtimeMs = bracket.Downtime.Sum(d =>
                                                  ParseBracketToMs(d.End) - ParseBracketToMs(d.Start));
        return combatTime - downtimeMs;
    }
}
