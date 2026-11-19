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
/// 弦が長いほどゲームへの問い合わせ（Physics.Raycast・車両の当たり判定）が減る。0.25 秒の弦と実際の弾道のずれは
/// 重力で g·t²/8 ≒ 8 cm 以下で、当たった弦は細かく調べ直すので、着弾点は変わらない（かすめるだけの当たりを見落とす程度）。
/// 車両は CompoundStructure.Raycast、地形は Physics.Raycast。
/// （DamageModel.Raycast は戻り値の DamageModelHit が参照型を含む構造体で、Il2CppInterop 経由の
///   out 引数受け渡しがメモリを壊してゲームが落ちるため使わない）
/// </summary>
internal sealed class BallisticSolver
{
    private const float ChordTime = 0.25f;
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
        var perf = SprocketMods.Debug.Perf.Begin();
        _targets.Refresh();
        SprocketMods.Debug.Perf.End("HUD/Solve/Targets", perf);

        // 積分は float だけで行う（この環境では Vector3 の演算 1 回ごとにゲーム側への呼び出しになり、数千回で 1 ms ほどかかる）
        var start = weapon.Origin;
        var v0 = weapon.InheritedVelocity + weapon.Direction * weapon.MuzzleVelocity;
        float px = start.x, py = start.y, pz = start.z;
        float vx = v0.x, vy = v0.y, vz = v0.z;
        float gx = gravity.x, gy = gravity.y, gz = gravity.z;
        var pos = start;
        var vel = v0;
        var travelled = 0f;
        var chordStart = pos;
        var chordStartIndex = 0;
        var chordStartLength = 0f;
        _chordPoints.Clear();
        _chordVelocities.Clear();
        Points.Add(pos);

        for (var i = 0; i < steps; i++)
        {
            var speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            var alive = speed >= shell.MinSpeed && speed > 0f;
            if (alive)
            {
                var k = shell.DragForce(speed) / (shell.Mass * speed);
                vx += (gx - vx * k) * dt;
                vy += (gy - vy * k) * dt;
                vz += (gz - vz * k) * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                travelled += MathF.Sqrt(vx * vx + vy * vy + vz * vz) * dt;
                pos = new Vector3 { x = px, y = py, z = pz };
                vel = new Vector3 { x = vx, y = vy, z = vz };
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
                    var segment = Core.Fast.Distance(to, from);
                    if (HitTest(from, to, mask, out var distance, out var normal, out var model))
                    {
                        SetImpact(from, to, distance, normal, model, _chordVelocities[k], length,
                            (chordStartIndex + k + Core.Fast.Clamp01(distance / MathF.Max(segment, 1e-5f))) * dt);
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
        var span = Core.Fast.Distance(to, from);
        var point = span > 1e-6f ? Core.Fast.Add(from, Core.Fast.Mul(Core.Fast.Sub(to, from), distance / span)) : from;
        Impact = new ImpactInfo
        {
            Kind = model is not null ? ImpactKind.DamageModel : ImpactKind.Terrain,
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
        normal = default;
        model = null;
        float ddx = b.x - a.x, ddy = b.y - a.y, ddz = b.z - a.z;
        var length = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
        if (length < 1e-5f)
            return false;
        var dir = Core.Fast.V3(ddx / length, ddy / length, ddz / length);

        var best = length;
        var hit = false;
        var perf = SprocketMods.Debug.Perf.Begin();
        var vehicleHit = _targets.Raycast(a, dir, length, out var dmDistance, out var dmNormal, out var dmModel);
        SprocketMods.Debug.Perf.End("HUD/Solve/Vehicles", perf);
        if (vehicleHit)
        {
            best = dmDistance;
            normal = dmNormal;
            model = dmModel;
            hit = true;
        }

        perf = SprocketMods.Debug.Perf.Begin();
        var terrainHit = Physics.Raycast(a, dir, out var rh, best, mask, QueryTriggerInteraction.Ignore) && !IsVehicleCollider(rh.collider);
        SprocketMods.Debug.Perf.End("HUD/Solve/Terrain", perf);
        if (terrainHit)
        {
            best = rh.m_Distance;
            normal = rh.m_Normal;
            model = null;
            hit = true;
        }

        distance = best;
        return hit;
    }

    /// <summary>車両は DamageModel 側で判定するので、物理コライダーの方は無視する。</summary>
    private static bool IsVehicleCollider(Collider collider)
    {
        if (!Core.Fast.Alive(collider))
            return false;
        var body = collider.attachedRigidbody;
        return Core.Fast.Alive(body) && Core.Fast.Alive(body.GetComponentInParent<Sprocket.Vehicles.Vehicle>());
    }

    /// <summary>ProjectilesMain.ProjectileLayerMask（ゲームの砲弾が当たる物理レイヤー）を使う。</summary>
    private int GetLayerMask()
    {
        if (!_maskFound && Core.Fast.Unscaled >= _nextMaskSearch)
        {
            _nextMaskSearch = Core.Fast.Unscaled + 2f;
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
            public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }

        private readonly List<Target> _targets = new();
        private readonly Dictionary<IntPtr, CompoundStructure> _wrappers = new();

        public void Refresh()
        {
            _targets.Clear();
            var models = DamageModel.models;
            if (models == null)
                return;
            var count = models.Count; // Count も値を返す呼び出し（ループの条件で毎回読まない）
            for (var i = 0; i < count; i++)
            {
                var model = models[i];
                if (model == null)
                    continue;
                if (!_wrappers.TryGetValue(model.Pointer, out var cs) || cs is null || cs.WasCollected)
                {
                    cs = model.TryCast<CompoundStructure>();
                    _wrappers[model.Pointer] = cs;
                }
                if (!Core.Fast.Alive(cs))
                    continue;
                cs.GetWorldBounds(out var min, out var max);
                _targets.Add(new Target { Structure = cs, MinX = min.x, MinY = min.y, MinZ = min.z, MaxX = max.x, MaxY = max.y, MaxZ = max.z });
            }
            if (_wrappers.Count > 256)
                _wrappers.Clear();
        }

        public bool Raycast(Vector3 origin, Vector3 dir, float length, out float distance, out Vector3 normal, out CompoundStructure model)
        {
            distance = length;
            normal = default;
            model = null;
            float ox = origin.x, oy = origin.y, oz = origin.z, dx = dir.x, dy = dir.y, dz = dir.z;
            for (var i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                var tMin = 0f;
                var tMax = distance;
                if (!Slab(ox, dx, t.MinX, t.MaxX, ref tMin, ref tMax) || !Slab(oy, dy, t.MinY, t.MaxY, ref tMin, ref tMax)
                    || !Slab(oz, dz, t.MinZ, t.MaxZ, ref tMin, ref tMax))
                    continue;
                if (!t.Structure.Raycast(origin, dir, distance, out DamageModelRaycastHit hit))
                    continue;
                if (hit.Distance < 0f || hit.Distance > distance)
                    continue;
                distance = hit.Distance;
                normal = hit.Normal;
                model = t.Structure;
            }
            return model is not null;
        }

        /// <summary>線分と箱の 1 軸ぶん（スラブ法）。float だけで計算する。</summary>
        private static bool Slab(float o, float d, float min, float max, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(d) < 1e-8f)
                return o >= min && o <= max;
            var inv = 1f / d;
            var t1 = (min - o) * inv;
            var t2 = (max - o) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            if (t1 > tMin)
                tMin = t1;
            if (t2 < tMax)
                tMax = t2;
            return tMin <= tMax;
        }
    }
}
