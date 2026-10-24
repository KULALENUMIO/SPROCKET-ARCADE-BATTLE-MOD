using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.UI;

/// <summary>HUD の見た目の設定（[HudStyle] セクション）。</summary>
public class HudStyleConfig
{
    public readonly ConfigEntry<float> PanelOpacity;
    public readonly ConfigEntry<float> Scale;

    public HudStyleConfig(ConfigFile cfg)
    {
        const string s = "HudStyle";
        PanelOpacity = cfg.Bind(s, "PanelOpacity", 0.6f,
            new ConfigDescription("文字の後ろに敷く黒いパネルの濃さ（0 = なし）", new AcceptableValueRange<float>(0f, 1f)));
        Scale = cfg.Bind(s, "Scale", 1f,
            new ConfigDescription("命中結果・照準した車両の情報・機関砲・キルログのパネル全体の大きさ", new AcceptableValueRange<float>(0.6f, 1.6f)));
    }
}

/// <summary>
/// MOD の HUD 共通のデザイン: 角丸の半透明の黒いパネルに文字を入れ、左端に色の帯（敵 = 赤 など）を付ける。
/// 色・フォント・パネル・行（名前 ─ バー ─ 値）の部品をまとめる。座標はすべて画面のピクセル（左下原点）。
/// </summary>
internal static class HudStyle
{
    public const string Red = "FF5A47";
    public const string Orange = "FF9A3C";
    public const string Yellow = "FFD84D";
    public const string Green = "8FE07A";
    public const string Blue = "6FB4FF";
    public const string Gold = "FFD24A";
    public const string White = "F2F2F2";
    public const string Grey = "A8A8A8";
    public const string Fire = "FF7A2A";

    private const int CornerTexture = 32;
    private const float CornerRadius = 12f; // テクスチャ上。表示は CornerScale 倍
    private const float CornerScale = 0.5f;

    private static Sprite _rounded;
    private static TMP_FontAsset _font;
    private static float _nextFontSearch;

    /// <summary>1080p 基準の拡大率（HudStyle.Scale を含む）。</summary>
    public static float Scale => Screen.height / 1080f * (Plugin.Style?.Scale.Value ?? 1f);

    public static Color Hex(string hex)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out var c);
        return c;
    }

    public static Color PanelColor => new(0.02f, 0.025f, 0.03f, Plugin.Style?.PanelOpacity.Value ?? 0.6f);
    public static Color DividerColor => new(1f, 1f, 1f, 0.14f);
    public static Color TrackColor => new(1f, 1f, 1f, 0.13f);

    /// <summary>残りの割合による色（破壊 = 赤、半分未満 = 橙、少し減った = 黄、無傷 = 緑）。</summary>
    public static string HealthHex(float fraction, bool destroyed)
    {
        if (destroyed || fraction <= 0f)
            return Red;
        if (fraction < 0.5f)
            return Orange;
        return fraction < 0.98f ? Yellow : Green;
    }

    // ------------------------------------------------------------------ フォント

    /// <summary>ゲーム内で使われている TMP フォント（日本語は Localization が足したフォールバックで出る）。</summary>
    public static TMP_FontAsset Font
    {
        get
        {
            if (_font != null && _font.WasCollected)
                _font = null;
            if (_font == null && Time.unscaledTime >= _nextFontSearch)
            {
                _nextFontSearch = Time.unscaledTime + 1f;
                try
                {
                    _font = TMP_Settings.defaultFontAsset;
                }
                catch (Exception)
                {
                    // TMP_Settings が無いビルドもある
                }
                if (_font == null)
                    _font = Object.FindAnyObjectByType(Il2CppType.Of<TextMeshProUGUI>())?.TryCast<TextMeshProUGUI>()?.font;
            }
            return _font;
        }
    }

    // ------------------------------------------------------------------ 作成

    public static GameObject CreateCanvas(string name, int order, out CanvasGroup group)
    {
        var root = new GameObject(name);
        Object.DontDestroyOnLoad(root);
        root.hideFlags = HideFlags.HideAndDontSave;
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        group = root.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        return root;
    }

    /// <summary>左下を基準に置く Image。rounded なら角丸のパネル。</summary>
    public static Image CreateImage(Transform parent, string name, bool rounded)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        if (rounded)
        {
            image.sprite = Rounded;
            image.type = Image.Type.Sliced;
        }
        var rt = image.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        return image;
    }

    /// <summary>左下を基準に置く文字（自動翻訳の対象外。翻訳は呼び出し側で済ませる）。</summary>
    public static TextMeshProUGUI CreateText(Transform parent, string name, TextAlignmentOptions alignment, bool exclude = true)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        if (exclude)
            Plugin.Localization?.Exclude(text);
        text.font = Font;
        text.raycastTarget = false;
        text.richText = true;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        try
        {
            // パネルの上なので縁取りは薄く（白っぽい背景でパネルを薄くしたときの保険）
            text.outlineWidth = 0.12f;
            text.outlineColor = new Color32(0, 0, 0, 200);
        }
        catch (Exception)
        {
            // アウトライン非対応のシェーダーなら無視
        }
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        return text;
    }

    public static void Place(Graphic graphic, float x, float y, float width, float height)
    {
        var rt = graphic.rectTransform;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
    }

    /// <summary>パネルの色と角の大きさ（画面の大きさに合わせる）。</summary>
    public static void StylePanel(Image panel, float scale)
    {
        panel.color = PanelColor;
        panel.pixelsPerUnitMultiplier = 1f / Mathf.Max(0.1f, CornerScale * scale);
    }

    public static void SetActive(Component component, bool active)
    {
        if (component != null && component.gameObject.activeSelf != active)
            component.gameObject.SetActive(active);
    }

    /// <summary>白い角丸の四角（9 スライス）。</summary>
    private static Sprite Rounded
    {
        get
        {
            if (_rounded == null || _rounded.WasCollected)
                _rounded = MakeRounded();
            return _rounded;
        }
    }

    private static unsafe Sprite MakeRounded()
    {
        const int size = CornerTexture;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // 角の円からの距離（辺の部分は 0）
                var cx = Mathf.Max(0f, Mathf.Max(CornerRadius - (x + 0.5f), x + 0.5f - (size - CornerRadius)));
                var cy = Mathf.Max(0f, Mathf.Max(CornerRadius - (y + 0.5f), y + 0.5f - (size - CornerRadius)));
                var d = Mathf.Sqrt(cx * cx + cy * cy);
                var a = Mathf.Clamp01(CornerRadius - d + 0.5f);
                var i = (y * size + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Mathf.RoundToInt(a * 255f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        var border = CornerRadius + 1f;
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}

/// <summary>
/// パネルの中の 1 行:  名前 ──── バー ──── 値
/// バーは残りの割合（fill &lt; 0 ならバー無し）。
/// </summary>
internal sealed class StatRow
{
    private readonly TextMeshProUGUI _label;
    private readonly TextMeshProUGUI _value;
    private readonly Image _track;
    private readonly Image _fill;
    private float _fraction = -1f;
    private string _lastLabel;
    private string _lastValue;

    public StatRow(Transform parent)
    {
        _track = HudStyle.CreateImage(parent, "RowTrack", true);
        _fill = HudStyle.CreateImage(parent, "RowFill", true);
        _label = HudStyle.CreateText(parent, "RowLabel", TextAlignmentOptions.MidlineLeft);
        _value = HudStyle.CreateText(parent, "RowValue", TextAlignmentOptions.MidlineRight);
        _label.overflowMode = TextOverflowModes.Ellipsis;
    }

    public void Set(string label, string labelHex, string value, string valueHex, float fraction)
    {
        label = "<color=#" + labelHex + ">" + label + "</color>";
        value = "<color=#" + valueHex + ">" + value + "</color>";
        if (label != _lastLabel)
        {
            _label.text = label;
            _lastLabel = label;
        }
        if (value != _lastValue)
        {
            _value.text = value;
            _lastValue = value;
        }
        _fraction = fraction;
        _fill.color = HudStyle.Hex(valueHex);
    }

    /// <summary>top から下へ height の行を置く。labelWidth / valueWidth は列の幅。</summary>
    public void Layout(float x, float top, float width, float height, float fontSize, float labelWidth, float valueWidth, float scale)
    {
        var y = top - height;
        _label.fontSize = fontSize;
        _value.fontSize = fontSize;
        HudStyle.Place(_label, x, y, labelWidth, height);
        HudStyle.Place(_value, x + width - valueWidth, y, valueWidth, height);

        var showBar = _fraction >= 0f;
        HudStyle.SetActive(_track, showBar);
        HudStyle.SetActive(_fill, showBar && _fraction > 0.001f);
        if (!showBar)
            return;
        var gap = fontSize * 0.5f;
        var barX = x + labelWidth + gap;
        var barWidth = width - labelWidth - valueWidth - gap * 2f;
        var barHeight = Mathf.Max(3f, fontSize * 0.32f);
        var barY = y + (height - barHeight) * 0.5f;
        _track.color = HudStyle.TrackColor;
        _track.pixelsPerUnitMultiplier = _fill.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
        HudStyle.Place(_track, barX, barY, barWidth, barHeight);
        HudStyle.Place(_fill, barX, barY, barWidth * Mathf.Clamp01(_fraction), barHeight);
    }

    public void SetActive(bool active)
    {
        HudStyle.SetActive(_label, active);
        HudStyle.SetActive(_value, active);
        HudStyle.SetActive(_track, active && _fraction >= 0f);
        HudStyle.SetActive(_fill, active && _fraction > 0.001f);
    }
}
