using System;
using Il2CppInterop.Runtime;
using Sprocket.Engines;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Powertrains.Engines;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Powertrains;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Arcade;

/// <summary>
/// Arcade Battle Mode の操作アシスト。プレイヤーが操作している車両にだけ働く。
///
/// ゲームの駆動系（エンジン・変速機・履帯の物理）はそのまま動かし、その上に閉ループの補助を足す:
///  - 前後: 入力から目標速度を作り、エンジン出力（最大トルク×最大回転数）由来の加速度で滑らかに追従させる。
///          変速中の駆動力の途切れやブレーキの急減速は補助力が打ち消すので、加減速が滑らかになる。
///          エンジン回転が上限に近いときは前進方向の補助を止める（過回転ダメージを起こさない）。
///  - 旋回: 目標の旋回角速度へ角速度を直接寄せる（このビルドでは Rigidbody.AddTorque が削除されている）。
///  - 安定: 横滑りの打ち消し、ピッチ・ロールの減衰、接地中の押し付け。
/// 補助力は重心に加えるので、それ自体は車体を傾けない。
/// エンジン停止・履帯破損・操縦手不在のときは推進と旋回の補助をしない。
/// </summary>
internal sealed class ArcadeController
{
    public static ArcadeController Instance { get; private set; }

    private const float SpeedSlack = 1.5f;      // 目標速度が実速度から離れすぎない幅（m/s）。壁や坂での暴走防止
    private const float SpeedGain = 3f;         // 速度誤差 → 加速度（1/秒）
    private const float TurnGain = 8f;          // 旋回角速度誤差 → 角加速度（1/秒）
    private const float RpmGuardStart = 0.94f;  // 最大回転数に対するこの割合から前進補助を絞る
    private const float FullTurnSpeed = 12.5f;  // TurnRateAtSpeed になる速度（m/s ≒ 45 km/h）
    private const float RpmToRadsPerSecond = 0.10471976f;

    private readonly PowertrainTuner _tuner = new();
    private readonly TurretAssist _turret = new();
    private VehicleController _controller;
    private float _nextControllerSearch;
    private float _referenceSpeed;
    private bool _hasReference;
    private bool _wasEnabled;

    public ArcadeController()
    {
        Instance = this;
    }

    public IntPtr TunedPowertrain => _tuner.Powertrain;

    public void BeforePowertrainFixedUpdate(PowertrainBehaviour powertrain) => _tuner.BeforePowertrainFixedUpdate(powertrain);

    public void Update()
    {
        var cfg = Plugin.Arcade;
        if (cfg.ToggleKey.Value.IsDown())
        {
            cfg.Enabled.Value = !cfg.Enabled.Value;
            Plugin.Logger.LogInfo($"Arcade Battle Mode {(cfg.Enabled.Value ? "ON" : "OFF")}");
        }
    }

    public void FixedUpdate()
    {
        var cfg = Plugin.Arcade;
        if (!cfg.Enabled.Value)
        {
            if (_wasEnabled)
            {
                _tuner.Restore();
                _turret.Clear();
                _hasReference = false;
                _wasEnabled = false;
            }
            return;
        }
        _wasEnabled = true;

        UpdateTurretTargets();

        if (!TryGetVehicle(out var v))
        {
            _tuner.Restore();
            _hasReference = false;
            return;
        }

        _tuner.Apply(v.Powertrain, v.Engine);
        Assist(v, cfg);
    }

    /// <summary>砲塔の補助は駆動系が使えない（操縦手不在など）ときも働かせるので別に集める。</summary>
    private void UpdateTurretTargets()
    {
        var vc = GetController();
        var vehicle = vc?.ControlledVehicle;
        var body = vehicle?.Rigidbody;
        if (body == null || !Plugin.Arcade.TurretAssist.Value)
        {
            _turret.Clear();
            return;
        }
        _turret.Refresh(vc, body.Pointer, body.gameObject);
    }

    private struct DriveTarget
    {
        public Rigidbody Body;
        public PowertrainBehaviour Powertrain;
        public EngineBehaviour Engine;
        public VehiclePowertrainBehaviourModule Module;
        public Driver Driver;
    }

    private bool TryGetVehicle(out DriveTarget target)
    {
        target = default;
        var vc = GetController();
        var vehicle = vc?.ControlledVehicle;
        if (vehicle == null)
            return false;

        var driver = vc.driver?.TryCast<Driver>();
        var powertrain = driver?.inputReciever?.TryCast<PowertrainBehaviour>();
        var engine = powertrain?.Engine?.TryCast<EngineBehaviour>();
        if (powertrain == null || engine == null)
            return false;

        var behaviour = vehicle.TryCast<VehicleBehaviour>();
        VehiclePowertrainBehaviourModule module = null;
        var modules = behaviour?.modules;
        if (modules != null)
        {
            for (var i = 0; i < modules.Count && module == null; i++)
                module = modules[i]?.TryCast<VehiclePowertrainBehaviourModule>();
        }

        var body = vehicle.Rigidbody;
        if (body == null)
            return false;

        target = new DriveTarget
        {
            Body = body,
            Powertrain = powertrain,
            Engine = engine,
            Module = module,
            Driver = driver,
        };
        return true;
    }

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

    private void Assist(in DriveTarget v, ArcadeConfig cfg)
    {
        var body = v.Body;
        var dt = Time.fixedDeltaTime;
        var t = body.transform;
        var forward = t.forward;
        var right = t.right;
        var up = t.up;
        var mass = body.mass;
        var velocity = body.linearVelocity;
        var forwardSpeed = Vector3.Dot(velocity, forward);
        var lateralSpeed = Vector3.Dot(velocity, right);

        var module = v.Module;
        var grounded = module == null
                       || (module.leftPropulsion != null && module.leftPropulsion.InContact)
                       || (module.rightPropulsion != null && module.rightPropulsion.InContact);
        var immobilized = module != null && module.immobilized;
        var engineRunning = v.Engine.State == EngineState.Running;
        var canDrive = grounded && !immobilized && engineRunning && v.Powertrain.functioning && v.Powertrain.DriverFunctioning;

        var solution = v.Driver.Solution;
        var throttle = Mathf.Clamp(solution.Throttle, -1f, 1f);
        var steer = Mathf.Clamp(solution.Steer, -1f, 1f);

        // ---- 前後
        if (!_hasReference)
        {
            _referenceSpeed = forwardSpeed;
            _hasReference = true;
        }
        if (canDrive)
        {
            var maxRpmSpeed = v.Engine.MaxRPM * RpmToRadsPerSecond;
            var power = v.Engine.MaxTorque * maxRpmSpeed * cfg.PowerMultiplier.Value;
            var acceleration = Mathf.Min(power / (mass * Mathf.Max(Mathf.Abs(_referenceSpeed), 2f)), cfg.MaxAcceleration.Value);

            var forwardCap = cfg.MaxForwardSpeed.Value > 0f ? cfg.MaxForwardSpeed.Value / 3.6f : 1000f;
            var target = throttle > 0.01f ? throttle * forwardCap
                : throttle < -0.01f ? throttle * cfg.MaxReverseSpeed.Value / 3.6f
                : 0f;

            float rate;
            var speeding = Mathf.Abs(target) > Mathf.Abs(_referenceSpeed) && (target * _referenceSpeed >= 0f);
            if (speeding)
                rate = acceleration;
            else if (Mathf.Abs(throttle) <= 0.01f)
                rate = cfg.CoastDeceleration.Value;
            else
                rate = cfg.BrakeDeceleration.Value;

            _referenceSpeed = Mathf.MoveTowards(_referenceSpeed, target, rate * dt);
            _referenceSpeed = Mathf.Clamp(_referenceSpeed, forwardSpeed - SpeedSlack, forwardSpeed + SpeedSlack);

            var maxForce = mass * (Mathf.Max(cfg.MaxAcceleration.Value, cfg.BrakeDeceleration.Value) + 2f);
            var force = Mathf.Clamp(mass * SpeedGain * (_referenceSpeed - forwardSpeed), -maxForce, maxForce);

            // 進行方向へ押す補助は、エンジン回転が上限付近なら絞る（過回転ダメージ防止・設計上の最高速度で頭打ち）
            if (force * Mathf.Sign(_referenceSpeed) > 0f)
            {
                var rpm = v.Engine.RPM / Mathf.Max(v.Engine.MaxRPM, 1f);
                force *= Mathf.Clamp01((1f - rpm) / (1f - RpmGuardStart));
            }
            body.AddForce(forward * force, ForceMode.Force);
        }
        else
        {
            _referenceSpeed = forwardSpeed;
        }

        // ---- 旋回・姿勢（角速度を直接調整）
        var angular = t.InverseTransformDirection(body.angularVelocity);
        if (canDrive)
        {
            var reverse = cfg.InvertSteerInReverse.Value && forwardSpeed < -0.5f ? -1f : 1f;
            var maxRate = Mathf.Lerp(cfg.TurnRate.Value, cfg.TurnRateAtSpeed.Value, Mathf.Abs(forwardSpeed) / FullTurnSpeed) * Mathf.Deg2Rad;
            var targetRate = steer * reverse * maxRate;
            var maxStep = cfg.TurnAcceleration.Value * dt;
            angular.y += Mathf.Clamp(TurnGain * (targetRate - angular.y) * dt, -maxStep, maxStep);
        }
        if (grounded && cfg.Stability.Value > 0f)
        {
            var damping = 1f / (1f + cfg.Stability.Value * dt);
            angular.x *= damping;
            angular.z *= damping;
        }
        body.angularVelocity = t.TransformDirection(angular);

        // ---- 横滑り・接地
        if (grounded && cfg.LateralGrip.Value > 0f)
        {
            var lateral = Mathf.Clamp(-mass * cfg.LateralGrip.Value * lateralSpeed, -mass * 8f, mass * 8f);
            body.AddForce(right * lateral, ForceMode.Force);
        }
        if (grounded && cfg.Downforce.Value > 0f)
            body.AddForce(-up * (mass * Physics.gravity.magnitude * cfg.Downforce.Value), ForceMode.Force);
    }
}
