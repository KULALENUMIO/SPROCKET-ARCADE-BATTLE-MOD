using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.WeaponFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Hits;

/// <summary>命中結果表示の設定（[HitReport] セクション）。</summary>
public class HitReportConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> ShowMarkers;
    public readonly ConfigEntry<float> Duration;
    public readonly ConfigEntry<float> MinDamagePercent;
    public readonly ConfigEntry<float> FontSize;
    public readonly ConfigEntry<int> MaxLines;

    public HitReportConfig(ConfigFile cfg)
    {
        const string s = "HitReport";
        Enabled = cfg.Bind(s, "Enabled", true, "自分の弾の命中結果（貫通・非貫通・跳弾）と、損傷・破壊したモジュールを照準の下に表示する");
        ShowMarkers = cfg.Bind(s, "ShowMarkers", true, "損傷・破壊したモジュールの位置に一瞬マーカーを出す");
        Duration = cfg.Bind(s, "Duration", 4f, new ConfigDescription("表示しておく時間（秒、最後の変化から）", new AcceptableValueRange<float>(1f, 15f)));
        MinDamagePercent = cfg.Bind(s, "MinDamagePercent", 2f,
            new ConfigDescription("これ未満の損傷（最大値に対する %）は表示しない", new AcceptableValueRange<float>(0f, 50f)));
        FontSize = cfg.Bind(s, "FontSize", 18f, new ConfigDescription("文字の大きさ（1080p 基準）", new AcceptableValueRange<float>(10f, 36f)));
        MaxLines = cfg.Bind(s, "MaxLines", 8, new ConfigDescription("モジュールの最大行数", new AcceptableValueRange<int>(2, 20)));
    }
}

internal enum HitOutcome
{
    None,
    Ricochet,
    NonPenetration,
    Penetration,
    KnockedOut,
}

internal enum ModuleState
{
    Damaged,
    Destroyed,
}

/// <summary>損害を受けたモジュール 1 種類（同じ種類はまとめる: 転輪 ×3 など）。</summary>
internal sealed class ModuleDamage
{
    public string Label;
    public bool IsCrew;
    public int Priority;
    public int Count;
    public ModuleState State;
    public float WorstFraction = 1f;
}

/// <summary>位置マーカー 1 つ。</summary>
internal struct ModuleMarker
{
    public Transform Transform;
    public ModuleState State;
    public float Fraction;
    public float Time;
}

/// <summary>1 両の目標に対する命中結果（連射で同じ目標に当て続けたものはまとめる）。</summary>
internal sealed class HitReportData
{
    public IntPtr Vehicle;
    public HitOutcome Outcome;
    public bool OnFire;
    public int Hits;
    public float LastChange;
    public readonly List<ModuleDamage> Modules = new();
    public readonly List<ModuleMarker> Markers = new();
    public int Version;
}

/// <summary>
/// War Thunder 風の命中結果。自車が撃った弾を追い、命中した車両のモジュールの体力が命中前からどれだけ減ったかを出す。
///
///  - 弾: CannonBehaviour.FireInternal が返す弾 ID のうち自車のもの。ProjectileRegister.projectileHistory に出た
///    HitDamageModelProjectileEvent の Type（Penetrated / Absorbed / Deflected）で 貫通 / 非貫通 / 跳弾。
///  - 損害: 各車両の VehicleBehaviour.healthRegister.entries（List&lt;VehicleComponentHealth&gt; = 部品と体力プールの組）を
///    メモリから直接読む（HealthPool.current / max）。貫通の計算と体力の減少は弾が当たった FixedUpdate の中で起きるので、
///    命中を見つけたフレームの 1 つ前の値を命中前として比べる。その後もしばらく追い、火災・誘爆などの損害も足す。
///  - 名前: 部品の型（CombustionEngine → エンジン など）。乗員の座席は乗員が今している役割（gunner → 砲手 など）。
/// </summary>
internal sealed unsafe class HitTracker
{
    public static HitTracker Instance { get; private set; }

    // IL2CPP の List<T>
    private const int ListItems = 0x10;
    private const int ListSize = 0x18;
    private const int ArrayData = 0x20;
    // VehicleHealthRegister / HealthPool（メタデータで確認）
    private const int RegisterEntries = 0x10;
    private const int EntryStride = 0x10;
    private const int PoolCurrent = 0x20;
    private const int PoolMax = 0x24;

    private const float ProjectileTimeout = 20f;
    private const float FollowTime = 3f;
    private const float VehicleSearchInterval = 1f;
    private const float EntriesRefreshInterval = 2f;
    private const int MaxEntries = 4096;
    private const int MaxMarkers = 16;

    private sealed class VehicleHealth
    {
        public IntPtr Vehicle;
        public IntPtr Body;
        public IntPtr Register;
        public IntPtr[] Owners = Array.Empty<IntPtr>();
        public IntPtr[] Pools = Array.Empty<IntPtr>();
        public float[] Max = Array.Empty<float>();
        public float[] Previous = Array.Empty<float>();
        public float[] Current = Array.Empty<float>();
        public bool HasPrevious;
        public float NextEntriesRefresh;
        public VehicleBehaviour Behaviour;
        public VehicleCrewWorkspaceModule Crew;
        public bool CrewSearched;
    }

    private sealed class Shot
    {
        public float Time;
        public IntPtr LastEvent;
    }

    private sealed class ActiveReport
    {
        public HitReportData Data;
        public VehicleHealth Target;
        public float[] Baseline;
        public IntPtr[] BaselineOwners;
        public float FollowUntil;
        public readonly Dictionary<IntPtr, string> Labels = new();
        public readonly HashSet<IntPtr> Marked = new();
    }

    private readonly Dictionary<int, Shot> _shots = new();
    private readonly List<int> _finished = new();
    private readonly Dictionary<IntPtr, VehicleHealth> _byVehicle = new();
    private readonly Dictionary<IntPtr, VehicleHealth> _byBody = new();
    private readonly Dictionary<IntPtr, bool> _playerWeapons = new();
    private readonly Dictionary<IntPtr, string> _classNames = new();
    private readonly List<IntPtr> _stale = new();
    private readonly HashSet<IntPtr> _knockedOut = new();
    private ActiveReport _active;
    private IntPtr _healthPoolClass;

    private VehicleController _controller;
    private float _nextControllerSearch;
    private float _nextVehicleSearch;
    private IntPtr _playerBody;
    private float _nextErrorLog;

    /// <summary>表示中の結果（無ければ null）。</summary>
    public HitReportData Current => _active?.Data;

    /// <summary>プレイヤーが今見ているカメラ（照準器・三人称）。</summary>
    public Camera ActiveCamera
    {
        get
        {
            var camera = _controller != null && !_controller.WasCollected ? _controller.ActiveCamera : null;
            return camera != null ? camera : Camera.main;
        }
    }

    private bool _killsSubscribed;

    public HitTracker() => Instance = this;

    private static HitReportConfig Cfg => Plugin.HitReport;

    // ------------------------------------------------------------------ 発射（FireInternal の後）

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (!Cfg.Enabled.Value || weapon == null || projectileId <= 0 || !IsPlayerWeapon(weapon))
            return;
        if (_shots.Count > 1024)
            _shots.Clear();
        _shots[projectileId] = new Shot { Time = Time.time };

        // 至近距離だと発射と同じフレームの FixedUpdate で命中するので、発射の時点の体力を命中前として取っておく
        try
        {
            RefreshVehicles();
            foreach (var v in _byVehicle.Values)
            {
                ReadHealth(v);
                (v.Previous, v.Current) = (v.Current, v.Previous);
                v.HasPrevious = v.Previous.Length == v.Owners.Length;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Hit report: snapshot failed: {e.Message}");
        }
    }

    private bool IsPlayerWeapon(WeaponBehaviour weapon)
    {
        var player = GetPlayerBody();
        if (player == IntPtr.Zero)
            return false;
        if (player != _playerBody)
        {
            _playerBody = player;
            _playerWeapons.Clear();
        }
        if (_playerWeapons.TryGetValue(weapon.Pointer, out var mine))
            return mine;
        var vehicle = weapon.Transform?.GetComponentInParent<VehicleBehaviour>();
        mine = vehicle != null && vehicle.Rigidbody != null && vehicle.Rigidbody.Pointer == player;
        _playerWeapons[weapon.Pointer] = mine;
        return mine;
    }

    private void OnKill(KillFeed.KillEvent e)
    {
        if (e.Kind != KillFeed.KillKind.Immobilized && e.VictimPointer != IntPtr.Zero)
        {
            if (_knockedOut.Count > 256)
                _knockedOut.Clear();
            _knockedOut.Add(e.VictimPointer);
        }
        if (_active != null && e.Kind == KillFeed.KillKind.Destroyed && e.VictimPointer == _active.Target.Vehicle)
        {
            _active.Data.Outcome = HitOutcome.KnockedOut;
            Touch(_active);
        }
    }

    // ------------------------------------------------------------------ 毎フレーム

    public void Update()
    {
        if (!Cfg.Enabled.Value || Time.timeScale <= 0f)
            return;
        if (!_killsSubscribed && KillFeed.KillTracker.Instance != null)
        {
            KillFeed.KillTracker.Instance.Killed += OnKill;
            _killsSubscribed = true;
        }
        try
        {
            var tracking = _shots.Count > 0 || (_active != null && Time.time < _active.FollowUntil);
            if (!tracking)
            {
                foreach (var v in _byVehicle.Values)
                    v.HasPrevious = false;
                ExpireReport();
                return;
            }

            RefreshVehicles();
            foreach (var v in _byVehicle.Values)
                ReadHealth(v);

            ProcessShots();
            if (_active != null && Time.time < _active.FollowUntil)
                UpdateReport(_active);
            ExpireReport();

            foreach (var v in _byVehicle.Values)
            {
                (v.Previous, v.Current) = (v.Current, v.Previous);
                v.HasPrevious = v.Previous.Length == v.Owners.Length;
            }
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Hit report failed: {e}");
            }
        }
    }

    private void ExpireReport()
    {
        if (_active != null && Time.time - _active.Data.LastChange > Cfg.Duration.Value + 1f && Time.time > _active.FollowUntil)
            _active = null;
    }

    private void ProcessShots()
    {
        if (_shots.Count == 0)
            return;
        var history = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>()?.projectileHistory;
        if (history == null)
            return;
        var now = Time.time;
        _finished.Clear();
        foreach (var pair in _shots)
        {
            var shot = pair.Value;
            if (now - shot.Time > ProjectileTimeout)
            {
                _finished.Add(pair.Key);
                continue;
            }
            if (!history.TryGetValue(pair.Key, out var latest) || latest == null || latest.Pointer == shot.LastEvent)
                continue;
            shot.LastEvent = latest.Pointer;
            var hit = latest.TryCast<HitDamageModelProjectileEvent>();
            if (hit != null)
                OnHit(hit, now);
            if (latest.Terminal)
                _finished.Add(pair.Key);
        }
        foreach (var id in _finished)
            _shots.Remove(id);
    }

    private void OnHit(HitDamageModelProjectileEvent hit, float now)
    {
        var body = hit.DamageModel?.RigidBody;
        if (body == null || body.Pointer == _playerBody || !_byBody.TryGetValue(body.Pointer, out var target))
            return;
        // 撃破済みの車両（残骸）への命中は数えない（撃破後に撃ち続けても「撃破 ×N」にしない）
        if (_knockedOut.Contains(target.Vehicle))
            return;

        var type = hit.Type;
        var outcome = (type & HitDamageModelProjectileEvent.HitType.Penetrated) != 0 ? HitOutcome.Penetration
            : (type & HitDamageModelProjectileEvent.HitType.Deflected) != 0 ? HitOutcome.Ricochet
            : HitOutcome.NonPenetration;

        var report = _active;
        if (report == null || report.Target != target || now - report.Data.LastChange > Cfg.Duration.Value)
        {
            report = new ActiveReport
            {
                Target = target,
                Data = new HitReportData { Vehicle = target.Vehicle },
                // 命中は前の Update の後の FixedUpdate で起きているので、前フレームの値が命中前
                Baseline = (float[])(target.HasPrevious ? target.Previous : target.Current).Clone(),
                BaselineOwners = (IntPtr[])target.Owners.Clone(),
            };
            _active = report;
        }
        report.Data.Hits++;
        if (outcome > report.Data.Outcome && report.Data.Outcome != HitOutcome.KnockedOut)
            report.Data.Outcome = outcome;
        report.FollowUntil = now + FollowTime;
        Touch(report);
    }

    private static void Touch(ActiveReport report)
    {
        report.Data.LastChange = Time.time;
        report.Data.Version++;
    }

    /// <summary>命中前からの減少を集計する。</summary>
    private void UpdateReport(ActiveReport report)
    {
        var target = report.Target;
        if (target.Behaviour == null || target.Behaviour.WasCollected)
            return;
        // 部品の並びが変わっていたら（再構築など）比べられない
        if (report.BaselineOwners.Length != target.Owners.Length)
            return;

        var minFraction = Cfg.MinDamagePercent.Value / 100f;
        var modules = new Dictionary<string, ModuleDamage>();
        var changed = false;
        for (var i = 0; i < target.Owners.Length; i++)
        {
            if (target.Owners[i] != report.BaselineOwners[i])
                return;
            var max = target.Max[i];
            if (!(max > 0f))
                continue;
            var before = report.Baseline[i];
            var now = target.Current[i];
            if (before <= 0f || before - now < max * minFraction && !(now <= 0f && before > 0f))
                continue;

            if (!report.Labels.TryGetValue(target.Owners[i], out var label))
            {
                label = Label(target, target.Owners[i], out _);
                report.Labels[target.Owners[i]] = label;
            }
            if (label == null)
                continue;

            var fraction = Mathf.Clamp01(now / max);
            var state = now <= 0f ? ModuleState.Destroyed : ModuleState.Damaged;
            if (!modules.TryGetValue(label, out var m))
            {
                var isCrew = ClassName(target.Owners[i]) == "CrewSeat";
                m = new ModuleDamage { Label = label, IsCrew = isCrew, Priority = Priority(label, isCrew) };
                modules[label] = m;
            }
            m.Count++;
            if (state > m.State || m.Count == 1)
                m.State = state;
            m.WorstFraction = Mathf.Min(m.WorstFraction, fraction);

            if (Cfg.ShowMarkers.Value && report.Data.Markers.Count < MaxMarkers && report.Marked.Add(target.Owners[i]))
            {
                var transform = new Il2CppSystem.Object(target.Owners[i]).TryCast<VehicleComponent>()?.transform;
                if (transform != null)
                {
                    report.Data.Markers.Add(new ModuleMarker { Transform = transform, State = state, Fraction = fraction, Time = Time.time });
                    changed = true;
                }
            }
        }

        var list = report.Data.Modules;
        if (!SameModules(list, modules))
        {
            list.Clear();
            list.AddRange(modules.Values);
            list.Sort((a, b) =>
            {
                if (a.State != b.State)
                    return b.State.CompareTo(a.State);
                if (a.Priority != b.Priority)
                    return a.Priority.CompareTo(b.Priority);
                return a.WorstFraction.CompareTo(b.WorstFraction);
            });
            changed = true;
        }

        var onFire = target.Behaviour != null && (target.Behaviour.Flags & VehicleFlags.OnFire) != 0;
        if (onFire != report.Data.OnFire)
        {
            report.Data.OnFire = onFire;
            changed = true;
        }
        if (changed)
            Touch(report);
    }

    private static bool SameModules(List<ModuleDamage> current, Dictionary<string, ModuleDamage> next)
    {
        if (current.Count != next.Count)
            return false;
        foreach (var m in current)
        {
            if (!next.TryGetValue(m.Label, out var n) || n.State != m.State || n.Count != m.Count
                || Mathf.Abs(n.WorstFraction - m.WorstFraction) > 0.01f)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ 体力の読み取り（メモリから直接）

    private void RefreshVehicles()
    {
        if (Time.unscaledTime < _nextVehicleSearch)
            return;
        _nextVehicleSearch = Time.unscaledTime + VehicleSearchInterval;
        var found = Object.FindObjectsByType(Il2CppType.Of<VehicleBehaviour>(), FindObjectsSortMode.None);
        var seen = new HashSet<IntPtr>();
        if (found != null)
        {
            foreach (var obj in found)
            {
                var vehicle = obj?.TryCast<VehicleBehaviour>();
                if (vehicle == null)
                    continue;
                seen.Add(vehicle.Pointer);
                if (_byVehicle.ContainsKey(vehicle.Pointer))
                    continue;
                var body = vehicle.Rigidbody;
                var register = vehicle.healthRegister;
                if (body == null || register == null)
                    continue;
                var v = new VehicleHealth { Vehicle = vehicle.Pointer, Body = body.Pointer, Register = register.Pointer, Behaviour = vehicle };
                _byVehicle[v.Vehicle] = v;
                _byBody[v.Body] = v;
            }
        }
        _stale.Clear();
        foreach (var ptr in _byVehicle.Keys)
        {
            if (!seen.Contains(ptr))
                _stale.Add(ptr);
        }
        foreach (var ptr in _stale)
        {
            var v = _byVehicle[ptr];
            _byVehicle.Remove(ptr);
            _byBody.Remove(v.Body);
            _knockedOut.Remove(ptr);
            if (_active != null && _active.Target == v)
                _active.FollowUntil = 0f;
        }
    }

    private void ReadHealth(VehicleHealth v)
    {
        if (Time.unscaledTime >= v.NextEntriesRefresh)
        {
            v.NextEntriesRefresh = Time.unscaledTime + EntriesRefreshInterval;
            ReadEntries(v);
        }
        var n = v.Pools.Length;
        if (v.Current.Length != n)
        {
            v.Current = new float[n];
            v.Previous = new float[n];
            v.HasPrevious = false;
        }
        for (var i = 0; i < n; i++)
        {
            var pool = v.Pools[i];
            v.Current[i] = pool == IntPtr.Zero ? 0f : *(float*)(pool + PoolCurrent);
        }
    }

    private void ReadEntries(VehicleHealth v)
    {
        if (_healthPoolClass == IntPtr.Zero)
            _healthPoolClass = Il2CppClassPointerStore<HealthPool>.NativeClassPtr;

        var list = *(IntPtr*)(v.Register + RegisterEntries);
        if (list == IntPtr.Zero)
            return;
        var items = *(IntPtr*)(list + ListItems);
        var size = *(int*)(list + ListSize);
        if (items == IntPtr.Zero || size < 0 || size > MaxEntries)
            return;
        if (size == v.Owners.Length && Same(v, items, size))
            return;

        var owners = new IntPtr[size];
        var pools = new IntPtr[size];
        var max = new float[size];
        for (var i = 0; i < size; i++)
        {
            var element = items + ArrayData + i * EntryStride;
            owners[i] = *(IntPtr*)element;
            var pool = *(IntPtr*)(element + 8);
            // HealthPool 以外の実装なら読まない
            if (pool != IntPtr.Zero && IL2CPP.il2cpp_object_get_class(pool) == _healthPoolClass)
            {
                pools[i] = pool;
                max[i] = *(float*)(pool + PoolMax);
            }
        }
        v.Owners = owners;
        v.Pools = pools;
        v.Max = max;
        v.HasPrevious = false;
    }

    private static bool Same(VehicleHealth v, IntPtr items, int size)
    {
        for (var i = 0; i < size; i++)
        {
            if (*(IntPtr*)(items + ArrayData + i * EntryStride) != v.Owners[i])
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ 照準した車両の状態（Targets.TargetInfoTracker から）

    /// <summary>照準した車両で表示するモジュール（表示順）。</summary>
    private static readonly string[] InspectedModules =
        { "Engine", "Transmission", "Main gun", "Breech", "Ammo rack", "Fuel tank", "Track", "Turret drive" };

    /// <summary>車両の乗員と主なモジュールの今の体力を読む。</summary>
    public bool Inspect(VehicleBehaviour vehicle, Targets.TargetDetails details)
    {
        details.Clear();
        if (vehicle == null || vehicle.WasCollected)
            return false;
        if (!_byVehicle.TryGetValue(vehicle.Pointer, out var v))
        {
            _nextVehicleSearch = 0f;
            RefreshVehicles();
            if (!_byVehicle.TryGetValue(vehicle.Pointer, out v))
                return false;
        }
        if (Time.unscaledTime >= v.NextEntriesRefresh)
        {
            v.NextEntriesRefresh = Time.unscaledTime + EntriesRefreshInterval;
            ReadEntries(v);
        }

        var n = Math.Min(v.Owners.Length, v.Pools.Length);
        for (var i = 0; i < n; i++)
        {
            var pool = v.Pools[i];
            var max = v.Max[i];
            if (pool == IntPtr.Zero || !(max > 0f) || ClassName(v.Owners[i]) == "CrewSeat")
                continue;
            var label = Label(v, v.Owners[i], out _);
            var order = label == null ? -1 : Array.IndexOf(InspectedModules, label);
            if (order < 0)
                continue;
            var fraction = Mathf.Clamp01(*(float*)(pool + PoolCurrent) / max);
            ModuleDamage m = null;
            foreach (var existing in details.Modules)
            {
                if (existing.Label == label)
                {
                    m = existing;
                    break;
                }
            }
            if (m == null)
            {
                m = new ModuleDamage { Label = label, Priority = order };
                details.Modules.Add(m);
            }
            m.Count++;
            m.WorstFraction = Mathf.Min(m.WorstFraction, fraction);
            if (fraction <= 0f)
                m.State = ModuleState.Destroyed;
        }
        details.Modules.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var crew = GetCrew(v);
        var operators = crew?.operators;
        if (operators != null)
        {
            for (var i = 0; i < operators.Length; i++)
            {
                var op = operators[i];
                if (op == null || op.internalOperator == null)
                    continue;
                details.CrewTotal++;
                if (op.Enabled)
                {
                    details.CrewAlive++;
                    var member = op.internalOperator.TryCast<CrewMember>();
                    if (member != null && member.HealthFraction < 0.99f)
                        details.CrewWounded++;
                    continue;
                }
                var seat = op.seat?.Component;
                details.DeadCrew.Add(seat != null ? CrewRole(v, seat.Pointer) : "Crew");
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ 名前

    private string ClassName(IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return string.Empty;
        var klass = IL2CPP.il2cpp_object_get_class(obj);
        if (_classNames.TryGetValue(klass, out var name))
            return name;
        name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? string.Empty;
        _classNames[klass] = name;
        return name;
    }

    /// <summary>部品の表示名（英語の原文。翻訳は表示側）。表示しない部品（装甲板など）は null。</summary>
    private string Label(VehicleHealth v, IntPtr owner, out bool isCrew)
    {
        isCrew = false;
        switch (ClassName(owner))
        {
            case "CrewSeat":
                isCrew = true;
                return CrewRole(v, owner);
            case "CombustionEngine":
            case "EngineVent":
                return "Engine";
            case "EngineExhaust":
                return "Exhaust";
            case "TransmissionBlock":
                return "Transmission";
            case "VehiclePowertrain":
            case "ClutchBrakingPowertrain":
            case "TwinTransmissionPowertrain":
            case "PowertrainComponent":
            case "DriveshaftBuilder":
            case "PowertrainSteeringControls":
                return "Drivetrain";
            case "FuelTank":
            case "FuelReserve":
            case "FuelPort":
                return "Fuel tank";
            case "AmmoRack":
            case "AmmoRackModel":
                return "Ammo rack";
            case "Cannon":
            case "CannonBarrelObject":
            case "CannonBarrelSegment":
                return "Main gun";
            case "CannonBreech":
                return "Breech";
            case "TraverseMotor":
            case "TraverseMotorModelComponent":
                return "Turret drive";
            case "TurretRing":
                return "Turret ring";
            case "TurretBasket":
                return "Turret basket";
            case "LayingDrive":
                return "Elevation drive";
            case "TrackAssembly":
            case "TrackBelt":
            case "TrackBeltSegmentArray":
                return "Track";
            case "WheelArray":
            case "WheelMount":
            case "WheelMountArray":
            case "Axle":
            case "AxleBogie":
            case "AxleRod":
            case "SpareWheel":
                return "Road wheels";
            case "Suspension":
            case "SuspensionArm":
            case "SuspensionSpring":
            case "TorsionBar":
            case "ChristieCoilSpring":
            case "LeafSpringSuspension":
            case "VoluteSuspensionSpring":
                return "Suspension";
            case "Radio":
            case "Antenna":
                return "Radio";
            case "GunnerSight":
                return "Gun sight";
            case "Viewport":
                return "Vision port";
            case "CommanderCupola":
                return "Cupola";
            case "Mantlet":
                return "Mantlet";
            default:
                return null; // 装甲板・装飾など
        }
    }

    private static readonly string[] RolePriority = { "commander", "gunner", "driver", "loader", "radio" };

    /// <summary>座席の乗員が今している役割（複数なら 車長 > 砲手 > 操縦手 > 装填手 > 無線手）。</summary>
    private static string CrewRole(VehicleHealth v, IntPtr seat)
    {
        try
        {
            var crew = GetCrew(v);
            if (crew == null)
                return "Crew";
            var operators = crew.operators;
            var tasks = crew.tasks;
            if (operators == null || tasks == null)
                return "Crew";
            for (var i = 0; i < operators.Length; i++)
            {
                var op = operators[i];
                var component = op?.seat?.Component;
                if (component == null || component.Pointer != seat)
                    continue;
                var roleId = op.ActiveRoleID.ID;
                if (roleId < 0)
                    roleId = op.seat.RoleID.ID;
                var best = int.MaxValue;
                for (var t = 0; t < tasks.Length; t++)
                {
                    var task = tasks[t];
                    if (task == null || task.RoleID.ID != roleId)
                        continue;
                    var index = Array.IndexOf(RolePriority, task.IconIdentifier?.ToLowerInvariant());
                    if (index >= 0 && index < best)
                        best = index;
                }
                return best switch
                {
                    0 => "Commander",
                    1 => "Gunner",
                    2 => "Driver",
                    3 => "Loader",
                    4 => "Radio operator",
                    _ => "Crew",
                };
            }
        }
        catch (Exception)
        {
            // 役割が読めない
        }
        return "Crew";
    }

    private static VehicleCrewWorkspaceModule GetCrew(VehicleHealth v)
    {
        if (v.CrewSearched && v.Crew != null && !v.Crew.WasCollected)
            return v.Crew;
        v.CrewSearched = true;
        v.Crew = null;
        var modules = v.Behaviour?.modules;
        if (modules == null)
            return null;
        for (var i = 0; i < modules.Count; i++)
        {
            var crew = modules[i]?.TryCast<VehicleCrewWorkspaceModule>();
            if (crew != null)
                return v.Crew = crew;
        }
        return null;
    }

    private static int Priority(string label, bool isCrew)
    {
        if (isCrew)
            return 0;
        return label switch
        {
            "Ammo rack" => 1,
            "Engine" => 2,
            "Main gun" => 3,
            "Breech" => 4,
            "Fuel tank" => 5,
            "Transmission" => 6,
            "Drivetrain" => 7,
            "Turret drive" => 8,
            "Elevation drive" => 9,
            "Turret ring" => 10,
            "Track" => 11,
            _ => 20,
        };
    }

    private IntPtr GetPlayerBody()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        var body = _controller?.ControlledVehicle?.Rigidbody;
        return body != null ? body.Pointer : IntPtr.Zero;
    }
}
