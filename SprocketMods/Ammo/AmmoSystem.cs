using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Ammo;

/// <summary>砲弾の種類。</summary>
public enum AmmoType
{
    AP = 0,
    APHE = 1,
    HE = 2,
}

/// <summary>砲弾の種類の設定（[Ammo] セクション）。</summary>
public class AmmoConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> ShowHud;
    public readonly ConfigEntry<KeyboardShortcut> KeyAP;
    public readonly ConfigEntry<KeyboardShortcut> KeyAPHE;
    public readonly ConfigEntry<KeyboardShortcut> KeyHE;
    public readonly ConfigEntry<float> ApheLossSmall;
    public readonly ConfigEntry<float> ApheLossLarge;
    public readonly ConfigEntry<float> ApheFuse;
    public readonly ConfigEntry<float> ApheFragments;
    public readonly ConfigEntry<float> HePenetration;
    public readonly ConfigEntry<bool> HeDestroysAll;
    public readonly ConfigEntry<bool> HeKillsExposedCrew;
    public readonly ConfigEntry<float> HeBlastRadius;

    public AmmoConfig(ConfigFile cfg)
    {
        const string s = "Ammo";
        Enabled = cfg.Bind(s, "Enabled", true, "砲弾の種類（徹甲弾・徹甲榴弾・榴弾）をキーで切り替える。弾薬庫の砲弾はどの種類としても撃てる");
        ShowHud = cfg.Bind(s, "ShowHud", true, "画面の下の中央に砲弾の種類と装填の状態を出す");
        KeyAP = cfg.Bind(s, "KeyAP", new KeyboardShortcut(KeyCode.Alpha1), "徹甲弾にするキー");
        KeyAPHE = cfg.Bind(s, "KeyAPHE", new KeyboardShortcut(KeyCode.Alpha2), "徹甲榴弾にするキー");
        KeyHE = cfg.Bind(s, "KeyHE", new KeyboardShortcut(KeyCode.Alpha3), "榴弾にするキー");
        ApheLossSmall = cfg.Bind(s, "ApheLossSmall", 0.15f,
            new ConfigDescription("徹甲榴弾の貫通力の低下（口径 20 mm 以下）。口径が大きいほど ApheLossLarge に近づく", new AcceptableValueRange<float>(0f, 0.6f)));
        ApheLossLarge = cfg.Bind(s, "ApheLossLarge", 0.05f,
            new ConfigDescription("徹甲榴弾の貫通力の低下（口径 150 mm 以上）", new AcceptableValueRange<float>(0f, 0.6f)));
        ApheFuse = cfg.Bind(s, "ApheFuse", 0.9f,
            new ConfigDescription("徹甲榴弾が装甲を抜けてから炸裂するまでの距離（m）", new AcceptableValueRange<float>(0.1f, 3f)));
        ApheFragments = cfg.Bind(s, "ApheFragments", 1f,
            new ConfigDescription("徹甲榴弾の炸裂の破片の数の倍率", new AcceptableValueRange<float>(0.2f, 3f)));
        HePenetration = cfg.Bind(s, "HePenetration", 0.1f,
            new ConfigDescription("榴弾の貫通力（徹甲弾に対する割合）", new AcceptableValueRange<float>(0.01f, 0.5f)));
        HeDestroysAll = cfg.Bind(s, "HeDestroysAll", true, "榴弾が装甲を抜けたら、その車両の全部の部品（乗員を含む）を壊す");
        HeKillsExposedCrew = cfg.Bind(s, "HeKillsExposedCrew", true,
            "榴弾が炸裂した点（車両・地面への着弾）から、まっすぐ線が通る乗員（オープントップ・穴の開いた車両など）を破片と爆風で倒す。" +
            "装甲などに遮られた乗員は無傷");
        HeBlastRadius = cfg.Bind(s, "HeBlastRadius", 1f,
            new ConfigDescription("榴弾の爆風が乗員に届く距離の倍率（口径 75 mm で約 4 m、150 mm で約 6.8 m）", new AcceptableValueRange<float>(0.2f, 3f)));
    }
}

/// <summary>
/// 砲弾の種類（1: 徹甲弾、2: 徹甲榴弾、3: 榴弾）。弾薬庫の砲弾はどれでも、選んだ種類として撃つ。
///
///  - 選ぶ: キーで選んだ種類が次に装填される。装填済みの砲弾と違う種類を選んだら、その場で抜いて装填し直す
///    （LoadTask.Unload → WeaponBehaviour.RequestReload）。装填中に選び直したら、その装填がそのまま新しい種類になる。
///  - 撃つ: 自車の砲が撃った弾の ID（CannonBehaviour.FireInternal）に、装填されていた種類を覚える。
///  - 貫通力: ゲームの貫通の計算（PenetrationUtils.ComputePenetration）は
///    P = c · (√質量 · 速さ / (K · 口径^a))^n で、K は弾の PenetratorConstant。貫通力を f 倍にするには K を f^(-1/n) 倍にする。
///    n は ComputePenetration を K と 2K で呼んで求める。CompoundStructure.Penetrate（すべての貫通計算の入口）の前処理で、
///    自分の弾（弾の今の位置・向きが PenetratorInfo と合うもの）の K だけを書き換える。速さ・質量はそのまま（ダメージは変わらない）。
///      徹甲榴弾: 口径 20 mm で 15 %、150 mm で 5 % 低下（間は直線）。榴弾: 10 分の 1。
///  - 炸裂（後処理）: 弾（OriginalPenetrator）が装甲を抜けていたら、徹甲榴弾は抜けてから ApheFuse m 進んだ所
///    （それより前に止まった・車両を抜けたならそこ）で炸裂し、全方向（少し前寄り）へ破片を飛ばす。破片はゲームの貫通の計算
///    （CompoundStructure.Penetrate、自分で呼ぶので SimulationRan の購読は一時的に外す）で飛ばし、ApplyFragmentHits で
///    当たった部品を傷つける。ヒットカメラにはその命中の破片として足す。榴弾は破片を飛ばしてから全部の部品を壊す。
///  - 弾道の HUD の貫通の予測（PenetrationProbe）も、Probing の間は装填中（無ければ選んだ）種類で計算する。
/// </summary>
internal sealed unsafe class AmmoSystem
{
    public static AmmoSystem Instance { get; private set; }

    private static AmmoConfig Cfg => Plugin.Ammo;

    public AmmoType Selected { get; private set; } = AmmoType.AP;
    public AmmoType Loaded { get; private set; } = AmmoType.AP;
    public bool HasGun { get; private set; }
    public LoadState State { get; private set; } = LoadState.NeverLoaded;
    public float LoadFraction { get; private set; }
    public float RemainingTime { get; private set; }
    public AmmoType LoadingType { get; private set; } = AmmoType.AP;

    /// <summary>自車が撃破された（乗員の全滅・誘爆。KillTracker）。弾の表示を引っ込め、撃破の画面を出す。</summary>
    public bool PlayerDead { get; private set; }

    // 装填の残り時間: ゲームの RemainingLoadTime は装填の段階の一部しか数えないので、前回の装填にかかった時間から数える
    private float _loadStart = float.NaN;
    private float _lastDuration = float.NaN;
    public float SelectedAt { get; private set; } = float.NegativeInfinity;

    /// <summary>弾道の HUD が貫通を予測している間（PenetrationProbe）。</summary>
    public static bool Probing;

    /// <summary>予測に使う種類（装填済みならその種類、無ければ選んだ種類）。</summary>
    public AmmoType ProbeType => State == LoadState.Loaded ? Loaded : Selected;

    private VehicleController _controller;
    private float _nextControllerSearch;
    private WeaponBehaviour _weapon;
    private IntPtr _weaponPtr;
    private LoadState _lastState = LoadState.NeverLoaded;

    // 砲ごとの弾の状態（砲を切り替えて戻ったときに、選んだ種類・装填済みの種類を元に戻す）。車両が変わったら捨てる
    private sealed class GunState
    {
        public AmmoType Selected, Loaded, LoadingType;
        public LoadState LastState;
        public float LoadStart, LastDuration;
    }

    private readonly Dictionary<IntPtr, GunState> _guns = new();
    private IntPtr _vehiclePtr;

    private sealed class Shot
    {
        public AmmoType Type;
        public float Time;
        public IntPtr Shooter; // 撃った車両の Rigidbody（爆風で倒した戦果に使う）
        public float Diameter; // 口径（m）。地面で炸裂したときの爆風の大きさに使う
    }

    private readonly Dictionary<int, Shot> _shots = new();
    private readonly List<int> _expired = new();
    private int _instanceStride;
    private int _fragmentStride;
    private static float _exponent = float.NaN;
    private static bool _inBurst;
    private static readonly System.Random Random = new();

    public AmmoSystem() => Instance = this;

    public static string ShortName(AmmoType type) => type switch
    {
        AmmoType.APHE => "APHE",
        AmmoType.HE => "HE",
        _ => "AP",
    };

    public static string LongName(AmmoType type) => type switch
    {
        AmmoType.APHE => "Armour-piercing high-explosive",
        AmmoType.HE => "High-explosive",
        _ => "Armour-piercing",
    };

    // ------------------------------------------------------------------ 毎フレーム: 選ぶ・装填

    public void Update()
    {
        if (!Cfg.Enabled.Value)
        {
            HasGun = false;
            return;
        }
        var weapon = GetPlayerWeapon();
        if (weapon == null)
        {
            HasGun = false;
            PlayerDead = false;
            // 砲の切り替えの途中で砲が無いフレームがあっても、その砲の状態は覚えておく
            SaveGun();
            _weapon = null;
            _weaponPtr = IntPtr.Zero;
            return;
        }
        var body = _controller.ControlledVehicle.Rigidbody;
        PlayerDead = Core.Fast.Alive(body) && SprocketMods.KillFeed.KillTracker.Instance?.IsBodyKnockedOut(body.Pointer) == true;
        if (weapon.Pointer != _weaponPtr)
            SwitchGun(weapon, Core.Fast.Alive(body) ? body.Pointer : IntPtr.Zero);
        HasGun = true;

        if (Core.Fast.TimeScale > 0f && !PlayerDead)
        {
            if (SprocketMods.UI.KeyInput.Down(Cfg.KeyAP.Value))
                Select(AmmoType.AP);
            else if (SprocketMods.UI.KeyInput.Down(Cfg.KeyAPHE.Value))
                Select(AmmoType.APHE);
            else if (SprocketMods.UI.KeyInput.Down(Cfg.KeyHE.Value))
                Select(AmmoType.HE);
        }

        var state = weapon.LoadState;
        var now = Time.time;
        if (state == LoadState.Loading && _lastState != LoadState.Loading)
        {
            LoadingType = Selected;
            _loadStart = now;
        }
        if (state == LoadState.Loaded && _lastState != LoadState.Loaded)
        {
            Loaded = _lastState == LoadState.Loading ? LoadingType : Selected;
            if (!float.IsNaN(_loadStart) && now - _loadStart > 0.2f)
                _lastDuration = now - _loadStart;
            _loadStart = float.NaN;
        }
        _lastState = state;
        State = state;
        try
        {
            LoadFraction = Mathf.Clamp01(weapon.LoadFraction);
        }
        catch (Exception)
        {
            LoadFraction = state == LoadState.Loaded ? 1f : 0f;
        }
        RemainingTime = Remaining(weapon, state, now);

        GroundBlasts();

        // 古い弾の記録を捨てる
        _expired.Clear();
        foreach (var pair in _shots)
        {
            if (now - pair.Value.Time > 20f)
                _expired.Add(pair.Key);
        }
        foreach (var id in _expired)
            _shots.Remove(id);
    }

    /// <summary>
    /// 操作する砲が変わった。同じ車両の砲の切り替えなら、今の砲の状態を覚え、切り替え先の砲の状態を戻す
    /// （初めて使う砲は、今選んでいる種類を引き継ぎ、違う種類が装填されていればキーを押したときと同じく装填し直す）。
    /// 乗り換え・新しい試合（車両が変わった）なら徹甲弾から。
    /// </summary>
    private void SwitchGun(WeaponBehaviour weapon, IntPtr vehicle)
    {
        var sameVehicle = vehicle != IntPtr.Zero && vehicle == _vehiclePtr;
        if (sameVehicle)
            SaveGun();
        else
            _guns.Clear();
        var carried = sameVehicle ? Selected : AmmoType.AP;
        _vehiclePtr = vehicle;
        _weapon = weapon;
        _weaponPtr = weapon.Pointer;
        if (_guns.TryGetValue(_weaponPtr, out var saved))
        {
            Selected = saved.Selected;
            Loaded = saved.Loaded;
            LoadingType = saved.LoadingType;
            _lastState = saved.LastState;
            _loadStart = saved.LoadStart;
            _lastDuration = saved.LastDuration;
            return;
        }
        Selected = Loaded = LoadingType = AmmoType.AP;
        _lastState = weapon.LoadState;
        _loadStart = _lastDuration = float.NaN;
        if (carried != AmmoType.AP)
            Select(carried);
    }

    private void SaveGun()
    {
        if (_weaponPtr == IntPtr.Zero)
            return;
        _guns[_weaponPtr] = new GunState
        {
            Selected = Selected, Loaded = Loaded, LoadingType = LoadingType,
            LastState = _lastState, LoadStart = _loadStart, LastDuration = _lastDuration,
        };
    }

    /// <summary>開発用（ammo select）。</summary>
    public void DebugSelect(AmmoType type) => Select(type);

    public string Describe() =>
        $"ammo gun={HasGun} selected={Selected} loaded={Loaded} loading={LoadingType} state={State} fraction={LoadFraction:0.00} remaining={RemainingTime:0.0} " +
        $"tracked shots={_shots.Count} everyone={(Everyone?.ToString() ?? "off")}";

    /// <summary>
    /// 装填の残り時間（ゲームの時間の秒）。前回の装填にかかった時間があればそこから、無ければ（最初の装填）進み具合から見積もる。
    /// </summary>
    private float Remaining(WeaponBehaviour weapon, LoadState state, float now)
    {
        if (state != LoadState.Loading)
            return 0f;
        var elapsed = float.IsNaN(_loadStart) ? 0f : now - _loadStart;
        if (!float.IsNaN(_lastDuration))
            return Mathf.Max(0f, _lastDuration - elapsed);
        if (LoadFraction > 0.08f && elapsed > 0.1f)
            return Mathf.Max(0f, elapsed * (1f - LoadFraction) / LoadFraction);
        try
        {
            return Mathf.Max(0f, weapon.RemainingLoadTime);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private void Select(AmmoType type)
    {
        Selected = type;
        SelectedAt = Core.Fast.Unscaled;
        var weapon = _weapon;
        if (weapon == null || weapon.WasCollected)
            return;
        var state = weapon.LoadState;
        if (state == LoadState.Loading)
        {
            // 装填中なら、その装填が新しい種類になる
            LoadingType = type;
            return;
        }
        if (state != LoadState.Loaded || Loaded == type || SprocketMods.Weapons.Autocannons.Get(weapon) != null)
            return;
        // 装填済みの砲弾と違う種類: 抜いて装填し直す
        try
        {
            var task = weapon.LoadTask?.TryCast<LoadTask>();
            if (task == null)
                return;
            task.Unload();
            weapon.RequestReload(new Il2CppSystem.EventArgs());
            LoadingType = type;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Ammo: reload for the new shell failed: {e.Message}");
        }
    }

    private WeaponBehaviour GetPlayerWeapon()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Core.Fast.Unscaled >= _nextControllerSearch)
        {
            _nextControllerSearch = Core.Fast.Unscaled + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        if (_controller == null || _controller.ControlledVehicle == null || SprocketMods.Debug.DebugConsole.IsHidden(_controller.ControlledVehicle.Rigidbody))
            return null;
        var gunner = _controller.activeGunner?.TryCast<Gunner>();
        return gunner?.weapon?.TryCast<WeaponBehaviour>();
    }

    // ------------------------------------------------------------------ 撃った弾

    /// <summary>開発用（ammo all）: すべての車両の弾をこの種類にする（AI どうしの対戦で炸裂を確かめる）。</summary>
    public static AmmoType? Everyone;

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (Everyone != null && weapon != null && projectileId > 0)
        {
            if (Everyone != AmmoType.AP)
                _shots[projectileId] = new Shot { Type = Everyone.Value, Time = Time.time, Shooter = ShooterBody(weapon), Diameter = Caliber(weapon) };
            return;
        }
        if (!Cfg.Enabled.Value || weapon == null || projectileId <= 0 || weapon.Pointer != _weaponPtr || Loaded == AmmoType.AP)
            return;
        if (_shots.Count > 256)
            _shots.Clear();
        _shots[projectileId] = new Shot
        {
            Type = Loaded,
            Time = Time.time,
            Shooter = _controller?.ControlledVehicle?.Rigidbody?.Pointer ?? IntPtr.Zero,
            Diameter = Caliber(weapon),
        };
    }

    private static IntPtr ShooterBody(WeaponBehaviour weapon)
    {
        try
        {
            var behaviour = weapon.Transform?.GetComponentInParent<VehicleBehaviour>();
            return behaviour?.Rigidbody?.Pointer ?? IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>砲の口径（m）。装填中の弾の定義から（分からなければ 75 mm）。</summary>
    private static float Caliber(WeaponBehaviour weapon)
    {
        try
        {
            var register = IProjectileTypeRegister.Instance;
            if (register != null && register.TryGet(weapon.LoadedProjectileTypeID, out var type)
                && type?.TryCast<ProjectileDefinition>() is { } def && def.Diameter > 0f)
                return def.Diameter;
        }
        catch (Exception)
        {
        }
        return 0.075f;
    }

    // ------------------------------------------------------------------ 貫通の計算（CompoundStructure.Penetrate の前後）

    /// <summary>前処理: 自分の徹甲榴弾・榴弾（と予測）なら貫通力を下げる。返り値は後処理へ渡す種類（-1 = 何もしない）。</summary>
    public static int BeforePenetrate(ref PenetratorInfo penetrator)
    {
        var system = Instance;
        if (system == null || _inBurst || !Cfg.Enabled.Value)
            return -1;
        AmmoType type;
        var shot = false;
        var id = 0;
        if (Probing)
        {
            if (!system.HasGun)
                return -1;
            type = system.ProbeType;
        }
        else
        {
            if (system._shots.Count == 0 || !system.MatchShot(penetrator.Position, penetrator.Velocity, out type, out id))
                return -1;
            shot = true;
        }
        if (type == AmmoType.AP)
            return -1;
        var factor = type == AmmoType.HE ? Cfg.HePenetration.Value : 1f - ApheLoss(penetrator.Diameter);
        penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, penetrator.MaxNormalizationAngle,
            ScaleConstant(penetrator, factor), penetrator.Position, penetrator.Velocity);
        // 後処理へ: 弾 ID × 4 + 種類
        return shot ? id * 4 + (int)type : -1;
    }

    /// <summary>後処理: 自分の弾が装甲を抜けていたら炸裂する。</summary>
    public static void AfterPenetrate(CompoundStructure structure, PenetratorInfo penetrator, IPenetrationSimulation result, int state)
    {
        if (state < 0 || result == null || structure == null || Instance == null)
            return;
        var sim = result.TryCast<PenetrationSimulation>();
        if (sim == null)
            return;
        Instance.Explode(structure, sim, penetrator, (AmmoType)(state & 3), state >> 2);
    }

    /// <summary>徹甲榴弾の貫通力の低下（口径 20 mm で ApheLossSmall、150 mm で ApheLossLarge）。</summary>
    private static float ApheLoss(float diameter)
    {
        var k = Mathf.InverseLerp(20f, 150f, diameter * 1000f);
        return Mathf.Lerp(Cfg.ApheLossSmall.Value, Cfg.ApheLossLarge.Value, k);
    }

    /// <summary>貫通力を factor 倍にする PenetratorConstant（K^(-n) に比例するので K × factor^(-1/n)）。</summary>
    private static ushort ScaleConstant(PenetratorInfo penetrator, float factor)
    {
        if (float.IsNaN(_exponent))
        {
            _exponent = 1.43f;
            try
            {
                var speed = Mathf.Max(100f, penetrator.Velocity.magnitude);
                var k = (ushort)Mathf.Clamp(penetrator.PenetratorConstant, 500, 30000);
                var a = PenetrationUtils.ComputePenetration(penetrator.Diameter, penetrator.Mass, speed, k);
                var b = PenetrationUtils.ComputePenetration(penetrator.Diameter, penetrator.Mass, speed, (ushort)(k * 2));
                if (a > 0f && b > 0f && a > b)
                    _exponent = Mathf.Log(a / b) / Mathf.Log(2f);
                Plugin.Logger.LogInfo($"Ammo: penetration exponent {_exponent:0.###} (K {k}: {a:0.#} mm, 2K: {b:0.#} mm)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Ammo: penetration exponent failed, using 1.43: {e.Message}");
            }
        }
        var scaled = penetrator.PenetratorConstant * Mathf.Pow(Mathf.Max(0.001f, factor), -1f / _exponent);
        return (ushort)Mathf.Clamp(Mathf.RoundToInt(scaled), 1, 65535);
    }

    /// <summary>
    /// 貫通計算の弾が、覚えている自分の弾（ProjectileRegister.pool の ProjectileInstance、メモリから読む）か。
    /// 貫通計算はその弾の物理ステップの中で走るので、弾の今の位置は着弾点のすぐ近く、向きは同じ。
    /// </summary>
    private bool MatchShot(Vector3 position, Vector3 velocity, out AmmoType type, out int shotId)
    {
        type = AmmoType.AP;
        shotId = 0;
        var speed = velocity.magnitude;
        if (speed < 1f)
            return false;
        var dir = velocity / speed;
        var register = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>();
        var map = register?.activeIdMap;
        if (map == null)
            return false;
        if (_instanceStride == 0)
        {
            uint align = 0;
            var klass = Il2CppClassPointerStore<ProjectileInstance>.NativeClassPtr;
            var size = klass != IntPtr.Zero ? IL2CPP.il2cpp_class_value_size(klass, ref align) : 0;
            _instanceStride = size >= 0x4C && size <= 0x100 ? size : 0x50;
        }
        var pool = *(IntPtr*)(register.Pointer + 0x38);
        if (pool == IntPtr.Zero)
            return false;
        var length = *(int*)(pool + 0x18);
        foreach (var pair in _shots)
        {
            var id = pair.Key;
            if (!map.TryGetValue(id, out var index) || index < 0 || index >= length)
                continue;
            var instance = pool + 0x20 + index * _instanceStride;
            if (*(int*)(instance + 0x18) != id)
                continue;
            var shellPosition = *(Vector3*)(instance + 0x28);
            var shellVelocity = *(Vector3*)(instance + 0x34);
            var shellSpeed = shellVelocity.magnitude;
            if (shellSpeed < 1f || Vector3.Dot(shellVelocity / shellSpeed, dir) < 0.97f)
                continue;
            var d = position - shellPosition;
            var along = Vector3.Dot(d, dir);
            if ((d - dir * along).magnitude < 1.5f && Mathf.Abs(along) < shellSpeed * 0.1f + 3f)
            {
                type = pair.Value.Type;
                shotId = id;
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ 炸裂

    private struct Frag
    {
        public Vector3 Start;
        public Vector3 End;
        public ushort Flags;
    }

    private void Explode(CompoundStructure structure, PenetrationSimulation sim, PenetratorInfo penetrator, AmmoType type, int shotId)
    {
        var fragments = ReadFragments(sim);
        if (fragments.Count == 0)
            return;
        // 弾（OriginalPenetrator = 1）が装甲を抜けた（Penetrated = 64）か
        var penetrated = false;
        foreach (var f in fragments)
            penetrated |= (f.Flags & 1) != 0 && (f.Flags & 64) != 0;
        var direction = penetrator.Velocity.sqrMagnitude > 1f ? penetrator.Velocity.normalized : Vector3.forward;

        // 榴弾は当たったところで必ず炸裂する（抜けたら中で、抜けなければ当たった面のすぐ外で）。露出した乗員を爆風で倒す
        if (type == AmmoType.HE)
        {
            var point = penetrated ? BurstPoint(fragments, 0.3f) : fragments[0].End - direction * 0.08f;
            _shots.TryGetValue(shotId, out var shot);
            _shots.Remove(shotId);
            CrewBlast(point, penetrator.Diameter, shot?.Shooter ?? IntPtr.Zero);
        }
        if (!penetrated)
            return;

        var burst = BurstPoint(fragments, type == AmmoType.HE ? 0.3f : Cfg.ApheFuse.Value);
        var caliber = penetrator.Diameter * 1000f;
        var heavy = type == AmmoType.HE;
        var count = Mathf.RoundToInt(Mathf.Clamp(caliber * (heavy ? 0.5f : 0.35f), heavy ? 24f : 14f, heavy ? 64f : 48f) * (heavy ? 1f : Cfg.ApheFragments.Value));
        var fragmentMass = Mathf.Max(0.01f, penetrator.Mass * (heavy ? 0.5f : 0.35f) / Mathf.Max(1, count));
        var fragmentDiameter = Mathf.Clamp(penetrator.Diameter * 0.15f, 0.006f, 0.025f);
        var speed = heavy ? 1100f : 900f;

        var body = structure.RigidBody;
        var hitCam = SprocketMods.HitCam.HitCamTracker.Instance;
        var internalHandler = structure.SimulationRanInternal;
        var publicHandler = structure.SimulationRan;
        structure.SimulationRanInternal = null;
        structure.SimulationRan = null;
        _inBurst = true;
        var applied = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                // 全方向（少しだけ弾の進む向きに寄せる）
                var random = RandomDirection();
                var dir = (random + direction * 0.45f).normalized;
                var info = new PenetratorInfo(fragmentMass, fragmentDiameter, penetrator.Density, penetrator.MaxNormalizationAngle,
                    penetrator.PenetratorConstant, burst, dir * speed * (0.8f + 0.4f * (float)Random.NextDouble()));
                var result = structure.Penetrate(ref info, PenetrationSimulationOptions.Default);
                var burstSim = result?.TryCast<PenetrationSimulation>();
                if (burstSim == null)
                    continue;
                burstSim.ApplyFragmentHits();
                applied++;
                if (body != null)
                    hitCam?.AppendBurst(body.Pointer, burstSim);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Ammo: burst failed: {e.Message}");
        }
        finally
        {
            _inBurst = false;
            structure.SimulationRanInternal = internalHandler;
            structure.SimulationRan = publicHandler;
        }

        var destroyed = 0;
        if (heavy && Cfg.HeDestroysAll.Value && body != null)
        {
            var vehicle = body.GetComponent<Vehicle>()?.behaviour;
            destroyed = SprocketMods.Repair.RepairSystem.Instance?.KillAll(vehicle) ?? 0;
        }
        if (body != null)
            hitCam?.TagAmmo(body.Pointer, LongName(type));
        Plugin.Logger.LogInfo($"Ammo: {ShortName(type)} burst {applied}/{count} fragments ({fragmentMass * 1000f:0} g, {fragmentDiameter * 1000f:0} mm)" +
                              (heavy ? $", destroyed {destroyed} parts" : ""));
    }

    // ------------------------------------------------------------------ 榴弾の爆風（露出した乗員）

    /// <summary>地面に落ちた榴弾（ProjectileRegister.projectileHistory の HitGroundProjectileEvent）も、その点で炸裂する。</summary>
    private void GroundBlasts()
    {
        if (_shots.Count == 0 || !Cfg.HeKillsExposedCrew.Value)
            return;
        var history = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>()?.projectileHistory;
        if (history == null)
            return;
        _expired.Clear();
        foreach (var pair in _shots)
        {
            if (pair.Value.Type != AmmoType.HE || !history.TryGetValue(pair.Key, out var latest) || latest == null)
                continue;
            var ground = latest.TryCast<HitGroundProjectileEvent>();
            if (ground == null)
                continue;
            _expired.Add(pair.Key);
            CrewBlast(ground.Position + ground.Normal * 0.1f, pair.Value.Diameter, pair.Value.Shooter);
        }
        foreach (var id in _expired)
            _shots.Remove(id);
    }

    private readonly List<Vehicle> _vehicles = new();
    private float _nextVehicleSearch;
    private int _blastLogs;

    /// <summary>
    /// 榴弾の爆風: 炸裂した点から半径 R（口径 75 mm で約 4 m）以内の車両の乗員ごとに、点から乗員の体（座席の上 0.35 / 0.75 / 1.1 m）へ
    /// まっすぐ線を引き、その車両の当たり判定（CompoundStructure.Raycast、車両の座標で）で最初に当たるものが
    /// その乗員（ObjectInfo.ID = 座席の VUID）か、何にも当たらなければ倒す（HealthUtils.Kill）。装甲・砲・エンジンなどが先なら無傷。
    /// </summary>
    /// <summary>開発用（ammo blasttest）: 各車両の前 1.5 m と真上 2.5 m で炸裂したら倒れる乗員の数（倒さない）。</summary>
    public IEnumerable<string> BlastTest()
    {
        var lines = new List<string>();
        RefreshVehicles();
        _nextVehicleSearch = 0f;
        var repair = SprocketMods.Repair.RepairSystem.Instance;
        foreach (var vehicle in _vehicles)
        {
            if (vehicle == null || vehicle.WasCollected || repair == null)
                continue;
            var structure = vehicle.damageModel?.TryCast<CompoundStructure>();
            var behaviour = vehicle.behaviour;
            var body = vehicle.Rigidbody;
            if (structure == null || behaviour == null || body == null)
                continue;
            var objects = structure.objects;
            var ids = ObjectIds(structure, objects);
            var t = vehicle.transform;
            var centre = body.worldCenterOfMass;
            var points = new[] { ("front", centre + t.forward * 4.0f + t.up * 0.6f), ("above", centre + t.up * 2.5f) };
            var text = $"{vehicle.name}:";
            foreach (var (label, point) in points)
            {
                int exposed = 0, crew = 0;
                repair.ForEachHealth(behaviour, (owner, pool) =>
                {
                    if (repair.ComponentClass(owner) != "CrewSeat")
                        return;
                    crew++;
                    var seat = new VehicleComponent(owner);
                    var vuid = ((VehiclePart)seat).VUID.Value;
                    foreach (var height in new[] { 0.35f, 0.75f, 1.1f })
                    {
                        if (Exposed(structure, objects, ids, point, seat.transform.position + t.up * height, vuid))
                        {
                            exposed++;
                            break;
                        }
                    }
                });
                text += $" {label} {exposed}/{crew} exposed";
            }
            lines.Add(text);
            if (lines.Count >= 6)
                break;
        }
        return lines;
    }

    private void CrewBlast(Vector3 point, float diameter, IntPtr shooter)
    {
        if (!Cfg.HeKillsExposedCrew.Value || SprocketMods.Repair.RepairSystem.Instance == null)
            return;
        var radius = Mathf.Clamp(1.5f + diameter * 1000f * 0.035f, 2f, 8f) * Cfg.HeBlastRadius.Value;
        RefreshVehicles();
        var repair = SprocketMods.Repair.RepairSystem.Instance;
        var killed = 0;
        var vehicles = 0;
        foreach (var vehicle in _vehicles)
        {
            if (vehicle == null || vehicle.WasCollected)
                continue;
            var body = vehicle.Rigidbody;
            if (body == null || (body.worldCenterOfMass - point).sqrMagnitude > (radius + 6f) * (radius + 6f))
                continue;
            var structure = vehicle.damageModel?.TryCast<CompoundStructure>();
            var behaviour = vehicle.behaviour;
            if (structure == null || behaviour == null)
                continue;
            var objects = structure.objects;
            var ids = ObjectIds(structure, objects);
            var up = vehicle.transform.up;
            var before = killed;
            repair.ForEachHealth(behaviour, (owner, pool) =>
            {
                if (repair.ComponentClass(owner) != "CrewSeat" || *(float*)(pool + 0x20) <= 0f)
                    return;
                var seat = new VehicleComponent(owner);
                var vuid = ((VehiclePart)seat).VUID.Value;
                var origin = seat.transform.position;
                foreach (var height in new[] { 0.35f, 0.75f, 1.1f })
                {
                    var target = origin + up * height;
                    if ((target - point).magnitude > radius || !Exposed(structure, objects, ids, point, target, vuid))
                        continue;
                    Sprocket.HealthUtils.Kill(new Sprocket.IDurable(pool));
                    killed++;
                    break;
                }
            });
            if (killed > before)
            {
                vehicles++;
                SprocketMods.KillFeed.KillTracker.Instance?.CreditBlast(shooter, body.Pointer);
            }
        }
        if (killed > 0 || _blastLogs < 5)
        {
            _blastLogs++;
            Plugin.Logger.LogInfo($"Ammo: HE blast radius {radius:0.0} m: killed {killed} exposed crew in {vehicles} vehicles");
        }
    }

    private readonly Dictionary<IntPtr, HashSet<int>> _objectIds = new();

    /// <summary>当たり判定の部品の ID（ObjectInfo.ID）の一覧（車両ごとに持つ）。</summary>
    private HashSet<int> ObjectIds(CompoundStructure structure, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ObjectInfo> objects)
    {
        if (_objectIds.TryGetValue(structure.Pointer, out var ids) && ids.Count == (objects?.Length ?? 0))
            return ids;
        if (_objectIds.Count > 128)
            _objectIds.Clear();
        ids = new HashSet<int>();
        for (var i = 0; objects != null && i < objects.Length; i++)
            ids.Add(objects[i].ID);
        _objectIds[structure.Pointer] = ids;
        return ids;
    }

    private readonly HashSet<IntPtr> _idLogged = new();

    /// <summary>
    /// 点 from から to までの線で、最初に当たるものがその乗員（vuid）か、何にも当たらない。
    /// 乗員の当たり判定が座席の VUID で見つからない車両では、最初に当たるものが乗員の体のすぐ手前（0.45 m 以内）なら届いたとする。
    /// </summary>
    private bool Exposed(CompoundStructure structure, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ObjectInfo> objects,
        HashSet<int> ids, Vector3 from, Vector3 to, int vuid)
    {
        var d = to - from;
        var distance = d.magnitude;
        if (distance < 0.05f)
            return true;
        // 当たり判定はその車両の座標で調べる（ゲームの CompoundStructure.Raycast と同じく、先に構造の行列を新しくしてから
        // 世界の座標を車両の Transform の座標に直す）。世界の座標のまま渡すとどこにも当たらず、装甲の向こうの乗員まで倒してしまう
        StructureIntersection hit;
        bool found;
        try
        {
            structure.UpdateStructureSpaceMatrices();
            var transform = structure.transform;
            var localFrom = transform.InverseTransformPoint(from);
            var localTo = transform.InverseTransformPoint(to);
            var localDir = localTo - localFrom;
            var localDistance = localDir.magnitude;
            if (localDistance < 0.05f)
                return true;
            found = structure.Raycast(localFrom, localDir / localDistance, localDistance + 0.05f, out hit);
        }
        catch (Exception e)
        {
            if (_blastLogs++ < 5)
                Plugin.Logger.LogWarning($"Ammo: crew check failed, treating the crew as covered: {e.Message}");
            return false;
        }
        if (!found)
            return true;
        var best = hit.distance;
        var known = ids.Contains(vuid);
        if (_idLogged.Add(structure.Pointer))
            Plugin.Logger.LogInfo($"Ammo: crew check for {structure.name} uses {(known ? "crew colliders (seat VUID)" : "distance to the crew (seat VUID not in the damage model)")}");
        if (!known)
            return best >= distance - 0.45f;
        var index = hit.objectIndex;
        return objects != null && index >= 0 && index < objects.Length && objects[index].ID == vuid;
    }

    private void RefreshVehicles()
    {
        _vehicles.Clear();
        foreach (var entry in Core.VehicleRegistry.Entries)
        {
            if (entry.Vehicle != null && !entry.Vehicle.WasCollected)
                _vehicles.Add(entry.Vehicle);
        }
    }

    /// <summary>
    /// 炸裂する点: 弾の経路（OriginalPenetrator の破片を順に）で、最初の装甲の表面（最初の破片の終わり）から fuse m 進んだ所。
    /// それより前に経路が終わる（止まった・車両を抜けた）なら、その少し手前。
    /// </summary>
    private static Vector3 BurstPoint(List<Frag> fragments, float fuse)
    {
        var first = fragments[0];
        var point = first.End;
        var travelled = 0f;
        for (var i = 1; i < fragments.Count; i++)
        {
            var f = fragments[i];
            if ((f.Flags & 1) == 0)
                continue;
            var d = f.End - f.Start;
            var length = d.magnitude;
            if (length < 1e-4f)
                continue;
            if (travelled + length >= fuse)
                return f.Start + d / length * (fuse - travelled);
            travelled += length;
            point = f.End - d / length * Mathf.Min(0.05f, length * 0.5f);
        }
        return point;
    }

    private static Vector3 RandomDirection()
    {
        // 球面上で一様
        var z = (float)Random.NextDouble() * 2f - 1f;
        var a = (float)(Random.NextDouble() * Math.PI * 2.0);
        var r = Mathf.Sqrt(1f - z * z);
        return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
    }

    /// <summary>PenetrationSimulation.fragments（Fragment 構造体）をメモリから読む（世界座標）。</summary>
    private List<Frag> ReadFragments(PenetrationSimulation sim)
    {
        var list = new List<Frag>();
        if (_fragmentStride == 0)
        {
            uint align = 0;
            var klass = Il2CppClassPointerStore<Fragment>.NativeClassPtr;
            var size = klass != IntPtr.Zero ? IL2CPP.il2cpp_class_value_size(klass, ref align) : 0;
            _fragmentStride = size >= 0x4A && size <= 0x100 ? size : 0x50;
        }
        var array = *(IntPtr*)(sim.Pointer + 0x80);
        var count = *(int*)(sim.Pointer + 0x88);
        if (array == IntPtr.Zero || count <= 0)
            return list;
        count = Math.Min(Math.Min(count, *(int*)(array + 0x18)), 600);
        var toWorld = sim.SimToWorldSpace;
        for (var i = 0; i < count; i++)
        {
            var f = array + 0x20 + i * _fragmentStride;
            var spawn = *(Vector3*)(f + 0x10);
            var dir = *(Vector3*)(f + 0x1C);
            var travel = *(float*)(f + 0xC);
            if (float.IsNaN(travel) || travel < 0f || travel > 200f)
                travel = 0f;
            list.Add(new Frag
            {
                Start = toWorld.MultiplyPoint3x4(spawn),
                End = toWorld.MultiplyPoint3x4(spawn + dir * travel),
                Flags = *(ushort*)(f + 0x48),
            });
        }
        return list;
    }
}
