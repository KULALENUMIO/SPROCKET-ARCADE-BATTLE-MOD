using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using UnityEngine;

namespace SprocketMods.Arcade;

/// <summary>Arcade Battle Mode の設定（設定ファイルの [Arcade] セクション）。</summary>
public class ArcadeConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyboardShortcut> ToggleKey;

    // 駆動系チューニング（自車のみ）
    public readonly ConfigEntry<bool> MaxTorque;
    public readonly ConfigEntry<bool> QuickShift;
    public readonly ConfigEntry<float> GearEngageTime;

    // 前後の加減速
    public readonly ConfigEntry<float> PowerMultiplier;
    public readonly ConfigEntry<float> MaxAcceleration;
    public readonly ConfigEntry<float> BrakeDeceleration;
    public readonly ConfigEntry<float> CoastDeceleration;
    public readonly ConfigEntry<float> MaxForwardSpeed;
    public readonly ConfigEntry<float> MaxReverseSpeed;

    // 旋回
    public readonly ConfigEntry<float> TurnRate;
    public readonly ConfigEntry<float> TurnRateAtSpeed;
    public readonly ConfigEntry<float> TurnAcceleration;
    public readonly ConfigEntry<bool> InvertSteerInReverse;

    // 安定性
    public readonly ConfigEntry<float> LateralGrip;
    public readonly ConfigEntry<float> Stability;
    public readonly ConfigEntry<float> Downforce;

    // 砲塔
    public readonly ConfigEntry<bool> TurretAssist;
    public readonly ConfigEntry<float> TurretSpeedMultiplier;
    public readonly ConfigEntry<float> TurretDesignMultiplier;
    public readonly ConfigEntry<string> TurretEraYears;
    public readonly ConfigEntry<string> TraverseRates;
    public readonly ConfigEntry<string> ElevationRates;

    public ArcadeConfig(ConfigFile cfg)
    {
        const string s = "Arcade";
        Enabled = cfg.Bind(s, "Enabled", false,
            "Arcade Battle Mode。World of Tanks のようにギアや回転数を気にせず、素早く滑らかに走行・旋回・停止できるようにする（自車のみ）");
        ToggleKey = cfg.Bind(s, "ToggleKey", new KeyboardShortcut(KeyCode.F10), "Arcade Battle Mode の切り替えキー");

        MaxTorque = cfg.Bind(s, "MaxTorque", true, "エンジンのトルクを回転数に関係なく最大値で維持する（トルクカーブを最大値で平らにする）");
        QuickShift = cfg.Bind(s, "QuickShift", true, "乗員の変速待ち（1〜3 秒）を無くし、変速機の接続も短縮する");
        GearEngageTime = cfg.Bind(s, "GearEngageTime", 0.03f,
            new ConfigDescription("QuickShift 時の変速機の切断/接続時間（秒）。元は 0.1〜0.3", new AcceptableValueRange<float>(0.01f, 0.3f)));

        PowerMultiplier = cfg.Bind(s, "PowerMultiplier", 1.5f,
            new ConfigDescription("加速力の倍率。エンジンの最大出力（最大トルク×最大回転数）から計算した加速度に掛ける", new AcceptableValueRange<float>(0.25f, 5f)));
        MaxAcceleration = cfg.Bind(s, "MaxAcceleration", 4f,
            new ConfigDescription("加速度の上限（m/s²）。低速時はエンジン出力よりこちらで決まる", new AcceptableValueRange<float>(0.5f, 15f)));
        BrakeDeceleration = cfg.Bind(s, "BrakeDeceleration", 6f,
            new ConfigDescription("逆方向に入力したときの減速度（m/s²）", new AcceptableValueRange<float>(0.5f, 20f)));
        CoastDeceleration = cfg.Bind(s, "CoastDeceleration", 3f,
            new ConfigDescription("入力を離したときの減速度（m/s²）", new AcceptableValueRange<float>(0.2f, 20f)));
        MaxForwardSpeed = cfg.Bind(s, "MaxForwardSpeed", 0f,
            new ConfigDescription("前進の最高速度（km/h）。0 なら変速機とエンジンの設計で決まる最高速度", new AcceptableValueRange<float>(0f, 200f)));
        MaxReverseSpeed = cfg.Bind(s, "MaxReverseSpeed", 20f,
            new ConfigDescription("後退の最高速度（km/h）", new AcceptableValueRange<float>(1f, 100f)));

        TurnRate = cfg.Bind(s, "TurnRate", 45f,
            new ConfigDescription("停止〜低速時の旋回速度（度/秒）", new AcceptableValueRange<float>(5f, 180f)));
        TurnRateAtSpeed = cfg.Bind(s, "TurnRateAtSpeed", 28f,
            new ConfigDescription("高速走行時（約 45 km/h 以上）の旋回速度（度/秒）", new AcceptableValueRange<float>(5f, 180f)));
        TurnAcceleration = cfg.Bind(s, "TurnAcceleration", 4f,
            new ConfigDescription("旋回の立ち上がり・止まりの速さ（rad/s²）", new AcceptableValueRange<float>(0.5f, 30f)));
        InvertSteerInReverse = cfg.Bind(s, "InvertSteerInReverse", false, "後退中はステアを逆にする（車のような操作）");

        LateralGrip = cfg.Bind(s, "LateralGrip", 4f,
            new ConfigDescription("横滑りを打ち消す強さ（1/秒）。0 で無効", new AcceptableValueRange<float>(0f, 20f)));
        Stability = cfg.Bind(s, "Stability", 3f,
            new ConfigDescription("車体のピッチ・ロールの揺れを抑える強さ（1/秒）。加減速時の前後の傾きも抑える。0 で無効", new AcceptableValueRange<float>(0f, 20f)));
        Downforce = cfg.Bind(s, "Downforce", 0.3f,
            new ConfigDescription("接地中に車体を地面へ押し付ける力（重力の何倍か）。0 で無効", new AcceptableValueRange<float>(0f, 2f)));

        TurretAssist = cfg.Bind(s, "TurretAssist", true,
            "砲塔の旋回・俯仰を加減速なしで動かす（最高速度のまま目標へ向かい、到達したらその場で止まる）");
        TurretSpeedMultiplier = cfg.Bind(s, "TurretSpeedMultiplier", 1f,
            new ConfigDescription("砲塔の旋回・俯仰速度の倍率", new AcceptableValueRange<float>(0.25f, 5f)));
        TurretDesignMultiplier = cfg.Bind(s, "TurretDesignMultiplier", 1f,
            new ConfigDescription("設計上の旋回速度に掛ける倍率。年代別の速度と比べて速い方を使う（0 なら年代別の速度だけ）", new AcceptableValueRange<float>(0f, 5f)));
        TurretEraYears = cfg.Bind(s, "TurretEraYears", "1918,1935,1945,1960",
            "年代の区切り（車両の設計年）。この年以前が各年代。例: 1918 以前 / 1919〜1935 / 1936〜1945 / 1946〜1960 / 1961 以降");
        TraverseRates = cfg.Bind(s, "TraverseRates", "12,20,30,40,50",
            "年代ごとの砲塔旋回速度（度/秒）。TurretEraYears の区切り数 + 1 個");
        ElevationRates = cfg.Bind(s, "ElevationRates", "6,10,15,20,25",
            "年代ごとの俯仰速度（度/秒）。TurretEraYears の区切り数 + 1 個");
    }
}
