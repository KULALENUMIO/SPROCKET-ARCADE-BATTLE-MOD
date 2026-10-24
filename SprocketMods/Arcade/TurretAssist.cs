using System;
using System.Collections.Generic;
using System.Globalization;
using Sprocket;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.Vehicles.Weapons;
using UnityEngine;

namespace SprocketMods.Arcade;

/// <summary>
/// Arcade Battle Mode の砲塔旋回・俯仰。加減速（イージング）なしで、最高速度のまま目標へ向かい、到達したらその場で止まる。
///
/// 砲塔（TurretBehaviour）と砲架（LayingDriveBehaviour）の各軸は RotationAxisPhysicsRegister.axes（RotationAxisPhysics の値型配列、
/// 要素 0x38 バイト）に登録され、RotationAxisPhysicsRegister.Update が 0.01 秒刻みで加速度制限・目標付近の精密モード付きの
/// 物理で動かしてから apply でトランスフォームへ反映する。
/// 自車の軸だけ Update の間 Active フラグを外してゲームの物理をスキップさせ、直後にこちらで角度を進めて apply を呼ぶ。
///
/// 速度 = max(年代別の基準速度, 設計上の最大速度 × 倍率) × 乗員係数。
/// 乗員係数はゲームが毎回 maxSpeed に書く「設計速度 × 練度などの係数」から求める（砲手不在なら 0 で動かない）。
/// </summary>
internal sealed unsafe class TurretAssist
{
    // RotationAxisPhysics のレイアウト（IL2CPP メタデータで確認）
    private const int AxisStride = 0x38;
    private const int AxisAngle = 0x0;
    private const int AxisVelocity = 0x4;
    private const int AxisAcceleration = 0x8;
    private const int AxisMin = 0xC;
    private const int AxisMax = 0x10;
    private const int AxisTarget = 0x14;
    private const int AxisMaxSpeed = 0x20;
    private const int AxisApply = 0x28;
    private const int AxisExtrapolation = 0x30;
    private const int AxisAimFlags = 0x34;
    private const int AxisResult = 0x35;
    private const int AxisFlags = 0x36;

    private const byte ActiveFlag = 1;         // RotationAxisFlags.Active
    private const byte LockFlag = 2;           // AimFlags.Lock（照準ロック中はゲームに任せる）
    private const byte WrappingFlag = 4;       // AimFlags.Wrapping
    private const byte TargetAchievable = 1;   // AimResultFlags.TargetAchievable
    private const float Epsilon = 0.001f;

    private struct ControlledAxis
    {
        public int Handle;
        public bool Elevation;
        public float DesignedSpeed;
        public string Source;
    }

    private readonly List<ControlledAxis> _axes = new();
    private readonly List<int> _suspended = new();
    private IntPtr _vehicle;
    private float _nextRefresh;
    private int _year = 1940;

    public static TurretAssist Instance { get; private set; }

    public TurretAssist()
    {
        Instance = this;
    }

    private static bool Active => Plugin.Arcade.Enabled.Value && Plugin.Arcade.TurretAssist.Value;

    /// <summary>自車の照準機構の軸を集める（ArcadeController から毎 FixedUpdate 呼ばれる）。</summary>
    public void Refresh(VehicleController controller, IntPtr vehicle, GameObject vehicleObject)
    {
        if (vehicle == _vehicle && Time.unscaledTime < _nextRefresh)
            return;
        var changed = vehicle != _vehicle;
        _vehicle = vehicle;
        _nextRefresh = Time.unscaledTime + 1f;
        _axes.Clear();

        if (changed)
        {
            _lastAxesLog = null;
            _year = GetVehicleYear(vehicleObject);
            if (Plugin.Cfg.DebugLog.Value)
                Plugin.Logger.LogInfo($"Arcade turret: vehicle year {_year}, traverse {EraRate(false):0.#} deg/s, elevation {EraRate(true):0.#} deg/s");
        }

        // 1. 照準手（GunLayer）が実際に動かしている照準機構。どの砲・どの砲塔でもこれが本体
        var layers = controller.gunLayers;
        if (layers != null)
        {
            for (var i = 0; i < layers.Length; i++)
                AddMechanism(layers[i]?.TryCast<GunLayer>()?.aimable, "gun layer");
        }

        var gunners = controller.gunners;
        if (gunners != null)
        {
            for (var i = 0; i < gunners.Length; i++)
            {
                var gunner = gunners[i]?.TryCast<Gunner>();
                if (gunner == null)
                    continue;
                // 2. 砲が今持っている旋回・俯仰機構（砲手の一覧は砲手を作った時点のコピーなので、後から付いた機構が漏れる）
                var cannon = gunner.weapon?.TryCast<CannonBehaviour>()?.mount?.TryCast<Cannon>();
                if (cannon != null)
                {
                    AddMechanism(cannon.HorizontalAimingMechanism, "cannon");
                    AddMechanism(cannon.VerticalAimingMechanism, "cannon");
                }
                // 3. 砲手の照準機構の一覧
                var mechanisms = gunner.aimingMechanisms;
                if (mechanisms == null)
                    continue;
                for (var j = 0; j < mechanisms.Length; j++)
                    AddMechanism(mechanisms[j], "gunner");
            }
        }

        if (Plugin.Cfg.DebugLog.Value)
            LogAxesIfChanged();
    }

    private string _lastAxesLog;

    private void LogAxesIfChanged()
    {
        var parts = new List<string>();
        foreach (var a in _axes)
            parts.Add($"#{a.Handle} {(a.Elevation ? "elevation" : "traverse")} {a.DesignedSpeed:0.#}deg/s ({a.Source})");
        var text = string.Join(", ", parts);
        if (text == _lastAxesLog)
            return;
        _lastAxesLog = text;
        Plugin.Logger.LogInfo($"Arcade turret: controlling {_axes.Count} axes: {text}");
    }

    public void Clear()
    {
        _axes.Clear();
        _vehicle = IntPtr.Zero;
    }

    private void AddMechanism(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase mechanism, string source)
    {
        if (mechanism == null)
            return;
        var turret = mechanism.TryCast<TurretBehaviour>();
        if (turret != null)
        {
            // 回転軸が縦向き（ローカル Y）なら旋回、横向きなら俯仰
            var axis = turret.axis;
            AddAxis(turret.physicsHandle, Mathf.Abs(axis.y) < 0.7f, turret.maxTraverseRate, source);
            return;
        }
        var laying = mechanism.TryCast<LayingDriveBehaviour>();
        if (laying != null)
        {
            AddAxis(laying.elevationHandle, true, laying.elevationInfo.MaxSpeed, source);
            AddAxis(laying.azimuthHandle, false, laying.azimuthInfo.MaxSpeed, source);
        }
    }

    private void AddAxis(int handle, bool elevation, float designedSpeed, string source)
    {
        if (handle < 0)
            return;
        foreach (var a in _axes)
        {
            if (a.Handle == handle)
                return;
        }
        _axes.Add(new ControlledAxis { Handle = handle, Elevation = elevation, DesignedSpeed = designedSpeed, Source = source });
    }

    // ---------------------------------------------------------------- Harmony (RotationAxisPhysicsRegister.Update)

    public void BeforeAxisUpdate()
    {
        _suspended.Clear();
        if (!Active || _axes.Count == 0 || !TryGetAxes(out var data, out var length))
            return;
        for (var i = 0; i < _axes.Count; i++)
        {
            var handle = _axes[i].Handle;
            if (handle >= length)
                continue;
            var axis = data + handle * AxisStride;
            if ((axis[AxisFlags] & ActiveFlag) == 0 || (axis[AxisAimFlags] & LockFlag) != 0)
                continue;
            axis[AxisFlags] &= unchecked((byte)~ActiveFlag);
            _suspended.Add(i);
        }
    }

    public void AfterAxisUpdate(float deltaTime)
    {
        if (_suspended.Count == 0)
            return;
        if (!TryGetAxes(out var data, out var length))
        {
            _suspended.Clear();
            return;
        }
        foreach (var index in _suspended)
        {
            var info = _axes[index];
            if (info.Handle >= length)
                continue;
            var axis = data + info.Handle * AxisStride;
            axis[AxisFlags] |= ActiveFlag;
            Move(axis, info, deltaTime);
        }
        _suspended.Clear();
    }

    private void Move(byte* axis, in ControlledAxis info, float deltaTime)
    {
        ref var angle = ref *(float*)(axis + AxisAngle);
        var min = *(float*)(axis + AxisMin);
        var max = *(float*)(axis + AxisMax);
        var target = Wrap180(*(float*)(axis + AxisTarget));
        var gameSpeed = *(float*)(axis + AxisMaxSpeed);

        // ゲームと同じ規則で目標までの差分を求める（RotationAxisPhysics.Update / ConstrainRotation）
        var fullRotation = max >= 180f && min <= -180f;
        var delta = target - angle;
        var achievable = true;
        if (fullRotation)
        {
            delta = Wrap180(delta);
        }
        else
        {
            var constrained = Mathf.Clamp(angle + delta, min, max) - angle;
            if (Mathf.Abs(delta - constrained) > Epsilon)
            {
                achievable = false;
                if ((axis[AxisAimFlags] & WrappingFlag) != 0)
                {
                    var alternative = delta < 0f ? delta + 360f : delta - 360f;
                    var alternativeConstrained = Mathf.Clamp(angle + alternative, min, max) - angle;
                    if (Mathf.Abs(alternative - alternativeConstrained) <= Epsilon)
                    {
                        constrained = alternativeConstrained;
                        achievable = true;
                    }
                }
            }
            delta = constrained;
        }

        var speed = GetSpeed(info, gameSpeed);
        var step = Mathf.Clamp(delta, -speed * deltaTime, speed * deltaTime);
        angle += step;
        if (fullRotation)
            angle = Wrap180(angle);
        else
            angle = Mathf.Clamp(angle, min, max);

        // 到達したら速度 0（ピタッと止まる）。動作中は一定速度（音の再生用）
        *(float*)(axis + AxisVelocity) = deltaTime > 0f && Mathf.Abs(step) < Mathf.Abs(delta) ? step / deltaTime : 0f;
        *(float*)(axis + AxisAcceleration) = 0f;
        *(float*)(axis + AxisExtrapolation) = 0f;
        if (achievable)
            axis[AxisResult] |= TargetAchievable;
        else
            axis[AxisResult] &= unchecked((byte)~TargetAchievable);

        var apply = *(IntPtr*)(axis + AxisApply);
        if (apply != IntPtr.Zero)
            new Il2CppSystem.Action(apply).Invoke();
    }

    private float GetSpeed(in ControlledAxis info, float gameSpeed)
    {
        if (gameSpeed <= 0f)
            return 0f; // 砲手不在・損傷などでゲーム側が動かさない状態
        var cfg = Plugin.Arcade;
        var crew = info.DesignedSpeed > 0f ? Mathf.Clamp01(gameSpeed / info.DesignedSpeed) : 1f;
        var baseSpeed = Mathf.Max(EraRate(info.Elevation), info.DesignedSpeed * cfg.TurretDesignMultiplier.Value);
        return baseSpeed * crew * cfg.TurretSpeedMultiplier.Value;
    }

    private static bool TryGetAxes(out byte* data, out int length)
    {
        data = null;
        length = 0;
        var array = RotationAxisPhysicsRegister.axes;
        if (array == null)
            return false;
        var p = (byte*)array.Pointer;
        length = *(int*)(p + 0x18);
        data = p + 0x20;
        return length > 0;
    }

    private static float Wrap180(float value)
    {
        value %= 360f;
        if (value > 180f)
            value -= 360f;
        else if (value < -180f)
            value += 360f;
        return value;
    }

    // ---------------------------------------------------------------- era

    private float EraRate(bool elevation)
    {
        var cfg = Plugin.Arcade;
        var years = ParseList(cfg.TurretEraYears.Value);
        var rates = ParseList(elevation ? cfg.ElevationRates.Value : cfg.TraverseRates.Value);
        if (rates.Count == 0)
            return elevation ? 12f : 26f;
        var era = 0;
        while (era < years.Count && _year > years[era])
            era++;
        return rates[Math.Min(era, rates.Count - 1)];
    }

    private static List<float> ParseList(string text)
    {
        var list = new List<float>();
        foreach (var part in (text ?? string.Empty).Split(','))
        {
            if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        }
        return list;
    }

    /// <summary>車両の設計年（VehicleInfo.Date）。取れなければ 1940。</summary>
    private static int GetVehicleYear(GameObject vehicleObject)
    {
        try
        {
            var vehicle = vehicleObject.GetComponent<Vehicle>() ?? vehicleObject.GetComponentInParent<Vehicle>();
            var info = vehicle?.info;
            if (info != null)
            {
                var year = info.Date.Year;
                if (year > 1800 && year < 2200)
                    return year;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Arcade turret: could not read vehicle date: {e.Message}");
        }
        return 1940;
    }
}
