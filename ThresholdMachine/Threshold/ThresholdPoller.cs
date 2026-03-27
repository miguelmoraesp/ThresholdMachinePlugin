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

    public ReportSnapshot? ReportSnapshot { get; set; }
    public ThresholdVerdict? ThresholdVerdict { get; set; }

    public PollerState State = PollerState.None;
    public string? TimeInCombat;

    public void Start()
    {
        SetState(PollerState.Polling);
        startTime = DateTime.Now;
        cancellationTokenSource = new CancellationTokenSource();
        ReportSnapshot = new();
        LastBracket = null;
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

            var bracket = manager.GetBracket(fight, TimeInCombat);
            if (bracket == null)
            {
                continue;
            }

            LastBracket = bracket;
            await PollData();
            if (ReportSnapshot == null)
            {
                continue;
            }
            
            ThresholdVerdict = new ThresholdVerdict(bracket, ReportSnapshot, configuration);
            ThresholdVerdict.GenerateVerdict();
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
        if (string.IsNullOrWhiteSpace(configuration.ReportCode))
            throw new InvalidOperationException("Report code not set. Open ⚙ Config.");

        var query = $$"""
                        {
                            reportData {
                                report(code: "{{configuration.ReportCode}}") {
                                    fights {
                                        id startTime endTime inProgress
                                    }
                                }
                            }
                        }
                      """;

        var reportResponse = await PostGqlAsync(query);
        var reportFights = reportResponse["data"]!["reportData"]!["report"]!["fights"]!.AsArray();
        if (reportFights.Count == 0)
            throw new Exception($"No fights found in report '{configuration.ReportCode}'. " +
                                "Make sure the report code is correct and the uploader is running.");

        JsonNode? current = null;
        foreach (var fight in reportFights)
        {
            if (fight?["inProgress"]?.GetValue<bool>() == true)
            {
                current = fight;
                break;
            }
        }

        current ??= reportFights[^1];

        if (current == null)
        {
            throw new Exception("Current fight not found");
        }
        
        var fightId = current["id"]!.GetValue<int>();
        var fightStart = current["startTime"]!.GetValue<long>();
        var inProgress = current["inProgress"]!.GetValue<bool>();

        var bracket = LastBracket;
        if (LastBracket == null)
        {
            bracket = manager.GetCurrentFight()?.KillTimeBrackets.First();
        }

        var tableQuery = $$$"""
                            {
                              reportData {
                                report(code: "{{{configuration.ReportCode}}}") {
                                  table(
                                    fightIDs: [{{{fightId}}}]
                                    dataType: DamageDone
                                    startTime: {{{fightStart}}}
                                    endTime: {{{fightStart + ParseBracketToMs(bracket.Bracket)}}}
                                  )
                                }
                              }
                            }
                            """;
        
        var tableResponse = await PostGqlAsync(tableQuery);

        var tableData = tableResponse["data"]!["reportData"]!["report"]!["table"]!["data"]!;
        var combatDowntime = tableData["damageDowntime"]?.GetValue<long>() ?? 0;
        var combatTime = tableData["combatTime"]!.GetValue<long>();
        var entries = tableData["entries"]!.AsArray();
        
        var divisor = CalculateActiveMs(combatTime, bracket) / 1000;

        Plugin.ChatGui.Print($"downtime {combatDowntime / 1000} dividing by {divisor}, with data {(combatTime - combatDowntime) / 1000}");
        
        var players = new List<PlayerData>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry == null)
            {
                continue;
            }
            
            var totalRDPS = entry["totalRDPS"]?.GetValue<double>() ?? 0;
            players.Add(new PlayerData
            {
                Name = entry["name"]?.GetValue<string>() ?? "Unknown",
                Job = NormalizeJob(entry["type"]?.GetValue<string>() ?? ""),
                totalRDPS = totalRDPS,
                RDPS = totalRDPS / divisor
            });
        }
        
        players.Sort((a, b) => b.RDPS.CompareTo(a.RDPS));

        return new ReportSnapshot
        {
            InProgress = inProgress,
            Players = players
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
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);

        var res = await httpClient.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        accessToken = json["access_token"]!.GetValue<string>();
        int exp = json["expires_in"]?.GetValue<int>() ?? 3600;
        tokenExpiration = DateTime.UtcNow.AddSeconds(exp - 120);

        Plugin.Log.Debug("OAuth token refreshed, expires in {0}s", exp);
    }

    private async Task<JsonNode> PostGqlAsync(string query)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query }),
                Encoding.UTF8, "application/json")
        };
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
