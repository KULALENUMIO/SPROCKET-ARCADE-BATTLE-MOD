using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using UnityEngine;

namespace SprocketMods;

/// <summary>Ballistics HUD の設定（BepInEx/config/sprocketmods.cfg）。</summary>
public class HudConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyboardShortcut> ToggleKey;
    public readonly ConfigEntry<bool> ShowTrajectory;
    public readonly ConfigEntry<KeyboardShortcut> ToggleTrajectoryKey;
    public readonly ConfigEntry<bool> ShowDetails;

    public readonly ConfigEntry<float> MaxFlightTime;
    public readonly ConfigEntry<float> PenetrationUpdateInterval;
    public readonly ConfigEntry<int> PenetrationSamples;
    public readonly ConfigEntry<float> SampleRadiusCalibers;

    public readonly ConfigEntry<float> ReticleSize;
    public readonly ConfigEntry<float> ReticleSmoothing;
    public readonly ConfigEntry<bool> ShowDistance;
    public readonly ConfigEntry<bool> ShowVerdictText;
    public readonly ConfigEntry<float> FontSize;
    public readonly ConfigEntry<float> TrajectoryWidth;
    public readonly ConfigEntry<int> TrajectorySegments;

    public readonly ColorSetting PenColor;
    public readonly ColorSetting PartialColor;
    public readonly ColorSetting NoPenColor;
    public readonly ColorSetting RicochetColor;
    public readonly ColorSetting FriendlyColor;
    public readonly ColorSetting NeutralColor;
    public readonly ColorSetting TrajectoryColor;

    public readonly ConfigEntry<bool> DebugLog;

    public HudConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind("General", "Enabled", true, "HUD を表示する");
        ToggleKey = cfg.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "HUD 表示の切り替えキー");
        ShowTrajectory = cfg.Bind("General", "ShowTrajectory", false, "弾道（予測軌跡）の線を表示する");
        ToggleTrajectoryKey = cfg.Bind("General", "ToggleTrajectoryKey", new KeyboardShortcut(KeyCode.F9), "弾道線表示の切り替えキー");
        ShowDetails = cfg.Bind("General", "ShowDetails", false, "着弾速度・飛翔時間・入射角も表示する");

        MaxFlightTime = cfg.Bind("Simulation", "MaxFlightTimeOverride", 0f,
            new ConfigDescription("弾道を計算する最大飛翔時間（秒）。0 なら弾の寿命（Lifetime）まで計算する", new AcceptableValueRange<float>(0f, 60f)));
        PenetrationUpdateInterval = cfg.Bind("Simulation", "PenetrationUpdateInterval", 0.1f,
            new ConfigDescription("貫通シミュレーションの再計算間隔（秒）", new AcceptableValueRange<float>(0f, 2f)));
        PenetrationSamples = cfg.Bind("Simulation", "PenetrationSamples", 5,
            new ConfigDescription("着弾点周辺で貫通判定を行うサンプル数（1 = 中心のみ）。一部だけ貫通する場合は「境界」表示になる", new AcceptableValueRange<int>(1, 9)));
        SampleRadiusCalibers = cfg.Bind("Simulation", "SampleRadiusCalibers", 1f,
            new ConfigDescription("周辺サンプルの半径（口径の倍数）", new AcceptableValueRange<float>(0f, 10f)));

        ReticleSize = cfg.Bind("Appearance", "ReticleSize", 44f, "クロスヘア（着弾マーカー）の直径（ピクセル）");
        ReticleSmoothing = cfg.Bind("Appearance", "ReticleSmoothing", 0.04f,
            new ConfigDescription("クロスヘアの動きの平滑化（秒）。0 で無効", new AcceptableValueRange<float>(0f, 0.5f)));
        ShowDistance = cfg.Bind("Appearance", "ShowDistance", true, "クロスヘアの横に着弾点までの距離を表示する");
        ShowVerdictText = cfg.Bind("Appearance", "ShowVerdictText", true, "クロスヘアの下に判定（PEN / NO PEN など）を文字で表示する");
        FontSize = cfg.Bind("Appearance", "FontSize", 16f, "文字サイズ");
        TrajectoryWidth = cfg.Bind("Appearance", "TrajectoryWidth", 2f, "弾道線の太さ（ピクセル）");
        TrajectorySegments = cfg.Bind("Appearance", "TrajectorySegments", 80,
            new ConfigDescription("弾道線の最大分割数", new AcceptableValueRange<int>(4, 300)));

        PenColor = new ColorSetting(cfg, "Penetrate", "#40FF4CFF", "貫通できる");
        PartialColor = new ColorSetting(cfg, "Partial", "#FFE633FF", "一部のみ貫通（装甲の境目など）");
        NoPenColor = new ColorSetting(cfg, "NoPenetrate", "#FF3333FF", "貫通できない");
        RicochetColor = new ColorSetting(cfg, "Ricochet", "#FF8C1AFF", "跳弾");
        FriendlyColor = new ColorSetting(cfg, "Friendly", "#59A6FFFF", "味方");
        NeutralColor = new ColorSetting(cfg, "NotEnemy", "#CCCCCCD9", "敵戦車ではない（地形・自車・何も無い）");
        TrajectoryColor = new ColorSetting(cfg, "Trajectory", "#FFFFFF8C", "弾道線");

        DebugLog = cfg.Bind("Debug", "DebugLog", false, "砲弾パラメータや判定結果をログに出す");
    }
}

/// <summary>色の設定値。IL2CPP 版 BepInEx の設定は Color 型に対応していないので "#RRGGBBAA" 文字列で保存する。</summary>
public class ColorSetting
{
    private readonly ConfigEntry<string> _entry;
    private string _parsedFrom;
    private Color _color;

    public ColorSetting(ConfigFile cfg, string key, string defaultHex, string description)
    {
        _entry = cfg.Bind("Colors", key, defaultHex, description + "（#RRGGBB または #RRGGBBAA）");
    }

    public Color Value
    {
        get
        {
            var text = _entry.Value;
            if (text != _parsedFrom)
            {
                _parsedFrom = text;
                if (!TryParse(text, out _color) && !TryParse((string)_entry.DefaultValue, out _color))
                    _color = Color.white;
            }
            return _color;
        }
    }

    private static bool TryParse(string text, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim().TrimStart('#');
        if (text.Length != 6 && text.Length != 8)
            return false;
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return false;
        if (text.Length == 6)
            v = (v << 8) | 0xFF;
        color = new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        return true;
    }
}
