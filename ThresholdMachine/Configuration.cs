using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace ThresholdMachine;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public string ReportCode { get; set; } = "";
    
    public string ActiveFight { get; set; } = "M9S";
    
    public bool AnnounceInPartyChat { get; set; } = false;

    public List<Fight> FightList { get; set; } = new List<Fight>();
    
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public class Fight
{
    public string FightDisplayName  { get; set; } = "";
    public string FightId { get; set; } = "";
    public List<KillTimeBracket> KillTimeBrackets { get; set; }= new List<KillTimeBracket>();
}

[Serializable]
public class DowntimePeriod
{
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
}

[Serializable]
public class KillTimeBracket
{
    public string Bracket { get; set; } = "";
    public List<JobThreshold> Thresholds { get; set; } = new();
    public List<DowntimePeriod> Downtime { get; set; } = new();
}
[Serializable]
public class JobThreshold
{
    public string JobId { get; set; } = "";
    public int Threshold { get; set; } = 0;
}
