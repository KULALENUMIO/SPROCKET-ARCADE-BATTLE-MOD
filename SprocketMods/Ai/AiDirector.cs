using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DamageModelling;
using Sprocket.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.WeaponFramework;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace SprocketMods.Ai;

/// <summary>AI 改善の設定（[AI] セクション）。</summary>
public class AiConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<int> Difficulty;
    public readonly ConfigEntry<bool> AimModel;
    public readonly ConfigEntry<bool> TurretWatch;
    public readonly ConfigEntry<bool> HearPlayerShots;
    public readonly ConfigEntry<bool> HitReaction;
    public readonly ConfigEntry<bool> DrivingSafety;
    public readonly ConfigEntry<bool> AutoAbandon;
    public readonly ConfigEntry<float> FlipTime;
    public readonly ConfigEntry<float> StuckTime;
    public readonly ConfigEntry<bool> Log;

    public AiConfig(ConfigFile cfg)
    {
        const string s = "AI";
        Enabled = cfg.Bind(s, "Enabled", true, "AI の改善を使う（オフにするとゲームの AI のまま）");
        Difficulty = cfg.Bind(s, "Difficulty", 1, new ConfigDescription("AI の強さ: 0 = 新兵, 1 = 標準, 2 = 熟練, 3 = エース", new AcceptableValueRange<int>(0, 3)));
        AimModel = cfg.Bind(s, "AimModel", true, "照準を作り直す（距離・自分の移動で外れやすく、撃つたびに少しずつ修正。反応も人間らしく遅く）");
        TurretWatch = cfg.Bind(s, "TurretWatch", true, "目標がいないとき、砲塔を脅威の方向（撃たれた方向・最後に敵がいた方向・敵陣の方向）へ向けておく");
        HearPlayerShots = cfg.Bind(s, "HearPlayerShots", true, "プレイヤーの発砲も AI に聞こえる音にする（ゲームでは AI の発砲だけ）");
        HitReaction = cfg.Bind(s, "HitReaction", true, "撃たれたら撃ってきた方を見る");
        DrivingSafety = cfg.Bind(s, "DrivingSafety", true, "長いバックをさせない（その場で向きを変える）、押されたら経路を立て直す、詰まったら下がってやり直す");
        AutoAbandon = cfg.Bind(s, "AutoAbandon", true, "転覆した・詰まって動けなくなった AI の車両は、乗員が車両を放棄する（撃破扱い、キルログに「放棄」）");
        FlipTime = cfg.Bind(s, "FlipTime", 10f, new ConfigDescription("70° 以上傾いて止まったままこの秒数たったら放棄", new AcceptableValueRange<float>(3f, 60f)));
        StuckTime = cfg.Bind(s, "StuckTime", 30f,
            new ConfigDescription("動こうとしているのに進めない時間がこの秒数に達したら放棄（履帯が切れるなど走行不能の車両は除く）", new AcceptableValueRange<float>(10f, 180f)));
        Log = cfg.Bind(s, "Log", false, "AI の照準の命中率などをログに出す（調整用）");
    }
}

/// <summary>
/// AI 改善の段階 1。ゲームの AI の下回りは使い、間抜けな動きを止める安全装置と照準を差し替える。
///  - 照準: ゲームの距離・速さの誤差と夾叉の表をやめ（GunLayerAI.maxRangeError / maxVelocityError = 0）、
///    FindAndAimAtTargetGunLayerTask.Update の後で照準の向きに自前の誤差を足す。誤差 = 距離に応じたばらつき ×（1 − 修正率）^撃った数、
///    下限あり。自分が動いている・自分が 10 m 以上動いた・目標が急に速さを変えた・撃たれた、で悪化 / 修正が戻る。毎発ランダム。
///    目標を捕まえてから撃てるまで・照準が合ってから撃つまでに、難易度に応じた反応の遅れを足す。
///  - 砲塔: 目標がいないとき、撃たれた方向 → 最後の目標の方向 → 敵陣の方向 へ向けておく
///  - 音: プレイヤー（AI でない車両）の発砲を AINoiseRegister に登録（AI の見回しは音の方向を優先する）
///  - 被弾: 撃ってきた方向を脅威として覚え、車長の視線をそちらへ向ける
///  - 操縦: 自動の後退を禁止（DriverAI.reverseAllowed = false。目的地が後ろでもその場で向きを変える）、
///    押されたら経路を計算し直す、入力があるのに動かなければ下がってやり直す
/// </summary>
internal sealed class AiDirector
{
    public static AiDirector Instance { get; private set; }

    private const float RefreshInterval = 1f;
    private const float ProjectileTimeout = 20f;
    private const float ThreatMemory = 15f;
    private const float TargetMemory = 30f;

    private sealed class Unit
    {
        public CommanderAI Commander;
        public DriverAI Driver;
        public Rigidbody Body;
        public VehicleBehaviour Vehicle;
        public IntPtr BodyPtr;
        public IntPtr DriverTask;
        public TeamID Team;
        public string Name;
        public float LastSeen;

        // 脅威（撃ってきた位置）
        public Vector3 ThreatPos;
        public float ThreatTime = float.NegativeInfinity;
        public Vector3 LastTargetPos;
        public float LastTargetTime = float.NegativeInfinity;

        // 操縦
        public float GameThrottle;
        public float GameSteer;
        public float OverrideUntil;
        public float OverrideThrottle;
        public float OverrideSteer;
        public readonly Queue<(float Time, Vector3 Pos, bool Driving)> Track = new();
        public float NextSample;
        public float PushCooldown;
        public float PushSince = float.NaN;
        public int StuckAttempts;
        public Vector3 StuckAnchor;
        public float StuckRecomputeAt = float.NaN;

        // 放棄
        public bool Abandoned;
        public float FlippedSince = float.NaN;
        public Vector3 ProgressAnchor;
        public float TryingTime;
        public float LastAbandonCheck;
    }

    private sealed class AimState
    {
        public Unit Unit;
        public int Target = int.MinValue;
        public float AcquiredAt;
        public float AcquireDelay;
        public int Shots;
        public Vector2 Bias;
        public Vector3 ShotPos;
        public Vector3 TargetVelocity;
        public float LastDistance = 500f;
        public Vector3 Pivot;
    }

    private sealed class Shot
    {
        public Vector3 Origin;
        public IntPtr ShooterBody;
        public float Time;
        public IntPtr LastEvent;
        // 命中率の記録（AI の弾だけ）
        public bool Stat;
        public IntPtr TargetBody;
        public int Bucket;
        public int Index;
    }

    private readonly Dictionary<IntPtr, Unit> _units = new();
    private readonly Dictionary<IntPtr, Unit> _byBody = new();
    private readonly Dictionary<IntPtr, Unit> _byDriver = new();
    private readonly Dictionary<IntPtr, AimState> _aims = new();
    private readonly Dictionary<IntPtr, IntPtr> _weaponBody = new();
    private readonly Dictionary<int, Shot> _shots = new();
    private readonly List<int> _finished = new();
    private readonly List<IntPtr> _stale = new();
    private readonly Dictionary<int, VehicleBehaviour> _vehicles = new();
    private readonly Dictionary<TeamID, (Vector3 Sum, int Count)> _spawns = new();
    private readonly HashSet<IntPtr> _spawnSeen = new();
    private readonly Dictionary<IntPtr, (float Range, float Velocity)> _layerDefaults = new();
    private readonly Dictionary<IntPtr, float> _reactionSeen = new();
    private readonly Random _random = new();
    private readonly int[,] _statShots = new int[3, 4];
    private readonly int[,] _statHits = new int[3, 4];
    private float _nextRefresh;
    private int _aimCalls;
    private int _aimResolved;
    private int _aiShots;
    private int _otherShots;
    private float _nextVehicleSearch;
    private float _nextStatLog;
    private float _nextErrorLog;
    private bool _applied;

    public AiDirector() => Instance = this;

    private static AiConfig Cfg => Plugin.Ai;

    private bool Active => Cfg.Enabled.Value;

    // ------------------------------------------------------------------ 難易度

    private int Level => Mathf.Clamp(Cfg.Difficulty.Value, 0, 3);
    private static readonly float[] ErrorScale = { 1.8f, 1f, 0.75f, 0.5f };
    private static readonly float[] Correction = { 0.2f, 0.3f, 0.4f, 0.5f };
    private static readonly (float Min, float Max)[] AcquireExtra = { (1f, 2f), (0.2f, 0.6f), (0f, 0.2f), (0f, 0f) };
    private const float FireTolerance = 0.25f;
    private static readonly (float Min, float Max)[] FireExtra = { (0.6f, 1f), (0.2f, 0.35f), (0.05f, 0.15f), (0f, 0f) };

    /// <summary>距離 d での照準のばらつき（m、標準偏差）。300 m で約 0.9 m、1000 m で約 2.2 m（難易度「標準」）。</summary>
    private float Sigma(float distance, int shots, bool moving)
    {
        var first = 0.3f + 0.0019f * distance;
        var floor = 0.25f + 0.00075f * distance;
        var sigma = Mathf.Max(floor, first * Mathf.Pow(1f - Correction[Level], shots));
        if (moving)
            sigma *= 3f;
        return sigma * ErrorScale[Level];
    }

    // ------------------------------------------------------------------ 毎フレーム

    public void Update()
    {
        try
        {
            if (!Active)
            {
                Restore();
                return;
            }
            if (Time.timeScale <= 0f)
                return;
            var now = Time.time;
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                Refresh();
            }
            ProcessShots(now);
            UpdateReaction(now);
            UpdateDriving(now);
            UpdateAbandon(now);
            if (Cfg.Log.Value && Time.unscaledTime >= _nextStatLog)
            {
                _nextStatLog = Time.unscaledTime + 60f;
                LogStats();
            }
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"AI director failed: {e}");
            }
        }
    }

    /// <summary>AI の一覧を作り直し、設定（照準の誤差・自動の後退）を当てる。</summary>
    private void Refresh()
    {
        var now = Time.time;
        if (Time.unscaledTime >= _nextVehicleSearch)
        {
            _nextVehicleSearch = Time.unscaledTime + 2f;
            SearchVehicles();
        }
        var list = AIRegister.register;
        if (list != null)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var commander = list[i]?.TryCast<CommanderAI>();
                if (commander == null)
                    continue;
                if (!_units.TryGetValue(commander.Pointer, out var unit))
                {
                    unit = CreateUnit(commander);
                    if (unit == null)
                        continue;
                    _units[commander.Pointer] = unit;
                    _byBody[unit.BodyPtr] = unit;
                    if (unit.DriverTask != IntPtr.Zero)
                        _byDriver[unit.DriverTask] = unit;
                }
                unit.LastSeen = now;
                Apply(unit);
            }
        }
        _stale.Clear();
        foreach (var pair in _units)
        {
            if (now - pair.Value.LastSeen > 3f || pair.Value.Body == null || pair.Value.Body.WasCollected)
                _stale.Add(pair.Key);
        }
        foreach (var key in _stale)
        {
            var unit = _units[key];
            _units.Remove(key);
            _byBody.Remove(unit.BodyPtr);
            _byDriver.Remove(unit.DriverTask);
        }
        if (_stale.Count > 0)
        {
            var dead = new List<IntPtr>();
            foreach (var pair in _aims)
            {
                if (!_units.ContainsValue(pair.Value.Unit))
                    dead.Add(pair.Key);
            }
            foreach (var key in dead)
                _aims.Remove(key);
        }
        _applied = true;
    }

    private Unit CreateUnit(CommanderAI commander)
    {
        var driver = commander.driver;
        var body = driver?.body;
        if (body == null)
            return null;
        var vehicle = body.GetComponent<VehicleBehaviour>() ?? body.GetComponentInParent<VehicleBehaviour>();
        TeamID team;
        try
        {
            team = vehicle != null ? vehicle.ID.TeamID : TeamID.None;
        }
        catch (Exception)
        {
            team = TeamID.None;
        }
        return new Unit
        {
            Commander = commander,
            Driver = driver,
            Body = body,
            Vehicle = vehicle,
            BodyPtr = body.Pointer,
            ProgressAnchor = body.position,
            DriverTask = driver.target?.Pointer ?? IntPtr.Zero,
            Team = team,
            Name = vehicle != null ? KillFeed.KillTracker.GetName(vehicle) : "?",
            StuckAnchor = body.position,
        };
    }

    /// <summary>照準の誤差をゲームから外し（自前で足す）、自動の後退を止める。</summary>
    private void Apply(Unit unit)
    {
        var layers = unit.Commander.gunLayers;
        if (layers != null)
        {
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer == null)
                    continue;
                if (!_layerDefaults.ContainsKey(layer.Pointer))
                    _layerDefaults[layer.Pointer] = (layer.maxRangeError, layer.maxVelocityError);
                var defaults = _layerDefaults[layer.Pointer];
                var aim = Cfg.AimModel.Value;
                layer.maxRangeError = aim ? 0f : defaults.Range;
                layer.maxVelocityError = aim ? 0f : defaults.Velocity;
            }
        }
        if (unit.Driver != null)
            unit.Driver.reverseAllowed = !Cfg.DrivingSafety.Value;
    }

    /// <summary>MOD をオフにしたら、変えた値を元に戻す。</summary>
    private void Restore()
    {
        if (!_applied)
            return;
        _applied = false;
        foreach (var unit in _units.Values)
        {
            if (unit.Body == null || unit.Body.WasCollected)
                continue;
            var layers = unit.Commander.gunLayers;
            for (var i = 0; layers != null && i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer != null && _layerDefaults.TryGetValue(layer.Pointer, out var d))
                {
                    layer.maxRangeError = d.Range;
                    layer.maxVelocityError = d.Velocity;
                }
            }
            if (unit.Driver != null)
                unit.Driver.reverseAllowed = true;
        }
        _units.Clear();
        _byBody.Clear();
        _byDriver.Clear();
        _aims.Clear();
    }

    /// <summary>車両の ID と位置。陣営ごとの出現位置（敵陣の方向に使う）も覚える。</summary>
    private void SearchVehicles()
    {
        _vehicles.Clear();
        var found = Object.FindObjectsByType(Il2CppType.Of<VehicleBehaviour>(), FindObjectsSortMode.None);
        if (found == null)
            return;
        foreach (var obj in found)
        {
            var vehicle = obj?.TryCast<VehicleBehaviour>();
            if (vehicle == null)
                continue;
            EntityID id;
            try
            {
                id = vehicle.ID;
            }
            catch (Exception)
            {
                continue;
            }
            _vehicles[Pack(id)] = vehicle;
            if (id.TeamID != TeamID.None && _spawnSeen.Add(vehicle.Pointer))
            {
                _spawns.TryGetValue(id.TeamID, out var s);
                _spawns[id.TeamID] = (s.Sum + vehicle.transform.position, s.Count + 1);
            }
        }
        if (_spawnSeen.Count > 512)
        {
            _spawnSeen.Clear();
            _spawns.Clear();
        }
    }

    private static int Pack(EntityID id) =>
        id.BuildNumber | ((byte)id.SpawnerID << 8) | ((byte)id.GroupID << 16) | ((byte)id.TeamID << 24);

    // ------------------------------------------------------------------ 照準（FindAndAimAtTargetGunLayerTask.Update の後）

    /// <summary>
    /// ゲームが出した照準の向きに自前の誤差を足す。目標がいなければ砲塔を見張りの方向へ向ける。
    /// output.AimDirection は「狙う点 − 砲の旋回中心」（長さ = 距離）。
    /// </summary>
    public void AdjustAim(IntPtr task, Vector3 pivot, ref GunLayerInstructions output, ref GunLayerTaskResult result)
    {
        if (!Active)
            return;
        _aimCalls++;
        var state = GetAimState(task, pivot);
        if (state == null)
            return;
        _aimResolved++;
        state.Pivot = pivot;
        var now = Time.time;
        var flags = output.Flags;

        if ((flags & GunLayerTaskFlags.AimAtDirection) == 0)
        {
            // 目標なし（PassThrough）なら見張りの方向へ。ゲームの捕捉の 1 秒（Running・フラグ無し）はそのまま
            if (result == GunLayerTaskResult.PassThrough && Cfg.TurretWatch.Value && TryWatchDirection(state.Unit, pivot, out var watch))
            {
                output.AimDirection = watch;
                output.Flags = GunLayerTaskFlags.AimAtDirection;
                result = GunLayerTaskResult.Running;
            }
            return;
        }
        if (!Cfg.AimModel.Value)
            return;

        var target = Pack(output.TargetID);
        var vector = output.AimDirection;
        var distance = vector.magnitude;
        if (distance < 1f)
            return;
        state.LastDistance = distance;

        // 目標が変わったら最初から（捕捉の遅れも数え直す）
        if (target != state.Target)
        {
            state.Target = target;
            state.AcquiredAt = now;
            var extra = AcquireExtra[Level];
            state.AcquireDelay = Range(extra.Min, extra.Max);
            state.Shots = 0;
            state.ShotPos = state.Unit.Body.position;
            state.TargetVelocity = TargetVelocity(target);
            state.Bias = Gaussian2();
        }
        else
        {
            // 自分が 10 m 以上動いた、目標が急に速さを変えた → 修正が半分戻る
            var moved = (state.Unit.Body.position - state.ShotPos).magnitude > 10f;
            var velocity = TargetVelocity(target);
            var maneuver = (velocity - state.TargetVelocity).magnitude > 4f;
            if (moved || maneuver)
            {
                state.Shots /= 2;
                state.ShotPos = state.Unit.Body.position;
                state.TargetVelocity = velocity;
                state.Bias = Gaussian2();
            }
        }
        var targetPos = pivot + vector;
        state.Unit.LastTargetPos = targetPos;
        state.Unit.LastTargetTime = now;

        // 照準に誤差を足す（目標の位置で、視線に垂直な左右・上下へずらす）
        var moving = state.Unit.Body.velocity.magnitude > 1.5f;
        var sigma = Sigma(distance, state.Shots, moving);
        if (now - state.Unit.ThreatTime < 2f)
            sigma *= 1.5f; // 撃たれた直後は動揺
        var forward = vector / distance;
        var right = Vector3.Cross(Vector3.up, forward);
        right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
        var up = Vector3.Cross(forward, right).normalized;
        output.AimDirection = vector + right * (state.Bias.x * sigma) + up * (state.Bias.y * sigma);

        // 捕捉の遅れの間は狙うだけで撃たない
        if (now - state.AcquiredAt < state.AcquireDelay)
            output.Flags &= ~GunLayerTaskFlags.FireWhenAimed;
    }

    private AimState GetAimState(IntPtr task, Vector3 pivot)
    {
        if (_aims.TryGetValue(task, out var state))
            return state;
        // 砲の旋回中心に一番近い AI 車両
        Unit best = null;
        var bestDistance = 8f;
        foreach (var unit in _units.Values)
        {
            if (unit.Body == null || unit.Body.WasCollected)
                continue;
            var d = (unit.Body.position - pivot).magnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = unit;
            }
        }
        if (best == null)
            return null;
        state = new AimState { Unit = best };
        _aims[task] = state;
        return state;
    }

    private Vector3 TargetVelocity(int target)
    {
        if (_vehicles.TryGetValue(target, out var vehicle) && vehicle != null && !vehicle.WasCollected)
        {
            var body = vehicle.Rigidbody;
            if (body != null)
                return body.velocity;
        }
        return Vector3.zero;
    }

    /// <summary>見張りの方向（撃たれた方向 → 最後に目標がいた方向 → 敵陣の方向）。</summary>
    private bool TryWatchDirection(Unit unit, Vector3 pivot, out Vector3 aim)
    {
        var now = Time.time;
        Vector3 point;
        if (now - unit.ThreatTime < ThreatMemory)
            point = unit.ThreatPos;
        else if (now - unit.LastTargetTime < TargetMemory)
            point = unit.LastTargetPos;
        else if (!TryEnemySide(unit, out point))
        {
            aim = default;
            return false;
        }
        var flat = point - pivot;
        flat.y = 0f;
        if (flat.sqrMagnitude < 25f)
        {
            aim = default;
            return false;
        }
        aim = flat.normalized * 300f; // 水平に 300 m 先
        return true;
    }

    private bool TryEnemySide(Unit unit, out Vector3 point)
    {
        point = default;
        var sum = Vector3.zero;
        var count = 0;
        foreach (var pair in _spawns)
        {
            if (pair.Key == unit.Team || pair.Key == TeamID.None || unit.Team == TeamID.None)
                continue;
            sum += pair.Value.Sum;
            count += pair.Value.Count;
        }
        if (count == 0)
            return false;
        point = sum / count;
        return true;
    }

    /// <summary>照準が合ってから撃つまでに、難易度に応じた遅れを足す（ゲームの 0.1〜0.5 秒に加えて）。</summary>
    private void UpdateReaction(float now)
    {
        if (!Cfg.AimModel.Value)
            return;
        var extra = FireExtra[Level];
        foreach (var unit in _units.Values)
        {
            var gunners = unit.Commander.gunners;
            for (var i = 0; gunners != null && i < gunners.Length; i++)
            {
                var task = gunners[i]?.currentTask?.TryCast<FireWhenAimedGunnerAITask>();
                if (task == null)
                    continue;
                // 砲が狙いに落ち着いてから撃つ（ゲームの 0.6° では、600 m で 6 m ずれていても撃ってしまう）
                if (task.MaxFireAngleDeviation > FireTolerance)
                    task.MaxFireAngleDeviation = FireTolerance;
                if (!task.waitingForReactionTime)
                    continue;
                var time = task.reactionFireTime;
                if (_reactionSeen.TryGetValue(task.Pointer, out var seen) && Mathf.Approximately(seen, time))
                    continue;
                if (extra.Max <= 0f)
                    continue;
                time += Range(extra.Min, extra.Max);
                task.reactionFireTime = time;
                _reactionSeen[task.Pointer] = time;
            }
        }
        if (_reactionSeen.Count > 1024)
            _reactionSeen.Clear();
    }

    // ------------------------------------------------------------------ 発射・命中（CannonBehaviour.FireInternal の後）

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (!Active || weapon == null || projectileId <= 0)
            return;
        var transform = weapon.Transform;
        if (transform == null)
            return;
        if (!_weaponBody.TryGetValue(weapon.Pointer, out var body))
        {
            var vehicle = transform.GetComponentInParent<VehicleBehaviour>();
            body = vehicle?.Rigidbody?.Pointer ?? IntPtr.Zero;
            if (_weaponBody.Count > 1024)
                _weaponBody.Clear();
            _weaponBody[weapon.Pointer] = body;
        }
        var origin = transform.position;
        var shot = new Shot { Origin = origin, ShooterBody = body, Time = Time.time };
        if (_byBody.TryGetValue(body, out var unit))
        {
            _aiShots++;
            // AI の発射: 撃った砲に一番近い照準（旋回中心）の修正を 1 段進める（砲が複数ある車両もあるので）
            AimState fired = null;
            var nearest = float.MaxValue;
            foreach (var candidate in _aims.Values)
            {
                if (candidate.Unit != unit || candidate.Target == int.MinValue)
                    continue;
                var d = (candidate.Pivot - origin).sqrMagnitude;
                if (d < nearest)
                {
                    nearest = d;
                    fired = candidate;
                }
            }
            if (fired != null)
            {
                var state = fired;
                if (Cfg.Log.Value && _vehicles.TryGetValue(state.Target, out var target) && target != null && !target.WasCollected)
                {
                    shot.Stat = true;
                    shot.TargetBody = target.Rigidbody?.Pointer ?? IntPtr.Zero;
                    shot.Bucket = state.LastDistance < 400f ? 0 : state.LastDistance < 800f ? 1 : 2;
                    shot.Index = Mathf.Min(state.Shots, 3);
                }
                if (Cfg.Log.Value && _aiShots <= 60)
                {
                    var moving = unit.Body.velocity.magnitude;
                    Plugin.Logger.LogInfo($"AI shot: {unit.Name} d={state.LastDistance:0} m shot#{state.Shots + 1} speed={moving:0.0} m/s " +
                                          $"sigma={Sigma(state.LastDistance, state.Shots, moving > 1.5f):0.00} m bias=({state.Bias.x:0.0},{state.Bias.y:0.0})");
                }
                state.Shots++;
                state.ShotPos = unit.Body.position;
                state.Bias = Gaussian2();
            }
        }
        else if (Cfg.HearPlayerShots.Value)
        {
            _otherShots++;
            // AI でない車両（プレイヤー）の発砲も音にする（AI の発砲はゲームが登録する）
            AINoiseRegister.Register(origin, 160f);
        }
        if (_shots.Count > 2048)
            _shots.Clear();
        _shots[projectileId] = shot;
    }

    /// <summary>弾の最新のイベントを見て、AI への命中（脅威の方向）と、AI の弾の当たり外れを記録する。</summary>
    private void ProcessShots(float now)
    {
        if (_shots.Count == 0)
            return;
        var history = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>()?.projectileHistory;
        if (history == null)
            return;
        _finished.Clear();
        foreach (var pair in _shots)
        {
            var shot = pair.Value;
            if (now - shot.Time > ProjectileTimeout)
            {
                if (shot.Stat)
                    Count(shot, false);
                _finished.Add(pair.Key);
                continue;
            }
            if (!history.TryGetValue(pair.Key, out var latest) || latest == null || latest.Pointer == shot.LastEvent)
                continue;
            shot.LastEvent = latest.Pointer;
            var hit = latest.TryCast<HitDamageModelProjectileEvent>();
            var body = hit?.DamageModel?.RigidBody;
            if (body != null && body.Pointer != shot.ShooterBody)
            {
                if (_byBody.TryGetValue(body.Pointer, out var victim))
                    OnAiHit(victim, shot.Origin, now);
                if (shot.Stat)
                {
                    Count(shot, body.Pointer == shot.TargetBody);
                    shot.Stat = false;
                }
            }
            if (latest.Terminal)
            {
                if (shot.Stat)
                    Count(shot, false);
                _finished.Add(pair.Key);
            }
        }
        foreach (var id in _finished)
            _shots.Remove(id);
    }

    private void OnAiHit(Unit unit, Vector3 origin, float now)
    {
        unit.ThreatPos = origin;
        unit.ThreatTime = now;
        if (!Cfg.HitReaction.Value)
            return;
        // 車長の視線を撃ってきた方へ（見回しは音の方向も優先するので、そのまましばらく向く）
        var view = unit.Commander.viewDirectionController;
        if (view == null)
            return;
        var dir = origin - unit.Body.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 1f)
            view.lookDirection = dir.normalized;
    }

    // ------------------------------------------------------------------ 操縦（Driver.ExecutePerform の前）

    /// <summary>AI の操縦手の入力を見て、安全装置の上書きがあれば差し替える。</summary>
    public void FilterDrive(Driver driver)
    {
        if (!Active || !Cfg.DrivingSafety.Value || !_byDriver.TryGetValue(driver.Pointer, out var unit))
            return;
        var solution = driver.Solution;
        unit.GameThrottle = solution.Throttle;
        unit.GameSteer = solution.Steer;
        if (Time.time < unit.OverrideUntil)
            driver.Solution = new DriveSolution(unit.OverrideSteer, unit.OverrideThrottle);
    }

    /// <summary>押されたら経路を立て直し、詰まったら下がってやり直す。</summary>
    private void UpdateDriving(float now)
    {
        if (!Cfg.DrivingSafety.Value)
            return;
        foreach (var unit in _units.Values)
        {
            var body = unit.Body;
            if (body == null || body.WasCollected || unit.Driver == null)
                continue;

            // 下がり終えたら経路を計算し直す
            if (!float.IsNaN(unit.StuckRecomputeAt) && now >= unit.StuckRecomputeAt)
            {
                unit.StuckRecomputeAt = float.NaN;
                unit.Driver.pathRecomputeRequired = true;
            }

            // 押された: 横滑りしている（履帯の車両は自分では横に動かない）、または
            // その場旋回でもなくハンドルもほとんど切っていないのに速く回っている
            var yawRate = Mathf.Abs(body.angularVelocity.y);
            var lateral = Mathf.Abs(Vector3.Dot(body.velocity, body.transform.right));
            var rotating = unit.Driver.movementState == DriverAI.MovementState.StopAndRotate;
            var sliding = lateral > 3.5f;
            var spinning = yawRate > 1.4f && !rotating && Mathf.Abs(unit.GameSteer) < 0.1f;
            if (!sliding && !spinning)
                unit.PushSince = float.NaN;
            else if (float.IsNaN(unit.PushSince))
                unit.PushSince = now;
            // 一瞬の揺れ（段差・着地）は無視し、しばらく続いたときだけ
            var pushed = !float.IsNaN(unit.PushSince) && now - unit.PushSince >= (sliding ? 0.5f : 0.3f);
            if (pushed && now >= unit.PushCooldown && now >= unit.OverrideUntil)
            {
                unit.PushSince = float.NaN;
                unit.PushCooldown = now + 4f;
                Override(unit, 0f, 0f, 1f);
                unit.StuckRecomputeAt = now + 1f;
                if (Cfg.Log.Value)
                    Plugin.Logger.LogInfo($"AI: {unit.Name} pushed (sideways {lateral:0.0} m/s, yaw {yawRate * Mathf.Rad2Deg:0}°/s), re-planning");
            }

            // 詰まり: 6 秒間ずっとアクセルを踏んでいるのに 2 m も動いていない
            if (now < unit.NextSample)
                continue;
            unit.NextSample = now + 0.5f;
            // 走行不能（履帯が切れたなど）なら動けないのは当然なので詰まり扱いしない
            var driving = Mathf.Abs(unit.GameThrottle) > 0.3f && now >= unit.OverrideUntil && !unit.Driver.Immobilized;
            unit.Track.Enqueue((now, body.position, driving));
            while (unit.Track.Count > 0 && now - unit.Track.Peek().Time > 6f)
                unit.Track.Dequeue();
            if ((body.position - unit.StuckAnchor).magnitude > 20f)
            {
                unit.StuckAnchor = body.position;
                unit.StuckAttempts = 0;
            }
            if (unit.Track.Count < 11 || unit.StuckAttempts >= 3)
                continue;
            var first = unit.Track.Peek();
            if (now - first.Time < 5.5f)
                continue;
            var allDriving = true;
            foreach (var sample in unit.Track)
                allDriving &= sample.Driving;
            if (!allDriving || (body.position - first.Pos).magnitude >= 2f)
                continue;
            unit.StuckAttempts++;
            var back = unit.GameThrottle > 0f ? -0.7f : 0.7f;
            Override(unit, back, 0f, 2.5f);
            unit.StuckRecomputeAt = now + 2.5f;
            unit.Track.Clear();
            if (Cfg.Log.Value)
                Plugin.Logger.LogInfo($"AI: {unit.Name} stuck, backing off (attempt {unit.StuckAttempts})");
        }
    }

    // ------------------------------------------------------------------ 放棄（転覆・詰まり）

    /// <summary>
    /// 転覆（70° 以上傾いて止まったまま FlipTime 秒）、または詰まり（動こうとしているのに 4 m 進めない時間の合計が StuckTime 秒）の
    /// AI の車両は、乗員が車両を捨てる。走行不能（履帯が切れたなど）でも撃てる車両は戦えるので捨てない。
    /// </summary>
    private void UpdateAbandon(float now)
    {
        if (!Cfg.AutoAbandon.Value)
            return;
        foreach (var unit in _units.Values)
        {
            if (unit.Abandoned || unit.Body == null || unit.Body.WasCollected || unit.Vehicle == null || unit.Vehicle.WasCollected)
                continue;
            var dt = now - unit.LastAbandonCheck;
            if (dt < 0.5f)
                continue;
            unit.LastAbandonCheck = now;
            if (dt > 2f)
                dt = 0.5f; // ポーズ明けなど
            var body = unit.Body;

            // 転覆
            var flipped = body.transform.up.y < 0.34f && body.velocity.magnitude < 2f;
            if (!flipped)
                unit.FlippedSince = float.NaN;
            else if (float.IsNaN(unit.FlippedSince))
                unit.FlippedSince = now;
            if (flipped && now - unit.FlippedSince >= Cfg.FlipTime.Value)
            {
                Abandon(unit, "flipped");
                continue;
            }

            // 詰まり: 4 m 進んだら数え直す。走行不能の車両と、アクセルを踏んでいない間（停止して撃つ・修理中）は数えない
            if ((body.position - unit.ProgressAnchor).magnitude > 4f)
            {
                unit.ProgressAnchor = body.position;
                unit.TryingTime = 0f;
            }
            else if (Mathf.Abs(unit.GameThrottle) > 0.3f && unit.Driver != null && !unit.Driver.Immobilized)
            {
                unit.TryingTime += dt;
                if (unit.TryingTime >= Cfg.StuckTime.Value)
                    Abandon(unit, "stuck");
            }
        }
    }

    private void Abandon(Unit unit, string reason)
    {
        unit.Abandoned = true;
        KillFeed.KillTracker.Instance?.ReportAbandoned(unit.Vehicle);
        var crew = Repair.RepairSystem.Instance?.KillCrew(unit.Vehicle) ?? 0;
        Plugin.Logger.LogInfo($"AI: {unit.Name} abandoned ({reason}, {crew} crew)");
    }

    private static void Override(Unit unit, float throttle, float steer, float seconds)
    {
        unit.OverrideThrottle = throttle;
        unit.OverrideSteer = steer;
        unit.OverrideUntil = Time.time + seconds;
    }

    // ------------------------------------------------------------------ 補助

    private float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    /// <summary>標準正規分布の 2 次元の乱数（Box-Muller）。</summary>
    private Vector2 Gaussian2()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        var r = Math.Sqrt(-2.0 * Math.Log(u1));
        return new Vector2((float)(r * Math.Cos(2 * Math.PI * u2)), (float)(r * Math.Sin(2 * Math.PI * u2)));
    }

    private void Count(Shot shot, bool hit)
    {
        _statShots[shot.Bucket, shot.Index]++;
        if (hit)
            _statHits[shot.Bucket, shot.Index]++;
    }

    private void LogStats()
    {
        Plugin.Logger.LogInfo($"AI director: {_units.Count} AI, {_aims.Count} aim states, aim hook {_aimCalls} calls ({_aimResolved} resolved), shots AI {_aiShots} / other {_otherShots}");
        var names = new[] { "<400m", "400-800m", ">800m" };
        for (var b = 0; b < 3; b++)
        {
            var line = $"AI aim {names[b]}:";
            for (var i = 0; i < 4; i++)
            {
                var shots = _statShots[b, i];
                line += $"  #{i + 1}{(i == 3 ? "+" : "")} {(_statHits[b, i] * 100f / Mathf.Max(1, shots)):0}% ({shots})";
            }
            Plugin.Logger.LogInfo(line);
        }
    }
}
