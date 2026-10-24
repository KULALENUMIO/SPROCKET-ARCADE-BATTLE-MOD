using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.WeaponFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods;

/// <summary>砲弾定義から読み出した、弾道・貫通計算に必要な値。</summary>
internal sealed class ShellInfo
{
    public int TypeId;
    public ProjectileDefinition Def;
    public string Name;
    public float Mass;
    public float Diameter;
    public float Length;
    public float MinSpeed;
    public float Lifetime;

    public bool HasArmourPiercing;
    public ushort PenetratorConstant;
    public bool HasExplosive;
    public bool HasPropellant;
    public float PropellantLength;

    // ProjectileDefinition.CalculateDragForce を 1 m/s 刻みで前計算した表（毎ステップの interop 呼び出しを避ける）
    private const int DragTableMaxSpeed = 3000;
    private float[] _dragTable;

    public float DragForce(float speed)
    {
        _dragTable ??= BuildDragTable();
        if (speed >= DragTableMaxSpeed)
            return Def.CalculateDragForce(speed);
        var i = (int)speed;
        var f = speed - i;
        return _dragTable[i] + (_dragTable[i + 1] - _dragTable[i]) * f;
    }

    private float[] BuildDragTable()
    {
        var table = new float[DragTableMaxSpeed + 1];
        for (var s = 0; s <= DragTableMaxSpeed; s++)
            table[s] = Def.CalculateDragForce(s);
        return table;
    }
}

/// <summary>プレイヤーが現在操作している主砲の発射条件。</summary>
internal struct WeaponState
{
    public Camera Camera;
    public Rigidbody OwnBody;
    public Vector3 Origin;
    public Vector3 Direction;
    public Vector3 InheritedVelocity;
    public float MuzzleVelocity;
    public ShellInfo Shell;
}

/// <summary>
/// ゲーム内オブジェクトの取得。
/// VehicleController → activeGunner(Gunner) → weapon(CannonBehaviour) → 装填中の弾種(ProjectileDefinition)
/// </summary>
internal sealed class GameAccess
{
    // ArmourPiercingProjectileFunction.HitDamageModel が PenetratorInfo に入れる固定値
    public const float MaxNormalizationAngle = 5f;
    private const float MetresToMillimetres = 1000f;

    private VehicleController _controller;
    private float _nextControllerSearch;

    private readonly Dictionary<int, ShellInfo> _shellCache = new();
    private int _lastTypeId = int.MinValue;
    private IntPtr _lastWeapon;
    private float _cachedMuzzleVelocity;
    private int _cachedMuzzleTypeId = int.MinValue;
    private IntPtr _cachedMuzzleWeapon;

    public bool TryGetWeaponState(out WeaponState state)
    {
        state = default;

        var vc = GetController();
        if (vc == null)
            return false;

        var vehicle = vc.ControlledVehicle;
        if (vehicle == null)
            return false;

        var gunnerControl = vc.activeGunner;
        if (gunnerControl == null)
            return false;
        var gunner = gunnerControl.TryCast<Gunner>();
        var trigger = gunner?.weapon;
        var weapon = trigger?.TryCast<CannonBehaviour>();
        if (weapon == null)
            return false;

        var typeId = GetProjectileTypeId(weapon);
        if (typeId == int.MinValue)
            return false;
        var shell = GetShellInfo(typeId);
        if (shell == null)
            return false;

        var camera = vc.ActiveCamera;
        if (camera == null)
            camera = Camera.main;
        if (camera == null)
            return false;

        state.Camera = camera;
        state.OwnBody = vehicle.Rigidbody;
        state.Origin = weapon.ShellSpawnPosition;
        state.Direction = GetFireDirection(weapon);
        state.InheritedVelocity = weapon.InheritedVelocity;
        state.MuzzleVelocity = GetMuzzleVelocity(weapon, shell);
        state.Shell = shell;

        if (Plugin.Cfg.DebugLog.Value && (typeId != _lastTypeId || weapon.Pointer != _lastWeapon))
        {
            _lastTypeId = typeId;
            _lastWeapon = weapon.Pointer;
            Plugin.Logger.LogInfo(
                $"Weapon shell '{shell.Name}' id={typeId} mass={shell.Mass:0.###}kg dia={shell.Diameter * 1000f:0.#}mm " +
                $"len={shell.Length * 1000f:0.#}mm AP={shell.HasArmourPiercing}(k={shell.PenetratorConstant}) HE={shell.HasExplosive} " +
                $"propellant={shell.PropellantLength:0.#} muzzle={state.MuzzleVelocity:0.#}m/s minSpeed={shell.MinSpeed} life={shell.Lifetime}");
        }
        return true;
    }

    private VehicleController GetController()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            var found = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>());
            _controller = found?.TryCast<VehicleController>();
        }
        return _controller;
    }

    /// <summary>装填済みの弾種。未装填（装填中）なら次に装填される弾種。</summary>
    private static int GetProjectileTypeId(WeaponBehaviour weapon)
    {
        var invalid = ProjectileTypeID.Invalid.Value;
        var loaded = weapon.LoadedProjectileTypeID.Value;
        if (loaded != invalid)
            return loaded;

        var loadTask = weapon.LoadTask?.TryCast<Sprocket.Vehicles.Weapons.LoadTask>();
        if (loadTask != null)
        {
            var requested = loadTask.Request.TypeID.Value;
            if (requested != invalid)
                return requested;
        }
        return int.MinValue;
    }

    private ShellInfo GetShellInfo(int typeId)
    {
        if (_shellCache.TryGetValue(typeId, out var cached) && cached.Def != null && !cached.Def.WasCollected)
            return cached;

        var register = IProjectileTypeRegister.Instance;
        if (register == null)
            return null;
        if (!register.TryGet(new ProjectileTypeID(typeId), out var type) || type == null)
            return null;
        var def = type.TryCast<ProjectileDefinition>();
        if (def == null)
            return null;

        var info = new ShellInfo
        {
            TypeId = typeId,
            Def = def,
            Name = def.Name,
            Mass = def.Mass,
            Diameter = def.Diameter,
            Length = def.Length,
            MinSpeed = def.MinSpeed,
            Lifetime = def.Lifetime,
        };
        ReadFunctions(info, def.Functions);
        ReadFunctions(info, def.LaunchFunctions);

        _shellCache[typeId] = info;
        return info;
    }

    private static void ReadFunctions(ShellInfo info, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ProjectileFunction> functions)
    {
        if (functions == null)
            return;
        for (var i = 0; i < functions.Length; i++)
        {
            var f = functions[i];
            if (f == null)
                continue;
            var ap = f.TryCast<ArmourPiercingProjectileFunction>();
            if (ap != null)
            {
                info.HasArmourPiercing = true;
                info.PenetratorConstant = ap.PenetratorConstant;
                continue;
            }
            var propellant = f.TryCast<DetonationPropellantProjectileFunction>();
            if (propellant != null)
            {
                info.HasPropellant = true;
                info.PropellantLength = propellant.PropellantLength;
                continue;
            }
            if (f.TryCast<ExplosiveProjectileFunction>() != null)
                info.HasExplosive = true;
        }
    }

    /// <summary>
    /// CannonBehaviour.FireInternal と同じく、最後の砲身トランスフォームの forward を使う。
    /// barrels は RecoiledBarrel(Transform, float) の値型配列なので、要素は 16 バイト間隔で並ぶ。
    /// </summary>
    private static unsafe Vector3 GetFireDirection(CannonBehaviour weapon)
    {
        try
        {
            var barrels = weapon.barrels;
            if (barrels != null)
            {
                var array = barrels.Pointer;
                var length = *(int*)(array + 0x18);
                if (length > 0)
                {
                    var transformPtr = *(IntPtr*)(array + 0x20 + (length - 1) * 16);
                    if (transformPtr != IntPtr.Zero)
                        return new Transform(transformPtr).forward;
                }
            }
        }
        catch (Exception)
        {
            // フォールバック
        }
        return weapon.Transform.forward;
    }

    /// <summary>
    /// DetonationPropellantProjectileFunction.AggregateLaunchInfo と同じ式で初速を求める。
    /// CalculateFireInfo(砲身長[mm], 口径[mm], 弾長[mm], 装薬長, 最大圧力)
    /// </summary>
    private float GetMuzzleVelocity(CannonBehaviour weapon, ShellInfo shell)
    {
        if (_cachedMuzzleTypeId == shell.TypeId && _cachedMuzzleWeapon == weapon.Pointer)
            return _cachedMuzzleVelocity;

        var velocity = 0f;
        if (shell.HasPropellant)
        {
            var launch = weapon.projectileLaunchInfo;
            var fireInfo = ShellProperties.CalculateFireInfo(
                launch.BarrelLength * MetresToMillimetres,
                shell.Diameter * MetresToMillimetres,
                shell.Length * MetresToMillimetres,
                shell.PropellantLength,
                launch.MaxPressure);
            velocity = fireInfo.MuzzleVelocity;
        }
        if (!(velocity > 0f))
        {
            var cannon = weapon.mount?.TryCast<Cannon>();
            if (cannon != null)
                velocity = cannon.muzzleVelocity;
        }

        _cachedMuzzleVelocity = velocity;
        _cachedMuzzleTypeId = shell.TypeId;
        _cachedMuzzleWeapon = weapon.Pointer;
        return velocity;
    }
}
