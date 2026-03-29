using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using ThresholdMachine.Threshold;

namespace ThresholdMachine.Event;

public class CombatEvent(ThresholdPoller poller) : IDisposable
{

    public void Enable()
    {
        Plugin.Condition.ConditionChange += OnConditionChanged;
    }

    public void Dispose()
    {
        Plugin.Condition.ConditionChange -= OnConditionChanged;
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat)
        {
            return;
        }

        if (poller.State == PollerState.None)
        {
            return;
        }

        if (value)
        {
            poller.Start();
        }
        else
        {
            poller.Stop();
        }
    }
}
