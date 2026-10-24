using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Sprocket.DamageModelling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods;

internal enum ImpactKind
{
    None,
    DamageModel,
    Terrain,
}

internal struct ImpactInfo
{
    public ImpactKind Kind;
    public Vector3 Point;
    public Vector3 Normal;
    public Vector3 Velocity;
    public float PathLength;
    public float FlightTime;
    public CompoundStructure Model;
}

/// <summary>
/// ゲームの ProjectileInstance.Update と同じ積分で弾道を予測する（手順）:
///   a = g - v̂ · CalculateDragForce(|v|) / Mass
///   v += a·dt;  p += v·dt        （半陰的オイラー、dt = FixedUpdate 間隔）
///   |v| が MinSpeed 未満、または寿命 Lifetime を超えたら消滅
///
/// 当たり判定はステップをまとめた弦（ChordTime 秒ぶん）で行い、当たった弦だけ 1 ステップずつ判定し直す。
/// 車両は CompoundStructure.Raycast、地形は Physics.Raycast。
/// （DamageModel.Raycast は戻り値の DamageModelHit が参照型を含む構造体で、Il2CppInterop 経由の
///   out 引数受け渡しがメモリを壊してゲームが落ちるため使わない）
/// </summary>
internal sealed class BallisticSolver
{
    private const float ChordTime = 0.04f;
    private const int MaxSteps = 40000;

    public readonly List<Vector3> Points = new(1024);
    public ImpactInfo Impact;

    private readonly List<Vector3> _chordPoints = new(64);
    private readonly List<Vector3> _chordVelocities = new(64);
    private readonly TargetSet _targets = new();

    private int _layerMask = Physics.DefaultRaycastLayers;
    private float _nextMaskSearch;
    private bool _maskFound;

    public void Solve(in WeaponState weapon, float maxTime)
    {
        Points.Clear();
        Impact = default;

        var shell = weapon.Shell;
        var gravity = Physics.gravity;
        var dt = Time.fixedDeltaTime;
        if (!(dt > 0f))
            dt = 0.02f;

        var lifetime = shell.Lifetime > 0f ? shell.Lifetime : 10f;
        if (maxTime > 0f)
            lifetime = Mathf.Min(lifetime, maxTime);
        var steps = Mathf.Min(MaxSteps, Mathf.CeilToInt(lifetime / dt));
        var chordSteps = Mathf.Max(1, Mathf.RoundToInt(ChordTime / dt));
        var mask = GetLayerMask();
        _targets.Refresh();

        var pos = weapon.Origin;
        var vel = weapon.InheritedVelocity + weapon.Direction * weapon.MuzzleVelocity;
        var travelled = 0f;
        var chordStart = pos;
        var chordStartIndex = 0;
        var chordStartLength = 0f;
        _chordPoints.Clear();
        _chordVelocities.Clear();
        Points.Add(pos);

        for (var i = 0; i < steps; i++)
        {
            var speed = vel.magnitude;
            var alive = speed >= shell.MinSpeed && speed > 0f;
            if (alive)
            {
                var accel = gravity - vel * (shell.DragForce(speed) / (shell.Mass * speed));
                vel += accel * dt;
                var next = pos + vel * dt;
                travelled += (next - pos).magnitude;
                pos = next;
                Points.Add(pos);
                _chordPoints.Add(pos);
                _chordVelocities.Add(vel);
            }

            var last = !alive || i == steps - 1;
            if (_chordPoints.Count == 0 || (_chordPoints.Count < chordSteps && !last))
            {
                if (!alive)
                    break;
                continue;
            }

            if (HitTest(chordStart, pos, mask, out _, out _, out _))
            {
                // 当たった弦の中を 1 ステップずつ判定し直して、ゲームと同じステップで着弾させる
                var from = chordStart;
                var length = chordStartLength;
                for (var k = 0; k < _chordPoints.Count; k++)
                {
                    var to = _chordPoints[k];
                    var segment = (to - from).magnitude;
                    if (HitTest(from, to, mask, out var distance, out var normal, out var model))
                    {
                        SetImpact(from, to, distance, normal, model, _chordVelocities[k], length,
                            (chordStartIndex + k + Mathf.Clamp01(distance / Mathf.Max(segment, 1e-5f))) * dt);
                        TrimPoints(chordStartIndex + k + 1);
                        return;
                    }
                    length += segment;
                    from = to;
                }
                // 弦では当たったが細分では外れた（かすめた）場合は弦の結果を採用
                if (HitTest(chordStart, pos, mask, out var d, out var n, out var m))
                {
                    var v = _chordVelocities[_chordVelocities.Count - 1];
                    SetImpact(chordStart, pos, d, n, m, v, chordStartLength, (chordStartIndex + _chordPoints.Count) * dt);
                    TrimPoints(chordStartIndex + _chordPoints.Count);
                    return;
                }
            }

            chordStart = pos;
            chordStartIndex += _chordPoints.Count;
            chordStartLength = travelled;
            _chordPoints.Clear();
            _chordVelocities.Clear();
            if (!alive)
                break;
        }

        Impact = new ImpactInfo
        {
            Kind = ImpactKind.None,
            Point = pos,
            Velocity = vel,
            PathLength = travelled,
            FlightTime = (Points.Count - 1) * dt,
        };
    }

    private void SetImpact(Vector3 from, Vector3 to, float distance, Vector3 normal, CompoundStructure model,
        Vector3 velocity, float pathBefore, float time)
    {
        var dir = (to - from).normalized;
        var point = from + dir * distance;
        Impact = new ImpactInfo
        {
            Kind = model != null ? ImpactKind.DamageModel : ImpactKind.Terrain,
            Point = point,
            Normal = normal,
            Velocity = velocity,
            PathLength = pathBefore + distance,
            FlightTime = time,
            Model = model,
        };
    }

    private void TrimPoints(int lastIndex)
    {
        if (lastIndex + 1 < Points.Count)
            Points.RemoveRange(lastIndex + 1, Points.Count - lastIndex - 1);
        Points[Points.Count - 1] = Impact.Point;
    }

    /// <summary>線分 a→b で最初に当たるもの（車両のダメージモデル または 地形）。</summary>
    private bool HitTest(Vector3 a, Vector3 b, int mask, out float distance, out Vector3 normal, out CompoundStructure model)
    {
        distance = 0f;
        normal = Vector3.zero;
        model = null;
        var delta = b - a;
        var length = delta.magnitude;
        if (length < 1e-5f)
            return false;
        var dir = delta / length;

        var best = length;
        var hit = false;
        if (_targets.Raycast(a, dir, length, out var dmDistance, out var dmNormal, out var dmModel))
        {
            best = dmDistance;
            normal = dmNormal;
            model = dmModel;
            hit = true;
        }

        if (Physics.Raycast(a, dir, out var rh, best, mask, QueryTriggerInteraction.Ignore)
            && !IsVehicleCollider(rh.collider))
        {
            best = rh.distance;
            normal = rh.normal;
            model = null;
            hit = true;
        }

        distance = best;
        return hit;
    }

    /// <summary>車両は DamageModel 側で判定するので、物理コライダーの方は無視する。</summary>
    private static bool IsVehicleCollider(Collider collider)
    {
        if (collider == null)
            return false;
        var body = collider.attachedRigidbody;
        return body != null && body.GetComponentInParent<Sprocket.Vehicles.Vehicle>() != null;
    }

    /// <summary>ProjectilesMain.ProjectileLayerMask（ゲームの砲弾が当たる物理レイヤー）を使う。</summary>
    private int GetLayerMask()
    {
        if (!_maskFound && Time.unscaledTime >= _nextMaskSearch)
        {
            _nextMaskSearch = Time.unscaledTime + 2f;
            var main = Object.FindAnyObjectByType(Il2CppType.Of<ProjectilesMain>())?.TryCast<ProjectilesMain>();
            if (main != null)
            {
                _layerMask = main.ProjectileLayerMask.value;
                _maskFound = true;
                if (Plugin.Cfg.DebugLog.Value)
                    Plugin.Logger.LogInfo($"Projectile layer mask: 0x{_layerMask:X}, fixedDeltaTime: {Time.fixedDeltaTime}");
            }
        }
        return _layerMask;
    }

    /// <summary>
    /// 登録されている全ダメージモデル（車両）と、そのワールド AABB。毎フレーム取り直す。
    /// </summary>
    private sealed class TargetSet
    {
        private struct Target
        {
            public CompoundStructure Structure;
            public Vector3 Min;
            public Vector3 Max;
        }

        private readonly List<Target> _targets = new();
        private readonly Dictionary<IntPtr, CompoundStructure> _wrappers = new();

        public void Refresh()
        {
            _targets.Clear();
            var models = DamageModel.models;
            if (models == null)
                return;
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i];
                if (model == null)
                    continue;
                if (!_wrappers.TryGetValue(model.Pointer, out var cs) || cs == null || cs.WasCollected)
                {
                    cs = model.TryCast<CompoundStructure>();
                    _wrappers[model.Pointer] = cs;
                }
                if (cs == null)
                    continue;
                cs.GetWorldBounds(out var min, out var max);
                _targets.Add(new Target { Structure = cs, Min = min, Max = max });
            }
            if (_wrappers.Count > 256)
                _wrappers.Clear();
        }

        public bool Raycast(Vector3 origin, Vector3 dir, float length, out float distance, out Vector3 normal, out CompoundStructure model)
        {
            distance = length;
            normal = Vector3.zero;
            model = null;
            for (var i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (!SegmentIntersectsBox(origin, dir, distance, t.Min, t.Max))
                    continue;
                if (!t.Structure.Raycast(origin, dir, distance, out DamageModelRaycastHit hit))
                    continue;
                if (hit.Distance < 0f || hit.Distance > distance)
                    continue;
                distance = hit.Distance;
                normal = hit.Normal;
                model = t.Structure;
            }
            return model != null;
        }

        private static bool SegmentIntersectsBox(Vector3 o, Vector3 d, float length, Vector3 min, Vector3 max)
        {
            var tMin = 0f;
            var tMax = length;
            for (var axis = 0; axis < 3; axis++)
            {
                var od = o[axis];
                var dd = d[axis];
                if (Mathf.Abs(dd) < 1e-8f)
                {
                    if (od < min[axis] || od > max[axis])
                        return false;
                    continue;
                }
                var inv = 1f / dd;
                var t1 = (min[axis] - od) * inv;
                var t2 = (max[axis] - od) * inv;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax)
                    return false;
            }
            return true;
        }
    }
}
