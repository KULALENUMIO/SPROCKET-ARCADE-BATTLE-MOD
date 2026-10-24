using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using SprocketMods.Hits;
using SprocketMods.KillFeed;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Targets;

/// <summary>照準した車両の情報の設定（[TargetInfo] セクション）。</summary>
public class TargetInfoConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> ShowFriendly;
    public readonly ConfigEntry<bool> ShowWrecks;
    public readonly ConfigEntry<bool> ShowName;
    public readonly ConfigEntry<bool> ShowStatus;
    public readonly ConfigEntry<bool> ShowDistance;
    public readonly ConfigEntry<bool> ShowCrew;
    public readonly ConfigEntry<bool> ShowCrewDetails;
    public readonly ConfigEntry<bool> ShowEngine;
    public readonly ConfigEntry<bool> ShowDamagedModules;
    public readonly ConfigEntry<bool> ShowFire;
    public readonly ConfigEntry<float> FontSize;
    public readonly ConfigEntry<float> MaxDistance;

    public TargetInfoConfig(ConfigFile cfg)
    {
        const string s = "TargetInfo";
        Enabled = cfg.Bind(s, "Enabled", true, "照準の先にある車両の名前・敵味方・撃破済みか・乗員・エンジンなどの状態を照準の上に表示する");
        ShowFriendly = cfg.Bind(s, "ShowFriendly", true, "味方の車両（残骸を含む）も表示する");
        ShowWrecks = cfg.Bind(s, "ShowWrecks", true, "撃破済みの車両（残骸）も表示する");
        ShowName = cfg.Bind(s, "ShowName", true, "車両名を表示する");
        ShowStatus = cfg.Bind(s, "ShowStatus", true, "稼働中 / 行動不能 / 撃破済み を表示する");
        ShowDistance = cfg.Bind(s, "ShowDistance", true, "車両までの距離を表示する");
        ShowCrew = cfg.Bind(s, "ShowCrew", true, "乗員の生存数（4/5 など）を表示する");
        ShowCrewDetails = cfg.Bind(s, "ShowCrewDetails", true, "戦死した乗員の役割と負傷者の数を表示する");
        ShowEngine = cfg.Bind(s, "ShowEngine", true, "エンジンの体力を常に表示する");
        ShowDamagedModules = cfg.Bind(s, "ShowDamagedModules", true, "損傷している主なモジュール（主砲・履帯・弾薬庫など）を表示する");
        ShowFire = cfg.Bind(s, "ShowFire", true, "火災を表示する");
        FontSize = cfg.Bind(s, "FontSize", 16f, new ConfigDescription("文字の大きさ（1080p 基準）", new AcceptableValueRange<float>(10f, 36f)));
        MaxDistance = cfg.Bind(s, "MaxDistance", 3000f,
            new ConfigDescription("これより遠い車両は表示しない（m）", new AcceptableValueRange<float>(100f, 10000f)));
    }
}

internal enum TargetState
{
    Operational,
    Immobilized,
    KnockedOut,
}

/// <summary>乗員と主なモジュールの今の状態（HitTracker.Inspect が埋める）。</summary>
internal sealed class TargetDetails
{
    public int CrewAlive;
    public int CrewTotal;
    public int CrewWounded;
    public readonly List<string> DeadCrew = new();
    public readonly List<ModuleDamage> Modules = new();

    public void Clear()
    {
        CrewAlive = 0;
        CrewTotal = 0;
        CrewWounded = 0;
        DeadCrew.Clear();
        Modules.Clear();
    }
}

/// <summary>照準した車両 1 両の表示内容。</summary>
internal sealed class TargetSnapshot
{
    public IntPtr Vehicle;
    public string Name;
    public Side Side;
    public TargetState State;
    public bool OnFire;
    public float Distance;
    public readonly TargetDetails Details = new();
    public int Version;
}

/// <summary>
/// 照準（画面の中央）の先にある車両を探し、その状態を読む。
///  - 車両: DamageModel.models の CompoundStructure を画面中央からのレイで判定（自車は除く）。
///    手前に地形・建物（ProjectilesMain.ProjectileLayerMask の物理コライダー）があれば見えていないとする。
///  - 撃破: キルログの判定、または乗員全滅。行動不能: VehicleFlags.Mobile を失った。
///  - 乗員・モジュール: HitTracker.Inspect（体力プールを直接読む）。
///  照準から外れても少しの間（Linger 秒）は残して、ちらつかないようにする。
/// </summary>
internal sealed class TargetInfoTracker
{
    public static TargetInfoTracker Instance { get; private set; }

    private const float PickInterval = 0.1f;
    private const float DetailsInterval = 0.25f;
    private const float Linger = 0.35f;

    private readonly Dictionary<IntPtr, CompoundStructure> _wrappers = new();
    private readonly Dictionary<IntPtr, VehicleBehaviour> _vehicles = new();
    private readonly HashSet<IntPtr> _hadMobile = new();

    private VehicleController _controller;
    private float _nextControllerSearch;
    private int _layerMask = Physics.DefaultRaycastLayers;
    private bool _maskFound;
    private float _nextMaskSearch;
    private float _nextPick;
    private float _nextDetails;
    private float _lastSeen = float.NegativeInfinity;
    private VehicleBehaviour _target;
    private TargetSnapshot _snapshot;
    private float _nextErrorLog;

    public TargetInfoTracker() => Instance = this;

    private static TargetInfoConfig Cfg => Plugin.TargetInfo;

    /// <summary>表示中の車両（無ければ null）。</summary>
    public TargetSnapshot Current { get; private set; }

    /// <summary>最後に照準に入っていた時刻（表示のフェード用）。</summary>
    public float LastSeen => _lastSeen;

    public void Update()
    {
        if (!Cfg.Enabled.Value || Time.timeScale <= 0f)
        {
            Current = null;
            return;
        }
        try
        {
            var now = Time.unscaledTime;
            if (now >= _nextPick)
            {
                _nextPick = now + PickInterval;
                Pick(now);
            }
            if (_target != null && (_target.WasCollected || now - _lastSeen > Linger))
            {
                _target = null;
                _snapshot = null;
            }
            if (_target != null && now >= _nextDetails)
            {
                _nextDetails = now + DetailsInterval;
                Refresh();
            }
            Current = _target != null ? _snapshot : null;
        }
        catch (Exception e)
        {
            Current = null;
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Target info failed: {e}");
            }
        }
    }

    // ------------------------------------------------------------------ 照準の先の車両

    private void Pick(float now)
    {
        var controller = GetController();
        var player = controller?.ControlledVehicle;
        var camera = controller?.ActiveCamera;
        if (camera == null)
            camera = Camera.main;
        if (player == null || camera == null)
        {
            _target = null;
            return;
        }
        var playerBody = player.Rigidbody;
        var playerBodyPtr = playerBody != null ? playerBody.Pointer : IntPtr.Zero;

        var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        var origin = ray.origin;
        var dir = ray.direction;
        var best = Cfg.MaxDistance.Value + 50f; // カメラは自車の後ろにあることがある
        CompoundStructure hit = null;

        var models = DamageModel.models;
        if (models != null)
        {
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i];
                if (model == null)
                    continue;
                if (!_wrappers.TryGetValue(model.Pointer, out var cs) || cs == null || cs.WasCollected)
                {
                    cs = model.TryCast<CompoundStructure>();
                    if (_wrappers.Count > 256)
                        _wrappers.Clear();
                    _wrappers[model.Pointer] = cs;
                }
                if (cs == null)
                    continue;
                var body = cs.RigidBody;
                if (body == null || body.Pointer == playerBodyPtr)
                    continue;
                cs.GetWorldBounds(out var min, out var max);
                if (!RayHitsBox(origin, dir, best, min, max))
                    continue;
                if (cs.Raycast(origin, dir, best, out float distance) && distance >= 0f && distance < best)
                {
                    best = distance;
                    hit = cs;
                }
            }
        }

        VehicleBehaviour vehicle = null;
        if (hit != null && !Occluded(origin, dir, best))
        {
            var point = origin + dir * best;
            var from = playerBody != null ? playerBody.position : origin;
            if ((point - from).magnitude <= Cfg.MaxDistance.Value)
                vehicle = GetVehicle(hit.RigidBody);
        }
        if (vehicle == null)
            return; // Linger の間は前の車両を残す

        var team = vehicle.ID.TeamID;
        var playerVehicle = player.TryCast<VehicleBehaviour>() ?? GetVehicle(playerBody);
        var playerTeam = playerVehicle != null ? playerVehicle.ID.TeamID : TeamID.None;
        if (!Cfg.ShowFriendly.Value && team != TeamID.None && team == playerTeam)
            return;

        _lastSeen = now;
        if (_target == null || _target.Pointer != vehicle.Pointer)
        {
            _target = vehicle;
            _snapshot = new TargetSnapshot
            {
                Vehicle = vehicle.Pointer,
                Name = KillTracker.GetName(vehicle),
                Side = team == TeamID.None || playerTeam == TeamID.None ? Side.Neutral
                    : team == playerTeam ? Side.Friendly : Side.Enemy,
            };
            _nextDetails = 0f;
        }
        var target = (origin + dir * best) - (playerBody != null ? playerBody.position : origin);
        _snapshot.Distance = target.magnitude;
    }

    /// <summary>車両より手前に地形・建物があるか（車両のコライダーは無視）。</summary>
    private bool Occluded(Vector3 origin, Vector3 dir, float distance)
    {
        var hits = Physics.RaycastAll(origin, dir, Mathf.Max(0f, distance - 0.25f), GetLayerMask(), QueryTriggerInteraction.Ignore);
        if (hits == null)
            return false;
        for (var i = 0; i < hits.Length; i++)
        {
            var collider = hits[i].collider;
            if (collider == null)
                continue;
            var body = collider.attachedRigidbody;
            if (body != null && body.GetComponentInParent<Vehicle>() != null)
                continue;
            return true;
        }
        return false;
    }

    private VehicleBehaviour GetVehicle(Rigidbody body)
    {
        if (body == null)
            return null;
        if (_vehicles.TryGetValue(body.Pointer, out var cached) && cached != null && !cached.WasCollected)
            return cached;
        var vehicle = body.GetComponent<VehicleBehaviour>() ?? body.GetComponentInParent<VehicleBehaviour>()
            ?? body.GetComponentInChildren<VehicleBehaviour>();
        if (_vehicles.Count > 256)
            _vehicles.Clear();
        _vehicles[body.Pointer] = vehicle;
        return vehicle;
    }

    // ------------------------------------------------------------------ 状態

    private void Refresh()
    {
        var snapshot = _snapshot;
        var vehicle = _target;
        HitTracker.Instance?.Inspect(vehicle, snapshot.Details);

        var flags = vehicle.Flags;
        var mobile = (flags & VehicleFlags.Mobile) != 0;
        if (mobile)
            _hadMobile.Add(vehicle.Pointer);
        var details = snapshot.Details;
        var crewDead = details.CrewTotal > 0 && details.CrewAlive == 0;
        var knockedOut = crewDead || (KillTracker.Instance?.IsKnockedOut(vehicle.Pointer) ?? false);
        snapshot.State = knockedOut ? TargetState.KnockedOut
            : !mobile && _hadMobile.Contains(vehicle.Pointer) ? TargetState.Immobilized
            : TargetState.Operational;
        snapshot.OnFire = (flags & VehicleFlags.OnFire) != 0;
        snapshot.Version++;
        if (_hadMobile.Count > 512)
            _hadMobile.Clear();
    }

    // ------------------------------------------------------------------ 補助

    private VehicleController GetController()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        return _controller;
    }

    /// <summary>ProjectilesMain.ProjectileLayerMask（砲弾が当たる物理レイヤー）。</summary>
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
            }
        }
        return _layerMask;
    }

    private static bool RayHitsBox(Vector3 o, Vector3 d, float length, Vector3 min, Vector3 max)
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
