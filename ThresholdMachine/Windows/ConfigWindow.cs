using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ThresholdMachine.Threshold;

namespace ThresholdMachine.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly FightThresholdManager manager;

    private string clientId;
    private string clientSecret;
    private string reportCode;
    private bool announceInPartyChat;
    private bool showSecret;

    private int selectedFight = 0;
    private int newBracketMinutes = 0;
    private int newBracketSeconds = 0;

    // Add-row downtime input state: key = widget id
    private readonly System.Collections.Generic.Dictionary<string, (int StartMin, int StartSec, int EndMin, int EndSec)> newDowntime = new();

    private const float RoleLabelWidth = 100f;
    private const float JobColumnWidth  = 90f;

    private static readonly (string Label, string[] Jobs)[] RoleGroups =
    [
        ("Tanks",        ["GNB", "PLD", "WAR", "DRK"]),
        ("Healers",      ["AST", "SGE", "WHM", "SCH"]),
        ("Melee",        ["VPR", "DRG", "MNK", "NIN", "SAM", "RPR"]),
        ("Phys Ranged",  ["BRD", "MCH", "DNC"]),
        ("Casters",      ["PCT", "BLM", "SMN", "RDM"]),
    ];

    public ConfigWindow(Plugin plugin, FightThresholdManager manager) : base("Threshold Machine configuration")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        configuration = plugin.Configuration;
        this.manager = manager;

        clientId = configuration.ClientId;
        clientSecret = configuration.ClientSecret;
        reportCode = configuration.ReportCode;
        announceInPartyChat = configuration.AnnounceInPartyChat;
        showSecret = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("cfg_tabs")) return;

        if (ImGui.BeginTabItem("FFLogs Config"))
        {
            ApiReportTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Thresholds"))
        {
            ThresholdTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void ApiReportTab()
    {
        ImGui.Spacing();
        ImGui.Text("FFLogs API Credentials");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Create API clients at:  fflogs.com → Account → API Clients");
        ImGui.Spacing();

        ImGui.Text("Client ID");
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("##cid", ref clientId, 128);

        ImGui.Text("Client Secret");
        ImGui.SetNextItemWidth(360);
        var secFlags = showSecret
                           ? ImGuiInputTextFlags.None
                           : ImGuiInputTextFlags.Password;
        ImGui.InputText("##csec", ref clientSecret, 256, secFlags);
        ImGui.SameLine();
        if (ImGui.Button(showSecret ? "Hide" : "Show"))
            showSecret = !showSecret;

        ImGui.Spacing();

        ImGui.Text("Report Code");
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##rc", ref reportCode, 64);

        ImGui.Spacing();
        if (ImGui.Checkbox("Auto announcement in party chat", ref announceInPartyChat))
        {
            configuration.AnnounceInPartyChat = announceInPartyChat;
            configuration.Save();
        }

        ImGui.Spacing();
        if (ImGui.Button("Save##api", new Vector2(110, 0)))
        {
            configuration.ClientId = clientId.Trim();
            configuration.ClientSecret = clientSecret.Trim();
            configuration.ReportCode = reportCode.Trim();
            configuration.Save();
            ImGui.SameLine();
            ImGui.Text("✅ Saved!");
        }
    }

    private void ThresholdTab()
    {
        ImGui.BeginChild("Fight", new Vector2(108, 0), true);
        for (var i = 0; i < configuration.FightList.Count; i++)
        {
            if (ImGui.Selectable(manager.GetFightKeys()[i], selectedFight == i))
                selectedFight = i;
        }
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("Editor", new Vector2(0, 0), false);
        var fight = configuration.FightList[selectedFight];

        var usePhases = fight.UsePhases;
        if (ImGui.Checkbox("Phase-based thresholds", ref usePhases))
        {
            fight.UsePhases = usePhases;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (fight.UsePhases)
        {
            DrawPhaseEditor(fight);
        }
        else
        {
            DrawBracketEditor(fight);
        }

        ImGui.EndChild();
    }

    private void DrawBracketEditor(Fight fight)
    {
        ImGui.Text($"{fight.FightId} — rDPS targets per kill-time bracket");
        ImGui.Separator();
        ImGui.Spacing();

        int removeIndex = -1;
        for (var bi = 0; bi < fight.KillTimeBrackets.Count; bi++)
        {
            if (DrawBracket(fight.KillTimeBrackets[bi], bi))
                removeIndex = bi;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (removeIndex >= 0)
        {
            fight.KillTimeBrackets.RemoveAt(removeIndex);
            newDowntime.Clear();
            configuration.Save();
        }

        ImGui.Text("≤");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(42);
        ImGui.InputInt("##nbm", ref newBracketMinutes, 0, 0);
        if (newBracketMinutes < 0) newBracketMinutes = 0;
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(42);
        ImGui.InputInt("##nbs", ref newBracketSeconds, 0, 0);
        newBracketSeconds = Math.Clamp(newBracketSeconds, 0, 59);
        ImGui.SameLine();
        ImGui.Text("(mm:ss)");
        ImGui.SameLine();
        if (ImGui.Button("+ Add Bracket"))
            manager.AddBracket(fight.FightId, $"{newBracketMinutes}:{newBracketSeconds:D2}");
    }

    private bool DrawBracket(KillTimeBracket bracket, int bracketIndex)
    {
        ParseBracket(bracket.Bracket, out var mins, out var secs);
        var id = $"b{bracketIndex}";

        ImGui.Text("≤");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##bm{bracketIndex}", ref mins, 0, 0))
        {
            if (mins < 0) mins = 0;
            bracket.Bracket = FormatBracket(mins, secs);
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##bs{bracketIndex}", ref secs, 0, 0))
        {
            secs = Math.Clamp(secs, 0, 59);
            bracket.Bracket = FormatBracket(mins, secs);
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.Text("(mm:ss)");
        ImGui.SameLine();
        ImGui.Text("  =  ");
        ImGui.SameLine();
        var remove = ImGui.Button($"Remove##rem{bracketIndex}");

        ImGui.Spacing();

        foreach (var (roleLabel, jobs) in RoleGroups)
            DrawRoleRow(bracket.Thresholds, id, roleLabel, jobs);

        DrawDowntimeEditor(bracket.Downtime, id);

        return remove;
    }

    private void DrawPhaseEditor(Fight fight)
    {
        ImGui.Text($"{fight.FightId} — rDPS targets per phase (FFLogs-observed transitions)");
        ImGui.Separator();
        ImGui.Spacing();

        int removeIndex = -1;
        for (var pi = 0; pi < fight.Phases.Count; pi++)
        {
            if (DrawPhase(fight.Phases[pi], selectedFight, pi))
                removeIndex = pi;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (removeIndex >= 0)
        {
            fight.Phases.RemoveAt(removeIndex);
            newDowntime.Clear();
            configuration.Save();
        }

        if (ImGui.Button("+ Add Phase"))
            manager.AddPhase(fight.FightId);
    }

    private bool DrawPhase(FightPhase phase, int fightIndex, int phaseIndex)
    {
        var id = $"f{fightIndex}p{phaseIndex}";

        var name = phase.Name;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText($"##pname{id}", ref name, 64))
        {
            phase.Name = name;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.Text("starts");
        ImGui.SameLine();
        ParseBracket(phase.FallbackStart, out var mins, out var secs);

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##pfm{id}", ref mins, 0, 0))
        {
            if (mins < 0) mins = 0;
            phase.FallbackStart = FormatBracket(mins, secs);
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##pfs{id}", ref secs, 0, 0))
        {
            secs = Math.Clamp(secs, 0, 59);
            phase.FallbackStart = FormatBracket(mins, secs);
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.Text("(mm:ss fallback)");
        ImGui.SameLine();
        var remove = ImGui.Button($"Remove##prem{id}");

        ImGui.Spacing();

        foreach (var (roleLabel, jobs) in RoleGroups)
            DrawRoleRow(phase.Thresholds, id, roleLabel, jobs);

        DrawDowntimeEditor(phase.Downtime, id);

        return remove;
    }

    private void DrawDowntimeEditor(System.Collections.Generic.List<DowntimePeriod> downtime, string id)
    {
        ImGui.Text("  Downtime");
        ImGui.SameLine();
        ImGui.TextDisabled("(subtracted from combat time)");

        int removeIndex = -1;
        for (var di = 0; di < downtime.Count; di++)
        {
            var period = downtime[di];
            ParseBracket(period.Start, out var startMin, out var startSec);
            ParseBracket(period.End, out var endMin, out var endSec);

            ImGui.Text("   −");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(42);
            if (ImGui.InputInt($"##dt_sm{di}_{id}", ref startMin, 0, 0))
            {
                if (startMin < 0) startMin = 0;
                period.Start = FormatBracket(startMin, startSec);
                configuration.Save();
            }
            ImGui.SameLine();
            ImGui.Text(":");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(42);
            if (ImGui.InputInt($"##dt_ss{di}_{id}", ref startSec, 0, 0))
            {
                startSec = Math.Clamp(startSec, 0, 59);
                period.Start = FormatBracket(startMin, startSec);
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.Text("→");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(42);
            if (ImGui.InputInt($"##dt_em{di}_{id}", ref endMin, 0, 0))
            {
                if (endMin < 0) endMin = 0;
                period.End = FormatBracket(endMin, endSec);
                configuration.Save();
            }
            ImGui.SameLine();
            ImGui.Text(":");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(42);
            if (ImGui.InputInt($"##dt_es{di}_{id}", ref endSec, 0, 0))
            {
                endSec = Math.Clamp(endSec, 0, 59);
                period.End = FormatBracket(endMin, endSec);
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.Text("(mm:ss)");
            ImGui.SameLine();
            if (ImGui.Button($"x##dt_rem{di}_{id}"))
                removeIndex = di;
        }

        if (removeIndex >= 0)
        {
            downtime.RemoveAt(removeIndex);
            configuration.Save();
        }

        if (!newDowntime.TryGetValue(id, out var add))
            add = (0, 0, 0, 0);

        ImGui.Text("   +");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##dt_nsm_{id}", ref add.StartMin, 0, 0) && add.StartMin < 0)
            add.StartMin = 0;
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##dt_nss_{id}", ref add.StartSec, 0, 0))
            add.StartSec = Math.Clamp(add.StartSec, 0, 59);

        ImGui.SameLine();
        ImGui.Text("→");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##dt_nem_{id}", ref add.EndMin, 0, 0) && add.EndMin < 0)
            add.EndMin = 0;
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(42);
        if (ImGui.InputInt($"##dt_nes_{id}", ref add.EndSec, 0, 0))
            add.EndSec = Math.Clamp(add.EndSec, 0, 59);

        ImGui.SameLine();
        ImGui.Text("(mm:ss)");
        ImGui.SameLine();
        if (ImGui.Button($"+ Add Downtime##dt_add_{id}"))
        {
            downtime.Add(new DowntimePeriod
            {
                Start = FormatBracket(add.StartMin, add.StartSec),
                End = FormatBracket(add.EndMin, add.EndSec),
            });
            add = (0, 0, 0, 0);
            configuration.Save();
        }

        newDowntime[id] = add;
    }

    private void DrawRoleRow(System.Collections.Generic.List<JobThreshold> thresholds, string id, string roleLabel, string[] jobs)
    {
        var cursorY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX());
        ImGui.Text(roleLabel);

        var baseX = ImGui.GetWindowPos().X + RoleLabelWidth + ImGui.GetScrollX();
        var startY = cursorY;

        for (var i = 0; i < jobs.Length; i++)
        {
            var job = jobs[i];
            var threshold = thresholds.Find(t => t.JobId == job);
            if (threshold == null) continue;

            var colX = baseX + i * JobColumnWidth;

            ImGui.SetCursorPos(new Vector2(colX - ImGui.GetWindowPos().X, startY));
            ImGui.Text(job);

            ImGui.SetCursorPos(new Vector2(colX - ImGui.GetWindowPos().X, startY + ImGui.GetTextLineHeight() + 2));
            ImGui.SetNextItemWidth(JobColumnWidth - 8);
            var val = threshold.Threshold;
            if (ImGui.InputInt($"##{job}_{id}", ref val, 0, 0))
            {
                if (val < 0) val = 0;
                threshold.Threshold = val;
                configuration.Save();
            }
        }

        ImGui.SetCursorPosY(startY + ImGui.GetTextLineHeight() * 2 + 10);
        ImGui.Dummy(new Vector2(0, 2));
    }

    private static void ParseBracket(string bracket, out int minutes, out int seconds)
    {
        minutes = 0;
        seconds = 0;
        var parts = bracket.Split(':');
        if (parts.Length >= 1) int.TryParse(parts[0], out minutes);
        if (parts.Length >= 2) int.TryParse(parts[1], out seconds);
    }

    private static string FormatBracket(int minutes, int seconds)
        => $"{minutes}:{seconds:D2}";
}
