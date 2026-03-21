using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ThresholdMachine.Event;
using ThresholdMachine.Threshold;
using ThresholdMachine.Windows;

namespace ThresholdMachine;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;
    
    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/machine";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Chungus Machine");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    private ThresholdPoller ThresholdPoller { get; init; }
    private CombatEvent CombatEvent { get; init; }
    private FightThresholdManager FightThresholdManager { get; init; }
    
    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        FightThresholdManager = new FightThresholdManager(Configuration);
        FightThresholdManager.Adapt();
        
        ThresholdPoller = new ThresholdPoller(Configuration, FightThresholdManager);

        ConfigWindow = new ConfigWindow(this, FightThresholdManager);
        MainWindow = new MainWindow(this, ThresholdPoller, FightThresholdManager);

        CombatEvent = new CombatEvent(ThresholdPoller);
        CombatEvent.Enable();
        
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Configure all the machine"
        });
        
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        CombatEvent.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
