using System.Collections.Generic;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMods.Teams;

/// <summary>
/// War Thunder 風の勢力ゲージ。画面上部の中央に半透明の黒いパネルを出し、
///   [ 5/8 ] ■■■■■□□□ ◆ ■■■■□□□□ [ 4/8 ]
/// 左が味方（青）、右が敵（赤）。ゲージは中央から外へ伸び、撃破されると外側の目盛りから暗くなる
/// （減った目盛りは一瞬白く光ってから暗くなる）。車両が多いとき（MaxSegments 超）は 1 本のバー。
/// </summary>
internal sealed class TeamBarView
{
    private const int MaxSegments = 40;
    private const float FlashTime = 0.9f;
    private const float FadeTime = 0.3f;

    private sealed class Side
    {
        public TextMeshProUGUI Count;
        public Image Track;
        public Image Fill;
        public readonly List<Image> Segments = new();
        public int LastAlive = -1;
        public int LostFrom;
        public int LostTo;
        public float LostAt = float.NegativeInfinity;
        public float ShownFraction = 1f;
        public string LastText;
    }

    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private Image _divider;
    private readonly Side _friendly = new();
    private readonly Side _enemy = new();
    private float _alpha;

    public void LateUpdate()
    {
        var tracker = TeamStrengthTracker.Instance;
        var show = Plugin.TeamBar.Enabled.Value && tracker != null && tracker.Valid && Time.timeScale > 0f;
        _alpha = Mathf.MoveTowards(_alpha, show ? 1f : 0f, Time.unscaledDeltaTime / FadeTime);
        if (_alpha <= 0f || tracker == null || !Ensure())
        {
            if (_root != null && !_root.WasCollected && _root.activeSelf)
                _root.SetActive(false);
            return;
        }
        if (!_root.activeSelf)
            _root.SetActive(true);
        _group.alpha = _alpha;

        var scale = HudStyle.Scale;
        var width = 600f * scale;
        var height = 46f * scale;
        var pad = 10f * scale;
        var numberWidth = 74f * scale;
        var centerGap = 22f * scale;
        var barHeight = 14f * scale;
        var cx = Screen.width * 0.5f;
        var top = Screen.height - 12f * scale;
        var bottom = top - height;
        var left = cx - width * 0.5f;
        var right = cx + width * 0.5f;
        var barY = bottom + (height - barHeight) * 0.5f;

        // 空を背にすることが多いので、ほかのパネルより濃くする
        HudStyle.StylePanel(_panel, scale);
        var panelColour = _panel.color;
        panelColour.a = Mathf.Min(1f, panelColour.a * 1.35f);
        _panel.color = panelColour;
        HudStyle.Place(_panel, left, bottom, width, height);

        // 中央の菱形
        var diamond = 12f * scale;
        _divider.color = new Color(1f, 1f, 1f, 0.85f);
        var rt = _divider.rectTransform;
        rt.anchoredPosition = new Vector2(cx, bottom + height * 0.5f);
        rt.sizeDelta = new Vector2(diamond, diamond);

        var barLength = cx - centerGap * 0.5f - (left + pad + numberWidth + 6f * scale);
        DrawSide(_friendly, tracker.FriendlyAlive, tracker.FriendlyTotal, HudStyle.Blue,
            cx - centerGap * 0.5f, -1f, barLength, barY, barHeight, scale);
        DrawSide(_enemy, tracker.EnemyAlive, tracker.EnemyTotal, HudStyle.Red,
            cx + centerGap * 0.5f, 1f, barLength, barY, barHeight, scale);

        var font = 26f * scale;
        _friendly.Count.fontSize = font;
        _enemy.Count.fontSize = font;
        HudStyle.Place(_friendly.Count, left + pad, bottom, numberWidth, height);
        HudStyle.Place(_enemy.Count, right - pad - numberWidth, bottom, numberWidth, height);
    }

    /// <summary>origin（中央側の端）から direction（-1 = 左、1 = 右）へ length のゲージを描く。</summary>
    private void DrawSide(Side side, int alive, int total, string hex, float origin, float direction, float length,
        float y, float height, float scale)
    {
        var now = Time.unscaledTime;
        if (side.LastAlive >= 0 && alive < side.LastAlive)
        {
            side.LostFrom = alive;
            side.LostTo = side.LastAlive;
            side.LostAt = now;
        }
        side.LastAlive = alive;

        var text = "<b>" + alive + "</b><size=58%><color=#D0D0D0>/" + total + "</color></size>";
        text = "<color=#" + hex + ">" + text + "</color>";
        if (text != side.LastText)
        {
            side.Count.text = text;
            side.LastText = text;
        }

        var colour = HudStyle.Hex(hex);
        var flash = Mathf.Clamp01((now - side.LostAt) / FlashTime);
        var segmented = Plugin.TeamBar.Segmented.Value && total > 0 && total <= MaxSegments;
        HudStyle.SetActive(side.Track, !segmented);
        HudStyle.SetActive(side.Fill, !segmented);
        if (segmented)
        {
            var gap = Mathf.Max(1f, (total > 20 ? 2f : 3f) * scale);
            var segment = (length - gap * (total - 1)) / total;
            for (var i = 0; i < total; i++)
            {
                var image = GetSegment(side, i);
                Color c;
                if (i < alive)
                    c = colour;
                else if (i >= side.LostFrom && i < side.LostTo && flash < 1f)
                    c = Color.Lerp(Color.white, HudStyle.TrackColor, flash); // 撃破された目盛りは白く光ってから暗く
                else
                    c = HudStyle.TrackColor;
                image.color = c;
                var start = i * (segment + gap);
                var x = direction < 0f ? origin - start - segment : origin + start;
                HudStyle.Place(image, x, y, segment, height);
            }
            HideSegments(side, total);
            side.ShownFraction = total > 0 ? alive / (float)total : 0f;
            return;
        }

        HideSegments(side, 0);
        var target = total > 0 ? alive / (float)total : 0f;
        side.ShownFraction = Mathf.MoveTowards(side.ShownFraction, target, Time.unscaledDeltaTime * 0.6f);
        side.Track.color = HudStyle.TrackColor;
        side.Track.pixelsPerUnitMultiplier = side.Fill.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
        var fillLength = length * side.ShownFraction;
        HudStyle.Place(side.Track, direction < 0f ? origin - length : origin, y, length, height);
        side.Fill.color = flash < 1f ? Color.Lerp(Color.white, colour, flash) : colour;
        HudStyle.Place(side.Fill, direction < 0f ? origin - fillLength : origin, y, fillLength, height);
    }

    private Image GetSegment(Side side, int index)
    {
        while (side.Segments.Count <= index)
            side.Segments.Add(HudStyle.CreateImage(_root.transform, "Segment", false));
        var image = side.Segments[index];
        HudStyle.SetActive(image, true);
        return image;
    }

    private static void HideSegments(Side side, int from)
    {
        for (var i = from; i < side.Segments.Count; i++)
            HudStyle.SetActive(side.Segments[i], false);
    }

    // ------------------------------------------------------------------ 作成

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
            _root = null;
        if (_root != null)
            return true;
        if (HudStyle.Font == null)
            return false;
        _root = HudStyle.CreateCanvas("SprocketTeamBar", 30001, out _group);
        var t = _root.transform;
        _panel = HudStyle.CreateImage(t, "Panel", true);
        _divider = HudStyle.CreateImage(t, "Divider", false);
        var drt = _divider.rectTransform;
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.localEulerAngles = new Vector3(0f, 0f, 45f);
        Create(_friendly, t, TextAlignmentOptions.MidlineLeft);
        Create(_enemy, t, TextAlignmentOptions.MidlineRight);
        return true;
    }

    private static void Create(Side side, Transform parent, TextAlignmentOptions alignment)
    {
        side.Track = HudStyle.CreateImage(parent, "Track", true);
        side.Fill = HudStyle.CreateImage(parent, "Fill", true);
        side.Count = HudStyle.CreateText(parent, "Count", alignment);
        side.Segments.Clear();
        side.LastAlive = -1;
        side.LastText = null;
        side.ShownFraction = 1f;
    }
}
