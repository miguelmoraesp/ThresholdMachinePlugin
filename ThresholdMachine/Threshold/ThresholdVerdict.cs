using System.Collections.Generic;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ThresholdMachine.Threshold;

public class ThresholdVerdict(KillTimeBracket bracket, ReportSnapshot snapshot, Configuration configuration)
{
    public void GenerateVerdict()
    {
        var above = new List<string>();
        var below = new List<string>();

        foreach (var snapshotPlayer in snapshot.Players)
        {
            var jobThreshold = GetThreshold(snapshotPlayer.Job);
            if (jobThreshold is { Threshold: 0 })
            {
                continue;
            }

            if (snapshotPlayer.RDPS >= jobThreshold!.Threshold)
            {
                above.Add(
                    $"{snapshotPlayer.Name} ({snapshotPlayer.Job} +{(int)(snapshotPlayer.RDPS - jobThreshold.Threshold):N0})");
            }
            else
            {
                below.Add(
                    $"{snapshotPlayer.Name} ({snapshotPlayer.Job} {(int)(snapshotPlayer.RDPS - jobThreshold.Threshold):N0})");
            }
        }

        switch (above.Count)
        {
            case >= 1:
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"KEEP! [{bracket.Bracket}] {above.Count} players above threshold!", Type = XivChatType.Echo});
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"{string.Join(" ", above)}", Type = XivChatType.Echo});
                break;
            case <= 0:
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"WIPE! [{bracket.Bracket}] everyone is below threshold", Type = XivChatType.Echo});
                Plugin.ChatGui.Print(new XivChatEntry { Message = $"{string.Join(" ", below)}", Type = XivChatType.Echo});
                break;
        }
        
        if (!configuration.AnnounceInPartyChat)
        {
            return;
        }

        unsafe
        {
            if (above.Count >= 1)
            {
                UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/p KEEP [{bracket.Bracket}] {above.Count} players above threshold!"));
                UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/p {string.Join(" ", above)}"));
                return;
            }

            UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/p WIPE [{bracket.Bracket}] everyone is below threshold"));
            UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/p {string.Join(" ", below)}"));
        }
    }

    private JobThreshold? GetThreshold(string jobId)
    {
        return bracket.Thresholds.Find(x => x.JobId == jobId);
    }
}
