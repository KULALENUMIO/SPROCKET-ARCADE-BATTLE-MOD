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
    private bool _rootActive;
    private Core.Fast.Projector _projector;
    private float _lastFrameTime = -1f;

    // 文字の大きさの覚え（preferredWidth / Height は読むたびに文字の配置を計算し、値も箱詰めされる）
    private readonly Dictionary<IntPtr, (string Text, float Size, float Width, float Height)> _textSize = new();

    public void SetActive(bool active)
    {
        if (!Core.Fast.Alive(_root) || _rootActive == active)
            return;
        _rootActive = active;
        _root.SetActive(active);
        if (!active)
            _hasSmoothed = false;
    }

    public bool Ensure()
    {
        if (_root is not null && !Core.Fast.Alive(_root))
            _root = null;
        if (_root is null)
        {
            Create();
            _rootActive = Core.Fast.Alive(_root) && _root.activeSelf;
        }
        if (!_fontResolved && Core.Fast.Unscaled >= _nextFontSearch)
            ResolveFont();
        return _root is not null;
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
        _nextFontSearch = Core.Fast.Unscaled + 1f;
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
        HudStyle.SetActive(plate, false);
        return plate;
    }

    /// <summary>文字の大きさに合わせて後ろの板を置く。pivot は文字の RectTransform と同じ。</summary>
    private void PlacePlate(Image plate, TextMeshProUGUI label, string text, float fontSize, Vector2 anchor, float pivotX, float pivotY)
    {
        var visible = HudStyle.IsActive(label) && Plugin.Style.PanelOpacity.Value > 0f;
        HudStyle.SetActive(plate, visible);
        if (!visible)
            return;
        var scale = Core.Fast.ScreenHeight / 1080f;
        var padX = fontSize * 0.45f;
        var padY = fontSize * 0.15f;
        if (!_textSize.TryGetValue(label.Pointer, out var measured) || measured.Text != text || measured.Size != fontSize)
        {
            measured = (text, fontSize, label.preferredWidth, label.preferredHeight);
            _textSize[label.Pointer] = measured;
        }
        // 文字の揃え位置（pivot）に合わせて板の左下を求める
        var originX = anchor.x - measured.Width * pivotX - padX;
        var originY = anchor.y - measured.Height * pivotY - padY;
        HudStyle.StylePanel(plate, scale);
        var c = HudStyle.PanelColor;
        c.a *= 0.85f;
        plate.color = c;
        HudStyle.Place(plate, originX, originY, measured.Width + padX * 2f, measured.Height + padY * 2f);
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
        _projector.Set(camera);
        var s = _projector.Screen(worldPoint);
        if (s.z <= 0.05f)
        {
            HideCrosshair();
            return;
        }

        // 1 フレームの長さ（Time.unscaledDeltaTime は箱詰めされるので、前のフレームの時刻との差）
        var now = Core.Fast.Unscaled;
        var dt = _lastFrameTime >= 0f ? Math.Clamp(now - _lastFrameTime, 0f, 0.25f) : 0f;
        _lastFrameTime = now;
        var target = Core.Fast.V2(s.x, s.y);
        var tau = cfg.ReticleSmoothing.Value;
        var dx = target.x - _smoothed.x;
        var dy = target.y - _smoothed.y;
        if (!_hasSmoothed || tau <= 0f || dx * dx + dy * dy > 250f * 250f)
            _smoothed = target;
        else
        {
            var k = 1f - MathF.Exp(-dt / tau);
            _smoothed = Core.Fast.V2(_smoothed.x + dx * k, _smoothed.y + dy * k);
        }
        _hasSmoothed = true;
        var p = _smoothed;

        var size = cfg.ReticleSize.Value;
        var shadow = Core.Fast.Rgba(0f, 0f, 0f, 0.55f * color.a);
        Place(_ringShadow, p, size, shadow);
        Place(_ring, p, size, color);
        var dotSize = MathF.Max(8f, size * 0.32f);
        Place(_dotShadow, p, dotSize, shadow);
        Place(_dot, p, dotSize, color);
        SetReticleVisible(true);

        var fontSize = cfg.FontSize.Value;
        if (_distance is not null)
        {
            var text = cfg.ShowDistance.Value ? distance : null;
            SetText(_distance, text, color, fontSize);
            var anchor = Core.Fast.V2(p.x + size * 0.5f + 10f, p.y);
            _distance.rectTransform.anchoredPosition = anchor;
            PlacePlate(_distancePlate, _distance, text, fontSize, anchor, 0f, 0.5f);
        }
        if (_verdict is not null)
        {
            var text = cfg.ShowVerdictText.Value ? verdict : null;
            SetText(_verdict, text, color, fontSize);
            var anchor = Core.Fast.V2(p.x, p.y - size * 0.5f - 8f);
            _verdict.rectTransform.anchoredPosition = anchor;
            PlacePlate(_verdictPlate, _verdict, text, fontSize, anchor, 0.5f, 1f);
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
        rt.sizeDelta = Core.Fast.V2(size, size);
        image.color = color;
    }

    private void SetReticleVisible(bool visible)
    {
        if (_reticleVisible == visible)
            return;
        _reticleVisible = visible;
        HudStyle.SetActive(_ringShadow, visible);
        HudStyle.SetActive(_ring, visible);
        HudStyle.SetActive(_dotShadow, visible);
        HudStyle.SetActive(_dot, visible);
        if (_distance is not null)
            HudStyle.SetActive(_distance, visible);
        if (_verdict is not null)
            HudStyle.SetActive(_verdict, visible);
        if (!visible)
        {
            HudStyle.SetActive(_distancePlate, false);
            HudStyle.SetActive(_verdictPlate, false);
        }
    }

    private static void SetText(TextMeshProUGUI label, string text, Color color, float fontSize)
    {
        var visible = !string.IsNullOrEmpty(text);
        if (HudStyle.IsActive(label) != visible)
            HudStyle.SetActive(label, visible);
        if (!visible)
            return;
        HudStyle.SetFontSize(label, fontSize);
        label.color = color;
        HudStyle.SetText(label, text);
    }

    // ---------------------------------------------------------------- trajectory (任意)

    public void ShowTrajectory(Camera camera, List<Vector3> points, Color color)
    {
        var cfg = Plugin.Cfg;
        var maxSegments = cfg.TrajectorySegments.Value;
        var width = cfg.TrajectoryWidth.Value;

        _screen.Clear();
        _projector.Set(camera);
        if (points.Count >= 2)
        {
            var stride = Math.Max(1, (int)MathF.Ceiling((points.Count - 1) / (float)maxSegments));
            for (var i = 0; i < points.Count; i += stride)
                _screen.Add(_projector.Screen(points[i]));
            if ((points.Count - 1) % stride != 0)
                _screen.Add(_projector.Screen(points[points.Count - 1]));
        }

        var used = 0;
        for (var i = 0; i + 1 < _screen.Count; i++)
        {
            var a = _screen[i];
            var b = _screen[i + 1];
            if (a.z <= 0.05f || b.z <= 0.05f)
                continue;
            var deltaX = b.x - a.x;
            var deltaY = b.y - a.y;
            var length = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (length < 0.5f)
                continue;

            var segment = GetSegment(used++);
            var rt = segment.rectTransform;
            rt.anchoredPosition = Core.Fast.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            rt.sizeDelta = Core.Fast.V2(length + width * 0.5f, width);
            rt.localRotation = Core.Fast.RotZ(MathF.Atan2(deltaY, deltaX) * (180f / MathF.PI));
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
            HudStyle.SetActive(image, false);
            image.transform.SetAsFirstSibling(); // クロスヘアより奥に描く
            _segments.Add(image);
        }
        return _segments[index];
    }

    private void SetActiveSegments(int count)
    {
        for (var i = count; i < _activeSegments; i++)
            HudStyle.SetActive(_segments[i], false);
        for (var i = _activeSegments; i < count; i++)
            HudStyle.SetActive(_segments[i], true);
        _activeSegments = count;
    }
}
