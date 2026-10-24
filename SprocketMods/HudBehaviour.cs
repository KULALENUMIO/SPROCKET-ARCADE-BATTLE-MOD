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
    private readonly Repair.RepairView _repairView = new();
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
        var localization = Localization.LocalizationManager.Instance;
        if (localization != null)
        {
            if (!_localizationStarted)
            {
                _localizationStarted = true;
                localization.Reload();
            }
            localization.Update();
        }
        _controller.Update();
        _arcade.Update();
        _killTracker.Update();
        _hitTracker.Update();
        _targetInfo.Update();
        _teamStrength.Update();
        _repair.Update();
        _aiDirector.Update();
        try
        {
            Weapons.AutocannonMuzzle.Update();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Autocannon muzzle failed: {e}");
        }
        try
        {
            _camera.Update();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextCameraErrorLog)
            {
                _nextCameraErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Camera update failed: {e}");
            }
        }
    }

    private void OnApplicationQuit() => Localization.LocalizationManager.Instance?.Shutdown();

    private void LateUpdate()
    {
        _controller.LateUpdate();
        try
        {
            _autocannonHud.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextAutocannonHudErrorLog)
            {
                _nextAutocannonHudErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Autocannon HUD failed: {e}");
            }
        }
        try
        {
            _killFeed.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextKillFeedErrorLog)
            {
                _nextKillFeedErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Kill feed view failed: {e}");
            }
        }
        try
        {
            _hitReport.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextHitReportErrorLog)
            {
                _nextHitReportErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Hit report view failed: {e}");
            }
        }
        _damageIndicator.Update();
        try
        {
            _repairView.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextRepairViewErrorLog)
            {
                _nextRepairViewErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Repair view failed: {e}");
            }
        }
        try
        {
            _minimap.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextMinimapErrorLog)
            {
                _nextMinimapErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Minimap failed: {e}");
            }
        }
        try
        {
            _teamBar.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextTeamBarErrorLog)
            {
                _nextTeamBarErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Team bar view failed: {e}");
            }
        }
        try
        {
            _targetInfoView.LateUpdate();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextTargetInfoErrorLog)
            {
                _nextTargetInfoErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Target info view failed: {e}");
            }
        }
    }

    private void FixedUpdate()
    {
        try
        {
            _arcade.FixedUpdate();
        }
        catch (Exception e)
        {
            _arcadeErrors++;
            if (Time.unscaledTime >= _nextArcadeErrorLog)
            {
                _nextArcadeErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Arcade update failed ({_arcadeErrors} errors so far): {e}");
            }
        }
        try
        {
            _repair.FixedUpdate(); // Arcade の後（修理中は止める方を優先）
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextRepairViewErrorLog)
            {
                _nextRepairViewErrorLog = Time.unscaledTime + 5f;
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
        if (cfg.ToggleKey.Value.IsDown())
        {
            cfg.Enabled.Value = !cfg.Enabled.Value;
            Plugin.Logger.LogInfo($"HUD {(cfg.Enabled.Value ? "enabled" : "disabled")}");
        }
        if (cfg.ToggleTrajectoryKey.Value.IsDown())
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
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
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
        if (!cfg.Enabled.Value || Time.timeScale <= 0f
            || !_access.TryGetWeaponState(out var weapon) || !(weapon.MuzzleVelocity > 0f))
        {
            _view.SetActive(false);
            return;
        }
        if (!_view.Ensure())
            return;
        _view.SetActive(true);

        _solver.Solve(weapon, cfg.MaxFlightTime.Value);
        var impact = _solver.Impact;
        var probe = _probe.Evaluate(weapon, impact);
        var camera = weapon.Camera;

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
