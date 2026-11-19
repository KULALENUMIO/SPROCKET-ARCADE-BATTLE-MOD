using System;
using HarmonyLib;
using Sprocket.SettingConfiguration;
using Sprocket.Vehicles.Powertrains;
using SprocketMods.Arcade;
using SprocketMods.Settings;
using SprocketMods.Weapons;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.WeaponFramework;

namespace SprocketMods;

/// <summary>ゲームのメソッドへの Harmony パッチ。失敗しても他の機能は動くように個別に当てる。</summary>
internal static class Patches
{
    public static void Apply(Harmony harmony)
    {
        TryPatch(harmony, typeof(PowertrainBehaviour), nameof(PowertrainBehaviour.FixedUpdate),
            prefix: nameof(PowertrainFixedUpdatePrefix));
        TryPatch(harmony, typeof(Sprocket.Vehicles.CrewSystems.Driver), nameof(Sprocket.Vehicles.CrewSystems.Driver.ExecutePerform),
            prefix: nameof(DriverPerformPrefix));
        TryPatch(harmony, typeof(Sprocket.EffectRegister), nameof(Sprocket.EffectRegister.Release), prefix: nameof(EffectReleasePrefix));
        TryPatch(harmony, typeof(Sprocket.Vehicles.VehicleRendererAtlaser), nameof(Sprocket.Vehicles.VehicleRendererAtlaser.Release),
            prefix: nameof(AtlaserReleasePrefix));
        TryPatch(harmony, typeof(Sprocket.VehicleControl.VehicleControlPlayerState), nameof(Sprocket.VehicleControl.VehicleControlPlayerState.InternalUpdate),
            prefix: nameof(VehicleControlUpdatePrefix));
        TryPatch(harmony, typeof(Sprocket.VehicleControl.VehicleControlPlayerState), nameof(Sprocket.VehicleControl.VehicleControlPlayerState.InternalLateUpdate),
            prefix: nameof(VehicleControlUpdatePrefix));
        TryPatch(harmony, typeof(Sprocket.Vehicles.PlateStructures.Design.PlateStructureEditor),
            nameof(Sprocket.Vehicles.PlateStructures.Design.PlateStructureEditor.OnGUI), postfix: nameof(PlateStructureOnGuiPostfix));
        TryPatch(harmony, typeof(Sprocket.ArtificialIntelligence.CommanderAI), nameof(Sprocket.ArtificialIntelligence.CommanderAI.CollectCombatInfo),
            postfix: nameof(CollectCombatInfoPostfix));
        TryPatch(harmony, typeof(Sprocket.ArtificialIntelligence.PrioritizedTargetBuffer), nameof(Sprocket.ArtificialIntelligence.PrioritizedTargetBuffer.Update),
            postfix: nameof(PrioritizeTargetsPostfix));
        TryPatch(harmony, typeof(Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask),
            nameof(Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask.Update), postfix: nameof(FindAndAimPostfix));
        TryPatch(harmony, typeof(Sprocket.RotationAxisPhysicsRegister), nameof(Sprocket.RotationAxisPhysicsRegister.Update),
            prefix: nameof(AxisUpdatePrefix), postfix: nameof(AxisUpdatePostfix));
        TryPatch(harmony, typeof(SettingsMenu), nameof(SettingsMenu.SetEditProfile),
            postfix: nameof(SettingsMenuPostfix));
        TryPatch(harmony, typeof(SettingsMenu), nameof(SettingsMenu.SetupMenuButtons),
            prefix: nameof(SettingsMenuPrefix));

        // 機関砲
        TryPatch(harmony, typeof(CannonBehaviour), nameof(CannonBehaviour.FireInternal),
            prefix: nameof(FireInternalPrefix), postfix: nameof(FireInternalPostfix));
        TryPatch(harmony, AccessTools.Method(typeof(WeaponBehaviour), nameof(WeaponBehaviour.RequestReload), new[] { typeof(Il2CppSystem.EventArgs) }),
            prefix: nameof(RequestReloadPrefix));
        TryPatch(harmony, AccessTools.PropertyGetter(typeof(WeaponBehaviour), nameof(WeaponBehaviour.CanFire)),
            postfix: nameof(CanFirePostfix));
        TryPatch(harmony, typeof(LoadTask), nameof(LoadTask.Update), prefix: nameof(LoadTaskUpdatePrefix));
        TryPatch(harmony, typeof(LoadTask), nameof(LoadTask.SetState), postfix: nameof(LoadTaskSetStatePostfix));
        TryPatch(harmony, AccessTools.PropertyGetter(typeof(LoadTask), nameof(LoadTask.LoadFraction)),
            postfix: nameof(LoadFractionPostfix));
        TryPatch(harmony, AccessTools.PropertyGetter(typeof(LoadTask), nameof(LoadTask.RemainingTime)),
            postfix: nameof(RemainingTimePostfix));
        TryPatch(harmony, typeof(Cannon), nameof(Cannon.OnFire), prefix: nameof(RecoilPrefix));
        TryPatch(harmony, typeof(Sprocket.Vehicles.Fires.MuzzleFlashEffect), nameof(Sprocket.Vehicles.Fires.MuzzleFlashEffect.Setup),
            prefix: nameof(MuzzleSetupPrefix), postfix: nameof(MuzzleSetupPostfix));
        TryPatch(harmony, typeof(VehicleController), nameof(VehicleController.OnWeaponFire), prefix: nameof(RecoilPrefix));

        // 砲弾の種類（Ammo/AmmoSystem.cs）: すべての貫通計算の入口で、自分の徹甲榴弾・榴弾の貫通力を下げ、抜けたら炸裂させる
        TryPatch(harmony, typeof(Sprocket.DamageModelling.CompoundStructure), nameof(Sprocket.DamageModelling.CompoundStructure.Penetrate),
            prefix: nameof(PenetratePrefix), postfix: nameof(PenetratePostfix));

        // 履帯の帯の当たり判定（Tracks/TrackDamage.cs）と、撃破だけで決める勝敗（KillFeed/DefeatRule.cs）
        TryPatch(harmony, typeof(Sprocket.Vehicles.Tracks.TrackAssembly), nameof(Sprocket.Vehicles.Tracks.TrackAssembly.BuildDamageModelColliders),
            postfix: nameof(TrackCollidersPostfix));
        TryPatch(harmony, typeof(Sprocket.MissionFramework.ImmobilizeObjective), nameof(Sprocket.MissionFramework.ImmobilizeObjective.UpdateObjective),
            postfix: nameof(ImmobilizeObjectivePostfix));

        // ヒットカメラ: 貫通計算の結果を受け取る
        TryPatch(harmony, typeof(Sprocket.Vehicles.Vehicle), nameof(Sprocket.Vehicles.Vehicle.OnDamageModelSimulationRan),
            postfix: nameof(SimulationRanPostfix));
    }

    private static void PenetratePrefix(ref Sprocket.DamageModelling.PenetratorInfo penetrator, out int __state)
    {
        __state = -1;
        try
        {
            __state = SprocketMods.Ammo.AmmoSystem.BeforePenetrate(ref penetrator);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Ammo (before penetration) failed: {e}");
        }
    }

    private static void PenetratePostfix(Sprocket.DamageModelling.CompoundStructure __instance, Sprocket.DamageModelling.PenetratorInfo penetrator,
        Sprocket.DamageModelling.IPenetrationSimulation __result, int __state)
    {
        if (__state < 0)
            return;
        try
        {
            SprocketMods.Ammo.AmmoSystem.AfterPenetrate(__instance, penetrator, __result, __state);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Ammo (after penetration) failed: {e}");
        }
    }

    private static void TrackCollidersPostfix(Sprocket.Vehicles.Tracks.TrackAssembly __instance,
        Sprocket.Vehicles.Colliders.IVehicleComponentDamageModelBuilder builder)
    {
        Guard(() => SprocketMods.Tracks.TrackDamage.AfterBuildColliders(__instance, builder));
    }

    private static void ImmobilizeObjectivePostfix(Sprocket.MissionFramework.ImmobilizeObjective __instance,
        ref Sprocket.MissionFramework.ObjectiveResult __result)
    {
        try
        {
            SprocketMods.KillFeed.DefeatRule.AfterUpdate(__instance, ref __result);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Defeat rule failed: {e}");
        }
    }

    private static void SimulationRanPostfix(Sprocket.Vehicles.Vehicle __instance, Sprocket.DamageModelling.PenetrationSimulationRunEvent e)
    {
        Guard(() => SprocketMods.HitCam.HitCamTracker.Instance?.OnSimulation(__instance, e));
    }

    /// <summary>サンドボックスの車両一覧を開いている間は、走行の操作とカメラを止めて背景を固定する（一覧の処理は止めない）。</summary>
    /// <summary>破棄済みのエフェクトは解放しない。例外になるとメニューへ戻るシーン遷移ごと止まる。</summary>
    private static bool EffectReleasePrefix(Sprocket.EffectRegister __instance, int index)
    {
        try
        {
            return !SprocketMods.Design.SandboxSpawner.SkipDestroyedEffect(__instance, index);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
            return true;
        }
    }

    private static void AtlaserReleasePrefix(Sprocket.Vehicles.VehicleRendererAtlaser __instance)
    {
        Guard(() => SprocketMods.Design.SandboxSpawner.ForgetDestroyedRenderers(__instance));
    }

    private static bool VehicleControlUpdatePrefix()
    {
        return !SprocketMods.Design.SandboxSpawner.Selecting;
    }

    /// <summary>砲塔の編集欄に円柱の項目を足す（処理は Design/CylinderTurret.cs）。</summary>
    private static void PlateStructureOnGuiPostfix(Sprocket.Vehicles.PlateStructures.Design.PlateStructureEditor __instance, Sprocket.UI.IGUILayout layout)
    {
        Guard(() => SprocketMods.Design.CylinderTurret.DrawGui(__instance, layout));
    }

    private static void TryPatch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null)
    {
        System.Reflection.MethodInfo original;
        try
        {
            original = AccessTools.Method(type, method);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Failed to find {type.Name}.{method}: {e}");
            return;
        }
        TryPatch(harmony, original, prefix, postfix);
    }

    private static void TryPatch(Harmony harmony, System.Reflection.MethodInfo original, string prefix = null, string postfix = null)
    {
        try
        {
            if (original == null)
                throw new MissingMethodException("method not found");
            harmony.Patch(original,
                prefix: prefix != null ? new HarmonyMethod(typeof(Patches), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(Patches), postfix) : null);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Failed to patch {original?.DeclaringType?.Name}.{original?.Name}: {e}");
        }
    }

    /// <summary>
    /// Driver は毎回 GearShiftDelay に「1〜3 秒」を書き込むので、駆動系が使う直前に上書きする。
    /// </summary>
    private static void PowertrainFixedUpdatePrefix(PowertrainBehaviour __instance)
    {
        var arcade = ArcadeController.Instance;
        if (arcade == null || !Plugin.Arcade.Enabled.Value)
            return;
        if (__instance.Pointer == arcade.TunedPowertrain)
            arcade.BeforePowertrainFixedUpdate(__instance);
    }

    /// <summary>修理中の車両は操縦手の入力（前後・左右）を 0 にする。エンジン・変速機が WASD に一切反応しなくなる。</summary>
    private static void DriverPerformPrefix(Sprocket.Vehicles.CrewSystems.Driver __instance)
    {
        if (SprocketMods.Repair.RepairSystem.IsDrivingBlocked(__instance.Pointer))
        {
            Guard(() => __instance.Solution = default);
            return;
        }
        Guard(() => SprocketMods.Ai.AiDirector.Instance?.FilterDrive(__instance));
    }

    /// <summary>AI の作戦を「その場で撃ち合う」に寄せ、動く判断は MOD の戦術でする（処理は Ai/AiTactics.cs）。</summary>
    private static void CollectCombatInfoPostfix(Sprocket.ArtificialIntelligence.CommanderAI __instance, Sprocket.ArtificialIntelligence.CombatStrategy output)
    {
        Guard(() => SprocketMods.Ai.AiDirector.Instance?.AfterCollectCombatInfo(__instance, output));
    }

    /// <summary>AI の目標の優先順位を付け直す（処理は Ai/AiTactics.cs）。</summary>
    private static void PrioritizeTargetsPostfix(Sprocket.ArtificialIntelligence.PrioritizedTargetBuffer __instance)
    {
        Guard(() => SprocketMods.Ai.AiDirector.Instance?.AfterPrioritize(__instance));
    }

    /// <summary>AI の照準に自前の誤差と反応の遅れを足し、目標がいなければ砲塔を見張りの方向へ（処理は Ai/AiDirector.cs）。</summary>
    private static void FindAndAimPostfix(Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask __instance,
        ref Sprocket.ArtificialIntelligence.GunLayerTaskInfo info, ref Sprocket.ArtificialIntelligence.GunLayerInstructions output,
        ref Sprocket.ArtificialIntelligence.GunLayerTaskResult __result)
    {
        try
        {
            SprocketMods.Ai.AiDirector.Instance?.AdjustAim(__instance.Pointer, info.PivotPosition, ref output, ref __result);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
        }
    }

    /// <summary>自車の砲塔・砲架の軸はゲームの加減速物理をスキップさせ、TurretAssist が動かす。</summary>
    private static void AxisUpdatePrefix() => TurretAssist.Instance?.BeforeAxisUpdate();

    private static void AxisUpdatePostfix(float deltaTime) => TurretAssist.Instance?.AfterAxisUpdate(deltaTime);

    private static void SettingsMenuPostfix(SettingsMenu __instance) => ModSettingsMenu.AddTo(__instance);

    private static void SettingsMenuPrefix(SettingsMenu __instance) => ModSettingsMenu.AddTo(__instance);

    // ------------------------------------------------------------------ 機関砲（処理は Weapons/Autocannons.cs）

    private static void FireInternalPrefix(CannonBehaviour __instance, out IntPtr __state) =>
        Autocannons.BeforeFire(__instance, out __state);

    private static void FireInternalPostfix(CannonBehaviour __instance, IntPtr __state, WeaponFireInfo __result)
    {
        Guard(() => Autocannons.AfterFire(__instance, __state));
        var projectileId = __result.ProjectileID;
        Guard(() => SprocketMods.KillFeed.KillTracker.Instance?.OnShot(__instance, projectileId));
        Guard(() => SprocketMods.Hits.HitTracker.Instance?.OnShot(__instance, projectileId));
        Guard(() => SprocketMods.HitCam.HitCamTracker.Instance?.OnShot(__instance, projectileId));
        Guard(() => SprocketMods.Ammo.AmmoSystem.Instance?.OnShot(__instance, projectileId));
        Guard(() => SprocketMods.Hits.DamageIndicator.Instance?.OnShot(__instance, projectileId));
        Guard(() => SprocketMods.Ai.AiDirector.Instance?.OnShot(__instance, projectileId));
    }

    private static bool RequestReloadPrefix(WeaponBehaviour __instance)
    {
        try
        {
            return Autocannons.BeforeRequestReload(__instance);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
            return true;
        }
    }

    private static void CanFirePostfix(WeaponBehaviour __instance, ref bool __result)
    {
        if (!__result)
            return;
        try
        {
            __result = Autocannons.FilterCanFire(__instance, true);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
        }
    }

    private static bool LoadTaskUpdatePrefix(LoadTask __instance, float travelSpeed, float manipulationSpeed, float deltaTime)
    {
        try
        {
            return Autocannons.ReplaceLoadTaskUpdate(__instance, travelSpeed, manipulationSpeed, deltaTime);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
            return true;
        }
    }

    private static void LoadTaskSetStatePostfix(LoadTask __instance, LoadState newState) =>
        Guard(() => Autocannons.AfterSetState(__instance, newState));

    private static void LoadFractionPostfix(LoadTask __instance, ref float __result)
    {
        try
        {
            __result = Autocannons.FilterLoadFraction(__instance, __result);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
        }
    }

    private static void RemainingTimePostfix(LoadTask __instance, ref float __result)
    {
        try
        {
            __result = Autocannons.FilterRemainingTime(__instance, __result);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
        }
    }

    /// <summary>Cannon.OnFire（車体への反動）と VehicleController.OnWeaponFire（画面の揺れ）。機関砲なら飛ばす。</summary>
    private static bool RecoilPrefix(Il2CppSystem.Object sender)
    {
        try
        {
            return !Autocannons.SkipRecoil(sender);
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
            return true;
        }
    }

    /// <summary>発砲の閃光・煙（MuzzleFlashEffect）。機関砲なら煙を消す（Weapons/AutocannonMuzzle.cs）。</summary>
    private static void MuzzleSetupPrefix(Sprocket.Vehicles.Fires.MuzzleFlashEffect __instance) =>
        Guard(() => AutocannonMuzzle.BeforeSetup(__instance));

    private static void MuzzleSetupPostfix(Sprocket.Vehicles.Fires.MuzzleFlashEffect __instance) =>
        Guard(() => AutocannonMuzzle.AfterSetup(__instance));

    private static float _nextAutocannonErrorLog;

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            LogAutocannonError(e);
        }
    }

    private static void LogAutocannonError(Exception e)
    {
        if (UnityEngine.Time.unscaledTime < _nextAutocannonErrorLog)
            return;
        _nextAutocannonErrorLog = UnityEngine.Time.unscaledTime + 5f;
        Plugin.Logger.LogError($"Autocannon failed: {e}");
    }
}
