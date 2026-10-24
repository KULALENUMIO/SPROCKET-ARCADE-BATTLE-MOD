using System;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMods.Repair;

/// <summary>
/// 修理の表示（画面中央の下）。
///  - 修理キーを押している 1 秒の間、白い円がぎゅるんと一周する（一周で修理が始まる）
///  - 修理中は白い円のタイマー（直し終わると一周）と、その中に残り秒数、下に「修理中」と「[F] 長押しで中止」
///  - 修理中にキーを押すと、薄い赤の円が一周して中止
///  - 修理できる損傷があって修理していないときは「[F] 長押しで修理（18 秒）」（ShowHint）
///  - 直し終えたら「修理完了」を少しの間出す
/// </summary>
internal sealed class RepairView
{
    private const float CompletedTime = 1.5f;

    private static Sprite _ring;

    private GameObject _root;
    private CanvasGroup _group;
    private Image _track;
    private Image _fill;
    private TextMeshProUGUI _seconds;
    private TextMeshProUGUI _label;
    private TextMeshProUGUI _hint;
    private string _lastSeconds;
    private string _lastLabel;
    private string _lastHint;

    public void LateUpdate()
    {
        var cfg = Plugin.Repair;
        var system = RepairSystem.Instance;
        if (!cfg.Enabled.Value || system == null || Time.timeScale <= 0f || !Ensure())
        {
            SetActive(false);
            return;
        }
        var mode = system.PlayerMode;
        var repairing = mode == RepairMode.Repairing;
        var arming = mode == RepairMode.Arming;
        var cancelling = repairing && system.PlayerArmFraction > 0f;
        var completed = !repairing && !arming && Time.unscaledTime - system.PlayerCompletedAt < CompletedTime;
        var hint = !repairing && !arming && !completed && system.PlayerDamaged && cfg.ShowHint.Value;
        if (!repairing && !arming && !completed && !hint)
        {
            SetActive(false);
            return;
        }
        SetActive(true);

        var s = Screen.height / 1080f;
        var cx = Screen.width * 0.5f;
        var cy = 230f * s;
        var ring = 76f * s;
        var key = cfg.Key.Value.ToString();

        var showRing = repairing || arming || completed;
        HudStyle.SetActive(_track, showRing);
        HudStyle.SetActive(_fill, showRing);
        HudStyle.SetActive(_seconds, showRing);
        HudStyle.SetActive(_label, showRing);
        HudStyle.SetActive(_hint, hint || repairing);
        if (showRing)
        {
            Place(_track, cx, cy, ring);
            Place(_fill, cx, cy, ring);
            _track.color = new Color(1f, 1f, 1f, 0.22f);
            float fill;
            string label;
            if (completed)
            {
                fill = 1f;
                label = T("Repair complete");
                _fill.color = Color.white;
            }
            else if (arming)
            {
                // 押している 1 秒で白い円がぎゅるんと一周して、修理が始まる
                fill = Ease(system.PlayerArmFraction);
                label = T("Starting repair");
                _fill.color = Color.white;
            }
            else if (cancelling)
            {
                fill = Ease(system.PlayerArmFraction);
                label = T("Cancelling repair");
                _fill.color = new Color(1f, 0.55f, 0.5f, 1f);
            }
            else
            {
                fill = system.PlayerFraction;
                label = T("Repairing");
                _fill.color = Color.white;
            }
            _fill.fillAmount = fill;

            var seconds = completed ? "0" : Mathf.CeilToInt(system.PlayerRemaining).ToString();
            if (seconds != _lastSeconds)
            {
                _seconds.text = "<b>" + seconds + "</b>";
                _lastSeconds = seconds;
            }
            _seconds.fontSize = 26f * s;
            HudStyle.Place(_seconds, cx - ring * 0.5f, cy - ring * 0.5f, ring, ring);

            if (label != _lastLabel)
            {
                _label.text = label;
                _lastLabel = label;
            }
            _label.fontSize = 17f * s;
            HudStyle.Place(_label, cx - 150f * s, cy - ring * 0.5f - 30f * s, 300f * s, 26f * s);
        }

        if (hint || repairing)
        {
            string text;
            float y;
            if (repairing)
            {
                // 修理中は「長押しで中止」を小さく
                text = "<color=#BBBBBB>" + T("Hold [KEY] to cancel").Replace("KEY", key) + "</color>";
                y = cy - ring * 0.5f - 54f * s;
                _hint.fontSize = 13f * s;
            }
            else
            {
                text = system.PlayerNoCrew
                    ? "<color=#BBBBBB>" + T("No crew to repair") + "</color>"
                    : "<color=#EEEEEE>" + T("Hold [KEY] to repair (" + Mathf.CeilToInt(system.PlayerRemaining) + " s)").Replace("KEY", key) + "</color>";
                y = cy - 14f * s;
                _hint.fontSize = 16f * s;
            }
            if (text != _lastHint)
            {
                _hint.text = text;
                _lastHint = text;
            }
            HudStyle.Place(_hint, cx - 250f * s, y, 500f * s, 28f * s);
        }
    }

    /// <summary>始めはゆっくり、すぐ加速して一気に回る。</summary>
    private static float Ease(float t) => t * t * (3f - 2f * t);

    private static void Place(Image image, float cx, float cy, float size) =>
        HudStyle.Place(image, cx - size * 0.5f, cy - size * 0.5f, size, size);

    private static string T(string english) => Plugin.Localization?.Translate(english) ?? english;

    // ------------------------------------------------------------------ 作成

    private void SetActive(bool active)
    {
        if (_root != null && !_root.WasCollected && _root.activeSelf != active)
            _root.SetActive(active);
    }

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
            _root = null;
        if (_root != null)
            return true;
        if (HudStyle.Font == null)
            return false;
        EnsureSprite();
        _root = HudStyle.CreateCanvas("SprocketRepair", 30005, out _group);
        var t = _root.transform;
        _track = HudStyle.CreateImage(t, "RingTrack", false);
        _track.sprite = _ring;
        _fill = HudStyle.CreateImage(t, "RingFill", false);
        _fill.sprite = _ring;
        _fill.type = Image.Type.Filled;
        _fill.fillMethod = Image.FillMethod.Radial360;
        _fill.fillOrigin = (int)Image.Origin360.Top;
        _fill.fillClockwise = true;
        _seconds = HudStyle.CreateText(t, "Seconds", TextAlignmentOptions.Center);
        _label = HudStyle.CreateText(t, "Label", TextAlignmentOptions.Center);
        _hint = HudStyle.CreateText(t, "Hint", TextAlignmentOptions.Center);
        _lastSeconds = _lastLabel = _lastHint = null;
        return true;
    }

    /// <summary>白いリング（外側に薄い黒い縁。明るい背景でも見えるように）。</summary>
    private static unsafe void EnsureSprite()
    {
        if (_ring != null && !_ring.WasCollected)
            return;
        const int size = 128;
        const float outerRadius = 0.92f;
        const float innerRadius = 0.78f;
        const float border = 0.05f;
        var pixels = new byte[size * size * 4];
        var texel = 2f / size;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = (px + 0.5f) * texel - 1f;
                var y = (py + 0.5f) * texel - 1f;
                var r = Mathf.Sqrt(x * x + y * y);
                var distance = Mathf.Min(outerRadius - r, r - innerRadius);
                var inner = Mathf.Clamp01(distance / texel + 0.5f);
                var outer = Mathf.Clamp01((distance + border) / texel + 0.5f) * 0.6f;
                var a = Mathf.Max(inner, outer);
                var v = a > 0f ? inner / a : 0f;
                var i = (py * size + px) * 4;
                var value = (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
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
        _ring = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _ring.hideFlags = HideFlags.HideAndDontSave;
    }
}
