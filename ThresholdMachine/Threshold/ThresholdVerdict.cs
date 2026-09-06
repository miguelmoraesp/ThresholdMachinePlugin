using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ThresholdMachine.Threshold;

public class ThresholdVerdict(string label, List<JobThreshold> thresholds, ReportSnapshot snapshot, Configuration configuration)
{
    public void GenerateVerdict()
    {
        var above = new List<string>();
        var below = new List<string>();

        foreach (var snapshotPlayer in snapshot.Players)
        {
            var jobThreshold = GetThreshold(snapshotPlayer.Job);
            if (jobThreshold is { Threshold: 0 } or null)
            {
                continue;
            }

            if (snapshotPlayer.RDPS >= jobThreshold.Threshold)
            {
                above.Add(
                    $"{snapshotPlayer.Name} ({snapshotPlayer.Job} +{(int)(snapshotPlayer.RDPS - jobThreshold.Threshold):N0})");
            }
            else
            {
                var diff = snapshotPlayer.RDPS >= jobThreshold.Threshold * 0.99;
                if (diff)
                {
                    above.Add(
                        $"{snapshotPlayer.Name} ({snapshotPlayer.Job} {(int)(snapshotPlayer.RDPS - jobThreshold.Threshold):N0})");
                    continue;
                }

                below.Add(
                    $"{snapshotPlayer.Name} ({snapshotPlayer.Job} {(int)(snapshotPlayer.RDPS - jobThreshold.Threshold):N0})");
            }
        }

        if (above.Count == 0 && below.Count == 0)
        {
            Plugin.ChatGui.Print(new XivChatEntry { Message = "No data found", Type = XivChatType.Echo});
            return;
        }

        PostVerdictInPartyChat(above, below);

        switch (above.Count)
        {
            case >= 1:
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"KEEP! [{label}] {above.Count} players above threshold!", Type = XivChatType.Echo});
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"{string.Join(" ", above)}", Type = XivChatType.Echo});
                break;

            case <= 0:
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"WIPE! [{label}] everyone is below threshold", Type = XivChatType.Echo});
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"{string.Join(" ", below)}", Type = XivChatType.Echo});
                break;
        }
    }

    private void PostVerdictInPartyChat(List<string> above, List<string> below) => Task.Run(async () =>
    {
        if (!configuration.AnnounceInPartyChat)
        {
            return;
        }

        if (above.Count >= 1)
        {
            await SendPartyChat($"KEEP! [{label}] {above.Count} {(above.Count == 1 ? "player" : "players")} above threshold!");
            foreach (var player in above)
            {
                await SendPartyChat(player);
            }
            return;
        }

        await SendPartyChat("WIPE!!!");
        foreach (var se in below)
        {
            await SendPartyChat(se);
        }
    });

    public async Task SendPartyChat(string msg)
    {
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/p {msg}"));
            }
        });
    }

    private JobThreshold? GetThreshold(string jobId)
    {
        return thresholds.Find(x => x.JobId == jobId);
    }
}
