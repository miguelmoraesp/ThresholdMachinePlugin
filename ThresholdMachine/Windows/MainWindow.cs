using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ThresholdMachine.Threshold;

namespace ThresholdMachine.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ThresholdPoller poller;
    private readonly FightThresholdManager manager;

    private string activeFight;

    public MainWindow(Plugin plugin, ThresholdPoller poller, FightThresholdManager manager)
        : base("Threshold Machine", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.poller = poller;
        this.manager = manager;

        activeFight = plugin.Configuration.ActiveFight;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.Button(poller.CanStart() ? "Start" : "Stop"))
        {
            poller.SetState(poller.CanStart() ? PollerState.WaitingForPull : PollerState.None);
            if (poller.State == PollerState.WaitingForPull)
            {
                poller.PollData();
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(75);

        var fightKeys = manager.GetFightKeys().ToArray();
        var index = Array.IndexOf(fightKeys, plugin.Configuration.ActiveFight);
        if (ImGui.Combo("", ref index, fightKeys, fightKeys.Length))
        {
            plugin.Configuration.ActiveFight = fightKeys[index];
            plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Text(GetMachineStateText());
        ImGui.Spacing();
        if (poller.State == PollerState.Polling)
        {
            ImGui.Text(poller.TimeInCombat);
            if (poller.ReportSnapshot != null)
            {
                DrawTable();
            }
            else
            {
                ImGui.Text("Still waiting for data until table is shown!");
            }
        }
        else if (poller.State == PollerState.WaitingForPull)
        {
            ImGui.Spacing();
            if (poller.ReportSnapshot != null)
            {
                DrawTable();
            }
            else
            {
                ImGui.Text("Still waiting for data until table is shown!");
            }
        }
    }

    private void DrawTable()
    {
        var flags = ImGuiTableFlags.Borders
                    | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.SizingFixedFit;
        var rowHeight  = ImGui.GetTextLineHeightWithSpacing();
        var tableHeight = (poller.ReportSnapshot!.Players.Count * rowHeight) + rowHeight + 4;        // rows + header + padding
        var avail  = ImGui.GetContentRegionAvail().Y - 4;
        var height = Math.Min(tableHeight, avail);

        if (!ImGui.BeginTable("##rdps", 5, flags, new Vector2(0, height)))
        {
            return;
        }
        
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Job",    ImGuiTableColumnFlags.WidthFixed,   44f);
        ImGui.TableSetupColumn("rDPS",   ImGuiTableColumnFlags.WidthFixed,   76f);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed,   76f);
        ImGui.TableSetupColumn("Δ rDPS", ImGuiTableColumnFlags.WidthFixed,   76f);
        ImGui.TableHeadersRow();
        
        var bracket = poller.LastBracket;
        if (bracket == null)
        {
            bracket = manager.GetCurrentFight()?.KillTimeBrackets.First();
        }
        
        foreach (var player in poller.ReportSnapshot.Players)
        {
            var jobThreshold = bracket?.Thresholds.Find(x => x.JobId == player.Job);
            if (jobThreshold is not { Threshold: > 0 })
            {
                continue;
            }

            ImGui.TableNextRow();
            
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(player.Name);
            
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(player.Job);
            
            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{player.RDPS:N0}");
            
            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"{jobThreshold.Threshold:N0}");
            
            ImGui.TableSetColumnIndex(4);
            
            var sign = player.RDPS >= jobThreshold.Threshold ? "+" : "-";;
            ImGui.Text($"{sign}{player.RDPS - jobThreshold.Threshold:N0}");
        }
        
        ImGui.EndTable();
    }

    private string GetMachineStateText()
    {
        return poller.State switch
        {
            PollerState.WaitingForPull => "Waiting for Pull to start",
            PollerState.Polling => "Polling thresholds, wait for next killtime bracket!",
            PollerState.None => "Machine is currently not started, hit start button to start tracking pulls",
            _ => "None"
        };
    }
}
