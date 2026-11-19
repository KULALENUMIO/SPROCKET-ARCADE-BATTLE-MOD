using System;
using System.Collections.Generic;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketMods.Core;

/// <summary>
/// 車両の台帳（MOD の各機能で共有する）。
///
/// 以前はキルログ・戦力バー・修理・AI・砲弾の爆風などが、それぞれ 1 秒ごとにシーン全体から車両を探していた
/// （Object.FindObjectsByType。1 回数 ms かかり、見つけた車両ごとに一時オブジェクトもできる）。
/// ここではゲームが持っている当たり判定の一覧（DamageModel.models、弾が当たる車両の一覧。読むだけなら軽い）から車両を作る:
///   CompoundStructure → RigidBody → Vehicle（GetComponent）→ Vehicle.behaviour（VehicleBehaviour）
/// この対応は車両ごとに 1 回だけ引いて覚え、一覧の読み直しは 0.2 秒に 1 回まで。撃破で車両ごと消えると当たり判定も一覧から外れるので、
/// 「消えた」車両の扱いは以前と同じになる。
/// </summary>
internal static class VehicleRegistry
{
    public sealed class Entry
    {
        public IntPtr Key; // CompoundStructure のポインタ
        public CompoundStructure Structure;
        public Rigidbody Body;
        public IntPtr BodyPtr;
        public Vehicle Vehicle;
        public VehicleBehaviour Behaviour;
        public IntPtr BehaviourPtr;
    }

    private const float RefreshInterval = 0.2f;

    private static readonly List<Entry> Current = new();
    private static readonly List<VehicleBehaviour> CurrentBehaviours = new();
    private static readonly Dictionary<IntPtr, Entry> Known = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();
    private static float _nextRefresh;
    private static int _version;

    /// <summary>今いる車両（当たり判定が登録されているもの）。</summary>
    public static IReadOnlyList<Entry> Entries
    {
        get
        {
            Refresh();
            return Current;
        }
    }

    /// <summary>今いる車両の VehicleBehaviour。</summary>
    public static IReadOnlyList<VehicleBehaviour> Behaviours
    {
        get
        {
            Refresh();
            return CurrentBehaviours;
        }
    }

    /// <summary>一覧が変わるたびに増える。</summary>
    public static int Version
    {
        get
        {
            Refresh();
            return _version;
        }
    }

    public static void Refresh(bool force = false)
    {
        var now = Core.Fast.Unscaled;
        if (!force && now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshInterval;
        var perf = Debug.Perf.Begin();
        try
        {
            Rebuild();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Vehicle registry: refresh failed: {e.Message}");
        }
        Debug.Perf.End("VehicleRegistry", perf);
    }

    private static void Rebuild()
    {
        Seen.Clear();
        var changed = false;
        var models = DamageModel.models;
        var count = models?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var model = models[i];
            if (model == null)
                continue;
            var key = model.Pointer;
            Seen.Add(key);
            if (Known.TryGetValue(key, out var entry))
            {
                if (entry.Vehicle == null || entry.Vehicle.WasCollected)
                {
                    Known.Remove(key);
                    changed = true;
                }
                continue;
            }
            entry = Resolve(model);
            Known[key] = entry; // 車両でない当たり判定も null の Vehicle で覚えて、毎回引き直さない
            if (entry.Behaviour != null)
                changed = true;
        }
        Gone.Clear();
        foreach (var pair in Known)
        {
            if (!Seen.Contains(pair.Key))
                Gone.Add(pair.Key);
        }
        foreach (var key in Gone)
        {
            if (Known[key].Behaviour != null)
                changed = true;
            Known.Remove(key);
        }
        if (!changed && Current.Count > 0)
            return;
        Current.Clear();
        CurrentBehaviours.Clear();
        foreach (var entry in Known.Values)
        {
            if (entry.Behaviour == null || entry.Behaviour.WasCollected)
                continue;
            Current.Add(entry);
            CurrentBehaviours.Add(entry.Behaviour);
        }
        _version++;
    }

    private static Entry Resolve(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase model)
    {
        var entry = new Entry { Key = model.Pointer };
        try
        {
            var structure = model.TryCast<CompoundStructure>();
            var body = structure?.RigidBody;
            var vehicle = body != null ? body.GetComponent<Vehicle>() : null;
            var behaviour = vehicle != null ? vehicle.behaviour : null;
            entry.Structure = structure;
            entry.Body = body;
            entry.BodyPtr = body != null ? body.Pointer : IntPtr.Zero;
            entry.Vehicle = vehicle;
            entry.Behaviour = behaviour;
            entry.BehaviourPtr = behaviour != null ? behaviour.Pointer : IntPtr.Zero;
        }
        catch (Exception)
        {
            // 壊れかけの当たり判定は車両なしとして覚える
        }
        return entry;
    }
}
