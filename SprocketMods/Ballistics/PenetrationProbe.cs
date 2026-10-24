using System;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.DamageModelling;
using UnityEngine;

namespace SprocketMods;

internal enum TargetKind
{
    None,
    Terrain,
    OwnVehicle,
    Friendly,
    Enemy,
}

internal enum PenVerdict
{
    Unknown,
    Penetrate,
    Partial,
    NoPenetrate,
    Ricochet,
    NotSimulated,
}

internal struct ProbeResult
{
    public TargetKind Target;
    public PenVerdict Verdict;
    public int Samples;
    public int Penetrations;
    public int Ricochets;
    public float ImpactAngle;
}

/// <summary>
/// 着弾点でゲーム本体の貫通シミュレーター CompoundStructure.Penetrate を実行して判定する。
///
/// PenetratorInfo は ArmourPiercingProjectileFunction.HitDamageModel と同じ値で作る:
///   Mass/Diameter = 弾定義, Density = ProjectileConstants.MetalDensity, MaxNormalizationAngle = 5,
///   PenetratorConstant = AP 機能の値, Position = 着弾点, Velocity = 着弾時の速度
/// 判定は最初の破片（元の弾体の第一装甲への衝突）のフラグ Penetrated / Bounced / Absorbed を見る
/// （ゲームが着弾痕エフェクトを決めるのと同じ情報）。
///
/// Penetrate() 自体は純粋な計算だが、最後に SimulationRan イベントを発火し、その購読側で
/// 被弾処理（ダメージ・カメラ揺れ・AI の反応）が走る。予測のための呼び出しでは
/// イベントを一時的に外してから呼び、すぐに元へ戻す。
/// </summary>
internal sealed class PenetrationProbe
{
    private ProbeResult _last;
    private float _lastTime = float.NegativeInfinity;
    private IntPtr _lastModel;
    private Vector3 _lastPoint;
    private int _lastShell;

    private IntPtr _ownBody;
    private TeamID _ownTeam;
    private bool _ownModelFound;
    private float _nextOwnSearch;

    public ProbeResult Evaluate(in WeaponState weapon, in ImpactInfo impact)
    {
        if (impact.Kind == ImpactKind.None)
            return new ProbeResult { Target = TargetKind.None };
        if (impact.Kind == ImpactKind.Terrain || impact.Model == null)
            return new ProbeResult { Target = TargetKind.Terrain };

        var cs = impact.Model;

        var target = Classify(weapon, cs);
        if (target != TargetKind.Enemy && target != TargetKind.Friendly)
            return new ProbeResult { Target = target };

        var cfg = Plugin.Cfg;
        var now = Time.unscaledTime;
        var unchanged = cs.Pointer == _lastModel && weapon.Shell.TypeId == _lastShell
                        && (impact.Point - _lastPoint).sqrMagnitude < 0.0004f;
        if (unchanged || now - _lastTime < cfg.PenetrationUpdateInterval.Value && cs.Pointer == _lastModel)
        {
            var cached = _last;
            cached.Target = target;
            return cached;
        }

        var result = new ProbeResult
        {
            Target = target,
            ImpactAngle = Vector3.Angle(-impact.Velocity, impact.Normal),
        };

        if (!weapon.Shell.HasArmourPiercing)
        {
            result.Verdict = PenVerdict.NotSimulated;
        }
        else
        {
            RunSamples(weapon, impact, cs, ref result);
        }

        _last = result;
        _lastTime = now;
        _lastModel = cs.Pointer;
        _lastPoint = impact.Point;
        _lastShell = weapon.Shell.TypeId;
        return result;
    }

    private void RunSamples(in WeaponState weapon, in ImpactInfo impact, CompoundStructure cs, ref ProbeResult result)
    {
        var shell = weapon.Shell;
        var density = ProjectileConstants.MetalDensity;

        var cfg = Plugin.Cfg;
        var sampleCount = Mathf.Max(1, cfg.PenetrationSamples.Value);
        var radius = shell.Diameter * cfg.SampleRadiusCalibers.Value;
        var dir = impact.Velocity.normalized;
        var right = Vector3.Cross(dir, Vector3.up);
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.Cross(dir, Vector3.forward);
        right.Normalize();
        var up = Vector3.Cross(right, dir);

        var penetrated = 0;
        var ricochets = 0;
        var valid = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            Vector3 point;
            if (i == 0 || radius <= 0f)
            {
                point = impact.Point;
            }
            else
            {
                var angle = (i - 1) * Mathf.PI * 2f / (sampleCount - 1);
                var offset = (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius;
                var back = Mathf.Max(0.5f, radius * 4f);
                var origin = impact.Point + offset - dir * back;
                if (!cs.Raycast(origin, dir, back * 2f, out DamageModelRaycastHit sampleHit))
                    continue;
                point = origin + dir * sampleHit.Distance;
            }

            var info = new PenetratorInfo(shell.Mass, shell.Diameter, density, GameAccess.MaxNormalizationAngle,
                shell.PenetratorConstant, point, impact.Velocity);
            var outcome = Simulate(cs, ref info);
            if (outcome == PenVerdict.Unknown)
                continue;
            valid++;
            if (outcome == PenVerdict.Penetrate)
                penetrated++;
            else if (outcome == PenVerdict.Ricochet)
                ricochets++;
        }

        result.Samples = valid;
        result.Penetrations = penetrated;
        result.Ricochets = ricochets;
        if (valid == 0)
            result.Verdict = PenVerdict.Unknown;
        else if (penetrated == valid)
            result.Verdict = PenVerdict.Penetrate;
        else if (penetrated > 0)
            result.Verdict = PenVerdict.Partial;
        else if (ricochets * 2 > valid)
            result.Verdict = PenVerdict.Ricochet;
        else
            result.Verdict = PenVerdict.NoPenetrate;

        if (cfg.DebugLog.Value)
        {
            Plugin.Logger.LogInfo(
                $"Probe {result.Verdict} pen={penetrated}/{valid} ric={ricochets} v={impact.Velocity.magnitude:0}m/s " +
                $"angle={result.ImpactAngle:0.0} path={impact.PathLength:0}m");
        }
    }

    private static PenVerdict Simulate(CompoundStructure cs, ref PenetratorInfo info)
    {
        var internalHandler = cs.SimulationRanInternal;
        var publicHandler = cs.SimulationRan;
        IPenetrationSimulation sim;
        cs.SimulationRanInternal = null;
        cs.SimulationRan = null;
        try
        {
            sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default);
        }
        finally
        {
            cs.SimulationRanInternal = internalHandler;
            cs.SimulationRan = publicHandler;
        }
        return sim == null ? PenVerdict.Unknown : SimulationReader.ReadFirstFragment(sim.Pointer);
    }

    /// <summary>着弾した車両が自車・味方・敵のどれか。チームは CompoundStructure.ID(EntityID).TeamID。</summary>
    private TargetKind Classify(in WeaponState weapon, CompoundStructure cs)
    {
        var ownBody = weapon.OwnBody != null ? weapon.OwnBody.Pointer : IntPtr.Zero;
        var targetBody = cs.RigidBody;
        if (ownBody != IntPtr.Zero && targetBody != null && targetBody.Pointer == ownBody)
            return TargetKind.OwnVehicle;

        UpdateOwnTeam(ownBody);
        var team = cs.ID.TeamID;
        if (_ownModelFound && _ownTeam != TeamID.None && team == _ownTeam)
            return TargetKind.Friendly;
        return TargetKind.Enemy;
    }

    private void UpdateOwnTeam(IntPtr ownBody)
    {
        if (ownBody != _ownBody)
        {
            _ownBody = ownBody;
            _ownModelFound = false;
            _nextOwnSearch = 0f;
        }
        if (_ownModelFound || ownBody == IntPtr.Zero || Time.unscaledTime < _nextOwnSearch)
            return;
        _nextOwnSearch = Time.unscaledTime + 1f;

        var models = DamageModel.models;
        if (models == null)
            return;
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var body = model?.RigidBody;
            if (body == null || body.Pointer != ownBody)
                continue;
            _ownTeam = model.ID.TeamID;
            _ownModelFound = true;
            if (Plugin.Cfg.DebugLog.Value)
                Plugin.Logger.LogInfo($"Own vehicle team: {_ownTeam}");
            return;
        }
    }
}

/// <summary>
/// PenetrationSimulation の結果をメモリから直接読む。
/// GetFragment(out PenetrationFragment) は参照型を含む構造体の out 引数で、Il2CppInterop 経由だとメモリを壊すため使わない。
/// fragments は Fragment 構造体がそのまま並ぶ配列（interop の型は Il2CppReferenceArray だが実体は値型配列）。
/// オフセットは IL2CPP のメタデータから実行時に取得する。
/// </summary>
internal static unsafe class SimulationReader
{
    // Fragment.flags (FragmentFlag) のビット
    private const ushort FlagDeflected = 16;
    private const ushort FlagEmbedded = 32;
    private const ushort FlagPenetrated = 64;

    private static bool _resolved;
    private static int _fragmentsOffset;
    private static int _fragCountOffset;
    private static int _flagsOffset;
    private static int _hitStructureOffset;

    public static PenVerdict ReadFirstFragment(IntPtr sim)
    {
        if (sim == IntPtr.Zero || !Resolve())
            return PenVerdict.Unknown;

        var count = *(int*)(sim + _fragCountOffset);
        var array = *(IntPtr*)(sim + _fragmentsOffset);
        if (count <= 0 || array == IntPtr.Zero || *(int*)(array + 0x18) <= 0)
            return PenVerdict.Unknown;

        var first = (byte*)array + 0x20; // Il2CppArray のヘッダ直後が要素 0
        if (*(short*)(first + _hitStructureOffset) == -1)
            return PenVerdict.Unknown; // PenetrationSimulation.Hit == false と同じ条件

        var flags = *(ushort*)(first + _flagsOffset);
        if ((flags & FlagPenetrated) != 0)
            return PenVerdict.Penetrate;
        if ((flags & FlagDeflected) != 0)
            return PenVerdict.Ricochet;
        if ((flags & FlagEmbedded) != 0)
            return PenVerdict.NoPenetrate;
        return PenVerdict.Unknown;
    }

    public static bool Resolve()
    {
        if (_resolved)
            return _fragmentsOffset > 0;
        _resolved = true;

        var simClass = Il2CppClassPointerStore<PenetrationSimulation>.NativeClassPtr;
        var fragmentClass = Il2CppClassPointerStore<Fragment>.NativeClassPtr;
        _fragmentsOffset = FieldOffset(simClass, "fragments");
        _fragCountOffset = FieldOffset(simClass, "fragCount");
        // 値型のフィールドオフセットはボックス化時のヘッダ込みなので、先頭フィールド diameter の位置を基準にする
        var structBase = FieldOffset(fragmentClass, "diameter");
        _flagsOffset = FieldOffset(fragmentClass, "flags") - structBase;
        _hitStructureOffset = FieldOffset(fragmentClass, "hitStructureIndex") - structBase;

        var ok = structBase >= 0 && _fragmentsOffset > 0 && _fragCountOffset > 0 && _flagsOffset > 0 && _hitStructureOffset > 0;
        Plugin.Logger.LogInfo(
            $"PenetrationSimulation layout: fragments=0x{_fragmentsOffset:X} fragCount=0x{_fragCountOffset:X} " +
            $"flags=0x{_flagsOffset:X} hitStructureIndex=0x{_hitStructureOffset:X} ({(ok ? "ok" : "FAILED")})");
        if (!ok)
            _fragmentsOffset = 0;
        return ok;
    }

    private static int FieldOffset(IntPtr klass, string name)
    {
        if (klass == IntPtr.Zero)
            return -1;
        var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        return field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
    }
}
