using System;
using System.Text;
using UnityEngine;

namespace SprocketMods;

/// <summary>
/// IL2CPP に注入する常駐コンポーネント。注入クラスのメソッドはすべて IL2CPP 側に登録されるため、
/// マネージド型を引数に取るロジックは HudController に分けてある。
/// </summary>
public class HudBehaviour : MonoBehaviour
{
    private readonly HudController _controller = new();
    private readonly Arcade.ArcadeController _arcade = new();
    private readonly Cameras.CameraZoom _camera = new();
    private readonly Weapons.AutocannonHud _autocannonHud = new();
    private readonly KillFeed.KillTracker _killTracker = new();
    private readonly KillFeed.KillFeedView _killFeed = new();
    private readonly Hits.HitTracker _hitTracker = new();
    private readonly Hits.HitReportView _hitReport = new();
    private readonly HitCam.HitCamTracker _hitCamTracker = new();
    private readonly HitCam.HitCamView _hitCam = new();
    private readonly Targets.TargetInfoTracker _targetInfo = new();
    private readonly Targets.TargetInfoView _targetInfoView = new();
    private float _nextTargetInfoErrorLog;
    private readonly Teams.TeamStrengthTracker _teamStrength = new();
    private readonly Teams.TeamBarView _teamBar = new();
    private float _nextTeamBarErrorLog;
    private readonly Map.MinimapView _minimap = new();
    private float _nextMinimapErrorLog;
    private readonly Hits.DamageIndicator _damageIndicator = new();
    private readonly Repair.RepairSystem _repair = new();
    private readonly Ai.AiDirector _aiDirector = new();
    private readonly Design.SandboxSpawner _sandboxSpawner = new();
    private readonly Debug.DebugConsole _debugConsole = new();
    private readonly Repair.RepairView _repairView = new();
    private readonly Ammo.AmmoSystem _ammo = new();
    private readonly Ammo.AmmoView _ammoView = new();
    private readonly UI.DeathScreen _deathScreen = new();
    private float _nextAmmoErrorLog;
    private float _nextRepairViewErrorLog;
    private float _nextHitReportErrorLog;
    private float _nextKillFeedErrorLog;
    private float _nextAutocannonHudErrorLog;
    private int _arcadeErrors;
    private float _nextCameraErrorLog;
    private float _nextArcadeErrorLog;

    public HudBehaviour(IntPtr ptr) : base(ptr)
    {
    }

    private bool _localizationStarted;

    private void Update()
    {
        Core.Fast.Tick();
        var localization = Localization.LocalizationManager.Instance;
        if (localization != null)
        {
            if (!_localizationStarted)
            {
                _localizationStarted = true;
                {
                    var perf = Debug.Perf.Begin();
                    localization.Reload();
                    Debug.Perf.End("Localization.Reload", perf);
                }
            }
            {
                var perf = Debug.Perf.Begin();
                localization.Update();
                Debug.Perf.End("Localization.Update", perf);
            }
        }
        {
            var perf = Debug.Perf.Begin();
            _controller.Update();
            Debug.Perf.End("Controller.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _arcade.Update();
            Debug.Perf.End("Arcade.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _killTracker.Update();
            Debug.Perf.End("KillTracker.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _hitTracker.Update();
            Debug.Perf.End("HitTracker.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _hitCamTracker.Update();
            Debug.Perf.End("HitCamTracker.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _targetInfo.Update();
            Debug.Perf.End("TargetInfo.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _teamStrength.Update();
            Debug.Perf.End("TeamStrength.Update", perf);
        }
        {
            var perf = Debug.Perf.Begin();
            _repair.Update();
            Debug.Perf.End("Repair.Update", perf);
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _ammo.Update();
                Debug.Perf.End("Ammo.Update", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextAmmoErrorLog)
            {
                _nextAmmoErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Ammo failed: {e}");
            }
        }
        {
            var perf = Debug.Perf.Begin();
            _aiDirector.Update();
            Debug.Perf.End("AiDirector.Update", perf);
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                Design.CylinderTurret.Update();
                Debug.Perf.End("Design.CylinderTurret.Update", perf);
            }
            {
                var perf = Debug.Perf.Begin();
                _sandboxSpawner.Update();
                Debug.Perf.End("SandboxSpawner.Update", perf);
            }
            {
                var perf = Debug.Perf.Begin();
                _debugConsole.Update();
                Debug.Perf.End("DebugConsole.Update", perf);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Design tools failed: {e}");
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                Weapons.AutocannonMuzzle.Update();
                Debug.Perf.End("Weapons.AutocannonMuzzle.Update", perf);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Autocannon muzzle failed: {e}");
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _camera.Update();
                Debug.Perf.End("Camera.Update", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextCameraErrorLog)
            {
                _nextCameraErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Camera update failed: {e}");
            }
        }
    }

    private void OnApplicationQuit() => Localization.LocalizationManager.Instance?.Shutdown();

    private void LateUpdate()
    {
        {
            var perf = Debug.Perf.Begin();
            _controller.LateUpdate();
            Debug.Perf.End("Controller.LateUpdate", perf);
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _autocannonHud.LateUpdate();
                Debug.Perf.End("AutocannonHud.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextAutocannonHudErrorLog)
            {
                _nextAutocannonHudErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Autocannon HUD failed: {e}");
            }
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _killFeed.LateUpdate();
                Debug.Perf.End("KillFeed.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextKillFeedErrorLog)
            {
                _nextKillFeedErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Kill feed view failed: {e}");
            }
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _hitReport.LateUpdate();
                Debug.Perf.End("HitReport.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextHitReportErrorLog)
            {
                _nextHitReportErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Hit report view failed: {e}");
            }
        }
        {
            var perf = Debug.Perf.Begin();
            _hitCam.LateUpdate();
            Debug.Perf.End("HitCam.LateUpdate", perf);
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _deathScreen.LateUpdate();
                Debug.Perf.End("DeathScreen.LateUpdate", perf);
            }
            {
                var perf = Debug.Perf.Begin();
                _ammoView.LateUpdate();
                Debug.Perf.End("AmmoView.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextAmmoErrorLog)
            {
                _nextAmmoErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Ammo view failed: {e}");
            }
        }
        {
            var perf = Debug.Perf.Begin();
            _damageIndicator.Update();
            Debug.Perf.End("DamageIndicator.Update", perf);
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _repairView.LateUpdate();
                Debug.Perf.End("RepairView.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextRepairViewErrorLog)
            {
                _nextRepairViewErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Repair view failed: {e}");
            }
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _minimap.LateUpdate();
                Debug.Perf.End("Minimap.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextMinimapErrorLog)
            {
                _nextMinimapErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Minimap failed: {e}");
            }
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _teamBar.LateUpdate();
                Debug.Perf.End("TeamBar.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextTeamBarErrorLog)
            {
                _nextTeamBarErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Team bar view failed: {e}");
            }
        }
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _targetInfoView.LateUpdate();
                Debug.Perf.End("TargetInfoView.LateUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextTargetInfoErrorLog)
            {
                _nextTargetInfoErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Target info view failed: {e}");
            }
        }
        Debug.Perf.Frame();
    }

    private void FixedUpdate()
    {
        try
        {
            {
                var perf = Debug.Perf.Begin();
                _arcade.FixedUpdate();
                Debug.Perf.End("Arcade.FixedUpdate", perf);
            }
        }
        catch (Exception e)
        {
            _arcadeErrors++;
            if (Core.Fast.Unscaled >= _nextArcadeErrorLog)
            {
                _nextArcadeErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Arcade update failed ({_arcadeErrors} errors so far): {e}");
            }
        }
        try
        {
            { // Arcade の後（修理中は止める方を優先）
                var perf = Debug.Perf.Begin();
                _repair.FixedUpdate();
                Debug.Perf.End("Repair.FixedUpdate", perf);
            }
        }
        catch (Exception e)
        {
            if (Core.Fast.Unscaled >= _nextRepairViewErrorLog)
            {
                _nextRepairViewErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"Repair hold failed: {e}");
            }
        }
    }
}

/// <summary>毎フレーム 弾道計算 → 貫通判定 → 描画 を行う。</summary>
internal sealed class HudController
{
    private GameAccess _access;
    private BallisticSolver _solver;
    private PenetrationProbe _probe;
    private HudView _view;
    private readonly StringBuilder _text = new();

    private int _errors;
    private float _nextErrorLog;

    public void Update()
    {
        var cfg = Plugin.Cfg;
        if (SprocketMods.UI.KeyInput.Down(cfg.ToggleKey.Value))
        {
            cfg.Enabled.Value = !cfg.Enabled.Value;
            Plugin.Logger.LogInfo($"HUD {(cfg.Enabled.Value ? "enabled" : "disabled")}");
        }
        if (SprocketMods.UI.KeyInput.Down(cfg.ToggleTrajectoryKey.Value))
            cfg.ShowTrajectory.Value = !cfg.ShowTrajectory.Value;
    }

    public void LateUpdate()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            _errors++;
            if (Core.Fast.Unscaled >= _nextErrorLog)
            {
                _nextErrorLog = Core.Fast.Unscaled + 5f;
                Plugin.Logger.LogError($"HUD update failed ({_errors} errors so far): {e}");
            }
            _view?.SetActive(false);
        }
    }

    private void Tick()
    {
        if (_access == null)
        {
            _access = new GameAccess();
            SimulationReader.Resolve(); // メモリ配置の解決結果を起動時にログへ出す
        }
        _solver ??= new BallisticSolver();
        _probe ??= new PenetrationProbe();
        _view ??= new HudView();

        var cfg = Plugin.Cfg;
        var perf = Debug.Perf.Begin();
        WeaponState weapon = default;
        var gotWeapon = cfg.Enabled.Value && Core.Fast.TimeScale > 0f && _access.TryGetWeaponState(out weapon);
        Debug.Perf.End("HUD/Access", perf);
        if (!gotWeapon || !(weapon.MuzzleVelocity > 0f))
        {
            _view.SetActive(false);
            return;
        }
        if (!_view.Ensure())
            return;
        _view.SetActive(true);

        perf = Debug.Perf.Begin();
        _solver.Solve(weapon, cfg.MaxFlightTime.Value);
        Debug.Perf.End("HUD/Solve", perf);
        var impact = _solver.Impact;
        perf = Debug.Perf.Begin();
        var probe = _probe.Evaluate(weapon, impact);
        Debug.Perf.End("HUD/Probe", perf);
        var camera = weapon.Camera;
        perf = Debug.Perf.Begin();

        if (cfg.ShowTrajectory.Value)
            _view.ShowTrajectory(camera, _solver.Points, cfg.TrajectoryColor.Value);
        else
            _view.HideTrajectory();

        // 何にも当たらない場合も、弾道の終点（寿命の位置）に灰色のクロスヘアを出す
        var distance = (impact.Point - weapon.Origin).magnitude;
        var distanceText = impact.Kind == ImpactKind.None
            ? ">" + distance.ToString("0") + " m"
            : distance.ToString("0") + " m";
        _view.ShowCrosshair(camera, impact.Point, GetColor(probe), distanceText, BuildVerdict(impact, probe));
        Debug.Perf.End("HUD/View", perf);
    }

    private Color GetColor(in ProbeResult probe)
    {
        var cfg = Plugin.Cfg;
        switch (probe.Target)
        {
            case TargetKind.Friendly:
                return cfg.FriendlyColor.Value;
            case TargetKind.Enemy:
                return probe.Verdict switch
                {
                    PenVerdict.Penetrate => cfg.PenColor.Value,
                    PenVerdict.Partial => cfg.PartialColor.Value,
                    PenVerdict.NoPenetrate => cfg.NoPenColor.Value,
                    PenVerdict.Ricochet => cfg.RicochetColor.Value,
                    PenVerdict.NotSimulated => cfg.PartialColor.Value,
                    _ => cfg.NeutralColor.Value,
                };
            default:
                return cfg.NeutralColor.Value;
        }
    }

    private string BuildVerdict(in ImpactInfo impact, in ProbeResult probe)
    {
        _text.Clear();
        switch (probe.Target)
        {
            case TargetKind.None:
                _text.Append(Label(LabelId.NoImpact));
                break;
            case TargetKind.Enemy:
                _text.Append(probe.Verdict switch
                {
                    PenVerdict.Penetrate => Label(LabelId.Penetrate),
                    PenVerdict.Partial => Label(LabelId.Partial),
                    PenVerdict.NoPenetrate => Label(LabelId.NoPenetrate),
                    PenVerdict.Ricochet => Label(LabelId.Ricochet),
                    PenVerdict.NotSimulated => Label(LabelId.NotSimulated),
                    _ => Label(LabelId.Unknown),
                });
                if (probe.Verdict == PenVerdict.Partial)
                    _text.Append(' ').Append(probe.Penetrations).Append('/').Append(probe.Samples);
                break;
            case TargetKind.Friendly:
                _text.Append(Label(LabelId.Friendly));
                break;
            case TargetKind.OwnVehicle:
                _text.Append(Label(LabelId.OwnVehicle));
                break;
            default:
                _text.Append(Label(LabelId.NotEnemy));
                break;
        }

        if (Plugin.Cfg.ShowDetails.Value && impact.Kind != ImpactKind.None)
        {
            _text.Append("\n<size=80%>")
                .Append(impact.Velocity.magnitude.ToString("0")).Append(" m/s  ")
                .Append(impact.FlightTime.ToString("0.00")).Append(" s");
            if (probe.Target == TargetKind.Enemy || probe.Target == TargetKind.Friendly)
                _text.Append("  ").Append(probe.ImpactAngle.ToString("0")).Append('°');
            _text.Append("</size>");
        }
        return _text.ToString();
    }

    private enum LabelId
    {
        Penetrate,
        Partial,
        NoPenetrate,
        Ricochet,
        NotSimulated,
        Unknown,
        Friendly,
        OwnVehicle,
        NotEnemy,
        NoImpact,
    }

    /// <summary>英語の原文。表示言語への翻訳は Localization（Languages/&lt;言語&gt;/sprocketmods.txt）が行う。</summary>
    private static string Label(LabelId id) => id switch
    {
        LabelId.Penetrate => "PEN",
        LabelId.Partial => "PEN?",
        LabelId.NoPenetrate => "NO PEN",
        LabelId.Ricochet => "RICOCHET",
        LabelId.NotSimulated => "HE (no AP sim)",
        LabelId.Unknown => "UNKNOWN",
        LabelId.Friendly => "FRIENDLY",
        LabelId.OwnVehicle => "OWN VEHICLE",
        LabelId.NotEnemy => "NOT ENEMY",
        LabelId.NoImpact => "NO IMPACT",
        _ => string.Empty,
    };
}
