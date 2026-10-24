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
