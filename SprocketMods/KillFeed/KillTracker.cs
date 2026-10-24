using System;
using System.Collections.Generic;
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

namespace SprocketMods.KillFeed;

/// <summary>キルログの設定（[KillFeed] セクション）。</summary>
public class KillFeedConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> ShowImmobilized;
    public readonly ConfigEntry<int> MaxEntries;
    public readonly ConfigEntry<float> Duration;
    public readonly ConfigEntry<float> FontSize;
    public readonly ConfigEntry<float> AttributionWindow;

    public KillFeedConfig(ConfigFile cfg)
    {
        const string s = "KillFeed";
        Enabled = cfg.Bind(s, "Enabled", true, "画面右上に撃破のログを表示する");
        ShowImmobilized = cfg.Bind(s, "ShowImmobilized", true, "撃破に加えて、行動不能（走行不能になったが武装は生きている）も表示する");
        MaxEntries = cfg.Bind(s, "MaxEntries", 6, new ConfigDescription("同時に表示する行数", new AcceptableValueRange<int>(1, 15)));
        Duration = cfg.Bind(s, "Duration", 8f, new ConfigDescription("1 行を表示しておく時間（秒）", new AcceptableValueRange<float>(2f, 30f)));
        FontSize = cfg.Bind(s, "FontSize", 18f, new ConfigDescription("文字の大きさ（1080p 基準）", new AcceptableValueRange<float>(10f, 36f)));
        AttributionWindow = cfg.Bind(s, "AttributionWindow", 120f,
            new ConfigDescription("撃破をこの秒数以内に最後に命中弾を与えた車両の戦果にする（火災・誘爆での撃破も含めるため長め）",
                new AcceptableValueRange<float>(5f, 600f)));
    }
}

internal enum KillKind
{
    Destroyed,
    Immobilized,
    /// <summary>転覆・詰まりで乗員が車両を捨てた（Ai/AiDirector.cs）。撃破者は付けない。</summary>
    Abandoned,
}

internal enum Side
{
    Player,
    Friendly,
    Enemy,
    Neutral,
}

internal readonly struct KillEvent
{
    public readonly KillKind Kind;
    public readonly string Attacker;
    public readonly Side AttackerSide;
    public readonly string Victim;
    public readonly Side VictimSide;
    public readonly IntPtr VictimPointer;

    public KillEvent(KillKind kind, string attacker, Side attackerSide, string victim, Side victimSide, IntPtr victimPointer)
    {
        VictimPointer = victimPointer;
        Kind = kind;
        Attacker = attacker;
        AttackerSide = attackerSide;
        Victim = victim;
        VictimSide = victimSide;
    }

    public bool InvolvesPlayer => AttackerSide == Side.Player || VictimSide == Side.Player;
}

/// <summary>
/// 撃破の検出と戦果の判定。
///
/// このビルドのゲームには撃破イベントが無い（KillEventArgs は作られず、KillLogger / KillObjective も中身が無い）ため、自前で行う。
///  - 撃破: 乗員が全員倒れた（VehicleCrewWorkspaceModule の全 operator が Enabled = HealthFraction &gt; 0 でない）とき。
///    ほかに、被弾後に「走行可能（Mobile）」「武装（Armed）」を両方失ったとき、命中直後に車両ごと消えた（誘爆など）とき。
///    ※ 撃破された車両も Armed フラグは残ったままなので、フラグだけでは試合中に撃破を判定できない。
///  - 行動不能: 被弾後に Mobile を失い、その状態が続いたとき（ゲームの対戦モードもこれを「倒した」と数える: ImmobilizeObjective）。
///  - 試合が終わってから（DeathmatchGameMode.missionRunning が false）はリセットで状態が変わるので判定しない。
///  - 戦果: 砲の発射（CannonBehaviour.FireInternal）が返す弾の ID と撃った車両を覚え、ProjectileRegister.projectileHistory
///    （弾 ID → 最新の弾イベント）に HitDamageModelProjectileEvent が出たら、命中したダメージモデルの Rigidbody から被弾車両を求める。
///    撃破時点から AttributionWindow 秒以内に最後に貫通弾（無ければ命中弾）を与えた車両を撃破者とする。
/// </summary>
internal sealed class KillTracker
{
    public static KillTracker Instance { get; private set; }

    private const float ScanInterval = 0.25f;
    private const float VehicleSearchInterval = 1f;
    private const float SpawnGrace = 3f;
    private const float ProjectileTimeout = 20f;
    private const float VanishKillWindow = 10f;
    private const float ImmobilizedConfirmTime = 1.5f;
    private const float RemobilizedResetTime = 10f;
    private const int MassVanishCount = 3;
    private const int MaxPendingProjectiles = 2048;

    private sealed class VehicleRecord
    {
        public IntPtr Pointer;
        public IntPtr Body;
        public string Name;
        public TeamID Team;
        public float FirstSeen;
        public float LastSeen;
        public bool HadMobile;
        public bool HadArmed;
        public bool ImmobilizedLogged;
        public bool KnockedOut;
        public bool HadCrew;
        public float ImmobileSince = float.NaN;
        public float MobileSince = float.NaN;
        public VehicleCrewWorkspaceModule Crew;
        public bool CrewSearched;
        public VehicleRecord LastHitBy;
        public float LastHitTime = float.NegativeInfinity;
        public VehicleRecord LastPenetratedBy;
        public float LastPenetrationTime = float.NegativeInfinity;
    }

    private sealed class PendingShot
    {
        public VehicleRecord Shooter;
        public float Time;
        public IntPtr LastEvent;
    }

    private readonly Dictionary<IntPtr, VehicleRecord> _byVehicle = new();
    private readonly Dictionary<IntPtr, VehicleRecord> _byBody = new();
    private readonly Dictionary<IntPtr, IntPtr> _weaponToVehicle = new();
    private readonly Dictionary<int, PendingShot> _shots = new();
    private readonly List<int> _finishedShots = new();
    private readonly List<VehicleBehaviour> _vehicles = new();
    private readonly List<IntPtr> _vanished = new();

    private VehicleController _controller;
    private float _nextControllerSearch;
    private float _nextVehicleSearch;
    private float _nextScan;
    private float _nextErrorLog;
    private DeathmatchGameMode _gameMode;
    private float _nextGameModeSearch;
    private bool _wasRunning = true;

    public event Action<KillEvent> Killed;

    public KillTracker() => Instance = this;

    private static KillFeedConfig Cfg => Plugin.KillFeed;

    // ------------------------------------------------------------------ 発射（CannonBehaviour.FireInternal の後）

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (!Cfg.Enabled.Value || weapon == null || projectileId <= 0)
            return;
        var shooter = GetShooter(weapon);
        if (shooter == null)
            return;
        if (_shots.Count >= MaxPendingProjectiles)
            _shots.Clear();
        _shots[projectileId] = new PendingShot { Shooter = shooter, Time = Time.time };
    }

    private VehicleRecord GetShooter(WeaponBehaviour weapon)
    {
        if (!_weaponToVehicle.TryGetValue(weapon.Pointer, out var vehiclePtr))
        {
            var transform = weapon.Transform;
            var vehicle = transform != null ? transform.GetComponentInParent<VehicleBehaviour>() : null;
            vehiclePtr = vehicle != null ? vehicle.Pointer : IntPtr.Zero;
            if (_weaponToVehicle.Count > 1024)
                _weaponToVehicle.Clear();
            _weaponToVehicle[weapon.Pointer] = vehiclePtr;
            if (vehicle != null && !_byVehicle.ContainsKey(vehiclePtr))
                Track(vehicle, Time.time);
        }
        return vehiclePtr != IntPtr.Zero && _byVehicle.TryGetValue(vehiclePtr, out var record) ? record : null;
    }

    /// <summary>乗員が車両を放棄した（撃破扱いにして、キルログに「放棄」と出す）。</summary>
    public void ReportAbandoned(VehicleBehaviour vehicle)
    {
        if (!Cfg.Enabled.Value || vehicle == null || vehicle.WasCollected)
            return;
        if (!_byVehicle.TryGetValue(vehicle.Pointer, out var record))
            record = Track(vehicle, Time.time);
        if (record.KnockedOut)
            return;
        record.KnockedOut = true;
        Report(KillKind.Abandoned, record, Time.time, GetPlayerBody(), "abandoned");
    }

    /// <summary>キルログで撃破と判定した車両か。</summary>
    public bool IsKnockedOut(IntPtr vehicle) => _byVehicle.TryGetValue(vehicle, out var record) && record.KnockedOut;

    // ------------------------------------------------------------------ 毎フレーム

    public void Update()
    {
        if (!Cfg.Enabled.Value || Time.timeScale <= 0f)
            return;
        try
        {
            UpdateShots();
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + ScanInterval;
                ScanVehicles();
            }
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Kill feed failed: {e}");
            }
        }
    }

    /// <summary>撃った弾の最新イベントを見て、車両への命中を記録する。</summary>
    private void UpdateShots()
    {
        if (_shots.Count == 0)
            return;
        var register = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>();
        var history = register?.projectileHistory;
        if (history == null)
            return;

        var now = Time.time;
        _finishedShots.Clear();
        foreach (var pair in _shots)
        {
            var shot = pair.Value;
            if (now - shot.Time > ProjectileTimeout)
            {
                _finishedShots.Add(pair.Key);
                continue;
            }
            if (!history.TryGetValue(pair.Key, out var latest) || latest == null || latest.Pointer == shot.LastEvent)
                continue;

            shot.LastEvent = latest.Pointer;
            var hit = latest.TryCast<HitDamageModelProjectileEvent>();
            if (hit != null)
                RecordHit(shot.Shooter, hit, now);
            if (latest.Terminal)
                _finishedShots.Add(pair.Key);
        }
        foreach (var id in _finishedShots)
            _shots.Remove(id);
    }

    private void RecordHit(VehicleRecord shooter, HitDamageModelProjectileEvent hit, float now)
    {
        var body = hit.DamageModel?.RigidBody;
        if (body == null || !_byBody.TryGetValue(body.Pointer, out var victim) || victim == shooter)
            return;
        victim.LastHitBy = shooter;
        victim.LastHitTime = now;
        if (hit.Type == HitDamageModelProjectileEvent.HitType.Penetrated)
        {
            victim.LastPenetratedBy = shooter;
            victim.LastPenetrationTime = now;
        }
    }

    // ------------------------------------------------------------------ 車両の状態

    private void ScanVehicles()
    {
        var now = Time.time;
        if (Time.unscaledTime >= _nextVehicleSearch)
        {
            _nextVehicleSearch = Time.unscaledTime + VehicleSearchInterval;
            _vehicles.Clear();
            var found = Object.FindObjectsByType(Il2CppType.Of<VehicleBehaviour>(), FindObjectsSortMode.None);
            if (found != null)
            {
                foreach (var obj in found)
                {
                    var vehicle = obj?.TryCast<VehicleBehaviour>();
                    if (vehicle != null)
                        _vehicles.Add(vehicle);
                }
            }
        }

        var running = IsMatchRunning();
        if (running != _wasRunning)
        {
            _wasRunning = running;
            Plugin.Logger.LogInfo($"Kill feed: match {(running ? "running" : "not running")}");
        }

        var player = GetPlayerBody();
        foreach (var vehicle in _vehicles)
        {
            if (vehicle == null || vehicle.WasCollected)
                continue;
            if (!_byVehicle.TryGetValue(vehicle.Pointer, out var record))
                record = Track(vehicle, now);
            record.LastSeen = now;
            if (running)
                Check(vehicle, record, now, player);
        }

        // 消えた車両: 直前に命中弾を受けていれば撃破（誘爆など）、そうでなければリセットなどで消えただけ
        _vanished.Clear();
        foreach (var pair in _byVehicle)
        {
            if (now - pair.Value.LastSeen > VehicleSearchInterval * 2.5f)
                _vanished.Add(pair.Key);
        }
        // 一度に何両も消えるのは試合のリセットなので撃破にしない
        var reportVanished = running && _vanished.Count < MassVanishCount;
        foreach (var ptr in _vanished)
        {
            var record = _byVehicle[ptr];
            if (reportVanished && !record.KnockedOut && now - record.LastHitTime <= VanishKillWindow)
                Report(KillKind.Destroyed, record, now, player, "vanished");
            _byVehicle.Remove(ptr);
            if (record.Body != IntPtr.Zero && _byBody.TryGetValue(record.Body, out var byBody) && byBody == record)
                _byBody.Remove(record.Body);
        }
    }

    private VehicleRecord Track(VehicleBehaviour vehicle, float now)
    {
        var record = new VehicleRecord
        {
            Pointer = vehicle.Pointer,
            Name = GetName(vehicle),
            FirstSeen = now,
            LastSeen = now,
        };
        try
        {
            record.Team = vehicle.ID.TeamID;
        }
        catch (Exception)
        {
            record.Team = TeamID.None;
        }
        var body = vehicle.Rigidbody;
        if (body != null)
        {
            record.Body = body.Pointer;
            _byBody[record.Body] = record;
        }
        _byVehicle[record.Pointer] = record;
        return record;
    }

    private void Check(VehicleBehaviour vehicle, VehicleRecord record, float now, IntPtr player)
    {
        var flags = vehicle.Flags;
        var mobile = (flags & VehicleFlags.Mobile) != 0;
        var armed = (flags & VehicleFlags.Armed) != 0;
        record.HadMobile |= mobile;
        record.HadArmed |= armed;
        var crew = CountCrew(vehicle, record, out var crewTotal);
        record.HadCrew |= crew > 0;
        if (record.KnockedOut || now - record.FirstSeen < SpawnGrace)
            return;

        // 乗員全滅（試合中の撃破の本命）
        if (record.HadCrew && crewTotal > 0 && crew == 0)
        {
            record.KnockedOut = true;
            Report(KillKind.Destroyed, record, now, player, "crew");
            return;
        }

        // 以降は被弾が原因のときだけ（エンジン停止やリセットでフラグが変わっても出さない）
        var recentlyHit = now - record.LastHitTime <= Cfg.AttributionWindow.Value;
        var lostMobility = record.HadMobile && !mobile;
        var lostArms = record.HadArmed && !armed;
        if (recentlyHit && (record.HadMobile || record.HadArmed)
            && (lostMobility || !record.HadMobile) && (lostArms || !record.HadArmed))
        {
            record.KnockedOut = true;
            Report(KillKind.Destroyed, record, now, player, "flags");
            return;
        }

        if (lostMobility)
        {
            record.MobileSince = float.NaN;
            if (float.IsNaN(record.ImmobileSince))
                record.ImmobileSince = now;
            if (!record.ImmobilizedLogged && recentlyHit && now - record.ImmobileSince >= ImmobilizedConfirmTime)
            {
                record.ImmobilizedLogged = true;
                if (Cfg.ShowImmobilized.Value)
                    Report(KillKind.Immobilized, record, now, player, "immobile");
            }
        }
        else
        {
            record.ImmobileSince = float.NaN;
            if (float.IsNaN(record.MobileSince))
                record.MobileSince = now;
            // 修理などで走行可能に戻って、しばらくたったら再び行動不能を出せるようにする（フラグのばたつき対策）
            if (record.ImmobilizedLogged && now - record.MobileSince >= RemobilizedResetTime)
                record.ImmobilizedLogged = false;
        }
    }

    /// <summary>動ける乗員の数。InjectedOperator.Enabled は乗員の HealthFraction &gt; 0。</summary>
    private static int CountCrew(VehicleBehaviour vehicle, VehicleRecord record, out int total)
    {
        total = 0;
        if (!record.CrewSearched || record.Crew == null || record.Crew.WasCollected)
        {
            record.CrewSearched = true;
            record.Crew = null;
            var modules = vehicle.modules;
            if (modules != null)
            {
                for (var i = 0; i < modules.Count; i++)
                {
                    var crewModule = modules[i]?.TryCast<VehicleCrewWorkspaceModule>();
                    if (crewModule != null)
                    {
                        record.Crew = crewModule;
                        break;
                    }
                }
            }
        }
        var operators = record.Crew?.operators;
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

    /// <summary>対戦モード（カスタムバトル）なら試合中かどうか。それ以外のモードでは常に true。</summary>
    private bool IsMatchRunning()
    {
        if (_gameMode != null && _gameMode.WasCollected)
            _gameMode = null;
        if (_gameMode == null && Time.unscaledTime >= _nextGameModeSearch)
        {
            _nextGameModeSearch = Time.unscaledTime + 2f;
            _gameMode = Object.FindAnyObjectByType(Il2CppType.Of<DeathmatchGameMode>())?.TryCast<DeathmatchGameMode>();
        }
        if (_gameMode == null)
            return true;
        return _gameMode.missionRunning && _gameMode.gameState == DeathmatchGameMode.ModeState.Running;
    }

    private void Report(KillKind kind, VehicleRecord victim, float now, IntPtr player, string reason)
    {
        var window = Cfg.AttributionWindow.Value;
        VehicleRecord attacker = null;
        if (kind == KillKind.Abandoned)
            attacker = null;
        else if (victim.LastPenetratedBy != null && now - victim.LastPenetrationTime <= window)
            attacker = victim.LastPenetratedBy;
        else if (victim.LastHitBy != null && now - victim.LastHitTime <= window)
            attacker = victim.LastHitBy;

        var playerTeam = GetPlayerTeam(player);
        var e = new KillEvent(kind,
            attacker?.Name, attacker != null ? SideOf(attacker, player, playerTeam) : Side.Neutral,
            victim.Name, SideOf(victim, player, playerTeam), victim.Pointer);
        Plugin.Logger.LogInfo($"Kill feed: {kind} {victim.Name} by {attacker?.Name ?? "(unknown)"} ({reason})");
        Killed?.Invoke(e);
    }

    private TeamID GetPlayerTeam(IntPtr player)
    {
        if (player != IntPtr.Zero && _byBody.TryGetValue(player, out var record))
            return record.Team;
        return TeamID.None;
    }

    private static Side SideOf(VehicleRecord record, IntPtr player, TeamID playerTeam)
    {
        if (player != IntPtr.Zero && record.Body == player)
            return Side.Player;
        if (playerTeam == TeamID.None || record.Team == TeamID.None)
            return Side.Neutral;
        return record.Team == playerTeam ? Side.Friendly : Side.Enemy;
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

    internal static string GetName(VehicleBehaviour vehicle)
    {
        try
        {
            var info = (vehicle.GetComponent<Vehicle>() ?? vehicle.GetComponentInParent<Vehicle>())?.info;
            var name = info?.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        catch (Exception)
        {
            // 名前が読めない車両
        }
        var objectName = vehicle.gameObject.name;
        return string.IsNullOrWhiteSpace(objectName) ? "?" : objectName.Replace("(Clone)", string.Empty).Trim();
    }
}
