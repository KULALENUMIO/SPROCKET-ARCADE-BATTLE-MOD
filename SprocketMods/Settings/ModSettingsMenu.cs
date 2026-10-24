using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Injection;
using Sprocket.SettingConfiguration;
using Sprocket.UI;

namespace SprocketMods.Settings;

/// <summary>
/// ゲームの設定画面に追加する「MODS」タブ。
/// SettingsMenu は subMenus 配列からタブを作り、選択されたタブの DisplayGUI(IGUILayout) で項目を描くので、
/// SettingsSubMenu を継承したクラスを IL2CPP に注入し、配列の末尾に加える。
/// 値は BepInEx の設定に直接書き込む（即時反映・自動保存）。
/// </summary>
public class ModSettingsMenu : SettingsSubMenu
{
    public const string TabName = "MODS";

    public ModSettingsMenu(IntPtr ptr) : base(ptr)
    {
    }

    public ModSettingsMenu() : base(ClassInjector.DerivedConstructorPointer<ModSettingsMenu>())
    {
        ClassInjector.DerivedConstructorBody(this);
        MenuName = TabName;
    }

    public override void DisplayGUI(IGUILayout layout)
    {
        try
        {
            var drawer = layout?.TryCast<IGUIElementDrawer>();
            if (drawer != null)
                ModSettingsUi.Draw(drawer, this);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"MODS settings tab failed: {e}");
        }
    }

    /// <summary>設定画面の subMenus に MODS タブが無ければ追加する。</summary>
    public static void AddTo(SettingsMenu menu)
    {
        try
        {
            var subMenus = menu.subMenus;
            if (subMenus == null)
                return;
            for (var i = 0; i < subMenus.Length; i++)
            {
                if (subMenus[i]?.TryCast<ModSettingsMenu>() != null)
                    return;
            }
            var extended = new Il2CppReferenceArray<SettingsSubMenu>(subMenus.Length + 1);
            for (var i = 0; i < subMenus.Length; i++)
                extended[i] = subMenus[i];
            extended[subMenus.Length] = new ModSettingsMenu();
            menu.subMenus = extended;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Failed to add MODS settings tab: {e}");
        }
    }
}

/// <summary>
/// MODS タブの中身。注入クラスにマネージド型の引数を持つメソッドを置けないので分けてある。
/// 項目が多く 1 ページに収まらないので、上にページ切り替えのタブ（IGUIElementDrawer.TabGroup）を置き、
/// 各ページは 1 列（2 列にすると名前の欄が狭すぎて文字が詰まる）。名前の欄は約 200 px なので、訳は短めにする。
/// ページを切り替えたら SettingsSubMenu.GUIRepaint を立てると、SettingsMenu.UpdateMenu が描き直す。
/// </summary>
internal static class ModSettingsUi
{
    private static readonly string[] Pages = { "General", "Driving", "Weapons & camera", "Combat HUD", "Map", "Target HUD", "Ballistics HUD", "AI" };
    private static int _page;
    private static Il2CppStringArray _pageNames;

    public static void Draw(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var columns = d.ColumnCount;
        try
        {
            d.ColumnCount = 1;

            DrawPageTabs(d, menu);
            switch (_page)
            {
                case 0:
                    DrawLanguage(d, menu);
                    DrawAppearance(d, menu);
                    break;
                case 1:
                    DrawDriving(d, menu);
                    break;
                case 2:
                    DrawCamera(d, menu);
                    DrawAutocannons(d, menu);
                    DrawRepair(d, menu);
                    break;
                case 3:
                    DrawKillFeed(d, menu);
                    DrawHitReport(d, menu);
                    DrawDamageIndicator(d, menu);
                    break;
                case 4:
                    DrawTeamBar(d, menu);
                    DrawMinimap(d, menu);
                    break;
                case 5:
                    DrawTargetInfo(d, menu);
                    break;
                case 6:
                    DrawBallistics(d, menu);
                    break;
                default:
                    DrawAi(d, menu);
                    break;
            }
        }
        finally
        {
            d.EndRow();
            d.ColumnCount = columns;
        }
    }

    private static void DrawPageTabs(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        _pageNames ??= new Il2CppStringArray(Pages);
        if (_page < 0 || _page >= Pages.Length)
            _page = 0;
        var onClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(new Action<int>(index =>
        {
            if (index == _page || index < 0 || index >= Pages.Length)
                return;
            _page = index;
            menu.GUIRepaint = true;
        }));
        d.TabGroup(onClick, _page, _pageNames);
        d.AddSpace(14f);
    }

    private static void Section(IGUIElementDrawer d, string title)
    {
        d.AddSpace(8f);
        d.Header(title);
    }

    private static void DrawAppearance(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var st = Plugin.Style;
        Section(d, "HUD appearance");
        Slider(d, menu, "Panel background opacity", st.PanelOpacity.Value, 0f, 1f, v => st.PanelOpacity.Value = v);
        Slider(d, menu, "Panel size", st.Scale.Value, 0.6f, 1.6f, v => st.Scale.Value = v);
    }

    private static void DrawDriving(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var a = Plugin.Arcade;
        Section(d, "Arcade Battle Mode");
        Toggle(d, menu, "Arcade Battle Mode", a.Enabled.Value, v => a.Enabled.Value = v,
            "World of Tanks-like driving: smooth automatic shifting, quick acceleration and turning, stable grip and level braking. " +
            $"Toggle in game with {a.ToggleKey.Value}.");
        Toggle(d, menu, "Max engine torque", a.MaxTorque.Value, v => a.MaxTorque.Value = v,
            "Keep the engine at its peak torque at any RPM.");
        Toggle(d, menu, "Quick gear shifts", a.QuickShift.Value, v => a.QuickShift.Value = v,
            "Remove the crew's 1-3 s gear change delay and shorten gear engagement.");
        Toggle(d, menu, "Invert steering in reverse", a.InvertSteerInReverse.Value, v => a.InvertSteerInReverse.Value = v,
            "Steer like a car while reversing.");
        Slider(d, menu, "Acceleration (x engine power)", a.PowerMultiplier.Value, 0.25f, 5f, v => a.PowerMultiplier.Value = v);
        Slider(d, menu, "Max acceleration (m/s2)", a.MaxAcceleration.Value, 0.5f, 15f, v => a.MaxAcceleration.Value = v);
        Slider(d, menu, "Brake deceleration (m/s2)", a.BrakeDeceleration.Value, 0.5f, 20f, v => a.BrakeDeceleration.Value = v);
        Slider(d, menu, "Coast deceleration (m/s2)", a.CoastDeceleration.Value, 0.2f, 20f, v => a.CoastDeceleration.Value = v);
        Slider(d, menu, "Reverse speed (km/h)", a.MaxReverseSpeed.Value, 1f, 100f, v => a.MaxReverseSpeed.Value = v);
        Slider(d, menu, "Turn rate (deg/s)", a.TurnRate.Value, 5f, 180f, v => a.TurnRate.Value = v);
        Slider(d, menu, "Turn rate at speed (deg/s)", a.TurnRateAtSpeed.Value, 5f, 180f, v => a.TurnRateAtSpeed.Value = v);
        Slider(d, menu, "Turn response (rad/s2)", a.TurnAcceleration.Value, 0.5f, 30f, v => a.TurnAcceleration.Value = v);
        Slider(d, menu, "Lateral grip", a.LateralGrip.Value, 0f, 20f, v => a.LateralGrip.Value = v);
        Slider(d, menu, "Stability (pitch/roll damping)", a.Stability.Value, 0f, 20f, v => a.Stability.Value = v);
        Slider(d, menu, "Downforce (g)", a.Downforce.Value, 0f, 2f, v => a.Downforce.Value = v);

        Section(d, "Turret");
        Toggle(d, menu, "Snappy turret and gun", a.TurretAssist.Value, v => a.TurretAssist.Value = v,
            "Turret traverse and gun elevation move at full speed with no easing and stop instantly. Speed depends on the vehicle's era.");
        Slider(d, menu, "Turret speed (x era speed)", a.TurretSpeedMultiplier.Value, 0.25f, 5f, v => a.TurretSpeedMultiplier.Value = v);
    }

    private static void DrawCamera(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var c = Plugin.Camera;
        Section(d, "War Thunder-style right click");
        Toggle(d, menu, "Right-click camera controls", c.Enabled.Value, v => c.Enabled.Value = v,
            $"Key: {c.Key.Value}. Not active while giving orders or with the cursor shown.");
        Toggle(d, menu, "Scope: toggle max / min zoom", c.ScopeZoomToggle.Value, v => c.ScopeZoomToggle.Value = v,
            "While aiming through the gunner sight, switch between maximum and minimum zoom.");
        Toggle(d, menu, "Third person: toggle cupola view", c.CupolaView.Value, v => c.CupolaView.Value = v,
            "In third person, switch to the commander's cupola view and back.");
        Slider(d, menu, "Transition time (s)", c.TransitionTime.Value, 0.05f, 0.5f, v => c.TransitionTime.Value = v);
    }

    private static void DrawAutocannons(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var ac = Plugin.Autocannon;
        Section(d, "Autocannons");
        Toggle(d, menu, "Autocannons", ac.Enabled.Value, v => ac.Enabled.Value = v,
            "Cannons named with [AC magazine/rpm], e.g. [AC 10/450], fire automatically from a magazine. One shell from the rack is one magazine.");
        Toggle(d, menu, "No autocannon recoil", ac.NoRecoil.Value, v => ac.NoRecoil.Value = v,
            "No hull impulse, screen shake or barrel recoil when an autocannon fires.");
        Toggle(d, menu, "Show magazine counter", ac.ShowHud.Value, v => ac.ShowHud.Value = v,
            "Rounds left and magazine reload progress for your selected autocannon.");
        Toggle(d, menu, "No autocannon muzzle smoke", ac.NoMuzzleSmoke.Value, v => ac.NoMuzzleSmoke.Value = v,
            "Autocannons keep the firing sound and a brief flash, but leave no smoke.");
        Slider(d, menu, "Magazine reload time (s)", ac.MagazineReloadTime.Value, 0.5f, 30f, v => ac.MagazineReloadTime.Value = v);
        Slider(d, menu, "Autocannon muzzle flash (s)", ac.MuzzleFlashTime.Value, 0f, 0.3f, v => ac.MuzzleFlashTime.Value = v);
    }

    private static readonly string[] Difficulties = { "Recruit", "Regular", "Veteran", "Ace" };

    private static void DrawAi(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var ai = Plugin.Ai;
        Section(d, "AI");
        Toggle(d, menu, "AI improvements", ai.Enabled.Value, v => ai.Enabled.Value = v,
            "Makes AI tanks behave less foolishly: human-like aim and reaction, turret watching threats, hearing your shots, reacting to hits and safer driving. Off = the game's own AI.");
        var labels = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var name in Difficulties)
            labels.Add(name);
        var onSelect = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<int>>(new Action<int>(index =>
        {
            if (index >= 0 && index < Difficulties.Length)
                ai.Difficulty.Value = index;
        }));
        d.Dropdown("AI difficulty", labels.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(), Math.Clamp(ai.Difficulty.Value, 0, 3), onSelect,
            "Reaction time, aim error and how fast the AI corrects its aim.");

        Section(d, "AI behaviour");
        Toggle(d, menu, "Human-like aim", ai.AimModel.Value, v => ai.AimModel.Value = v,
            "Aim error grows with distance and when moving, and is corrected a little with each shot. Adds reaction time.");
        Toggle(d, menu, "Turret watches threats", ai.TurretWatch.Value, v => ai.TurretWatch.Value = v,
            "With no target, the turret points where the last shot came from, where an enemy was last seen, or toward the enemy side.");
        Toggle(d, menu, "AI hears your shots", ai.HearPlayerShots.Value, v => ai.HearPlayerShots.Value = v,
            "Your gunfire makes noise the AI turns toward (the game only does this for AI shots).");
        Toggle(d, menu, "React to hits", ai.HitReaction.Value, v => ai.HitReaction.Value = v,
            "When hit, the AI looks toward where the shot came from.");
        Toggle(d, menu, "Safe driving", ai.DrivingSafety.Value, v => ai.DrivingSafety.Value = v,
            "No long reversing (turns in place instead), re-plans after being pushed, backs off and retries when stuck.");
        Toggle(d, menu, "Abandon flipped or stuck vehicles", ai.AutoAbandon.Value, v => ai.AutoAbandon.Value = v,
            "The crew of an AI vehicle that has flipped over or is stuck for good bails out (counts as knocked out).");
        Slider(d, menu, "Abandon after flipped (s)", ai.FlipTime.Value, 3f, 60f, v => ai.FlipTime.Value = (float)Math.Round(v));
        Slider(d, menu, "Abandon after stuck (s)", ai.StuckTime.Value, 10f, 180f, v => ai.StuckTime.Value = (float)Math.Round(v));
        Toggle(d, menu, "AI log", ai.Log.Value, v => ai.Log.Value = v,
            "Writes AI hit rates by distance and shot number, and driving recoveries, to the log (for tuning).");
    }

    private static void DrawRepair(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var r = Plugin.Repair;
        Section(d, "Repair");
        Toggle(d, menu, "Repair", r.Enabled.Value, v => r.Enabled.Value = v,
            $"Hold {r.Key.Value} to repair everything except the engine (War Thunder style). Fires cannot be put out.");
        Toggle(d, menu, "AI repairs", r.AiRepair.Value, v => r.AiRepair.Value = v,
            "AI vehicles also repair themselves when damaged.");
        Toggle(d, menu, "Show repair hint", r.ShowHint.Value, v => r.ShowHint.Value = v,
            "When there is damage to repair, show the key and repair time at the bottom of the screen.");
        Slider(d, menu, "Repair time, crew of 4 (s)", r.BaseTime.Value, 5f, 90f, v => r.BaseTime.Value = (float)Math.Round(v));
    }

    private static void DrawKillFeed(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var kf = Plugin.KillFeed;
        Section(d, "Kill feed");
        Toggle(d, menu, "Kill feed", kf.Enabled.Value, v => kf.Enabled.Value = v,
            "Shows who destroyed whom at the top right of the screen.");
        Toggle(d, menu, "Show immobilized vehicles", kf.ShowImmobilized.Value, v => kf.ShowImmobilized.Value = v,
            "Also log vehicles that can no longer move but are still armed.");
        Slider(d, menu, "Kill feed lines", kf.MaxEntries.Value, 1f, 15f, v => kf.MaxEntries.Value = (int)Math.Round(v));
        Slider(d, menu, "Kill feed duration (s)", kf.Duration.Value, 2f, 30f, v => kf.Duration.Value = v);
        Slider(d, menu, "Kill feed text size", kf.FontSize.Value, 10f, 36f, v => kf.FontSize.Value = v);
    }

    private static void DrawHitReport(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var hr = Plugin.HitReport;
        Section(d, "Hit report");
        Toggle(d, menu, "Hit report", hr.Enabled.Value, v => hr.Enabled.Value = v,
            "Shows the result of your hits (penetration, ricochet...) and the modules and crew you damaged.");
        Toggle(d, menu, "Damaged module markers", hr.ShowMarkers.Value, v => hr.ShowMarkers.Value = v,
            "Briefly marks the position of damaged modules on the target.");
        Slider(d, menu, "Hit report duration (s)", hr.Duration.Value, 1f, 15f, v => hr.Duration.Value = v);
        Slider(d, menu, "Hit report text size", hr.FontSize.Value, 10f, 36f, v => hr.FontSize.Value = v);
    }

    private static void DrawDamageIndicator(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var di = Plugin.DamageIndicator;
        Section(d, "Damage indicator");
        Toggle(d, menu, "Damage indicator", di.Enabled.Value, v => di.Enabled.Value = v,
            "When your vehicle is hit, a red arc around the crosshair points to where the shot came from (War Thunder / WoT style).");
        Toggle(d, menu, "Show non-penetrating hits", di.ShowNonPenetrating.Value, v => di.ShowNonPenetrating.Value = v,
            "Also show hits that did not penetrate (orange) and ricochets (grey).");
        Slider(d, menu, "Damage indicator duration (s)", di.Duration.Value, 1f, 10f, v => di.Duration.Value = v);
        Slider(d, menu, "Damage indicator radius (px)", di.Radius.Value, 80f, 400f, v => di.Radius.Value = (float)Math.Round(v));
    }

    private static void DrawTeamBar(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var tb = Plugin.TeamBar;
        Section(d, "Team strength bar");
        Toggle(d, menu, "Team strength bar", tb.Enabled.Value, v => tb.Enabled.Value = v,
            "War Thunder-style bar at the top of the screen: friendly vehicles left (blue), enemies right (red). It shrinks as vehicles are knocked out. A guide only; the match does not end when it runs out.");
        Toggle(d, menu, "One segment per vehicle", tb.Segmented.Value, v => tb.Segmented.Value = v,
            "Split the bar into one segment per vehicle (a single bar when there are more than 40).");
    }

    private static void DrawMinimap(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var mm = Plugin.Minimap;
        Section(d, "Minimap");
        Toggle(d, menu, "Minimap", mm.Enabled.Value, v => mm.Enabled.Value = v,
            "War Thunder-style map at the bottom left: terrain colours, relief shading and contour lines, with friendly and enemy positions.");
        Slider(d, menu, "Minimap size", mm.Size.Value, 160f, 560f, v => mm.Size.Value = (float)Math.Round(v / 10f) * 10f);
        Toggle(d, menu, "Show enemies on map", mm.ShowEnemies.Value, v => mm.ShowEnemies.Value = v,
            "Show enemy positions on the minimap.");
        Toggle(d, menu, "Map grid", mm.ShowGrid.Value, v => mm.ShowGrid.Value = v,
            "Grid squares labelled A-1, B-2 ...");
        Toggle(d, menu, "View direction", mm.ShowViewDirection.Value, v => mm.ShowViewDirection.Value = v,
            "Lines showing where your camera is looking.");
        Toggle(d, menu, "Contour lines", mm.ShowContours.Value, v => mm.ShowContours.Value = v,
            "Draw contour lines on the map (the map is redrawn).");
    }

    private static void DrawTargetInfo(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var ti = Plugin.TargetInfo;
        Section(d, "Target info");
        Toggle(d, menu, "Target info", ti.Enabled.Value, v => ti.Enabled.Value = v,
            "Shows the name, side, knocked-out state, crew and engine condition of the vehicle under your crosshair.");
        Toggle(d, menu, "Show friendly vehicles", ti.ShowFriendly.Value, v => ti.ShowFriendly.Value = v,
            "Also shows information for friendly vehicles and wrecks.");
        Toggle(d, menu, "Show wrecks", ti.ShowWrecks.Value, v => ti.ShowWrecks.Value = v,
            "Also shows knocked-out vehicles (marked as enemy / friendly wreck).");
        Slider(d, menu, "Target info text size", ti.FontSize.Value, 10f, 36f, v => ti.FontSize.Value = v);
        Slider(d, menu, "Max distance (m)", ti.MaxDistance.Value, 100f, 10000f, v => ti.MaxDistance.Value = (float)Math.Round(v / 50f) * 50f);

        Section(d, "Shown information");
        Toggle(d, menu, "Vehicle name", ti.ShowName.Value, v => ti.ShowName.Value = v,
            "The vehicle's name next to enemy / friendly.");
        Toggle(d, menu, "Status", ti.ShowStatus.Value, v => ti.ShowStatus.Value = v,
            "Operational / immobilized / knocked out.");
        Toggle(d, menu, "Show target distance", ti.ShowDistance.Value, v => ti.ShowDistance.Value = v,
            "Shows the distance to the vehicle.");
        Toggle(d, menu, "Crew count", ti.ShowCrew.Value, v => ti.ShowCrew.Value = v,
            "Crew alive out of the full crew, with a bar.");
        Toggle(d, menu, "Crew details", ti.ShowCrewDetails.Value, v => ti.ShowCrewDetails.Value = v,
            "Roles of the crew killed and the number wounded.");
        Toggle(d, menu, "Engine", ti.ShowEngine.Value, v => ti.ShowEngine.Value = v,
            "Engine condition, always shown.");
        Toggle(d, menu, "Damaged modules", ti.ShowDamagedModules.Value, v => ti.ShowDamagedModules.Value = v,
            "Main gun, breech, tracks, ammo racks, fuel tanks, transmission and turret drive, when damaged.");
        Toggle(d, menu, "On fire", ti.ShowFire.Value, v => ti.ShowFire.Value = v,
            "Whether the vehicle is on fire.");
    }

    private static void DrawBallistics(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var h = Plugin.Cfg;
        Section(d, "Ballistics HUD");
        Toggle(d, menu, "Ballistics HUD", h.Enabled.Value, v => h.Enabled.Value = v,
            $"Impact crosshair with penetration indicator. Toggle in game with {h.ToggleKey.Value}.");
        Toggle(d, menu, "Show trajectory line", h.ShowTrajectory.Value, v => h.ShowTrajectory.Value = v,
            $"Toggle in game with {h.ToggleTrajectoryKey.Value}.");
        Toggle(d, menu, "Show distance", h.ShowDistance.Value, v => h.ShowDistance.Value = v, null);
        Toggle(d, menu, "Show verdict text", h.ShowVerdictText.Value, v => h.ShowVerdictText.Value = v, "PEN / NO PEN / RICOCHET ...");
        Toggle(d, menu, "Show impact details", h.ShowDetails.Value, v => h.ShowDetails.Value = v, "Impact speed, flight time and angle.");
        Slider(d, menu, "Crosshair size (px)", h.ReticleSize.Value, 16f, 128f, v => h.ReticleSize.Value = v);
    }

    /// <summary>言語の選択肢は Languages フォルダから作るので、フォルダを足せば選べる言語も増える。</summary>
    private static void DrawLanguage(IGUIElementDrawer d, ModSettingsMenu menu)
    {
        var l = Plugin.Localization;
        if (l == null)
            return;
        Section(d, "Language");

        const string auto = "auto";
        var values = new System.Collections.Generic.List<string> { auto, Localization.LocalizationManager.Off };
        values.AddRange(l.AvailableLanguages());
        var labels = new Il2CppSystem.Collections.Generic.List<string>();
        labels.Add("Auto (Windows language)");
        labels.Add("Off (English)");
        for (var i = 2; i < values.Count; i++)
            labels.Add(values[i]);

        var selected = values.FindIndex(v => v.Equals(l.Language.Value, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
            selected = 0;
        var onSelect = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<int>>(new Action<int>(index =>
        {
            if (index >= 0 && index < values.Count)
                l.Language.Value = values[index];
        }));
        d.Dropdown("Language", labels.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(), selected, onSelect,
            "Display language. Translations are in BepInEx/plugins/SprocketMods/Languages.");
        Toggle(d, menu, "Collect untranslated texts", l.CollectUntranslated.Value, v => l.CollectUntranslated.Value = v,
            "Saves texts without a translation to Languages/<language>/_untranslated.txt so they can be translated later.");
    }

    private static void Toggle(IGUIElementDrawer d, ModSettingsMenu menu, string label, bool value, Action<bool> set, string tooltip)
    {
        var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(set);
        d.ToggleField(label, value, callback, tooltip ?? string.Empty);
    }

    private static void Slider(IGUIElementDrawer d, ModSettingsMenu menu, string label, float value, float min, float max, Action<float> set)
    {
        var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(new Action<float>(v => set((float)Math.Round(v, 2))));
        d.Slider(label, value, min, max, callback);
    }
}
