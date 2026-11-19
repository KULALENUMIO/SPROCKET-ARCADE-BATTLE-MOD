using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.WeaponFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.HitCam;

/// <summary>ヒットカメラの設定（[HitCam] セクション）。</summary>
public class HitCamConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> ShowNonPenetrating;
    public readonly ConfigEntry<bool> KillCam;
    public readonly ConfigEntry<float> Size;
    public readonly ConfigEntry<float> HitDuration;
    public readonly ConfigEntry<float> KillDuration;
    public readonly ConfigEntry<bool> AllShots;
    public readonly ConfigEntry<float> FittingOpacity;
    public readonly ConfigEntry<float> FittingBrightness;
    public readonly ConfigEntry<float> Exposure;

    public HitCamConfig(ConfigFile cfg)
    {
        const string s = "HitCam";
        Enabled = cfg.Bind(s, "Enabled", true,
            "War Thunder 風のヒットカメラ。自分の弾が当たると、画面右上に命中した瞬間の車両を透かして弾と破片の飛び方を見せる");
        ShowNonPenetrating = cfg.Bind(s, "ShowNonPenetrating", true, "貫通しなかった命中（非貫通・跳弾）も表示する");
        KillCam = cfg.Bind(s, "KillCam", true, "撃破したときは車両の周りを回りながら、撃破した弾をもう一度見せる");
        Size = cfg.Bind(s, "Size", 440f, new ConfigDescription("表示の幅（1080p 基準の px）", new AcceptableValueRange<float>(280f, 760f)));
        HitDuration = cfg.Bind(s, "HitDuration", 3.2f, new ConfigDescription("最後の命中から表示を続ける時間（秒）。表示中に同じ車両へ当たった弾は同じカメラで続けて見せる", new AcceptableValueRange<float>(1.5f, 10f)));
        KillDuration = cfg.Bind(s, "KillDuration", 6f, new ConfigDescription("撃破を表示する時間（秒）", new AcceptableValueRange<float>(3f, 15f)));
        AllShots = cfg.Bind(s, "AllShots", false, "開発用: 自分以外（AI どうし）の命中も表示する");
        FittingOpacity = cfg.Bind(s, "FittingOpacity", 0.22f, new ConfigDescription("透かした後の外側の装備品（履帯・転輪・フェンダー・小物など）の濃さ。車体の装甲より濃くして形を見せる", new AcceptableValueRange<float>(0f, 1f)));
        FittingBrightness = cfg.Bind(s, "FittingBrightness", 0.5f, new ConfigDescription("外側の装備品を明るい灰色に寄せる度合い", new AcceptableValueRange<float>(0f, 1f)));
        Exposure = cfg.Bind(s, "Exposure", 13f, new ConfigDescription("ヒットカメラの露出（EV100。大きいほど暗い）", new AcceptableValueRange<float>(6f, 18f)));
    }
}

/// <summary>命中の結果。</summary>
internal enum HitCamOutcome
{
    Penetration,
    NonPenetration,
    Ricochet,
}

/// <summary>命中した瞬間の車両の部品 1 つ（メッシュとその位置）。</summary>
internal sealed class CapturedPart
{
    public Mesh Mesh;
    public bool OwnsMesh; // 履帯など、その瞬間の形を焼き付けたメッシュ（使い終わったら破棄する）
    public Matrix4x4 Matrix; // 撮影フレーム（車両の位置を原点、向きは世界のまま）での配置
    public Material[] Materials;
    public VehiclePartType PartType;
    public int Module = -1; // CapturedModule の番号（体力のある部品の一部なら）
    public IntPtr Owner; // 板の構造（PlateStructure）なら、その構造
    public bool Fitting; // 外側の装備品（履帯・転輪・サスペンション・装備品・フェンダーなどの板）。透かしても形が分かるように濃く描く
}

/// <summary>体力のある部品（エンジン・弾薬庫・乗員の座席など）1 つ。</summary>
internal sealed unsafe class CapturedModule
{
    public VehicleComponent Component; // 参照を持って GC で消えないようにする
    public Il2CppSystem.Object PoolRef;
    public IntPtr Pool;
    public float Max;
    public float Before; // 命中前の体力の割合
    public string Class;
    public Bounds Bounds; // 撮影フレーム
    public bool HasBounds;
    public Vector3 Position; // 部品の位置（撮影フレーム）

    public bool IsCrewSeat => Class == "CrewSeat";

    /// <summary>今の体力の割合（HealthPool.current / max をメモリから直接読む）。</summary>
    public float Fraction()
    {
        if (Pool == IntPtr.Zero || !(Max > 0f) || PoolRef == null || PoolRef.WasCollected)
            return Before;
        return Mathf.Clamp01(*(float*)(Pool + 0x20) / Max);
    }

    // 命中の結果（確定したときに 1 回だけ決める。その後の火災・撃破後の減少・ほかの弾は混ぜない）
    public float Result = float.NaN; // 命中の直後の体力の割合
    public int Cause = -1; // 傷つけた破片（Fragments の番号）。-1 = 傷ついていない・分からない
    public float CauseAlong; // その破片の経路のどこで届いたか（0〜1）

    /// <summary>この命中で傷ついた。</summary>
    public bool Damaged => !float.IsNaN(Result) && (Result < Before - 0.005f || Before > 0f && Result <= 0f);

    // 開発用（hitcam audit）: 命中の次のフレームの体力と、その後に減った記録
    public float After = float.NaN;
    public float Last = float.NaN;
    public string Changes = "";

    public bool IsAmmo => Class.Contains("Ammo");
    public bool IsEngine => Class == "CombustionEngine";
    public bool IsFuel => Class.StartsWith("Fuel", StringComparison.Ordinal);
}

/// <summary>貫通計算の破片 1 つ（撮影フレームの座標）。</summary>
internal struct CapturedFragment
{
    public Vector3 Start;
    public Vector3 End;
    public int Parent;
    public float Diameter;
    public float Speed;
    public ushort Flags;

    public bool IsPenetrator => (Flags & 1) != 0;
    public bool Stopped => (Flags & (8 | 32)) != 0; // Killed / Embedded
}

/// <summary>命中 1 回の記録。</summary>
internal sealed class HitCapture
{
    public float Time;
    public IntPtr Body;
    public IntPtr Behaviour;
    public string VictimName;
    public string Ammo = ""; // 徹甲榴弾・榴弾なら AP 以外の名前（Ammo/AmmoSystem）
    public Transform Root;
    public Vector3 RootPosition;
    public Quaternion RootRotation;
    public readonly List<CapturedPart> Parts = new();
    public readonly List<CapturedFragment> Fragments = new();
    public readonly List<CapturedModule> Modules = new();
    public VehicleBehaviour BehaviourObject;
    public bool WasOnFire;
    public Bounds Bounds;
    public Vector3 Entry;
    public Vector3 Direction;
    public Vector3 Normal;
    public HitCamOutcome Outcome = HitCamOutcome.Penetration;
    public bool Confirmed;
    public bool Released;
    public bool Audited; // 開発用（hitcam audit）
    public int ShotId; // 自分の弾と分かったときの弾 ID
    public float ConfirmedAt; // 確定した時刻（Time.time）
    public bool FireStarted; // この命中から少しの間に火災が起きた（誘爆・火災の輪を出す）
    public string ViewNote = ""; // 開発用: ヒットカメラが表示したか、しなかった理由

    /// <summary>今の車両の世界座標の点を、撮影フレームの座標にする（車両はその後も動いているため）。</summary>
    public bool ToCaptureFrame(Vector3 world, out Vector3 point)
    {
        point = default;
        if (Root == null || Root.WasCollected)
            return false;
        var local = Quaternion.Inverse(Root.rotation) * (world - Root.position);
        point = RootRotation * local;
        return true;
    }

    /// <summary>
    /// メッシュを複製して持っておく。撃破（誘爆で砲塔が飛ぶなど）で車両のメッシュが作り直されると、元のメッシュは消えるため。
    /// 読めないメッシュ（乗員などの共有の素材）は消えないので複製しない。
    /// </summary>
    public void Preserve()
    {
        if (Preserved || Released)
            return;
        Preserved = true;
        foreach (var part in Parts)
        {
            if (part.OwnsMesh || part.Mesh == null || part.Mesh.WasCollected || !part.Mesh.isReadable)
                continue;
            var copy = Object.Instantiate(part.Mesh);
            copy.name = "HitCam " + part.Mesh.name;
            part.Mesh = copy;
            part.OwnsMesh = true;
        }
    }

    public bool Preserved;

    public void Release()
    {
        if (Released)
            return;
        Released = true;
        foreach (var part in Parts)
        {
            if (part.OwnsMesh && part.Mesh != null && !part.Mesh.WasCollected)
                Object.Destroy(part.Mesh);
        }
        Parts.Clear();
        Modules.Clear();
        BehaviourObject = null;
    }

    /// <summary>今燃えているか。</summary>
    public bool OnFire()
    {
        var b = BehaviourObject;
        return b != null && !b.WasCollected && (b.Flags & VehicleFlags.OnFire) != 0;
    }
}

/// <summary>
/// ヒットカメラの記録。
///
///  - 貫通計算: CompoundStructure.Penetrate が車両ごとに PenetrationSimulation を走らせ、Vehicle.OnDamageModelSimulationRan に渡す。
///    その後処理（Patches）で、破片の配列 PenetrationSimulation.fragments（Fragment 構造体、メモリから直接読む）を
///    SimToWorldSpace で世界座標にして写し取る。Fragment の木（parentIndex）が砲弾と破片の経路そのもの。
///  - 姿勢: 同じ瞬間に Vehicle.rendererRegister の描画部品（メッシュと位置）を写し取る。砲塔の向きもそのまま残る。
///  - 誰の弾か: 自車の砲が撃った弾 ID を覚え、ProjectileRegister.projectileHistory に命中（HitDamageModelProjectileEvent）が出たら、
///    同じ車両・同じ位置の記録を自分の命中として確定する。自分の弾が飛んでいる間しか写し取らない。
/// </summary>
internal sealed unsafe class HitCamTracker
{
    public static HitCamTracker Instance { get; private set; }

    // Fragment（メタデータで確認）
    private const int FragDiameter = 0x0;
    private const int FragSpeed = 0x8;
    private const int FragTravel = 0xC;
    private const int FragSpawn = 0x10;
    private const int FragDirection = 0x1C;
    private const int FragParent = 0x40;
    private const int FragFlags = 0x48;
    private const int ArrayLength = 0x18;
    private const int ArrayData = 0x20;
    private const int MaxFragments = 600;
    private const int MaxParts = 1500;
    private const float ProjectileTimeout = 12f;
    private const float MatchWindow = 1.5f;

    // XRayFilter of BattleOverlay: 外板として透かす部品
    internal const VehiclePartType XRayParts = (VehiclePartType)23425;
    // 体力の登録（HitTracker と同じ）
    private const int RegisterEntries = 0x10;
    private const int ListItems = 0x10;
    private const int ListSize = 0x18;
    private const int EntryStride = 0x10;
    private const int PoolCurrent = 0x20;
    private const int PoolMax = 0x24;
    private IntPtr _healthPoolClass;
    private readonly Dictionary<IntPtr, string> _classNames = new();

    private sealed class Shot
    {
        public int Id;
        public float Time;
        public IntPtr LastEvent;
        public string Events = ""; // 開発用（hitcam proxy）: 命中の記録の流れ
        public float RetiredAt;
    }

    private readonly List<Shot> _retired = new(); // 開発用: 飛び終えた弾（少し待ってから結果をログに書く）

    private readonly Dictionary<int, Shot> _shots = new();
    private readonly List<int> _finished = new();
    private readonly Dictionary<IntPtr, bool> _playerWeapons = new();
    private readonly List<HitCapture> _recent = new();
    private int _fragmentStride;
    private VehicleController _controller;
    private float _nextControllerSearch;
    private IntPtr _playerBody;
    private float _nextErrorLog;
    private bool _killsSubscribed;

    /// <summary>自分の命中が確定した（ヒットカメラで表示する）。</summary>
    public event Action<HitCapture> Hit;

    /// <summary>自分が撃破した（最後に貫通した命中の記録）。</summary>
    public event Action<HitCapture> Killed;

    public HitCamTracker() => Instance = this;

    private static HitCamConfig Cfg => Plugin.HitCam;

    // ------------------------------------------------------------------ 発射

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (!Cfg.Enabled.Value || weapon == null || projectileId <= 0 || !IsPlayerWeapon(weapon))
            return;
        if (_shots.Count > 512)
            _shots.Clear();
        _shots[projectileId] = new Shot { Id = projectileId, Time = Time.time };
    }

    private bool IsPlayerWeapon(WeaponBehaviour weapon)
    {
        if (ProxyMode)
        {
            if (_playerWeapons.TryGetValue(weapon.Pointer, out var proxy))
                return proxy;
            var owner = weapon.Transform?.GetComponentInParent<VehicleBehaviour>();
            proxy = owner != null && owner.Rigidbody != null && _proxies.Contains(owner.Rigidbody.Pointer);
            _playerWeapons[weapon.Pointer] = proxy;
            return proxy;
        }
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

    // ------------------------------------------------------------------ 貫通計算（Vehicle.OnDamageModelSimulationRan の後）

    public void OnSimulation(Vehicle vehicle, PenetrationSimulationRunEvent e)
    {
        if (!Cfg.Enabled.Value || vehicle == null || e == null)
            return;
        if (_shots.Count == 0 && !Cfg.AllShots.Value)
            return;
        var body = vehicle.Rigidbody;
        if (body == null || IsShooter(body.Pointer))
            return;
        var sim = e.Simulation?.TryCast<PenetrationSimulation>();
        if (sim == null)
            return;

        var capture = new HitCapture
        {
            Time = Time.time,
            Body = body.Pointer,
            Root = vehicle.transform,
            RootPosition = vehicle.transform.position,
            RootRotation = vehicle.transform.rotation,
        };
        VehicleBehaviour behaviour = null;
        try
        {
            behaviour = vehicle.behaviour;
            capture.Behaviour = behaviour != null ? behaviour.Pointer : IntPtr.Zero;
            capture.BehaviourObject = behaviour;
            capture.WasOnFire = capture.OnFire();
            capture.VictimName = behaviour != null ? KillFeed.KillTracker.GetName(behaviour) : "?";
        }
        catch (Exception)
        {
            capture.VictimName = "?";
        }

        ReadFragments(sim, capture);
        if (capture.Fragments.Count == 0)
            return;
        // 最初の破片（砲弾）は、この物理ステップの始めの砲弾の位置から、最初に当たった装甲の表面まで。その終わりが着弾点
        var first = capture.Fragments[0];
        capture.Entry = first.End;
        capture.Direction = (first.End - first.Start).sqrMagnitude > 1e-6f ? (first.End - first.Start).normalized : Vector3.forward;
        capture.Normal = -capture.Direction;
        Dictionary<IntPtr, int> modules;
        try
        {
            modules = CaptureModules(behaviour, capture);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Hit cam: module capture failed: {ex.Message}");
            modules = new Dictionary<IntPtr, int>();
            capture.Modules.Clear();
        }
        CaptureParts(vehicle, capture, modules);
        if (capture.Parts.Count == 0)
            return;
        var check = capture.Bounds;
        check.Expand(0.6f);
        if (!check.Contains(capture.Entry) && Time.unscaledTime >= _nextOutsideLog)
        {
            _nextOutsideLog = Time.unscaledTime + 2f;
            var f0 = capture.Fragments[0];
            Plugin.Logger.LogInfo($"Hit cam: entry outside {capture.VictimName}: frags={capture.Fragments.Count} f0 flags=0x{f0.Flags:X} parent={f0.Parent} " +
                                  $"start={f0.Start} end={f0.End} bounds={capture.Bounds.center}/{capture.Bounds.size} sim pen pos={PenetratorPosition(sim) - capture.RootPosition}");
        }

        Remember(capture);
        // 貫通計算の砲弾が、今飛んでいる自分の砲弾なら、次の Update で自分の命中として確定する
        // （命中の記録（projectileHistory）は最新の 1 件しか残らず、砲弾が車両を抜けて地面に当たると見落とすため）
        if (_shots.Count > 0)
        {
            var id = PlayerShell(capture, out var note, out var near);
            if (id > 0)
            {
                capture.ShotId = id;
                _pending.Add(capture);
            }
            if (note != null && _shots.TryGetValue(id > 0 ? id : near, out var shot))
                shot.Events += $" sim[{capture.VictimName} {Outcome(capture)} frags={capture.Fragments.Count} parts={capture.Parts.Count} {note}]";
        }
        else if (Cfg.AllShots.Value && Time.unscaledTime >= _nextDebugCapture)
        {
            // 開発用: 誰の弾でも表示する（ダメージが入った後の次の Update で確定する）
            _nextDebugCapture = Time.unscaledTime + 1f;
            _pending.Add(capture);
        }
    }

    private float _nextDebugCapture;

    private void Remember(HitCapture capture)
    {
        var now = Time.time;
        var confirmed = 0;
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var old = _recent[i];
            if (old.Confirmed)
                confirmed++;
            // 確定した記録は撃破の表示に使うので長めに残す（メッシュを複製しているので新しい 6 つまで）
            var keep = old.Confirmed ? 60f : MatchWindow * 2f;
            if (now - old.Time > keep || _recent.Count > 24 || old.Confirmed && confirmed > 6)
            {
                if (AuditLog && old.Confirmed)
                    WriteAudit(old, $" (early, {now - old.Time:0.0}s)");
                if (!HitCamView.IsShowing(old))
                    old.Release();
                _recent.RemoveAt(i);
            }
        }
        _recent.Add(capture);
    }

    /// <summary>
    /// 命中の結果。HitDamageModelProjectileEvent.Type は砲弾が車両の中で止まると Absorbed（非貫通と同じ）になるため、それだけでは決めない。
    /// 中の部品の体力が減った・砲弾が装甲を抜けた（破片の木の Penetrated）・命中の種類が Penetrated のどれかなら貫通。
    /// この時点（命中の次の Update）では、ダメージはもう入っている。
    /// </summary>
    private static HitCamOutcome Decide(HitCapture capture, HitDamageModelProjectileEvent.HitType type)
    {
        if ((type & HitDamageModelProjectileEvent.HitType.Penetrated) != 0)
            return HitCamOutcome.Penetration;
        foreach (var module in capture.Modules)
        {
            if (module.Damaged)
                return HitCamOutcome.Penetration;
        }
        var fragments = Outcome(capture);
        if (fragments == HitCamOutcome.Penetration)
            return HitCamOutcome.Penetration;
        return (type & HitDamageModelProjectileEvent.HitType.Deflected) != 0 || fragments == HitCamOutcome.Ricochet
            ? HitCamOutcome.Ricochet
            : HitCamOutcome.NonPenetration;
    }

    private static HitCamOutcome Outcome(HitCapture capture)
    {
        // 砲弾（OriginalPenetrator）の枝のどれかが装甲を抜けていれば貫通
        foreach (var f in capture.Fragments)
        {
            if (f.IsPenetrator && (f.Flags & 64) != 0)
                return HitCamOutcome.Penetration;
        }
        foreach (var f in capture.Fragments)
        {
            if (f.IsPenetrator && (f.Flags & 16) != 0)
                return HitCamOutcome.Ricochet;
        }
        return HitCamOutcome.NonPenetration;
    }

    private float _nextOutsideLog;
    private readonly List<HitCapture> _pending = new();
    private int _instanceStride;

    /// <summary>
    /// 貫通計算の砲弾（最初の破片の始点と向き）が、自分の砲弾の今の位置（ProjectileRegister.pool の ProjectileInstance、メモリから直接読む）の
    /// 飛ぶ線の上にあるか。貫通計算はその砲弾の物理ステップの中で走るので、位置はほぼ同じ。
    /// </summary>
    private int PlayerShell(HitCapture capture, out string note, out int near)
    {
        note = null;
        near = 0;
        var register = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>();
        var map = register?.activeIdMap;
        if (map == null)
        {
            note = "no activeIdMap";
            return 0;
        }
        if (_instanceStride == 0)
        {
            uint align = 0;
            var klass = Il2CppClassPointerStore<ProjectileInstance>.NativeClassPtr;
            var size = klass != IntPtr.Zero ? IL2CPP.il2cpp_class_value_size(klass, ref align) : 0;
            _instanceStride = size >= 0x4C && size <= 0x100 ? size : 0x50;
        }
        var pool = *(IntPtr*)(register.Pointer + 0x38);
        if (pool == IntPtr.Zero)
        {
            note = "no pool";
            return 0;
        }
        var length = *(int*)(pool + ArrayLength);
        var first = capture.Fragments[0];
        var start = first.Start + capture.RootPosition;
        var dir = capture.Direction;
        var nearest = float.PositiveInfinity;
        foreach (var id in _shots.Keys)
        {
            if (!map.TryGetValue(id, out var index) || index < 0 || index >= length)
                continue;
            var instance = pool + ArrayData + index * _instanceStride;
            if (*(int*)(instance + 0x18) != id)
                continue;
            var position = *(Vector3*)(instance + 0x28);
            var velocity = *(Vector3*)(instance + 0x34);
            var speed = velocity.magnitude;
            var d = start - position;
            var along = Vector3.Dot(d, dir);
            var perpendicular = (d - dir * along).magnitude;
            var dot = speed > 1f ? Vector3.Dot(velocity / speed, dir) : 0f;
            if (speed >= 1f && dot >= 0.97f && perpendicular < 1.5f && Mathf.Abs(along) < speed * 0.1f + 3f)
            {
                note = $"matched shell {id} perp={perpendicular:0.00} along={along:0.0} dot={dot:0.000}";
                return id;
            }
            var miss = (start - position).magnitude;
            if (miss < 30f && miss < nearest)
            {
                nearest = miss;
                near = id;
                note = $"near shell {id} but no match: dist={miss:0.0} perp={perpendicular:0.00} along={along:0.0} dot={dot:0.000} speed={speed:0}";
            }
        }
        return 0;
    }

    /// <summary>貫通計算の時点で自分の砲弾と分かった命中を、ダメージが入った後（次の Update）に確定する。</summary>
    private void ConfirmPending()
    {
        if (_pending.Count == 0)
            return;
        foreach (var capture in _pending)
        {
            if (capture.Confirmed || capture.Released)
                continue;
            capture.Confirmed = true;
            capture.Preserve();
            Settle(capture);
            capture.Outcome = Decide(capture, 0);
            Hit?.Invoke(capture);
            if (_shots.TryGetValue(capture.ShotId, out var shot))
                shot.Events += $" confirmed[{capture.VictimName} {capture.Outcome} view={capture.ViewNote}]";
        }
        _pending.Clear();
    }

    /// <summary>貫通計算の砲弾の位置（PenetrationSimulation.Penetrator.Position、ワールド座標）。</summary>
    private static Vector3 PenetratorPosition(PenetrationSimulation sim) => *(Vector3*)(sim.Pointer + 0x48 + 0x14);

    private void ReadFragments(PenetrationSimulation sim, HitCapture capture)
    {
        if (_fragmentStride == 0)
        {
            uint align = 0;
            var klass = Il2CppClassPointerStore<Fragment>.NativeClassPtr;
            var size = klass != IntPtr.Zero ? IL2CPP.il2cpp_class_value_size(klass, ref align) : 0;
            _fragmentStride = size >= 0x4A && size <= 0x100 ? size : 0x50;
            Plugin.Logger.LogInfo($"Hit cam: fragment size 0x{_fragmentStride:X}");
        }

        var simPtr = sim.Pointer;
        var array = *(IntPtr*)(simPtr + 0x80);
        var count = *(int*)(simPtr + 0x88);
        if (array == IntPtr.Zero || count <= 0)
            return;
        var length = *(int*)(array + ArrayLength);
        count = Math.Min(Math.Min(count, length), MaxFragments);
        var toWorld = sim.SimToWorldSpace;
        var origin = capture.RootPosition;
        for (var i = 0; i < count; i++)
        {
            var f = array + ArrayData + i * _fragmentStride;
            var spawn = *(Vector3*)(f + FragSpawn);
            var dir = *(Vector3*)(f + FragDirection);
            var travel = *(float*)(f + FragTravel);
            if (float.IsNaN(travel) || travel < 0f || travel > 200f)
                travel = 0f;
            var start = toWorld.MultiplyPoint3x4(spawn);
            var end = toWorld.MultiplyPoint3x4(spawn + dir * travel);
            capture.Fragments.Add(new CapturedFragment
            {
                Start = start - origin,
                End = end - origin,
                Parent = *(short*)(f + FragParent),
                Diameter = *(float*)(f + FragDiameter),
                Speed = *(float*)(f + FragSpeed),
                Flags = *(ushort*)(f + FragFlags),
            });
        }
    }

    /// <summary>
    /// 体力のある部品（VehicleBehaviour.healthRegister の各組）と、その部品の描画（VehicleComponent.models の各 LOD）を対応付ける。
    /// 貫通計算の直後で、まだダメージは入っていない（ApplyFragmentHits は Penetrate を呼んだ側が後で呼ぶ）ので、今の体力が命中前。
    /// </summary>
    private Dictionary<IntPtr, int> CaptureModules(VehicleBehaviour behaviour, HitCapture capture)
    {
        var map = new Dictionary<IntPtr, int>();
        var register = behaviour?.healthRegister;
        if (register == null)
            return map;
        if (_healthPoolClass == IntPtr.Zero)
            _healthPoolClass = Il2CppClassPointerStore<HealthPool>.NativeClassPtr;
        var list = *(IntPtr*)(register.Pointer + RegisterEntries);
        if (list == IntPtr.Zero)
            return map;
        var items = *(IntPtr*)(list + ListItems);
        var size = *(int*)(list + ListSize);
        if (items == IntPtr.Zero || size <= 0 || size > 4096)
            return map;
        for (var i = 0; i < size; i++)
        {
            var element = items + ArrayData + i * EntryStride;
            var owner = *(IntPtr*)element;
            var pool = *(IntPtr*)(element + 8);
            if (owner == IntPtr.Zero || pool == IntPtr.Zero || IL2CPP.il2cpp_object_get_class(pool) != _healthPoolClass)
                continue;
            var max = *(float*)(pool + PoolMax);
            if (!(max > 0f))
                continue;
            var component = new VehicleComponent(owner);
            var className = ClassName(owner);
            var models = component.models;
            // 乗員の座席・履帯は描画が無くても入れる（乗員の模型は後で近い座席に、履帯のリンクは履帯ごとに結び付ける）
            var bare = className == "CrewSeat" || className == "TrackAssembly";
            if ((models == null || models.Count == 0) && !bare)
                continue;
            var index = capture.Modules.Count;
            var added = bare;
            for (var g = 0; models != null && g < models.Count; g++)
            {
                var lods = models[g]?.lods;
                if (lods == null)
                    continue;
                for (var l = 0; l < lods.Length; l++)
                {
                    var renderer = lods[l]?.renderer;
                    if (renderer == null)
                        continue;
                    map[renderer.Pointer] = index;
                    added = true;
                }
            }
            if (!added)
                continue;
            capture.Modules.Add(new CapturedModule
            {
                Component = component,
                PoolRef = new Il2CppSystem.Object(pool),
                Pool = pool,
                Max = max,
                Before = Mathf.Clamp01(*(float*)(pool + PoolCurrent) / max),
                Class = className,
                Position = ComponentPosition(component) - capture.RootPosition,
            });
        }
        return map;
    }

    private static Vector3 ComponentPosition(VehicleComponent component)
    {
        try
        {
            var transform = component.transform;
            return transform != null ? transform.position : Vector3.zero;
        }
        catch (Exception)
        {
            return Vector3.zero;
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

    /// <summary>描画部品をその瞬間の位置で写し取る（LOD 0 のものだけ。隠れている内部の部品も含める）。</summary>
    private void CaptureParts(Vehicle vehicle, HitCapture capture, Dictionary<IntPtr, int> modules)
    {
        var register = vehicle.rendererRegister;
        var renderers = register?.renderers;
        if (renderers == null)
            return;
        var origin = capture.RootPosition;
        var hasBounds = false;
        var bounds = new Bounds();

        var combined = CombinedMeshes();
        _ownerMap = StructureOwners(vehicle);

        // 描画グループの LOD 1 以降と影用の代わりのメッシュは描かない（LOD 0 と同じ場所に重なって、ちらつく）
        _skip.Clear();
        var groups = register.groups;
        if (groups != null)
        {
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group == null)
                    continue;
                var lods = group.lods;
                if (lods != null)
                {
                    for (var l = 1; l < lods.Length; l++)
                    {
                        var r = lods[l]?.renderer;
                        if (r != null && (lods[0]?.renderer == null || lods[0].renderer.Pointer != r.Pointer))
                            _skip.Add(r.Pointer);
                    }
                }
                var shadow = group.shadow?.renderer;
                if (shadow != null)
                    _skip.Add(shadow.Pointer);
            }
        }

        // 弾薬庫: 遠くでは箱（低詳細の模型）で描かれ、中の砲弾 1 発ずつの模型は消されている。砲弾を写して、箱は写さない
        var racks = GetRacks(vehicle);
        foreach (var rack in racks)
        {
            if (rack == null || rack.WasCollected || ShellCount(rack) == 0)
                continue;
            var groups2 = ((VehicleComponent)rack).models;
            for (var g = 0; groups2 != null && g < groups2.Count; g++)
            {
                var lods = groups2[g]?.lods;
                for (var l = 0; lods != null && l < lods.Length; l++)
                {
                    var r = lods[l]?.renderer;
                    if (r != null)
                        _skip.Add(r.Pointer);
                }
            }
        }

        void Add(Renderer renderer, Mesh mesh, bool owns, Matrix4x4 matrix, VehiclePartType partType, int forcedModule = -1)
        {
            var materials = renderer.sharedMaterials;
            var copy = new Material[materials?.Length ?? 0];
            for (var m = 0; m < copy.Length; m++)
                copy[m] = materials[m];
            var module = forcedModule >= 0 ? forcedModule : modules.TryGetValue(renderer.Pointer, out var m2) ? m2 : -1;
            var owner = (partType & (VehiclePartType.PlateStructure | VehiclePartType.PlateStructureInterior)) != 0 && _ownerMap.TryGetValue(renderer.Pointer, out var structure) ? structure : IntPtr.Zero;
            var b = renderer.bounds;
            b.center -= origin;
            AddPart(mesh, owns, matrix, copy, partType, module, owner, b);
        }

        void AddPart(Mesh mesh, bool owns, Matrix4x4 matrix, Material[] materials, VehiclePartType partType, int module, IntPtr owner, Bounds b)
        {
            capture.Parts.Add(new CapturedPart { Mesh = mesh, OwnsMesh = owns, Matrix = matrix, Materials = materials, PartType = partType, Module = module, Owner = owner });
            if (module >= 0)
            {
                var cm = capture.Modules[module];
                if (cm.HasBounds)
                    cm.Bounds.Encapsulate(b);
                else
                {
                    cm.Bounds = b;
                    cm.HasBounds = true;
                }
            }
            if (hasBounds)
                bounds.Encapsulate(b);
            else
            {
                bounds = b;
                hasBounds = true;
            }
        }

        for (var i = 0; i < renderers.Count && capture.Parts.Count < MaxParts; i++)
        {
            var vr = renderers[i];
            if (vr == null)
                continue;
            var hide = (int)vr.HideFlags;
            // MarkedForDestroy = 消える途中、User = 意図的に隠した。
            // Baked（結合メッシュに置き換え済み）は写す: エンジン・変速機・燃料タンク・弾薬庫などは遠くでは結合メッシュで描かれ、
            // 元の部品に Baked が付くが、結合メッシュは低い LOD の分しか無い（LOD 0 の姿は元の部品にしか無い）
            if ((hide & (1 | 16)) != 0)
                continue;
            var lodMask = vr.lodMask;
            if (lodMask != 0 && (lodMask & 1) == 0)
                continue;
            var partType = vr.associatedPartType;
            if (partType == VehiclePartType.Decal)
                continue;
            var renderer = vr.renderer;
            if (renderer == null || renderer.WasCollected || !renderer.gameObject.activeInHierarchy || _skip.Contains(renderer.Pointer))
                continue;
            if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                continue;

            Mesh mesh;
            var owns = false;
            Matrix4x4 matrix;
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                if (skinned.sharedMesh == null)
                    continue;
                mesh = new Mesh();
                skinned.BakeMesh(mesh, true);
                owns = true;
                var t = skinned.transform;
                matrix = Matrix4x4.TRS(t.position - origin, t.rotation, Vector3.one);
            }
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;
                var world = renderer.localToWorldMatrix;
                matrix = Matrix4x4.Translate(-origin) * world;
            }

            // 結合メッシュそのもの（元の部品と重なる）は写さない
            if (!owns && combined.Contains(mesh.Pointer))
                continue;
            Add(renderer, mesh, owns, matrix, partType);
        }

        // 履帯: リンクごとの模型は描画の登録に入っていない（TrackBeltRenderer.models の MeshFilter）
        foreach (var belt in GetBelts(vehicle))
        {
            if (belt == null || belt.WasCollected)
                continue;
            // その履帯（TrackAssembly）の体力の部品
            var trackModule = -1;
            var assembly = belt.ParentAssembly;
            for (var m = 0; assembly != null && m < capture.Modules.Count; m++)
            {
                if (capture.Modules[m].Component?.Pointer == assembly.Pointer)
                {
                    trackModule = m;
                    break;
                }
            }
            var beltRenderer = belt.beltRenderer?.TryCast<Sprocket.ContinuousTracks.TrackBeltRenderer>();
            var links = beltRenderer?.models;
            if (links == null)
            {
                // 遠くの車両（と 0.2.56 からは多くの車両）の帯は GPU インスタンシングで描かれ、リンクごとの模型が無い
                AddInstancedLinks(belt, trackModule);
                continue;
            }
            for (var i = 0; i < links.Length && capture.Parts.Count < MaxParts; i++)
            {
                var filter = links[i];
                if (filter == null || filter.WasCollected || !filter.gameObject.activeInHierarchy)
                    continue;
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null || !renderer.enabled)
                    continue;
                Add(renderer, mesh, false, Matrix4x4.Translate(-origin) * filter.transform.localToWorldMatrix, VehiclePartType.TrackBelt, trackModule);
            }
        }
        foreach (var rack in racks)
        {
            if (rack == null || rack.WasCollected)
                continue;
            var shells = rack.models;
            var count = ShellCount(rack);
            for (var i = 0; i < count && capture.Parts.Count < MaxParts; i++)
            {
                var shell = shells[i];
                if (shell == null || shell.WasCollected)
                    continue;
                AddShellPart(shell.ProjectileMeshFilter, shell.ProjectileRenderer);
                AddShellPart(shell.PropellantMeshFilter, shell.PropellantRenderer);
            }
        }

        // GPU インスタンシングの帯（TrackBeltInstancedRenderer）: SegmentToMatrixJob と同じ計算でリンクごとの位置を作る。
        //   リンク s の行列 = TRS(parent.position, parent.rotation) × TRS((高さ, 0, 前後), Euler(0, 角度[rad→deg], -90), segmentModelInfo.localScale)
        //   高さ・前後・角度は帯のシミュレーション（TrackBeltBehaviour.segmentTransforms、NativeArray<SegmentTransform> 12 バイトずつ）。
        //   メッシュは segmentModelInfo.lods[s % 種類数][0]
        void AddInstancedLinks(Sprocket.Vehicles.Tracks.Belts.TrackBelt belt, int trackModule)
        {
            var inst = belt.beltRenderer?.TryCast<Sprocket.ContinuousTracks.Instanced.TrackBeltInstancedRenderer>();
            var parent = inst?.parent;
            var source = inst?.belt;
            if (inst == null || parent == null || source == null)
                return;
            var info = inst.segmentModelInfo;
            var lods = info?.lods;
            var variants = lods?.Length ?? 0;
            if (variants == 0)
                return;
            var behaviour = source.Pointer;
            if (behaviour == IntPtr.Zero || ClassName(behaviour) != "TrackBeltBehaviour")
                return;
            var buffer = *(IntPtr*)(behaviour + 0x108);
            var length = *(int*)(behaviour + 0x110);
            if (buffer == IntPtr.Zero || length <= 0 || length > 1000)
                return;
            var material = info.material;
            var materials = new Material[] { material };
            var scale = info.localScale;
            var beltToWorld = Matrix4x4.Translate(-origin) * Matrix4x4.TRS(parent.position, parent.rotation, Vector3.one);
            for (var s = 0; s < length && capture.Parts.Count < MaxParts; s++)
            {
                var t = (float*)(buffer + s * 12);
                var variant = lods[s % variants];
                var mesh = variant != null && variant.Length > 0 ? variant[0] : null;
                if (mesh == null)
                    continue;
                var matrix = beltToWorld * Matrix4x4.TRS(new Vector3(t[0], 0f, t[1]), Quaternion.Euler(0f, t[2] * Mathf.Rad2Deg, -90f), scale);
                var mb = mesh.bounds;
                var b = new Bounds(matrix.MultiplyPoint3x4(mb.center), Vector3.zero);
                for (var c = 0; c < 8; c++)
                    b.Encapsulate(matrix.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))));
                AddPart(mesh, false, matrix, materials, VehiclePartType.TrackBelt, trackModule, IntPtr.Zero, b);
            }
        }

        void AddShellPart(MeshFilter filter, MeshRenderer renderer)
        {
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || renderer == null)
                return;
            Add(renderer, mesh, false, Matrix4x4.Translate(-origin) * filter.transform.localToWorldMatrix, VehiclePartType.Ammunition);
        }

        capture.Bounds = bounds;
        LinkCrew(capture);
        MarkFittings(capture);
    }

    private Dictionary<IntPtr, IntPtr> _ownerMap = new();
    private readonly Dictionary<IntPtr, (float Time, Dictionary<IntPtr, IntPtr> Map)> _structureMaps = new();

    /// <summary>
    /// 描画部品 → それが属する板の構造（PlateStructure）。車両の Transform の階層は平らにされているので親をたどれない。
    /// 車両の部品の登録（Vehicle.objects の各 VehicleObject.behaviours）から PlateStructure を集め、その描画（高詳細・低詳細・影・models）から引く。
    /// 車両ごとに 5 秒持つ。
    /// </summary>
    private Dictionary<IntPtr, IntPtr> StructureOwners(Vehicle vehicle)
    {
        var now = Time.unscaledTime;
        if (_structureMaps.TryGetValue(vehicle.Pointer, out var cached) && now - cached.Time < 5f)
            return cached.Map;
        if (_structureMaps.Count > 64)
            _structureMaps.Clear();
        var map = new Dictionary<IntPtr, IntPtr>();
        try
        {
            var objects = vehicle.objects?.register;
            for (var i = 0; objects != null && i < objects.Count; i++)
            {
                var behaviours = objects[i]?.behaviours;
                for (var b = 0; behaviours != null && b < behaviours.Length; b++)
                {
                    var structure = behaviours[b]?.TryCast<Sprocket.Vehicles.PlateStructures.PlateStructure>();
                    if (structure == null)
                        continue;
                    var owner = structure.Pointer;
                    void Put(Renderer r)
                    {
                        if (r != null)
                            map[r.Pointer] = owner;
                    }
                    Put(structure.highDetailRenderer?.renderer);
                    Put(structure.lowDetailRenderer?.renderer);
                    Put(structure.shadowProxyRenderer?.renderer);
                    var models = ((VehicleComponent)structure).models;
                    for (var g = 0; models != null && g < models.Count; g++)
                    {
                        var lods = models[g]?.lods;
                        for (var l = 0; lods != null && l < lods.Length; l++)
                            Put(lods[l]?.renderer);
                        Put(models[g]?.shadow?.renderer);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Hit cam: structure map failed: {e.Message}");
        }
        _structureMaps[vehicle.Pointer] = (now, map);
        return map;
    }

    /// <summary>
    /// 外側の装備品に印を付ける。履帯・転輪・サスペンション・装備品・収納・増加装甲は部品の種類で決まる。
    /// 板の構造は、車体・砲塔もフェンダーなども同じ PlateStructure なので形で分ける: 箱が中の部品（乗員の座席・エンジン・砲など）の箱と
    /// 1 つも重ならない構造（フェンダー・サイドスカート・追加の板など）を装備品とする。砲の防盾のように砲と重なる板や、
    /// 撃たれた板（着弾点を含む）は装甲として透かす。
    /// </summary>
    private static void MarkFittings(HitCapture capture)
    {
        const VehiclePartType fittingTypes = VehiclePartType.TrackBelt | VehiclePartType.Wheel | VehiclePartType.Suspension
                                             | VehiclePartType.Equipment | VehiclePartType.Stowage | VehiclePartType.AppliqueArmour;
        var boxes = new Dictionary<IntPtr, Bounds>();
        foreach (var part in capture.Parts)
        {
            if (part.Owner == IntPtr.Zero || part.Mesh == null)
                continue;
            var box = WorldBox(part);
            if (boxes.TryGetValue(part.Owner, out var all))
            {
                all.Encapsulate(box);
                boxes[part.Owner] = all;
            }
            else
                boxes[part.Owner] = box;
        }
        var compartments = new HashSet<IntPtr>();
        foreach (var pair in boxes)
        {
            var box = pair.Value;
            box.Expand(0.05f);
            if (box.Contains(capture.Entry))
            {
                compartments.Add(pair.Key);
                continue;
            }
            foreach (var module in capture.Modules)
            {
                var inside = module.Position != Vector3.zero && box.Contains(module.Position);
                // 部品の箱の 3 割以上がこの構造の箱の中（砲の防盾など）。少し触れるだけ（エンジンの横のフェンダーなど）は数えない
                if (!inside && module.HasBounds)
                    inside = Overlap(box, module.Bounds) >= 0.3f;
                if (inside)
                {
                    compartments.Add(pair.Key);
                    break;
                }
            }
        }
        foreach (var part in capture.Parts)
            part.Fitting = (part.PartType & fittingTypes) != 0 || part.Owner != IntPtr.Zero && !compartments.Contains(part.Owner);
    }

    /// <summary>箱 b のうち箱 a の中にある体積の割合。</summary>
    private static float Overlap(Bounds a, Bounds b)
    {
        var min = Vector3.Max(a.min, b.min);
        var max = Vector3.Min(a.max, b.max);
        var size = max - min;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
            return 0f;
        var volume = b.size.x * b.size.y * b.size.z;
        return volume > 1e-6f ? size.x * size.y * size.z / volume : 0f;
    }

    /// <summary>部品のメッシュの境界を撮影フレームの箱にする。</summary>
    internal static Bounds WorldBox(CapturedPart part)
    {
        var b = part.Mesh.bounds;
        var m = part.Matrix;
        var box = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
        for (var i = 0; i < 8; i++)
        {
            var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            box.Encapsulate(m.MultiplyPoint3x4(corner));
        }
        return box;
    }

    private readonly Dictionary<IntPtr, Sprocket.Vehicles.AmmunitionStorage.AmmoRackModel[]> _racks = new();

    /// <summary>弾薬庫の、今入っている（表示する）砲弾の数。</summary>
    private static int ShellCount(Sprocket.Vehicles.AmmunitionStorage.AmmoRackModel rack)
    {
        var shells = rack.models;
        return shells == null ? 0 : Math.Min(shells.Count, (int)rack.displayCap);
    }

    /// <summary>車両の弾薬庫の模型（車両ごとに覚えておく）。</summary>
    private Sprocket.Vehicles.AmmunitionStorage.AmmoRackModel[] GetRacks(Vehicle vehicle)
    {
        if (_racks.TryGetValue(vehicle.Pointer, out var racks) && (racks.Length == 0 || racks[0] != null && !racks[0].WasCollected))
            return racks;
        if (_racks.Count > 64)
            _racks.Clear();
        var found = vehicle.GetComponentsInChildren<Sprocket.Vehicles.AmmunitionStorage.AmmoRackModel>(true);
        racks = new Sprocket.Vehicles.AmmunitionStorage.AmmoRackModel[found?.Length ?? 0];
        for (var i = 0; i < racks.Length; i++)
            racks[i] = found[i];
        _racks[vehicle.Pointer] = racks;
        return racks;
    }

    /// <summary>
    /// どの部品の描画にも結び付かなかった中の部品の模型を、位置がいちばん近い体力のある部品に結び付ける。
    /// 乗員の模型は乗員の座席（体力 = その乗員）へ。エンジンなどは描画がまとめられて（VehicleRendererAtlaser）元の部品から外れていることがある。
    /// </summary>
    private static void LinkCrew(HitCapture capture)
    {
        const VehiclePartType interior = VehiclePartType.Crew | VehiclePartType.Powertrain | VehiclePartType.Ammunition | VehiclePartType.Fuel
                                         | VehiclePartType.Weapon | VehiclePartType.Motor;
        foreach (var part in capture.Parts)
        {
            if (part.Module >= 0 || (part.PartType & interior) == 0 || part.Mesh == null)
                continue;
            var crew = part.PartType == VehiclePartType.Crew;
            var centre = part.Matrix.MultiplyPoint3x4(part.Mesh.bounds.center);
            var size = Vector3.Scale(part.Mesh.bounds.size, part.Matrix.lossyScale).magnitude;
            var best = -1;
            var bestDistance = crew ? 1.5f : 1.2f + size * 0.5f;
            for (var i = 0; i < capture.Modules.Count; i++)
            {
                var module = capture.Modules[i];
                if (module.IsCrewSeat != crew)
                    continue;
                var distance = (module.Position - centre).magnitude;
                if (module.HasBounds)
                {
                    var box = module.Bounds;
                    box.Expand(0.1f);
                    if (box.Contains(centre))
                        distance = 0f;
                    else
                        distance = Mathf.Min(distance, (box.ClosestPoint(centre) - centre).magnitude + 0.2f);
                }
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            if (best < 0)
                continue;
            part.Module = best;
            var cm = capture.Modules[best];
            var partBox = new Bounds(centre, Vector3.Scale(part.Mesh.bounds.size, part.Matrix.lossyScale));
            if (cm.HasBounds)
                cm.Bounds.Encapsulate(partBox);
            else
            {
                cm.Bounds = partBox;
                cm.HasBounds = true;
            }
        }
    }

    private readonly HashSet<IntPtr> _skip = new();
    private readonly HashSet<IntPtr> _combined = new();
    private float _nextCombinedRefresh;

    /// <summary>VehicleRendererAtlaser が部品をまとめて作った結合メッシュ（1 秒ごとに読み直す）。</summary>
    private HashSet<IntPtr> CombinedMeshes()
    {
        if (Time.unscaledTime < _nextCombinedRefresh)
            return _combined;
        _nextCombinedRefresh = Time.unscaledTime + 1f;
        _combined.Clear();
        try
        {
            var meshes = VehicleRendererAtlaser.active?.combinedMeshes;
            for (var i = 0; meshes != null && i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                if (mesh != null)
                    _combined.Add(mesh.Pointer);
            }
        }
        catch (Exception)
        {
            // 結合が無い場面
        }
        return _combined;
    }
    private readonly Dictionary<IntPtr, Sprocket.Vehicles.Tracks.Belts.TrackBelt[]> _belts = new();

    /// <summary>車両の履帯（車両ごとに覚えておく）。</summary>
    private Sprocket.Vehicles.Tracks.Belts.TrackBelt[] GetBelts(Vehicle vehicle)
    {
        if (_belts.TryGetValue(vehicle.Pointer, out var belts) && belts.Length > 0 && belts[0] != null && !belts[0].WasCollected)
            return belts;
        if (_belts.Count > 64)
            _belts.Clear();
        // Transform の階層は平らにされているので、部品の登録（履帯 TrackAssembly の Belt）から集める
        var list = new List<Sprocket.Vehicles.Tracks.Belts.TrackBelt>();
        foreach (var track in Tracks.TrackDamage.Assemblies(vehicle))
        {
            var belt = track?.Belt;
            if (belt != null)
                list.Add(belt);
        }
        if (list.Count == 0)
        {
            var found = vehicle.GetComponentsInChildren<Sprocket.Vehicles.Tracks.Belts.TrackBelt>(true);
            for (var i = 0; found != null && i < found.Length; i++)
                list.Add(found[i]);
        }
        belts = list.ToArray();
        _belts[vehicle.Pointer] = belts;
        return belts;
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
            ProcessShots();
            ConfirmPending();
            if (AuditLog)
                Audit();
            if (ProxyMode)
                UpdateProxy();
            WatchFires();
            ReportShots();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Hit cam failed: {e}");
            }
        }
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
                shot.Events += " timeout";
                _finished.Add(pair.Key);
                continue;
            }
            if (!history.TryGetValue(pair.Key, out var latest) || latest == null || latest.Pointer == shot.LastEvent)
                continue;
            shot.LastEvent = latest.Pointer;
            var hit = latest.TryCast<HitDamageModelProjectileEvent>();
            {
                var victim = "-";
                try
                {
                    victim = hit?.DamageModel?.RigidBody?.name ?? "-";
                }
                catch (Exception)
                {
                }
                shot.Events += hit != null
                    ? $" event[{hit.Type} on {victim}{(latest.Terminal ? " terminal" : "")}]"
                    : $" event[{latest.GetIl2CppType().Name}{(latest.Terminal ? " terminal" : "")}]";
            }
            if (hit != null)
            {
                shot.Events += $" -> {OnPlayerHit(hit, now)}";
            }
            if (latest.Terminal)
                _finished.Add(pair.Key);
        }
        foreach (var id in _finished)
        {
            if (_shots.TryGetValue(id, out var done))
            {
                done.RetiredAt = now;
                _retired.Add(done);
            }
            _shots.Remove(id);
        }
    }

    private string OnPlayerHit(HitDamageModelProjectileEvent hit, float now)
    {
        var body = hit.DamageModel?.RigidBody;
        if (body == null)
            return "no body";
        HitCapture best = null;
        var bestDistance = float.MaxValue;
        foreach (var capture in _recent)
        {
            if (capture.Confirmed || capture.Body != body.Pointer || now - capture.Time > MatchWindow)
                continue;
            var entry = capture.Entry + capture.RootPosition;
            var distance = (DistanceToRay(hit.Position, entry, capture.Direction));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = capture;
            }
        }
        if (best == null || bestDistance > 4f)
        {
            var already = 0;
            foreach (var capture in _recent)
                already += capture.Confirmed && capture.Body == body.Pointer && now - capture.Time <= MatchWindow ? 1 : 0;
            return best == null ? $"no capture to match (already confirmed {already})" : $"capture too far {bestDistance:0.0} m";
        }

        best.Confirmed = true;
        best.Preserve();
        Settle(best);
        if (hit.Normal.sqrMagnitude > 1e-6f)
            best.Normal = hit.Normal.normalized;
        best.Outcome = Decide(best, hit.Type);
        Hit?.Invoke(best);
        return $"confirmed {best.Outcome} view={best.ViewNote}";
    }

    private static float DistanceToRay(Vector3 point, Vector3 origin, Vector3 direction)
    {
        var d = point - origin;
        var along = Vector3.Dot(d, direction);
        return (d - direction * along).magnitude + Mathf.Max(0f, -along) * 0.1f;
    }

    // ------------------------------------------------------------------ 撃破

    private void OnKill(KillFeed.KillEvent e)
    {
        if (e.VictimPointer != IntPtr.Zero)
            _killedAt[e.VictimPointer] = Time.time;
        if (!Cfg.Enabled.Value || !Cfg.KillCam.Value || e.Kind != KillFeed.KillKind.Destroyed || e.VictimPointer == IntPtr.Zero)
            return;
        if (e.AttackerSide != KillFeed.Side.Player && !Cfg.AllShots.Value && !ProxyMode)
            return;
        // 最後に自分が貫通させた命中（無ければ最後の命中）
        HitCapture best = null;
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var capture = _recent[i];
            if (!capture.Confirmed || capture.Released || capture.Behaviour != e.VictimPointer)
                continue;
            if (best == null || capture.Outcome == HitCamOutcome.Penetration && best.Outcome != HitCamOutcome.Penetration)
                best = capture;
            if (best.Outcome == HitCamOutcome.Penetration)
                break;
        }
        if (best != null)
            Killed?.Invoke(best);
    }

    public bool IsTracked(HitCapture capture) => _recent.Contains(capture);

    // ------------------------------------------------------------------ 徹甲榴弾・榴弾の炸裂（Ammo/AmmoSystem から）

    /// <summary>この物理ステップに写し取った、その車両の命中（炸裂はその命中の貫通計算の直後に起きる）。</summary>
    private HitCapture CurrentCapture(IntPtr body)
    {
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var capture = _recent[i];
            if (capture.Body == body && !capture.Released && Time.time - capture.Time < 0.05f)
                return capture;
        }
        return null;
    }

    /// <summary>
    /// 炸裂の破片（自分で走らせた貫通計算）を、その命中の破片の木に足す。根の破片は、弾の経路のうち炸裂点に最も近い破片の子にする
    /// （その破片が炸裂点に着いた時刻から飛び出す）。弾ではないので OriginalPenetrator の印は外す。
    /// </summary>
    public void AppendBurst(IntPtr body, PenetrationSimulation sim)
    {
        var capture = CurrentCapture(body);
        if (capture == null || sim == null || capture.Fragments.Count > 1400)
            return;
        var start = capture.Fragments.Count;
        ReadFragments(sim, capture);
        if (capture.Fragments.Count == start)
            return;
        var origin = capture.Fragments[start].Start;
        var parent = 0;
        var best = float.PositiveInfinity;
        for (var i = 0; i < start; i++)
        {
            var f = capture.Fragments[i];
            if (!f.IsPenetrator)
                continue;
            var d = f.End - f.Start;
            var t = d.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(origin - f.Start, d) / d.sqrMagnitude) : 0f;
            var miss = (f.Start + d * t - origin).sqrMagnitude;
            if (miss < best)
            {
                best = miss;
                parent = i;
            }
        }
        for (var i = start; i < capture.Fragments.Count; i++)
        {
            var f = capture.Fragments[i];
            var local = f.Parent;
            f.Parent = local < 0 || local >= i - start ? (short)parent : (short)(local + start);
            f.Flags = (ushort)(f.Flags & ~1);
            capture.Fragments[i] = f;
        }
    }

    /// <summary>その命中に砲弾の種類の名前を付ける（ヒットカメラの題に出す）。</summary>
    public void TagAmmo(IntPtr body, string ammo)
    {
        var capture = CurrentCapture(body);
        if (capture != null)
            capture.Ammo = ammo;
    }

    // ------------------------------------------------------------------ 開発用: hitcam proxy

    /// <summary>
    /// 開発用: AI の陣営 1 つを自分とみなし、その弾を自分の弾と同じ流れ（発射 → 貫通計算 → 命中の記録 → 表示）で追い、
    /// 弾ごとの結果をログに書く。ProxyTeam がその陣営の生きている車両を返す（2 秒ごとに読み直す）。
    /// </summary>
    public static bool ProxyMode;

    public static Func<List<IntPtr>> ProxyTeam;
    private readonly HashSet<IntPtr> _proxies = new();
    private float _nextProxyRefresh;

    private bool IsShooter(IntPtr body) => ProxyMode ? _proxies.Contains(body) : body == GetPlayerBody();

    private void UpdateProxy()
    {
        var now = Time.time;
        if (now >= _nextProxyRefresh)
        {
            _nextProxyRefresh = now + 2f;
            var count = _proxies.Count;
            _proxies.Clear();
            foreach (var body in ProxyTeam?.Invoke() ?? new List<IntPtr>())
                _proxies.Add(body);
            if (_proxies.Count != count)
            {
                _playerWeapons.Clear();
                Plugin.Logger.LogInfo($"proxy: {_proxies.Count} shooters");
            }
        }
    }

    /// <summary>
    /// 飛び終えた自分の弾のうち、車両に当たったのにヒットカメラが出なかったものをログに書く（どこで落ちたかの記録付き）。
    /// hitcam proxy の間は全部書く。
    /// </summary>
    private void ReportShots()
    {
        var now = Time.time;
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            var shot = _retired[i];
            if (now - shot.RetiredAt < 1f)
                continue;
            _retired.RemoveAt(i);
            var events = shot.Events;
            var touched = events.Contains("sim[") || events.Contains("Hit") && !events.Contains("HitGround");
            var shown = events.Contains("view=shown") || events.Contains("view=added");
            if (ProxyMode)
                Plugin.Logger.LogInfo($"proxy shot {shot.Id} ({now - shot.Time:0.0}s):{(events.Length > 0 ? events : " nothing")}");
            else if (touched && !shown)
                Plugin.Logger.LogInfo($"Hit cam: no camera for shot {shot.Id}:{events}");
        }
    }

    // ------------------------------------------------------------------ 命中の結果

    /// <summary>
    /// 確定した命中の結果を決める（ダメージはもう入っている）。部品ごとに命中の直後の体力を読み、傷ついた部品は、
    /// その部品の箱を最初に通る破片（無ければいちばん近くを通る破片）を「傷つけた破片」にする。表示はその破片が届いた瞬間に色を変える。
    /// </summary>
    private static void Settle(HitCapture capture)
    {
        capture.ConfirmedAt = Time.time;
        foreach (var module in capture.Modules)
        {
            module.Result = module.Fraction();
            module.Cause = -1;
            if (module.Damaged && module.HasBounds)
                FindCause(capture, module);
        }
    }

    private static void FindCause(HitCapture capture, CapturedModule module)
    {
        // 履帯: 帯全体の箱は大きすぎる（輪の内側も含む）ので、リンクごとの箱で調べる
        if (module.Class == "TrackAssembly" && FindTrackCause(capture, module))
            return;
        var box = module.Bounds;
        box.Expand(0.12f);
        var fragments = capture.Fragments;
        // 箱を通る破片のうち、木の中で早く生まれたもの（番号が小さい = 早い）
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            var d = f.End - f.Start;
            var length = d.magnitude;
            if (length < 1e-4f)
                continue;
            if (box.Contains(f.Start))
            {
                module.Cause = i;
                module.CauseAlong = 0f;
                return;
            }
            if (box.IntersectRay(new Ray(f.Start, d / length), out var distance) && distance <= length)
            {
                module.Cause = i;
                module.CauseAlong = distance / length;
                return;
            }
        }
        // どれも通らない（部品の箱が大まかで、かすめただけ）: いちばん近くを通る破片
        var best = float.PositiveInfinity;
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            for (var k = 0; k <= 16; k++)
            {
                var point = Vector3.Lerp(f.Start, f.End, k / 16f);
                var miss = (box.ClosestPoint(point) - point).sqrMagnitude;
                if (miss < best)
                {
                    best = miss;
                    module.Cause = i;
                    module.CauseAlong = k / 16f;
                }
            }
        }
    }

    private static bool FindTrackCause(HitCapture capture, CapturedModule module)
    {
        var index = capture.Modules.IndexOf(module);
        var boxes = new List<Bounds>();
        foreach (var part in capture.Parts)
        {
            if (part.Module == index && part.Mesh != null)
            {
                var box = WorldBox(part);
                box.Expand(0.05f);
                boxes.Add(box);
            }
        }
        if (boxes.Count == 0)
            return false;
        var fragments = capture.Fragments;
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            var d = f.End - f.Start;
            var length = d.magnitude;
            if (length < 1e-4f)
                continue;
            var ray = new Ray(f.Start, d / length);
            var best = float.PositiveInfinity;
            foreach (var box in boxes)
            {
                if (box.Contains(f.Start))
                    best = 0f;
                else if (box.IntersectRay(ray, out var distance) && distance <= length)
                    best = Mathf.Min(best, distance);
            }
            if (float.IsPositiveInfinity(best))
                continue;
            module.Cause = i;
            module.CauseAlong = best / length;
            return true;
        }
        return false;
    }

    /// <summary>確定してから 1.5 秒の間に燃え始めた車両は、その命中で火災が起きたとする。</summary>
    private void WatchFires()
    {
        var now = Time.time;
        foreach (var capture in _recent)
        {
            if (!capture.Confirmed || capture.Released || capture.WasOnFire || capture.FireStarted || now - capture.ConfirmedAt > 1.5f)
                continue;
            if (capture.OnFire())
                capture.FireStarted = true;
        }
    }

    // ------------------------------------------------------------------ 開発用: hitcam audit

    /// <summary>開発用: 命中ごとに、体力が減った部品が弾・破片の経路の上にあったか、いつ減ったかをログに書く。</summary>
    public static bool AuditLog;

    private readonly Dictionary<IntPtr, float> _killedAt = new();

    private void Audit()
    {
        var now = Time.time;
        foreach (var capture in _recent)
        {
            if (capture.Released || capture.Audited)
                continue;
            var age = now - capture.Time;
            if (age <= 0f)
                continue;
            foreach (var m in capture.Modules)
            {
                var v = m.Fraction();
                if (float.IsNaN(m.After))
                {
                    m.After = v;
                    m.Last = v;
                    continue;
                }
                if (v < m.Last - 0.005f)
                {
                    m.Changes += $" {v:P0}@{age:0.0}s";
                    m.Last = v;
                }
            }
            if (age >= 8f)
                WriteAudit(capture, "");
        }
    }

    private void WriteAudit(HitCapture capture, string note)
    {
        if (capture.Audited || capture.Released)
            return;
        capture.Audited = true;
        var killed = _killedAt.TryGetValue(capture.Behaviour, out var k) && k >= capture.Time - 0.1f ? $" killed@{k - capture.Time:0.0}s" : "";
        var lines = new List<string> { $"audit {capture.VictimName} outcome={capture.Outcome} frags={capture.Fragments.Count} fire={capture.WasOnFire}->{capture.OnFire()}{killed}{note}" };
        var preDead = new List<string>();
        foreach (var m in capture.Modules)
        {
            if (m.Before <= 0f)
            {
                preDead.Add(m.Class);
                continue;
            }
            var now = m.Fraction();
            var after = float.IsNaN(m.After) ? now : m.After;
            if (!(now < m.Before - 0.005f || now <= 0f))
                continue;
            var parts = 0;
            foreach (var p in capture.Parts)
                parts += p.Module >= 0 && capture.Modules[p.Module] == m ? 1 : 0;
            var path = "nobox";
            var nearest = float.PositiveInfinity;
            if (m.HasBounds)
            {
                var box = m.Bounds;
                box.Expand(0.12f);
                var shell = false;
                var spall = false;
                foreach (var f in capture.Fragments)
                {
                    var d = f.End - f.Start;
                    var length = d.magnitude;
                    if (length < 1e-4f)
                        continue;
                    if (box.Contains(f.Start) || box.IntersectRay(new Ray(f.Start, d / length), out var distance) && distance <= length)
                    {
                        if (f.IsPenetrator)
                            shell = true;
                        else
                            spall = true;
                    }
                    for (var s = 0; s <= 10; s++)
                    {
                        var point = f.Start + d * (s / 10f);
                        nearest = Mathf.Min(nearest, (box.ClosestPoint(point) - point).magnitude);
                    }
                }
                path = shell ? "shell" : spall ? "spall" : "none";
            }
            var immediate = after < m.Before - 0.005f || after <= 0f;
            var tag = !immediate ? "LATER" : path == "none" ? "OFFPATH" : path == "nobox" ? "NOBOX" : "PATH";
            lines.Add($"  {tag} {m.Class} {m.Before:P0} -> {after:P0} -> {now:P0}{m.Changes} path={path} nearest={nearest:0.00} parts={parts} " +
                                  $"bounds={(m.HasBounds ? m.Bounds.size.ToString() : "-")}");
        }
        if (lines.Count == 1)
            return;
        if (preDead.Count > 0)
            lines.Add($"  dead before the hit: {string.Join(", ", preDead)}");
        foreach (var line in lines)
            Plugin.Logger.LogInfo(line);
    }

    /// <summary>開発用: 最後に写し取った車両の描画部品を全部書き出す（写し取らなかったものは理由も）。</summary>
    public IEnumerable<string> DumpRenderers()
    {
        var lines = new List<string>();
        var capture = _recent.Count > 0 ? _recent[^1] : null;
        var vehicle = capture?.Root != null && !capture.Root.WasCollected ? capture.Root.GetComponent<Vehicle>() : null;
        if (vehicle == null)
        {
            lines.Add("no vehicle");
            return lines;
        }
        lines.Add($"vehicle {capture.VictimName}");
        var listed = new HashSet<IntPtr>();
        var renderers = vehicle.rendererRegister?.renderers;
        for (var i = 0; renderers != null && i < renderers.Count; i++)
        {
            var vr = renderers[i];
            var r = vr?.renderer;
            if (r == null)
                continue;
            listed.Add(r.Pointer);
            var mesh = r.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? r.GetComponent<MeshFilter>()?.sharedMesh;
            lines.Add($"  reg {vr.associatedPartType} hide={(int)vr.HideFlags} lod={vr.lodMask} active={r.gameObject.activeInHierarchy} enabled={r.enabled} " +
                      $"shadow={r.shadowCastingMode} skip={_skip.Contains(r.Pointer)} combined={(mesh != null && CombinedMeshes().Contains(mesh.Pointer))} {r.GetIl2CppType().Name} mesh={mesh?.name} v={mesh?.vertexCount} go={r.gameObject.name}");
        }
        var all = vehicle.GetComponentsInChildren<Renderer>(true);
        var others = 0;
        var groups = new Dictionary<string, int>();
        for (var i = 0; all != null && i < all.Length; i++)
        {
            var r = all[i];
            if (r == null || listed.Contains(r.Pointer))
                continue;
            others++;
            var mesh = r.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? r.GetComponent<MeshFilter>()?.sharedMesh;
            var key = $"active={r.gameObject.activeInHierarchy} enabled={r.enabled} shadow={r.shadowCastingMode} {r.GetIl2CppType().Name} " +
                      $"mesh={mesh?.name} go={r.gameObject.name} parent={r.transform.parent?.name}";
            groups.TryGetValue(key, out var n);
            groups[key] = n + 1;
        }
        foreach (var pair in groups)
            lines.Add($"  other x{pair.Value} {pair.Key}");
        lines.Add($"  {others} renderers under the vehicle are not in the register");
        if (capture != null)
        {
            var fittings = 0;
            var owners = new HashSet<IntPtr>();
            var fittingOwners = new HashSet<IntPtr>();
            foreach (var p in capture.Parts)
            {
                fittings += p.Fitting ? 1 : 0;
                if (p.Owner != IntPtr.Zero)
                {
                    owners.Add(p.Owner);
                    if (p.Fitting)
                        fittingOwners.Add(p.Owner);
                }
            }
            lines.Add($"  fittings: {fittings} of {capture.Parts.Count} parts; plate structures {owners.Count}, of which fittings {fittingOwners.Count}");
        }
        foreach (var rack in GetRacks(vehicle))
        {
            if (rack == null)
                continue;
            var shells = rack.models;
            var active = 0;
            for (var i = 0; shells != null && i < shells.Count; i++)
                active += shells[i] != null && shells[i].gameObject.activeSelf ? 1 : 0;
            lines.Add($"  ammo rack models={shells?.Count} displayCap={rack.displayCap} activeSelf={active} hd={rack.hdModel} optimized={rack.optimizedMode} lod={rack.targetLOD}");
        }
        return lines;
    }

    /// <summary>開発用: 中の部品の体力が減った、最後の確定した命中。</summary>
    public HitCapture LatestDamaged()
    {
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var c = _recent[i];
            if (!c.Confirmed || c.Released)
                continue;
            foreach (var m in c.Modules)
            {
                if (m.Damaged)
                    return c;
            }
        }
        return null;
    }

    /// <summary>開発用: 最後に確定した命中（無ければ最後の記録）。</summary>
    public HitCapture Latest(HitCamOutcome? outcome = null)
    {
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            if (_recent[i].Confirmed && !_recent[i].Released && (outcome == null || _recent[i].Outcome == outcome))
                return _recent[i];
        }
        if (outcome != null)
            return null;
        return _recent.Count > 0 && !_recent[^1].Released ? _recent[^1] : null;
    }

    /// <summary>開発用: 記録の中身。</summary>
    public IEnumerable<string> Describe(HitCapture capture)
    {
        var lines = new List<string>();
        if (capture == null)
        {
            lines.Add("no capture");
            return lines;
        }
        lines.Add($"capture {capture.VictimName} t={capture.Time:0.0} outcome={capture.Outcome} confirmed={capture.Confirmed} " +
                  $"parts={capture.Parts.Count} fragments={capture.Fragments.Count} bounds={capture.Bounds.size} entry={capture.Entry} dir={capture.Direction}");
        var deadMeshes = 0;
        var deadMaterials = 0;
        foreach (var p in capture.Parts)
        {
            if (p.Mesh == null || p.Mesh.WasCollected)
                deadMeshes++;
            foreach (var m in p.Materials)
            {
                if (m == null || m.WasCollected)
                    deadMaterials++;
            }
        }
        lines.Add($"  preserved={capture.Preserved} dead meshes={deadMeshes} dead materials={deadMaterials} onFire={capture.OnFire()} (was {capture.WasOnFire})");
        foreach (var m in capture.Modules)
        {
            var parts = 0;
            foreach (var p in capture.Parts)
                parts += p.Module >= 0 && capture.Modules[p.Module] == m ? 1 : 0;
            lines.Add($"  module {m.Class} health {m.Before:P0} -> hit {m.Result:P0} (now {m.Fraction():P0}) cause={m.Cause}@{m.CauseAlong:0.00} parts={parts} bounds={(m.HasBounds ? m.Bounds.size.ToString() : "-")}");
        }
        var byType = new Dictionary<string, int>();
        foreach (var p in capture.Parts)
        {
            var key = $"{p.PartType} [{string.Join(",", Array.ConvertAll(p.Materials, m => m != null && !m.WasCollected ? m.shader?.name ?? "?" : "null"))}]";
            byType.TryGetValue(key, out var n);
            byType[key] = n + 1;
        }
        foreach (var pair in byType)
            lines.Add($"  part {pair.Value,3} x {pair.Key}");
        for (var i = 0; i < Math.Min(capture.Fragments.Count, 12); i++)
        {
            var f = capture.Fragments[i];
            lines.Add($"  frag {i} parent={f.Parent} flags=0x{f.Flags:X} d={f.Diameter:0.###} v={f.Speed:0} len={(f.End - f.Start).magnitude:0.00}");
        }
        return lines;
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
