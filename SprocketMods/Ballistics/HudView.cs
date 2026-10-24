using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods;

/// <summary>
/// uGUI による描画（このビルドでは IMGUI の GUI クラスが削除されているため Canvas + Image + TextMeshPro を使う）。
/// War Thunder AB 風に、着弾点へ円形のクロスヘア（切れ目の入ったリング＋中心点）を置き、判定を色で示す。
/// 距離と判定の文字には、背景が明るくても読めるよう角丸の半透明の黒い板を敷く。
/// 画面座標は Camera.WorldToScreenPoint のピクセル座標をそのまま、左下原点のアンカーで配置する。
/// </summary>
internal sealed class HudView
{
    private GameObject _root;
    private Image _ringShadow;
    private Image _ring;
    private Image _dotShadow;
    private Image _dot;
    private TextMeshProUGUI _distance;
    private TextMeshProUGUI _verdict;
    private Image _distancePlate;
    private Image _verdictPlate;
    private readonly List<Image> _segments = new();
    private readonly List<Vector3> _screen = new(512);
    private int _activeSegments;

    private static Sprite _ringSprite;
    private static Sprite _ringShadowSprite;
    private static Sprite _dotSprite;
    private static Sprite _dotShadowSprite;

    private bool _fontResolved;
    private float _nextFontSearch;

    private bool _reticleVisible = true;
    private bool _hasSmoothed;
    private Vector2 _smoothed;

    public void SetActive(bool active)
    {
        if (_root != null && _root.activeSelf != active)
        {
            _root.SetActive(active);
            if (!active)
                _hasSmoothed = false;
        }
    }

    public bool Ensure()
    {
        if (_root != null && _root.WasCollected)
            _root = null;
        if (_root == null)
            Create();
        if (!_fontResolved && Time.unscaledTime >= _nextFontSearch)
            ResolveFont();
        return _root != null;
    }

    private void Create()
    {
        EnsureSprites();

        _root = new GameObject("SprocketBallisticsHUD");
        Object.DontDestroyOnLoad(_root);
        _root.hideFlags = HideFlags.HideAndDontSave;
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;

        _segments.Clear();
        _activeSegments = 0;
        _ringShadow = CreateImage("RingShadow", _ringShadowSprite);
        _ring = CreateImage("Ring", _ringSprite);
        _dotShadow = CreateImage("DotShadow", _dotShadowSprite);
        _dot = CreateImage("Dot", _dotSprite);
        _distance = null;
        _verdict = null;
        _distancePlate = null;
        _verdictPlate = null;
        _fontResolved = false;
        _nextFontSearch = 0f;
        _reticleVisible = true;
        _hasSmoothed = false;
    }

    private Image CreateImage(string name, Sprite sprite)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        if (sprite != null)
            image.sprite = sprite;
        var rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        return image;
    }

    // ---------------------------------------------------------------- sprites

    private static void EnsureSprites()
    {
        if (_ringSprite != null && !_ringSprite.WasCollected)
            return;
        const int size = 128;
        const float radius = 54f;
        // 4 方向に切れ目の入ったリング（白。色は Image.color で付ける）
        _ringSprite = MakeSprite(size, (x, y) => Ring(x, y, size, radius, 3.5f, 14f));
        _ringShadowSprite = MakeSprite(size, (x, y) => Ring(x, y, size, radius, 7.5f, 10f));
        _dotSprite = MakeSprite(32, (x, y) => Disc(x, y, 32, 5.5f));
        _dotShadowSprite = MakeSprite(32, (x, y) => Disc(x, y, 32, 9f));
    }

    /// <summary>中心からの距離が radius ± halfWidth の帯。0/90/180/270° の周りに gapDegrees ぶんの切れ目。</summary>
    private static float Ring(float x, float y, int size, float radius, float halfWidth, float gapDegrees)
    {
        var c = (size - 1) * 0.5f;
        var dx = x - c;
        var dy = y - c;
        var d = Mathf.Sqrt(dx * dx + dy * dy);
        var a = Mathf.Clamp01(halfWidth + 0.5f - Mathf.Abs(d - radius));
        var angle = Mathf.Abs(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg) % 90f;
        var fromAxis = Mathf.Min(angle, 90f - angle);
        var gapEdge = (fromAxis - gapDegrees) * Mathf.Deg2Rad * radius;
        return a * Mathf.Clamp01(gapEdge + 0.5f);
    }

    private static float Disc(float x, float y, int size, float radius)
    {
        var c = (size - 1) * 0.5f;
        var dx = x - c;
        var dy = y - c;
        return Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
    }

    private static unsafe Sprite MakeSprite(int size, Func<float, float, float> alpha)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha(x, y)) * 255f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // ---------------------------------------------------------------- text

    /// <summary>ゲーム内で使われている TMP フォントを借りる。</summary>
    private void ResolveFont()
    {
        _nextFontSearch = Time.unscaledTime + 1f;
        TMP_FontAsset font = null;
        try
        {
            font = TMP_Settings.defaultFontAsset;
        }
        catch (Exception)
        {
            // TMP_Settings が無いビルドもある
        }
        if (font == null)
        {
            var text = Object.FindAnyObjectByType(Il2CppType.Of<TextMeshProUGUI>())?.TryCast<TextMeshProUGUI>();
            font = text?.font;
        }
        if (font == null)
            return;

        _distancePlate = CreatePlate("DistancePlate");
        _verdictPlate = CreatePlate("VerdictPlate");
        _distance = CreateText("Distance", font, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0.5f));
        _verdict = CreateText("Verdict", font, TextAlignmentOptions.Top, new Vector2(0.5f, 1f));

        _fontResolved = true;
        Plugin.Logger.LogInfo($"HUD font: {font.name}");
    }

    private Image CreatePlate(string name)
    {
        var plate = HudStyle.CreateImage(_root.transform, name, true);
        plate.gameObject.SetActive(false);
        return plate;
    }

    /// <summary>文字の大きさに合わせて後ろの板を置く。pivot は文字の RectTransform と同じ。</summary>
    private static void PlacePlate(Image plate, TextMeshProUGUI label, Vector2 anchor, Vector2 pivot)
    {
        var visible = label != null && label.gameObject.activeSelf && Plugin.Style.PanelOpacity.Value > 0f;
        HudStyle.SetActive(plate, visible);
        if (!visible)
            return;
        var scale = Screen.height / 1080f;
        var padX = label.fontSize * 0.45f;
        var padY = label.fontSize * 0.15f;
        var size = new Vector2(label.preferredWidth + padX * 2f, label.preferredHeight + padY * 2f);
        // 文字の揃え位置（pivot）に合わせて板の左下を求める
        var origin = anchor - Vector2.Scale(new Vector2(label.preferredWidth, label.preferredHeight), pivot) - new Vector2(padX, padY);
        HudStyle.StylePanel(plate, scale);
        var c = plate.color;
        c.a *= 0.85f;
        plate.color = c;
        HudStyle.Place(plate, origin.x, origin.y, size.x, size.y);
    }

    private TextMeshProUGUI CreateText(string name, TMP_FontAsset font, TextAlignmentOptions alignment, Vector2 pivot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.raycastTarget = false;
        text.richText = true;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        try
        {
            text.outlineWidth = 0.25f;
            text.outlineColor = new Color32(0, 0, 0, 255);
        }
        catch (Exception)
        {
            // アウトライン非対応のシェーダーなら無視
        }
        var rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = pivot;
        rt.sizeDelta = new Vector2(400f, 80f);
        return text;
    }

    // ---------------------------------------------------------------- crosshair

    public void ShowCrosshair(Camera camera, Vector3 worldPoint, Color color, string distance, string verdict)
    {
        var cfg = Plugin.Cfg;
        var s = camera.WorldToScreenPoint(worldPoint);
        if (s.z <= 0.05f)
        {
            HideCrosshair();
            return;
        }

        var target = new Vector2(s.x, s.y);
        var tau = cfg.ReticleSmoothing.Value;
        if (!_hasSmoothed || tau <= 0f || (target - _smoothed).sqrMagnitude > 250f * 250f)
            _smoothed = target;
        else
            _smoothed = Vector2.Lerp(_smoothed, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime / tau));
        _hasSmoothed = true;
        var p = _smoothed;

        var size = cfg.ReticleSize.Value;
        var shadow = new Color(0f, 0f, 0f, 0.55f * color.a);
        Place(_ringShadow, p, size, shadow);
        Place(_ring, p, size, color);
        var dotSize = Mathf.Max(8f, size * 0.32f);
        Place(_dotShadow, p, dotSize, shadow);
        Place(_dot, p, dotSize, color);
        SetReticleVisible(true);

        var fontSize = cfg.FontSize.Value;
        if (_distance != null)
        {
            SetText(_distance, cfg.ShowDistance.Value ? distance : null, color, fontSize);
            _distance.rectTransform.anchoredPosition = p + new Vector2(size * 0.5f + 10f, 0f);
            PlacePlate(_distancePlate, _distance, _distance.rectTransform.anchoredPosition, new Vector2(0f, 0.5f));
        }
        if (_verdict != null)
        {
            SetText(_verdict, cfg.ShowVerdictText.Value ? verdict : null, color, fontSize);
            _verdict.rectTransform.anchoredPosition = p + new Vector2(0f, -size * 0.5f - 8f);
            PlacePlate(_verdictPlate, _verdict, _verdict.rectTransform.anchoredPosition, new Vector2(0.5f, 1f));
        }
    }

    public void HideCrosshair()
    {
        SetReticleVisible(false);
        _hasSmoothed = false;
    }

    private static void Place(Image image, Vector2 position, float size, Color color)
    {
        var rt = image.rectTransform;
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(size, size);
        image.color = color;
    }

    private void SetReticleVisible(bool visible)
    {
        if (_reticleVisible == visible)
            return;
        _reticleVisible = visible;
        _ringShadow.gameObject.SetActive(visible);
        _ring.gameObject.SetActive(visible);
        _dotShadow.gameObject.SetActive(visible);
        _dot.gameObject.SetActive(visible);
        if (_distance != null)
            _distance.gameObject.SetActive(visible);
        if (_verdict != null)
            _verdict.gameObject.SetActive(visible);
        if (!visible)
        {
            HudStyle.SetActive(_distancePlate, false);
            HudStyle.SetActive(_verdictPlate, false);
        }
    }

    private static void SetText(TextMeshProUGUI label, string text, Color color, float fontSize)
    {
        var visible = !string.IsNullOrEmpty(text);
        if (label.gameObject.activeSelf != visible)
            label.gameObject.SetActive(visible);
        if (!visible)
            return;
        label.fontSize = fontSize;
        label.color = color;
        label.text = text;
    }

    // ---------------------------------------------------------------- trajectory (任意)

    public void ShowTrajectory(Camera camera, List<Vector3> points, Color color)
    {
        var cfg = Plugin.Cfg;
        var maxSegments = cfg.TrajectorySegments.Value;
        var width = cfg.TrajectoryWidth.Value;

        _screen.Clear();
        if (points.Count >= 2)
        {
            var stride = Mathf.Max(1, Mathf.CeilToInt((points.Count - 1) / (float)maxSegments));
            for (var i = 0; i < points.Count; i += stride)
                _screen.Add(camera.WorldToScreenPoint(points[i]));
            if ((points.Count - 1) % stride != 0)
                _screen.Add(camera.WorldToScreenPoint(points[points.Count - 1]));
        }

        var used = 0;
        for (var i = 0; i + 1 < _screen.Count; i++)
        {
            var a = _screen[i];
            var b = _screen[i + 1];
            if (a.z <= 0.05f || b.z <= 0.05f)
                continue;
            var delta = new Vector2(b.x - a.x, b.y - a.y);
            var length = delta.magnitude;
            if (length < 0.5f)
                continue;

            var segment = GetSegment(used++);
            var rt = segment.rectTransform;
            rt.anchoredPosition = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            rt.sizeDelta = new Vector2(length + width * 0.5f, width);
            rt.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            segment.color = color;
        }
        SetActiveSegments(used);
    }

    public void HideTrajectory() => SetActiveSegments(0);

    private Image GetSegment(int index)
    {
        while (_segments.Count <= index)
        {
            var image = CreateImage($"Trajectory{_segments.Count}", null);
            image.gameObject.SetActive(false);
            image.transform.SetAsFirstSibling(); // クロスヘアより奥に描く
            _segments.Add(image);
        }
        return _segments[index];
    }

    private void SetActiveSegments(int count)
    {
        for (var i = count; i < _activeSegments; i++)
            _segments[i].gameObject.SetActive(false);
        for (var i = _activeSegments; i < count; i++)
            _segments[i].gameObject.SetActive(true);
        _activeSegments = count;
    }
}
