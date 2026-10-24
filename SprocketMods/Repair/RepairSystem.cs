using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.ContinuousTracks;
using Sprocket.Vehicles.Tracks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Repair;

/// <summary>修理の設定（[Repair] セクション）。</summary>
public class RepairConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyboardShortcut> Key;
    public readonly ConfigEntry<float> BaseTime;
    public readonly ConfigEntry<bool> AiRepair;
    public readonly ConfigEntry<float> AiDelay;
    public readonly ConfigEntry<bool> ShowHint;

    public RepairConfig(ConfigFile cfg)
    {
        const string s = "Repair";
        Enabled = cfg.Bind(s, "Enabled", true, "キーの長押しで、エンジン以外の損傷した部品を修理する（War Thunder 風。火災は消せない）");
        Key = cfg.Bind(s, "Key", new KeyboardShortcut(KeyCode.F), "修理のキー（長押し）");
        BaseTime = cfg.Bind(s, "BaseTime", 20f,
            new ConfigDescription("乗員 4 人が全員そろっているときの修理時間（秒）。乗員が多いほど短く、乗員が減ると長くなる",
                new AcceptableValueRange<float>(5f, 90f)));
        AiRepair = cfg.Bind(s, "AiRepair", true, "AI の車両も損傷したら修理する");
        AiDelay = cfg.Bind(s, "AiDelay", 3f, new ConfigDescription("AI が損傷に気付いてから修理を始めるまでの時間（秒）", new AcceptableValueRange<float>(0f, 20f)));
        ShowHint = cfg.Bind(s, "ShowHint", true, "修理できる損傷があるとき、画面下に「F 長押しで修理」と修理時間を表示する");
    }
}

internal enum RepairMode
{
    Idle,
    /// <summary>キーを押して修理を始めようとしている（ArmTime 秒で開始）。</summary>
    Arming,
    Repairing,
}

/// <summary>
/// War Thunder 風の修理。
///  - 自車: 修理キーを ArmTime 秒（1 秒）押し続けると修理が始まり、あとはキーを離しても進む。修理中にもう一度
///    ArmTime 秒押すと中止。
///  - AI: 損傷に気付いてから AiDelay 秒後に修理を始める。
///  - 修理中はその場に止める（Rigidbody の水平方向の位置と向きを固定）。終わったら元に戻して普通に走れる。
///  - 修理時間 = BaseTime × √(4 / 満席の乗員数) × (満席の乗員数 / 生きている乗員数)。
///    乗員が多い車両ほど早く、乗員が減ると長くなる（途中で乗員が倒れたら残り時間が伸びる）。乗員が全滅したら中止。
///  - 直すもの: 車両の healthRegister の部品の体力（HealthUtils.FullyHeal。ゲームの体力変化の処理が走る）と、切れた履帯
///    （TrackAssembly.Broken と、履帯の帯のシミュレーション TrackBeltBehaviour の切れた状態。帯は ResetPositions で履き直す）。
///    エンジン（CombustionEngine / EngineVent / EngineExhaust）と乗員は直さない。火災も消さない。
/// </summary>
internal sealed unsafe class RepairSystem
{
    public static RepairSystem Instance { get; private set; }

    // IL2CPP の List<T> と VehicleHealthRegister / HealthPool（Hits/HitTracker.cs と同じ）
    private const int ListItems = 0x10;
    private const int ListSize = 0x18;
    private const int ArrayData = 0x20;
    private const int RegisterEntries = 0x10;
    private const int EntryStride = 0x10;
    private const int PoolCurrent = 0x20;
    private const int PoolMax = 0x24;
    private const int MaxEntries = 4096;

    private const float ScanInterval = 0.5f;
    private const float SearchInterval = 1f;
    private const float ReferenceCrew = 4f;
    public const float ArmTime = 1f;

    // その場に止める強さ（1 物理ステップで位置・向きのずれをこの割合だけ戻す）と、戻す速さの上限
    private const float HoldStiffness = 0.25f;
    private const float HoldMaxSpeed = 3f;
    private const float HoldMaxYawRate = 1f;

    private static readonly HashSet<string> NotRepairable = new() { "CombustionEngine", "EngineVent", "EngineExhaust", "CrewSeat" };

    private sealed class VehicleState
    {
        public VehicleBehaviour Vehicle;
        public string Name;
        public VehicleCrewWorkspaceModule Crew;
        public bool CrewSearched;
        public bool Damaged;
        public RepairMode Mode;
        public float Progress;
        public float DamagedSince = float.NaN;
        public float LastSeen;
        public Rigidbody HeldBody;
        public Vector3 HoldPosition;
        public float HoldYaw;
        public readonly List<IntPtr> Drivers = new();
    }

    private readonly Dictionary<IntPtr, VehicleState> _vehicles = new();
    private readonly Dictionary<IntPtr, string> _classNames = new();
    private readonly List<IntPtr> _stale = new();
    private IntPtr _healthPoolClass;
    private VehicleController _controller;
    private float _nextControllerSearch;
    private float _nextSearch;
    private float _nextScan;
    private float _nextErrorLog;

    // 自車のキー操作
    private float _armTime;
    private bool _waitForRelease;

    public RepairSystem() => Instance = this;

    private static RepairConfig Cfg => Plugin.Repair;

    // ------------------------------------------------------------------ 表示用（自車）

    /// <summary>自車に修理できる損傷がある。</summary>
    public bool PlayerDamaged { get; private set; }

    /// <summary>自車の修理の状態。</summary>
    public RepairMode PlayerMode { get; private set; }

    /// <summary>修理を始める（修理中なら中止する）までのキーの押し具合（0〜1）。</summary>
    public float PlayerArmFraction { get; private set; }

    /// <summary>自車の修理の進み具合（0〜1）。</summary>
    public float PlayerFraction { get; private set; }

    /// <summary>自車の修理の残り時間（秒）。</summary>
    public float PlayerRemaining { get; private set; }

    /// <summary>乗員が全滅していて直せない。</summary>
    public bool PlayerNoCrew { get; private set; }

    /// <summary>直し終えた時刻（「修理完了」の表示用）。</summary>
    public float PlayerCompletedAt { get; private set; } = float.NegativeInfinity;

    // ------------------------------------------------------------------ 毎フレーム

    public void Update()
    {
        if (!Cfg.Enabled.Value)
        {
            ReleaseAll();
            PlayerDamaged = false;
            PlayerMode = RepairMode.Idle;
            return;
        }
        if (Time.timeScale <= 0f)
            return;
        try
        {
            var now = Time.time;
            if (Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + SearchInterval;
                Search();
            }
            var scan = Time.unscaledTime >= _nextScan;
            if (scan)
                _nextScan = Time.unscaledTime + ScanInterval;

            var player = GetPlayerVehicle();
            PlayerDamaged = false;
            PlayerNoCrew = false;
            PlayerArmFraction = 0f;
            var playerSeen = false;
            foreach (var pair in _vehicles)
            {
                var state = pair.Value;
                var vehicle = state.Vehicle;
                if (vehicle == null || vehicle.WasCollected)
                    continue;
                var isPlayer = player != null && pair.Key == player.Pointer;
                if (scan || isPlayer)
                    state.Damaged = HasRepairableDamage(vehicle);
                if (!state.Damaged && state.Mode != RepairMode.Repairing)
                {
                    state.Mode = RepairMode.Idle;
                    state.Progress = 0f;
                    state.DamagedSince = float.NaN;
                    continue;
                }
                if (float.IsNaN(state.DamagedSince))
                    state.DamagedSince = now;

                var required = RequiredTime(vehicle, state, out var alive);
                if (isPlayer)
                {
                    playerSeen = true;
                    UpdatePlayer(state, required, alive);
                }
                else
                {
                    UpdateAi(state, required, alive, now);
                }
            }
            if (!playerSeen)
            {
                PlayerMode = RepairMode.Idle;
                _armTime = 0f;
            }
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Repair failed: {e}");
            }
        }
    }

    private void UpdatePlayer(VehicleState state, float required, int alive)
    {
        PlayerDamaged = state.Damaged;
        PlayerNoCrew = alive == 0;

        // キー: ArmTime 秒押し続けると 開始（修理中なら中止）。開始・中止の後は一度離すまで受け付けない
        var pressed = Cfg.Key.Value.IsPressed();
        if (!pressed)
            _waitForRelease = false;
        var canArm = alive > 0 && (state.Mode == RepairMode.Repairing || state.Damaged);
        if (pressed && !_waitForRelease && canArm)
        {
            _armTime += Time.unscaledDeltaTime;
            if (_armTime >= ArmTime)
            {
                _armTime = 0f;
                _waitForRelease = true;
                if (state.Mode == RepairMode.Repairing)
                    Stop(state, "cancelled");
                else
                    Begin(state);
            }
        }
        else
        {
            _armTime = 0f;
        }
        PlayerArmFraction = Mathf.Clamp01(_armTime / ArmTime);

        if (state.Mode == RepairMode.Repairing)
        {
            if (alive == 0)
                Stop(state, "no crew");
            else if (Advance(state, required, Time.deltaTime))
                PlayerCompletedAt = Time.unscaledTime;
        }
        else if (_armTime > 0f)
        {
            state.Mode = RepairMode.Arming;
        }
        else
        {
            state.Mode = RepairMode.Idle;
        }

        PlayerMode = state.Mode;
        PlayerFraction = state.Mode == RepairMode.Repairing ? Mathf.Clamp01(state.Progress / required) : 0f;
        PlayerRemaining = alive > 0 ? Mathf.Max(0f, required - (state.Mode == RepairMode.Repairing ? state.Progress : 0f)) : 0f;
    }

    private void UpdateAi(VehicleState state, float required, int alive, float now)
    {
        if (state.Mode == RepairMode.Repairing)
        {
            if (alive == 0)
                Stop(state, "no crew");
            else
                Advance(state, required, Time.deltaTime);
            return;
        }
        if (Cfg.AiRepair.Value && alive > 0 && state.Damaged && now - state.DamagedSince >= Cfg.AiDelay.Value)
            Begin(state);
    }

    private void Begin(VehicleState state)
    {
        state.Mode = RepairMode.Repairing;
        state.Progress = 0f;
        Hold(state);
    }

    private void Stop(VehicleState state, string reason)
    {
        Release(state);
        state.Mode = RepairMode.Idle;
        state.Progress = 0f;
        if (Plugin.Cfg.DebugLog.Value)
            Plugin.Logger.LogInfo($"Repair: {state.Name} stopped ({reason})");
    }

    /// <summary>修理を進め、終わったら直して true。</summary>
    private bool Advance(VehicleState state, float required, float dt)
    {
        state.Progress += dt;
        if (state.Progress < required)
            return false;
        var repaired = RepairAll(state.Vehicle);
        Plugin.Logger.LogInfo($"Repair: {state.Name} repaired {repaired} part(s) in {required:0.0} s");
        Release(state);
        state.Mode = RepairMode.Idle;
        state.Progress = 0f;
        state.Damaged = false;
        state.DamagedSince = float.NaN;
        return true;
    }

    // ------------------------------------------------------------------ その場に止める

    /// <summary>
    /// 修理を始めた場所を覚える。実際に止めるのは FixedUpdate で、水平方向の速度と左右の回転を毎ステップ打ち消し、
    /// 覚えた場所・向きからのずれを少しずつ戻す（上下とピッチ・ロールはそのままなので、坂や段差でも自然に落ち着く）。
    /// ※ Rigidbody.constraints で固定すると、履帯の物理の計算が壊れて車両が遠くへ吹き飛ぶので使わない。
    /// </summary>
    private static void Hold(VehicleState state)
    {
        BlockDriving(state);
        var body = state.Vehicle != null && !state.Vehicle.WasCollected ? state.Vehicle.Rigidbody : null;
        if (body == null || state.HeldBody != null)
            return;
        state.HeldBody = body;
        state.HoldPosition = body.position;
        state.HoldYaw = body.rotation.eulerAngles.y;
    }

    private static void Release(VehicleState state)
    {
        state.HeldBody = null;
        foreach (var driver in state.Drivers)
            BlockedDrivers.Remove(driver);
        state.Drivers.Clear();
    }

    // ------------------------------------------------------------------ 操縦の無効化

    /// <summary>修理中の車両の操縦手（Driver）。Driver.ExecutePerform の前に操縦の入力を 0 にする（Patches から）。</summary>
    private static readonly HashSet<IntPtr> BlockedDrivers = new();

    /// <summary>この操縦手の車両が修理中なら true（入力を無視させる）。</summary>
    public static bool IsDrivingBlocked(IntPtr driver) => BlockedDrivers.Count > 0 && BlockedDrivers.Contains(driver);

    /// <summary>
    /// 車両の操縦手（乗員の仕事 InjectedTask.internalTask が Driver のもの）を覚える。修理中は WASD・クルーズコントロール・AI の
    /// 操縦がすべて操縦手の入力（Driver.Solution の Throttle / Steer）を通るので、ここを 0 にすれば
    /// エンジンも変速機も一切反応しない。
    /// </summary>
    private static void BlockDriving(VehicleState state)
    {
        var tasks = state.Crew?.tasks;
        if (tasks == null)
            return;
        for (var i = 0; i < tasks.Length; i++)
        {
            var driver = tasks[i]?.internalTask?.TryCast<Driver>();
            if (driver == null || state.Drivers.Contains(driver.Pointer))
                continue;
            state.Drivers.Add(driver.Pointer);
            BlockedDrivers.Add(driver.Pointer);
        }
    }

    /// <summary>物理ステップごとに、修理中の車両をその場に止める（HudBehaviour.FixedUpdate から）。</summary>
    public void FixedUpdate()
    {
        if (!Cfg.Enabled.Value)
            return;
        var dt = Time.fixedDeltaTime;
        if (!(dt > 0f))
            return;
        foreach (var state in _vehicles.Values)
        {
            var body = state.HeldBody;
            if (state.Mode != RepairMode.Repairing || body == null)
                continue;
            if (body.WasCollected)
            {
                state.HeldBody = null;
                continue;
            }
            // 水平方向: 速度を捨て、ずれの分だけ戻す速度にする
            var offset = state.HoldPosition - body.position;
            offset.y = 0f;
            var back = Vector3.ClampMagnitude(offset * (HoldStiffness / dt), HoldMaxSpeed);
            var v = body.velocity;
            body.velocity = new Vector3(back.x, v.y, back.z);

            // 左右の回転: ヨーの角速度を捨て、向きのずれの分だけ戻す
            var yawError = Mathf.DeltaAngle(body.rotation.eulerAngles.y, state.HoldYaw) * Mathf.Deg2Rad;
            var yawRate = Mathf.Clamp(yawError * (HoldStiffness / dt), -HoldMaxYawRate, HoldMaxYawRate);
            var w = body.angularVelocity;
            body.angularVelocity = new Vector3(w.x, yawRate, w.z);
        }
    }

    private void ReleaseAll()
    {
        foreach (var state in _vehicles.Values)
        {
            if (state.Mode == RepairMode.Repairing)
            {
                Release(state);
                state.Mode = RepairMode.Idle;
                state.Progress = 0f;
            }
        }
    }

    /// <summary>修理時間。乗員が全滅していれば alive = 0。</summary>
    private static float RequiredTime(VehicleBehaviour vehicle, VehicleState state, out int alive)
    {
        alive = CountCrew(vehicle, state, out var total);
        if (total <= 0)
        {
            alive = 1; // 乗員の仕組みが無い車両
            return Cfg.BaseTime.Value;
        }
        if (alive <= 0)
            return float.PositiveInfinity;
        var full = Cfg.BaseTime.Value * Mathf.Sqrt(ReferenceCrew / total);
        return Mathf.Clamp(full * total / alive, 3f, 300f);
    }

    // ------------------------------------------------------------------ 損傷の読み取りと修理

    /// <summary>エンジンと乗員以外で体力が減った部品、または切れた履帯があるか。</summary>
    private bool HasRepairableDamage(VehicleBehaviour vehicle)
    {
        var found = false;
        ForEachEntry(vehicle, (owner, pool) =>
        {
            if (found)
                return;
            var name = ClassName(owner);
            if (NotRepairable.Contains(name))
                return;
            var max = *(float*)(pool + PoolMax);
            if (max > 0f && *(float*)(pool + PoolCurrent) < max * 0.999f)
                found = true;
            else if (name == "TrackAssembly" && IsBrokenTrack(owner))
                found = true;
        });
        return found;
    }

    private int RepairAll(VehicleBehaviour vehicle)
    {
        var count = 0;
        ForEachEntry(vehicle, (owner, pool) =>
        {
            var name = ClassName(owner);
            if (NotRepairable.Contains(name))
                return;
            var max = *(float*)(pool + PoolMax);
            if (max > 0f && *(float*)(pool + PoolCurrent) < max * 0.999f)
            {
                try
                {
                    // ゲームの体力変化の処理（HealthChanged → 部品の OnHealthChanged）が走るように公式の関数で直す
                    HealthUtils.FullyHeal(new IDurable(pool));
                }
                catch (Exception)
                {
                    new HealthPool(pool).HealthDelta(max - *(float*)(pool + PoolCurrent));
                }
                count++;
            }
            if (name == "TrackAssembly" && RepairTrack(owner))
                count++;
        });
        return count;
    }

    /// <summary>乗員が全員車両を降りる（乗員の座席の体力をゲームの HealthUtils.Kill で 0 にする。ゲームはこれを撃破として扱う）。</summary>
    public int KillCrew(VehicleBehaviour vehicle)
    {
        var count = 0;
        if (vehicle == null || vehicle.WasCollected)
            return 0;
        ForEachEntry(vehicle, (owner, pool) =>
        {
            if (ClassName(owner) != "CrewSeat" || *(float*)(pool + PoolCurrent) <= 0f)
                return;
            HealthUtils.Kill(new IDurable(pool));
            count++;
        });
        return count;
    }

    private static bool IsBrokenTrack(IntPtr owner)
    {
        var track = new Il2CppSystem.Object(owner).TryCast<TrackAssembly>();
        if (track == null)
            return false;
        if (track.Broken)
            return true;
        var behaviour = track.Controller?.TryCast<TrackBehaviour>();
        return behaviour != null && (behaviour.Broken || BeltBroken(behaviour.segmentController));
    }

    /// <summary>
    /// 履帯を直す。TrackAssembly.Broken と TrackBehaviour（走行の状態）を戻し、帯のシミュレーション（TrackBeltBehaviour）の
    /// 切れた状態（broken / breakRequested と各リンクの SegmentFlags.Broken）を消して ResetPositions で車輪の周りに履き直す。
    /// （TrackAssembly.Broken = false だけでは帯は外れたままの見た目になる）
    /// </summary>
    private static bool RepairTrack(IntPtr owner)
    {
        var track = new Il2CppSystem.Object(owner).TryCast<TrackAssembly>();
        if (track == null)
            return false;
        var behaviour = track.Controller?.TryCast<TrackBehaviour>();
        var wasBroken = track.Broken || behaviour != null && (behaviour.Broken || BeltBroken(behaviour.segmentController));
        if (!wasBroken)
            return false;
        if (track.Broken)
            track.Broken = false;
        if (behaviour != null)
        {
            if (behaviour.Broken)
                behaviour.Repair();
            FixBelt(behaviour.segmentController); // ゲーム側で状態が戻っていても、帯は必ず履き直す
        }
        return true;
    }

    // TrackBeltBehaviour のフィールドの位置（実行時に IL2CPP から引く）
    private static IntPtr _beltClass;
    private static int _beltBroken = -1;
    private static int _beltBreakRequested = -1;
    private static int _beltSegmentFlags = -1;
    private const byte SegmentBroken = 16; // SegmentFlags.Broken

    private static bool ResolveBelt(IntPtr belt)
    {
        var klass = IL2CPP.il2cpp_object_get_class(belt);
        if (klass == _beltClass)
            return _beltBroken >= 0;
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        if (name != "TrackBeltBehaviour")
            return false;
        _beltClass = klass;
        _beltBroken = Offset(klass, "broken");
        _beltBreakRequested = Offset(klass, "breakRequested");
        _beltSegmentFlags = Offset(klass, "segmentFlags");
        if (_beltBroken < 0 || _beltBreakRequested < 0 || _beltSegmentFlags < 0)
        {
            Plugin.Logger.LogWarning("Repair: TrackBeltBehaviour fields not found, track belts are not re-fitted");
            _beltBroken = -1;
            return false;
        }
        return true;
    }

    private static int Offset(IntPtr klass, string field)
    {
        var f = IL2CPP.il2cpp_class_get_field_from_name(klass, field);
        return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f);
    }

    private static bool BeltBroken(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase controller)
    {
        if (controller == null)
            return false;
        var belt = controller.Pointer;
        if (belt == IntPtr.Zero || !ResolveBelt(belt))
            return false;
        return *(bool*)(belt + _beltBroken) || *(bool*)(belt + _beltBreakRequested);
    }

    /// <summary>帯の切れた状態を消して、車輪の周りに履き直す。</summary>
    private static void FixBelt(Sprocket.ContinuousTracks.ITrackBeltBehaviour controller)
    {
        if (controller == null)
            return;
        var belt = controller.Pointer;
        if (belt == IntPtr.Zero || !ResolveBelt(belt))
            return;
        *(bool*)(belt + _beltBroken) = false;
        *(bool*)(belt + _beltBreakRequested) = false;
        // NativeArray<SegmentFlags>（m_Buffer, m_Length）。リンクのつながりが切れた印を消す
        var flags = *(IntPtr*)(belt + _beltSegmentFlags);
        var length = *(int*)(belt + _beltSegmentFlags + 8);
        if (flags != IntPtr.Zero && length > 0 && length < 100000)
        {
            for (var i = 0; i < length; i++)
            {
                var f = (byte*)(flags + i);
                if ((*f & SegmentBroken) != 0)
                    *f = (byte)(*f & ~SegmentBroken);
            }
        }
        controller.ResetPositions(); // リンクを車輪の周りの元の位置に戻す
    }

    /// <summary>VehicleBehaviour.healthRegister.entries（部品と体力プール）をメモリから読む。</summary>
    private void ForEachEntry(VehicleBehaviour vehicle, Action<IntPtr, IntPtr> action)
    {
        var register = vehicle.healthRegister;
        if (register == null)
            return;
        if (_healthPoolClass == IntPtr.Zero)
            _healthPoolClass = Il2CppClassPointerStore<HealthPool>.NativeClassPtr;
        var list = *(IntPtr*)(register.Pointer + RegisterEntries);
        if (list == IntPtr.Zero)
            return;
        var items = *(IntPtr*)(list + ListItems);
        var size = *(int*)(list + ListSize);
        if (items == IntPtr.Zero || size <= 0 || size > MaxEntries)
            return;
        for (var i = 0; i < size; i++)
        {
            var element = items + ArrayData + i * EntryStride;
            var owner = *(IntPtr*)element;
            var pool = *(IntPtr*)(element + 8);
            if (owner == IntPtr.Zero || pool == IntPtr.Zero || IL2CPP.il2cpp_object_get_class(pool) != _healthPoolClass)
                continue;
            action(owner, pool);
        }
    }

    private string ClassName(IntPtr obj)
    {
        var klass = IL2CPP.il2cpp_object_get_class(obj);
        if (_classNames.TryGetValue(klass, out var name))
            return name;
        name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? string.Empty;
        _classNames[klass] = name;
        return name;
    }

    /// <summary>動ける乗員の数（InjectedOperator.Enabled は乗員の HealthFraction &gt; 0）。</summary>
    private static int CountCrew(VehicleBehaviour vehicle, VehicleState state, out int total)
    {
        total = 0;
        if (!state.CrewSearched || state.Crew != null && state.Crew.WasCollected)
        {
            state.CrewSearched = true;
            state.Crew = null;
            var modules = vehicle.modules;
            if (modules != null)
            {
                for (var i = 0; i < modules.Count; i++)
                {
                    var crew = modules[i]?.TryCast<VehicleCrewWorkspaceModule>();
                    if (crew != null)
                    {
                        state.Crew = crew;
                        break;
                    }
                }
            }
        }
        var operators = state.Crew?.operators;
        if (operators == null)
            return 0;
        var alive = 0;
        for (var i = 0; i < operators.Length; i++)
        {
            var op = operators[i];
            if (op == null || op.internalOperator == null)
                continue;
            total++;
            if (op.Enabled)
                alive++;
        }
        return alive;
    }

    // ------------------------------------------------------------------ 車両

    private void Search()
    {
        var now = Time.unscaledTime;
        var found = Object.FindObjectsByType(Il2CppType.Of<VehicleBehaviour>(), FindObjectsSortMode.None);
        if (found != null)
        {
            foreach (var obj in found)
            {
                var vehicle = obj?.TryCast<VehicleBehaviour>();
                if (vehicle == null)
                    continue;
                if (!_vehicles.TryGetValue(vehicle.Pointer, out var state))
                {
                    state = new VehicleState { Vehicle = vehicle, Name = KillFeed.KillTracker.GetName(vehicle) };
                    _vehicles[vehicle.Pointer] = state;
                }
                state.LastSeen = now;
            }
        }
        _stale.Clear();
        foreach (var pair in _vehicles)
        {
            if (now - pair.Value.LastSeen > SearchInterval * 2.5f || pair.Value.Vehicle == null || pair.Value.Vehicle.WasCollected)
                _stale.Add(pair.Key);
        }
        foreach (var ptr in _stale)
        {
            Release(_vehicles[ptr]); // 修理中に消えた車両の操縦の無効化も解く
            _vehicles.Remove(ptr);
        }
    }

    private VehicleBehaviour GetPlayerVehicle()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        var player = _controller?.ControlledVehicle;
        if (player == null)
            return null;
        var vehicle = player.TryCast<VehicleBehaviour>();
        if (vehicle == null)
        {
            var body = player.Rigidbody;
            vehicle = body != null ? body.GetComponent<VehicleBehaviour>() ?? body.GetComponentInParent<VehicleBehaviour>() : null;
        }
        if (vehicle != null && !_vehicles.ContainsKey(vehicle.Pointer))
            _vehicles[vehicle.Pointer] = new VehicleState { Vehicle = vehicle, Name = KillFeed.KillTracker.GetName(vehicle), LastSeen = Time.unscaledTime };
        return vehicle;
    }
}
