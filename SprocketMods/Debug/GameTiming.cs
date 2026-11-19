using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SprocketMods.Debug;

/// <summary>
/// 開発用: ゲーム本体の主な処理の時間（perf on の間だけ数える）。
///  - GameController.SceneFixedUpdate: 物理の 1 ステップ全体（車両の処理 → Physics.Simulate → 車両の後処理）。
///    Physics.Simulate はこの中に埋め込まれていて単独では測れないので、全体から車両の処理を引いた分が物理
///  - GameController.SceneUpdate: 毎フレームの処理全体
///  - VehiclesGamePlugin: 車両の処理（毎フレーム・LateUpdate・物理の前後）
///  - ゲームモード・エフェクト・砲塔の軸の物理
/// 名前は "Game/..."（MOD の合計には足さない）。フレームの残り（描画など）は Perf の "Game/Other" に出す。
/// </summary>
internal static class GameTiming
{
    private static readonly Dictionary<MethodBase, string> Keys = new();

    public static void Patch(Harmony harmony)
    {
        Hook(harmony, typeof(Sprocket.GameControl.GameController), "SceneFixedUpdate");
        Hook(harmony, typeof(Sprocket.GameControl.GameController), "SceneUpdate");
        Hook(harmony, typeof(Sprocket.VehiclesGamePlugin), "GameUpdate");
        Hook(harmony, typeof(Sprocket.VehiclesGamePlugin), "GameLateUpdate");
        Hook(harmony, typeof(Sprocket.VehiclesGamePlugin), "GameFixedUpdate");
        Hook(harmony, typeof(Sprocket.VehiclesGamePlugin), "GameLateFixedUpdate");
        Hook(harmony, typeof(Sprocket.DeathmatchGameMode), "ModeUpdate");
        Hook(harmony, typeof(Sprocket.DeathmatchGameMode), "ModeFixedUpdate");
        Hook(harmony, typeof(Sprocket.EffectRegister), "Update");
        Hook(harmony, typeof(Sprocket.RotationAxisPhysicsRegister), "Update");
    }

    private static void Hook(Harmony harmony, Type type, string name)
    {
        try
        {
            var method = AccessTools.Method(type, name);
            if (method == null)
            {
                Plugin.Logger.LogWarning($"Game timing: {type.Name}.{name} not found");
                return;
            }
            Keys[method] = "Game/" + type.Name + "." + name;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GameTiming), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(GameTiming), nameof(Postfix)));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Game timing: {type.Name}.{name} not patched: {e.Message}");
        }
    }

    private static void Prefix(out long __state) => __state = Perf.Begin();

    private static void Postfix(long __state, MethodBase __originalMethod)
    {
        if (__state == 0)
            return;
        if (Keys.TryGetValue(__originalMethod, out var key))
            Perf.End(key, __state);
    }
}
