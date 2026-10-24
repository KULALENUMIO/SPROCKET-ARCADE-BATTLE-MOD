using System;
using System.Collections.Generic;
using Sprocket.Engines;
using Sprocket.Vehicles.Powertrains;

namespace SprocketMods.Arcade;

/// <summary>
/// 自車の駆動系データを Arcade 用に書き換え、無効化・乗り換え時に元へ戻す。
///
/// 変速時間やクラッチ速度の多くは C# の const でネイティブコードに埋め込まれていて変更できないため、
/// インスタンスごとに保持されている値だけを書き換える（オフセットは IL2CPP メタデータで確認）:
///   EngineBehaviour.physicsJob(+0x60).torqueCurveLookupTable(+0x38)  NativeArray&lt;float2&gt;（回転数, トルク）
///   TransmissionBehaviour.updateJob(+0x20).gearDisengagePeriod(+0x28) / gearEngagePeriod(+0x2C)
///   PowertrainBehaviour.hardShiftDelay(+0x70)  = GearShiftDelay。Driver が毎回「1〜3 秒（乗員の練度次第）」を書き込む
/// </summary>
internal sealed unsafe class PowertrainTuner
{
    private const int EngineTorqueCurveArray = 0x60 + 0x38;
    private const int TransmissionDisengagePeriod = 0x20 + 0x28;
    private const int TransmissionEngagePeriod = 0x20 + 0x2C;
    private const int PowertrainShiftDelay = 0x70;

    private PowertrainBehaviour _powertrain;
    private EngineBehaviour _engine;
    private float[] _originalTorque;
    private readonly List<(TransmissionBehaviour transmission, float disengage, float engage)> _transmissions = new();

    /// <summary>現在チューニング中の駆動系（Harmony パッチから参照）。</summary>
    public IntPtr Powertrain => _powertrain != null ? _powertrain.Pointer : IntPtr.Zero;

    public void Apply(PowertrainBehaviour powertrain, EngineBehaviour engine)
    {
        if (_powertrain == null || powertrain.Pointer != _powertrain.Pointer)
        {
            Restore();
            _powertrain = powertrain;
            _engine = engine;
        }

        var cfg = Plugin.Arcade;
        if (cfg.MaxTorque.Value)
            FlattenTorque();
        else
            RestoreTorque();

        if (cfg.QuickShift.Value)
            QuickenTransmissions(cfg.GearEngageTime.Value);
        else
            RestoreTransmissions();
    }

    /// <summary>PowertrainBehaviour.FixedUpdate の直前に呼ばれる（Driver が書いた変速待ちを打ち消す）。</summary>
    public void BeforePowertrainFixedUpdate(PowertrainBehaviour powertrain)
    {
        if (Plugin.Arcade.QuickShift.Value)
            *(float*)(powertrain.Pointer + PowertrainShiftDelay) = 0f;
    }

    public void Restore()
    {
        RestoreTorque();
        RestoreTransmissions();
        _powertrain = null;
        _engine = null;
    }

    // ---------------------------------------------------------------- torque

    private void FlattenTorque()
    {
        if (_originalTorque != null || _engine == null || _engine.WasCollected)
            return;
        if (!TryGetTorqueTable(out var table, out var length))
            return;

        _originalTorque = new float[length];
        var max = 0f;
        for (var i = 0; i < length; i++)
        {
            _originalTorque[i] = table[i * 2 + 1];
            max = Math.Max(max, _originalTorque[i]);
        }
        for (var i = 0; i < length; i++)
            table[i * 2 + 1] = max;

        if (Plugin.Cfg.DebugLog.Value)
            Plugin.Logger.LogInfo($"Arcade: torque curve flattened ({length} points, peak {max:0.##})");
    }

    private void RestoreTorque()
    {
        if (_originalTorque == null)
            return;
        if (_engine != null && !_engine.WasCollected && TryGetTorqueTable(out var table, out var length)
            && length == _originalTorque.Length)
        {
            for (var i = 0; i < length; i++)
                table[i * 2 + 1] = _originalTorque[i];
        }
        _originalTorque = null;
    }

    /// <summary>NativeArray のレイアウト: m_Buffer(ポインタ) +0x0, m_Length(int) +0x8。要素は float2(x=回転数, y=トルク)。</summary>
    private bool TryGetTorqueTable(out float* table, out int length)
    {
        var array = _engine.Pointer + EngineTorqueCurveArray;
        table = *(float**)array;
        length = *(int*)(array + 8);
        return table != null && length > 0 && length < 4096;
    }

    // ---------------------------------------------------------------- transmissions

    private void QuickenTransmissions(float time)
    {
        if (_transmissions.Count == 0)
        {
            var list = _powertrain.Transmissions;
            if (list == null)
                return;
            for (var i = 0; i < list.Length; i++)
            {
                var t = list[i]?.TryCast<TransmissionBehaviour>();
                if (t == null)
                    continue;
                _transmissions.Add((t,
                    *(float*)(t.Pointer + TransmissionDisengagePeriod),
                    *(float*)(t.Pointer + TransmissionEngagePeriod)));
            }
            if (Plugin.Cfg.DebugLog.Value)
                Plugin.Logger.LogInfo($"Arcade: {_transmissions.Count} transmission(s) set to quick shift ({time}s)");
        }
        foreach (var (t, _, _) in _transmissions)
        {
            if (t.WasCollected)
                continue;
            *(float*)(t.Pointer + TransmissionDisengagePeriod) = time;
            *(float*)(t.Pointer + TransmissionEngagePeriod) = time;
        }
    }

    private void RestoreTransmissions()
    {
        foreach (var (t, disengage, engage) in _transmissions)
        {
            if (t.WasCollected)
                continue;
            *(float*)(t.Pointer + TransmissionDisengagePeriod) = disengage;
            *(float*)(t.Pointer + TransmissionEngagePeriod) = engage;
        }
        _transmissions.Clear();
    }
}
