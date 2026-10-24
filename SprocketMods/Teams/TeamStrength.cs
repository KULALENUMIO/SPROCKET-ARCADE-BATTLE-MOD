using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Teams;

/// <summary>勢力ゲージの設定（[TeamBar] セクション）。</summary>
public class TeamBarConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<bool> Segmented;

    public TeamBarConfig(ConfigFile cfg)
    {
        const string s = "TeamBar";
        Enabled = cfg.Bind(s, "Enabled", true, "画面上部に味方（左・青）と敵（右・赤）の残りの車両数のゲージを表示する（カスタムバトル・シナリオ）");
        Segmented = cfg.Bind(s, "Segmented", true, "ゲージを車両 1 両ずつの目盛りに区切る（多いときは 1 本のバー）");
    }
}

/// <summary>ミニマップに出す車両 1 両。</summary>
internal readonly struct MapUnit
{
    public readonly Vector3 Position;
    public readonly Vector3 Forward;
    public readonly KillFeed.Side Side;
    public readonly bool Alive;

    public MapUnit(Vector3 position, Vector3 forward, KillFeed.Side side, bool alive)
    {
        Position = position;
        Forward = forward;
        Side = side;
        Alive = alive;
    }
}

/// <summary>
/// 勢力ゲージとミニマップ用に、陣営ごとの車両の総数と残り（撃破されていない）数を数える。
///  - 車両: シーンの VehicleBehaviour。陣営は VehicleBehaviour.ID.TeamID。自車の陣営が味方、それ以外（None を除く）が敵。
///  - 撃破: キルログの判定（KillTracker.IsKnockedOut）か、乗員全滅。撃破後に消えた車両・命中後に消えた車両も撃破として残す。
///  - 総数: 試合中に見た車両の数（途中で増えた車両も足す）。一度に半分以上が消えたら試合のリセット・退出とみなして数え直す。
/// ゲージが 0 になっても試合は終わらない（目安）。
/// </summary>
internal sealed class TeamStrengthTracker
{
    public static TeamStrengthTracker Instance { get; private set; }

    private const float SearchInterval = 1f;
    private const float StatusInterval = 0.25f;

    private sealed class Unit
    {
        public VehicleBehaviour Vehicle;
        public TeamID Team;
        public bool Alive = true;
        public bool Present = true;
        public VehicleCrewWorkspaceModule Crew;
        public bool CrewSearched;
    }

    private readonly Dictionary<IntPtr, Unit> _units = new();
    private readonly HashSet<IntPtr> _seen = new();
    private readonly List<IntPtr> _gone = new();
    private VehicleController _controller;
    private float _nextControllerSearch;
    private float _nextSearch;
    private float _nextStatus;
    private TeamID _playerTeam = TeamID.None;
    private IntPtr _playerVehicle;
    private float _nextErrorLog;

    public TeamStrengthTracker() => Instance = this;

    /// <summary>味方・敵が両方いて表示できるとき true。</summary>
    public bool Valid { get; private set; }
    public int FriendlyAlive { get; private set; }
    public int FriendlyTotal { get; private set; }
    public int EnemyAlive { get; private set; }
    public int EnemyTotal { get; private set; }

    /// <summary>自車を操作しているとき true（ミニマップを出す条件）。</summary>
    public bool HasPlayer => _playerVehicle != IntPtr.Zero;

    /// <summary>今いる車両の位置・向き・陣営（自車を含む）。消えた車両は含めない。</summary>
    public void CollectUnits(List<MapUnit> list)
    {
        list.Clear();
        foreach (var pair in _units)
        {
            var unit = pair.Value;
            var vehicle = unit.Vehicle;
            if (!unit.Present || vehicle == null || vehicle.WasCollected)
                continue;
            var transform = vehicle.transform;
            var side = pair.Key == _playerVehicle ? KillFeed.Side.Player
                : unit.Team == TeamID.None || _playerTeam == TeamID.None ? KillFeed.Side.Neutral
                : unit.Team == _playerTeam ? KillFeed.Side.Friendly : KillFeed.Side.Enemy;
            list.Add(new MapUnit(transform.position, transform.forward, side, unit.Alive));
        }
    }

    public void Update()
    {
        if (!Plugin.TeamBar.Enabled.Value && !Plugin.Minimap.Enabled.Value)
        {
            Valid = false;
            return;
        }
        try
        {
            var now = Time.unscaledTime;
            if (now >= _nextSearch)
            {
                _nextSearch = now + SearchInterval;
                Search();
            }
            if (now >= _nextStatus)
            {
                _nextStatus = now + StatusInterval;
                UpdateStatus();
            }
        }
        catch (Exception e)
        {
            Valid = false;
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Team strength failed: {e}");
            }
        }
    }

    /// <summary>シーンの車両を探し、新しい車両を足し、消えた車両を撃破扱いにする。</summary>
    private void Search()
    {
        _seen.Clear();
        var found = Object.FindObjectsByType(Il2CppType.Of<VehicleBehaviour>(), FindObjectsSortMode.None);
        if (found != null)
        {
            foreach (var obj in found)
            {
                var vehicle = obj?.TryCast<VehicleBehaviour>();
                if (vehicle == null)
                    continue;
                _seen.Add(vehicle.Pointer);
                if (_units.ContainsKey(vehicle.Pointer))
                    continue;
                TeamID team;
                try
                {
                    team = vehicle.ID.TeamID;
                }
                catch (Exception)
                {
                    team = TeamID.None;
                }
                _units[vehicle.Pointer] = new Unit { Vehicle = vehicle, Team = team };
            }
        }

        _gone.Clear();
        var present = 0;
        foreach (var pair in _units)
        {
            if (!pair.Value.Present)
                continue;
            if (_seen.Contains(pair.Key))
                present++;
            else
                _gone.Add(pair.Key);
        }
        // 全部消えた、または一度に半分以上（3 両以上）消えたら、試合のリセットかシーンの切り替え
        if (_gone.Count > 0 && (present == 0 || _gone.Count >= 3 && _gone.Count * 2 >= present + _gone.Count))
        {
            _units.Clear();
            Plugin.Logger.LogInfo("Team strength: reset");
            return;
        }
        foreach (var ptr in _gone)
        {
            var unit = _units[ptr];
            unit.Present = false;
            unit.Alive = false; // 誘爆などで消えた
            unit.Vehicle = null;
        }
    }

    private void UpdateStatus()
    {
        var playerTeam = GetPlayerTeam();
        if (playerTeam != TeamID.None)
            _playerTeam = playerTeam;

        int friendlyAlive = 0, friendlyTotal = 0, enemyAlive = 0, enemyTotal = 0;
        var killTracker = KillFeed.KillTracker.Instance;
        foreach (var pair in _units)
        {
            var unit = pair.Value;
            if (unit.Team == TeamID.None)
                continue;
            if (unit.Alive && unit.Present)
            {
                if (unit.Vehicle == null || unit.Vehicle.WasCollected)
                    unit.Alive = false;
                else if (killTracker != null && killTracker.IsKnockedOut(pair.Key) || CrewDead(unit))
                    unit.Alive = false;
            }
            if (unit.Team == _playerTeam)
            {
                friendlyTotal++;
                if (unit.Alive)
                    friendlyAlive++;
            }
            else
            {
                enemyTotal++;
                if (unit.Alive)
                    enemyAlive++;
            }
        }
        FriendlyAlive = friendlyAlive;
        FriendlyTotal = friendlyTotal;
        EnemyAlive = enemyAlive;
        EnemyTotal = enemyTotal;
        // 自車を操作していないとき（メニューなど）は出さない
        Valid = playerTeam != TeamID.None && friendlyTotal > 0 && enemyTotal > 0;
    }

    /// <summary>乗員が全員倒れているか（InjectedOperator.Enabled は乗員の HealthFraction &gt; 0）。</summary>
    private static bool CrewDead(Unit unit)
    {
        if (!unit.CrewSearched || unit.Crew != null && unit.Crew.WasCollected)
        {
            unit.CrewSearched = true;
            unit.Crew = null;
            var modules = unit.Vehicle.modules;
            if (modules != null)
            {
                for (var i = 0; i < modules.Count; i++)
                {
                    var crew = modules[i]?.TryCast<VehicleCrewWorkspaceModule>();
                    if (crew != null)
                    {
                        unit.Crew = crew;
                        break;
                    }
                }
            }
        }
        var operators = unit.Crew?.operators;
        if (operators == null)
            return false;
        var total = 0;
        for (var i = 0; i < operators.Length; i++)
        {
            var op = operators[i];
            if (op == null || op.internalOperator == null)
                continue;
            total++;
            if (op.Enabled)
                return false;
        }
        return total > 0;
    }

    private TeamID GetPlayerTeam()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        _playerVehicle = IntPtr.Zero;
        var player = _controller?.ControlledVehicle;
        if (player == null)
            return TeamID.None;
        var vehicle = player.TryCast<VehicleBehaviour>();
        if (vehicle == null)
        {
            var body = player.Rigidbody;
            vehicle = body != null ? body.GetComponent<VehicleBehaviour>() ?? body.GetComponentInParent<VehicleBehaviour>() : null;
        }
        if (vehicle == null)
            return TeamID.None;
        _playerVehicle = vehicle.Pointer;
        if (!_units.ContainsKey(vehicle.Pointer))
            _nextSearch = 0f; // 自車がまだ一覧に無ければすぐ探す
        try
        {
            return vehicle.ID.TeamID;
        }
        catch (Exception)
        {
            return TeamID.None;
        }
    }
}
