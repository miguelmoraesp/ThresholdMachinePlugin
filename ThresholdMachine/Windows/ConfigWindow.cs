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

    // Per-bracket downtime input state: key = bracketIndex
    private readonly System.Collections.Generic.Dictionary<int, (int StartMin, int StartSec, int EndMin, int EndSec)> newDowntime = new();

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

        ImGui.Text($"{fight.FightId} \u2014 rDPS targets per kill-time bracket");
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
            newDowntime.Remove(removeIndex);
            configuration.Save();
        }

        ImGui.Text("\u2264");
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

        ImGui.EndChild();
    }

    private bool DrawBracket(KillTimeBracket bracket, int bracketIndex)
    {
        ParseBracket(bracket.Bracket, out var mins, out var secs);

        ImGui.Text("\u2264");
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
        
        ImGui.Text("Downtime Windows");
        ImGui.SameLine();
        ImGui.TextDisabled("(damage not applied — subtracted from divisor)");
        ImGui.Spacing();

        int removeDowntime = -1;
        for (var di = 0; di < bracket.Downtime.Count; di++)
        {
            var dt = bracket.Downtime[di];
            ParseBracket(dt.Start, out var dsm, out var dss);
            ParseBracket(dt.End,   out var dem, out var des);

            ImGui.Text("  Start");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(36);
            if (ImGui.InputInt($"##dtsm{bracketIndex}_{di}", ref dsm, 0, 0))
            {
                if (dsm < 0) dsm = 0;
                dt.Start = FormatBracket(dsm, dss);
                configuration.Save();
            }
            ImGui.SameLine(); ImGui.Text(":"); ImGui.SameLine();
            ImGui.SetNextItemWidth(36);
            if (ImGui.InputInt($"##dtss{bracketIndex}_{di}", ref dss, 0, 0))
            {
                dss = Math.Clamp(dss, 0, 59);
                dt.Start = FormatBracket(dsm, dss);
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.Text("  End");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(36);
            if (ImGui.InputInt($"##dtem{bracketIndex}_{di}", ref dem, 0, 0))
            {
                if (dem < 0) dem = 0;
                dt.End = FormatBracket(dem, des);
                configuration.Save();
            }
            ImGui.SameLine(); ImGui.Text(":"); ImGui.SameLine();
            ImGui.SetNextItemWidth(36);
            if (ImGui.InputInt($"##dtes{bracketIndex}_{di}", ref des, 0, 0))
            {
                des = Math.Clamp(des, 0, 59);
                dt.End = FormatBracket(dem, des);
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"  ({DowntimeDuration(dt.Start, dt.End)})");
            ImGui.SameLine();
            if (ImGui.Button($"Remove##dtrem{bracketIndex}_{di}"))
                removeDowntime = di;

            ImGui.Spacing();
        }

        if (removeDowntime >= 0)
        {
            bracket.Downtime.RemoveAt(removeDowntime);
            configuration.Save();
        }
        
        if (!newDowntime.ContainsKey(bracketIndex))
            newDowntime[bracketIndex] = (1, 45, 2, 10);

        var (nsm, nss, nem, nes) = newDowntime[bracketIndex];

        ImGui.Text("  +");
        ImGui.SameLine();
        ImGui.Text("Start");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(36);
        if (ImGui.InputInt($"##ndsm{bracketIndex}", ref nsm, 0, 0)) { if (nsm < 0) nsm = 0; }
        ImGui.SameLine(); ImGui.Text(":"); ImGui.SameLine();
        ImGui.SetNextItemWidth(36);
        if (ImGui.InputInt($"##ndss{bracketIndex}", ref nss, 0, 0)) nss = Math.Clamp(nss, 0, 59);

        ImGui.SameLine();
        ImGui.Text("End");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(36);
        if (ImGui.InputInt($"##ndem{bracketIndex}", ref nem, 0, 0)) { if (nem < 0) nem = 0; }
        ImGui.SameLine(); ImGui.Text(":"); ImGui.SameLine();
        ImGui.SetNextItemWidth(36);
        if (ImGui.InputInt($"##ndes{bracketIndex}", ref nes, 0, 0)) nes = Math.Clamp(nes, 0, 59);

        newDowntime[bracketIndex] = (nsm, nss, nem, nes);

        ImGui.SameLine();
        if (ImGui.Button($"+ Add Downtime##addt{bracketIndex}"))
        {
            bracket.Downtime.Add(new DowntimePeriod
            {
                Start = FormatBracket(nsm, nss),
                End   = FormatBracket(nem, nes)
            });
            configuration.Save();
        }

        ImGui.Spacing();
        
        foreach (var (roleLabel, jobs) in RoleGroups)
            DrawRoleRow(bracket, bracketIndex, roleLabel, jobs);

        return remove;
    }

    private static string DowntimeDuration(string start, string end)
    {
        ParseBracket(start, out var sm, out var ss);
        ParseBracket(end,   out var em, out var es);
        var totalSec = (em * 60 + es) - (sm * 60 + ss);
        return totalSec >= 0 ? $"{totalSec}s downtime" : "invalid";
    }

    private void DrawRoleRow(KillTimeBracket bracket, int bracketIndex, string roleLabel, string[] jobs)
    {
        var cursorY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX());
        ImGui.Text(roleLabel);

        var baseX = ImGui.GetWindowPos().X + RoleLabelWidth + ImGui.GetScrollX();
        var startY = cursorY;

        for (var i = 0; i < jobs.Length; i++)
        {
            var job = jobs[i];
            var threshold = bracket.Thresholds.Find(t => t.JobId == job);
            if (threshold == null) continue;

            var colX = baseX + i * JobColumnWidth;

            ImGui.SetCursorPos(new Vector2(colX - ImGui.GetWindowPos().X, startY));
            ImGui.Text(job);

            ImGui.SetCursorPos(new Vector2(colX - ImGui.GetWindowPos().X, startY + ImGui.GetTextLineHeight() + 2));
            ImGui.SetNextItemWidth(JobColumnWidth - 8);
            var val = threshold.Threshold;
            if (ImGui.InputInt($"##{job}_{bracketIndex}", ref val, 0, 0))
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
