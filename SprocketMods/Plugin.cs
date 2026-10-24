using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SprocketMods.Arcade;
using SprocketMods.Settings;

namespace SprocketMods;

[BepInPlugin(Guid, "Sprocket Mods", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "sprocketmods";
    public const string Version = "1.11.0";

    internal static ManualLogSource Logger;
    internal static HudConfig Cfg;
    internal static ArcadeConfig Arcade;
    internal static Cameras.CameraConfig Camera;
    internal static Weapons.AutocannonConfig Autocannon;
    internal static KillFeed.KillFeedConfig KillFeed;
    internal static Hits.HitReportConfig HitReport;
    internal static Targets.TargetInfoConfig TargetInfo;
    internal static Teams.TeamBarConfig TeamBar;
    internal static Map.MinimapConfig Minimap;
    internal static Hits.DamageIndicatorConfig DamageIndicator;
    internal static Repair.RepairConfig Repair;
    internal static Ai.AiConfig Ai;
    internal static UI.HudStyleConfig Style;
    internal static Localization.LocalizationManager Localization;

    public override void Load()
    {
        Logger = Log;
        Cfg = new HudConfig(Config);
        Arcade = new ArcadeConfig(Config);
        Camera = new Cameras.CameraConfig(Config);
        Autocannon = new Weapons.AutocannonConfig(Config);
        KillFeed = new KillFeed.KillFeedConfig(Config);
        HitReport = new Hits.HitReportConfig(Config);
        TargetInfo = new Targets.TargetInfoConfig(Config);
        TeamBar = new Teams.TeamBarConfig(Config);
        Minimap = new Map.MinimapConfig(Config);
        DamageIndicator = new Hits.DamageIndicatorConfig(Config);
        Repair = new Repair.RepairConfig(Config);
        Ai = new Ai.AiConfig(Config);
        Style = new UI.HudStyleConfig(Config);
        Localization = new Localization.LocalizationManager(Config);

        ClassInjector.RegisterTypeInIl2Cpp<HudBehaviour>();
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ModSettingsMenu>();
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to register MODS settings tab: {e}");
        }
        var harmony = new Harmony(Guid);
        Patches.Apply(harmony);
        SprocketMods.Localization.LocalizationHooks.Apply(harmony);
        AddComponent<HudBehaviour>();

        Log.LogInfo($"Loaded. HUD: {Cfg.ToggleKey.Value} (trajectory {Cfg.ToggleTrajectoryKey.Value}), " +
                    $"Arcade Battle Mode: {(Arcade.Enabled.Value ? "ON" : "OFF")} ({Arcade.ToggleKey.Value})");
    }
}
