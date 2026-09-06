using System;
using System.Collections.Generic;
using System.Linq;

namespace ThresholdMachine.Threshold;

public class FightThresholdManager(Configuration config)
{
    private readonly string[] fightList = ["M9S:M9S", "M10S:M10S", "M11S:M11S", "M12SP1:M12S Phase 1", "M12SP2:M12S Phase 2", "DMU:Dancing Mad"];
    private readonly string[] jobList = [
        "PLD", "WAR", "DRK", "GNB",
        "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC",
        "BLM", "SMN", "RDM", "PCT"
    ];
    
    public void Adapt()
    {
        if (config.FightList.Count <= fightList.Length)
        {
            foreach (var fightString in fightList)
            {
                var strings = fightString.Split(":");
                var fightKey = strings[0];
                var fightDisplayName = strings[1];

                var fight = new Fight
                {
                    FightId = fightKey,
                    FightDisplayName = fightDisplayName
                };

                if (config.FightList.Contains(fight))
                {
                    continue;
                }
                
                config.FightList.Add(fight);
                Plugin.Log.Debug("Successfully loaded " + fightDisplayName + " into configuration");
            }

            config.Save();
        }

        if (config.FightList.Count > 0)
        {
            Plugin.Log.Debug("Skipping fight loading list");
        }
    }

    public Fight? GetCurrentFight()
    {
        return GetFight(config.ActiveFight);
    }

    public KillTimeBracket? GetBracket(Fight fight, string bracket)
    {
        return fight.KillTimeBrackets.Find(b => b.Bracket == bracket);
    }

    public FightPhase? GetPhase(Fight fight, int index)
    {
        return index >= 0 && index < fight.Phases.Count ? fight.Phases[index] : null;
    }

    public void AddPhase(string fightId)
    {
        var fight = GetFight(fightId);
        if (fight == null)
        {
            Plugin.Log.Error("No fight found for " + fightId);
            return;
        }

        var phase = new FightPhase
        {
            Name = $"Phase {fight.Phases.Count + 1}"
        };

        foreach (var job in jobList)
        {
            phase.Thresholds.Add(new JobThreshold
            {
                JobId = job,
                Threshold = 0
            });
        }

        fight.Phases.Add(phase);
        config.Save();
        Plugin.Log.Debug("Successfully added " + phase.Name + " to configuration");
    }

    public void AddBracket(string fightId, string bracketString)
    {
        var fight = GetFight(fightId);
        if (fight == null)
        {
            Plugin.Log.Error("No fight found for " + fightId);
            return;
        }

        var bracket = new KillTimeBracket
        {
            Bracket = bracketString
        };
        
        foreach (var job in jobList)
        {
            var jobThreshold = new JobThreshold
            {
                JobId = job,
                Threshold = 0
            };
            
            bracket.Thresholds.Add(jobThreshold);
        }
        
        fight.KillTimeBrackets.Add(bracket);
        config.Save();
        Plugin.Log.Debug("Successfully added " + bracketString + " to configuration");
    }

    public List<string> GetFightKeys()
    {
        return config.FightList.Select(x => x.FightId).ToList();
    }
    
    private Fight? GetFight(string fightId)
    {
        return config.FightList.Find(f => f.FightId == fightId);
    }
}
