using System;
using System.Collections.Generic;
using System.Text;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMods.Hits;

/// <summary>
/// 命中結果の表示。照準の下に半透明の黒いパネルを出し、その中に
///   ┃ 貫通                          ×3
///   ┃ ──────────────────────────────
///   ┃ 砲手        □□□□□□□□□□   戦死
///   ┃ エンジン    ■■■■□□□□□□   42%
///   ┃ 火災
/// を並べる（左端の帯と見出しの色: 撃破 = 赤 / 貫通 = 橙 / 非貫通 = 灰 / 跳弾 = 青灰）。
/// 損傷したモジュールの位置には菱形のマーカーを出す。色: 破壊・戦死 = 赤、損傷 = 橙〜黄（残り体力で変わる）。
/// 文言は英語の原文を Localization で訳して組み立てる（このテキストは自動翻訳の対象から外す）。
/// </summary>
internal sealed class HitReportView
{
    private const float FadeTime = 0.6f;
    private const float MarkerTime = 2.5f;

    private const string RicochetHex = "B3D1F2";
    private const string GreyHex = "C8C8C8";

    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private Image _accent;
    private Image _divider;
    private TextMeshProUGUI _header;
    private TextMeshProUGUI _count;
    private TextMeshProUGUI _note;
    private readonly List<StatRow> _rows = new();
    private int _rowCount;
    private bool _hasNote;
    private string _accentHex = GreyHex;

    private readonly List<Image> _markers = new();
    private static Sprite _diamond;
    private HitReportData _shown;
    private int _shownVersion = -1;
    private readonly StringBuilder _sb = new();

    public void LateUpdate()
    {
        var cfg = Plugin.HitReport;
        var tracker = HitTracker.Instance;
        var data = cfg.Enabled.Value ? tracker?.Current : null;
        var alpha = data == null ? 0f : Mathf.Clamp01((cfg.Duration.Value - (Time.time - data.LastChange)) / FadeTime);
        if (alpha <= 0f || Time.timeScale <= 0f || !Ensure())
        {
            SetActive(false);
            return;
        }
        SetActive(true);
        _group.alpha = alpha;

        if (data != _shown || data.Version != _shownVersion)
        {
            _shown = data;
            _shownVersion = data.Version;
            Compose(data);
        }
        Layout();
        DrawMarkers(data, tracker.ActiveCamera);
    }

    // ------------------------------------------------------------------ 内容

    private void Compose(HitReportData data)
    {
        var (header, colour) = data.Outcome switch
        {
            HitOutcome.KnockedOut => ("KNOCKED OUT", HudStyle.Red),
            HitOutcome.Penetration => ("PENETRATION", HudStyle.Orange),
            HitOutcome.Ricochet => ("RICOCHET", RicochetHex),
            _ => ("NON-PENETRATION", GreyHex),
        };
        _accentHex = colour;
        _header.text = "<b><color=#" + colour + ">" + T(header) + "</color></b>";
        var showCount = data.Hits > 1 && data.Outcome != HitOutcome.KnockedOut;
        _count.text = showCount ? "<color=#" + HudStyle.Grey + ">×" + data.Hits + "</color>" : string.Empty;

        _rowCount = 0;
        var max = Plugin.HitReport.MaxLines.Value;
        var hidden = 0;
        foreach (var m in data.Modules)
        {
            if (_rowCount >= max)
            {
                hidden++;
                continue;
            }
            var destroyed = m.State == ModuleState.Destroyed;
            var label = T(m.Label);
            if (m.Count > 1)
                label += " ×" + m.Count;
            var value = destroyed
                ? T(m.IsCrew ? "killed" : "destroyed")
                : Mathf.RoundToInt(m.WorstFraction * 100f) + "%";
            Row().Set(label, HudStyle.White, value, HudStyle.HealthHex(m.WorstFraction, destroyed), destroyed ? 0f : m.WorstFraction);
        }
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].SetActive(i < _rowCount);

        _sb.Clear();
        if (data.OnFire)
            _sb.Append("<b><color=#").Append(HudStyle.Fire).Append('>').Append(T("On fire")).Append("</color></b>");
        if (hidden > 0)
        {
            if (_sb.Length > 0)
                _sb.Append("  ");
            _sb.Append("<color=#").Append(HudStyle.Grey).Append(">… +").Append(hidden).Append("</color>");
        }
        if (data.Modules.Count == 0 && !data.OnFire && data.Outcome >= HitOutcome.Penetration)
            _sb.Append("<color=#").Append(HudStyle.Grey).Append('>').Append(T("No damage")).Append("</color>");
        _hasNote = _sb.Length > 0;
        _note.text = _sb.ToString();
        HudStyle.SetActive(_note, _hasNote);
    }

    private StatRow Row()
    {
        if (_rowCount == _rows.Count)
            _rows.Add(new StatRow(_root.transform));
        return _rows[_rowCount++];
    }

    private static string T(string english) => Plugin.Localization?.Translate(english) ?? english;

    // ------------------------------------------------------------------ 配置

    private void Layout()
    {
        var scale = HudStyle.Scale;
        var font = Plugin.HitReport.FontSize.Value * scale * 0.9f;
        var pad = font * 0.75f;
        var width = font * 17f;
        var accentWidth = Mathf.Max(2f, 4f * scale);
        var inner = width - pad * 2f - accentWidth;

        var headerHeight = font * 1.6f;
        var rowHeight = font * 1.5f;
        var noteHeight = _hasNote ? font * 1.4f : 0f;
        var dividerGap = font * 0.4f;
        var body = _rowCount * rowHeight + noteHeight;
        var height = pad + headerHeight + (body > 0f ? dividerGap * 2f + 1f : 0f) + body + pad * 0.8f;

        var left = Screen.width * 0.5f - width * 0.5f;
        var top = Screen.height * 0.5f - 140f * scale;
        var bottom = top - height;

        HudStyle.StylePanel(_panel, scale);
        HudStyle.Place(_panel, left, bottom, width, height);
        _accent.color = HudStyle.Hex(_accentHex);
        _accent.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
        HudStyle.Place(_accent, left + pad * 0.45f, bottom + pad * 0.6f, accentWidth, height - pad * 1.2f);

        var x = left + pad + accentWidth;
        var y = top - pad;
        _header.fontSize = font * 1.3f;
        HudStyle.Place(_header, x, y - headerHeight, inner - font * 3f, headerHeight);
        _count.fontSize = font * 1.1f;
        HudStyle.Place(_count, x + inner - font * 3f, y - headerHeight, font * 3f, headerHeight);
        y -= headerHeight;

        HudStyle.SetActive(_divider, body > 0f);
        if (body > 0f)
        {
            y -= dividerGap;
            _divider.color = HudStyle.DividerColor;
            HudStyle.Place(_divider, x, y - 1f, inner, Mathf.Max(1f, scale));
            y -= dividerGap + 1f;
        }
        for (var i = 0; i < _rowCount; i++)
        {
            _rows[i].Layout(x, y, inner, rowHeight, font, font * 7.6f, font * 3.4f, scale);
            y -= rowHeight;
        }
        if (_hasNote)
        {
            _note.fontSize = font;
            HudStyle.Place(_note, x, y - noteHeight, inner, noteHeight);
        }
    }

    // ------------------------------------------------------------------ マーカー

    private void DrawMarkers(HitReportData data, Camera camera)
    {
        var used = 0;
        if (Plugin.HitReport.ShowMarkers.Value && camera != null)
        {
            var scale = Screen.height / 1080f;
            var now = Time.time;
            foreach (var marker in data.Markers)
            {
                var age = now - marker.Time;
                if (age > MarkerTime || marker.Transform == null || marker.Transform.WasCollected)
                    continue;
                var screen = camera.WorldToScreenPoint(marker.Transform.position);
                if (screen.z <= 0.05f)
                    continue;
                var image = GetMarker(used++);
                var c = HudStyle.Hex(HudStyle.HealthHex(marker.Fraction, marker.State == ModuleState.Destroyed));
                // 出た瞬間は大きく、すぐ落ち着く
                var pop = 1f + 0.8f * Mathf.Clamp01(1f - age / 0.25f);
                c.a = Mathf.Clamp01((MarkerTime - age) / 0.5f);
                image.color = c;
                var rt = image.rectTransform;
                rt.anchoredPosition = new Vector2(screen.x, screen.y);
                rt.sizeDelta = Vector2.one * (marker.State == ModuleState.Destroyed ? 18f : 14f) * scale * pop;
            }
        }
        for (var i = used; i < _markers.Count; i++)
            HudStyle.SetActive(_markers[i], false);
    }

    private Image GetMarker(int index)
    {
        while (_markers.Count <= index)
        {
            var go = new GameObject("HitMarker");
            go.transform.SetParent(_root.transform, false);
            go.transform.SetSiblingIndex(0); // パネルより奥
            var image = go.AddComponent<Image>();
            image.sprite = _diamond;
            image.raycastTarget = false;
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            _markers.Add(image);
        }
        var marker = _markers[index];
        HudStyle.SetActive(marker, true);
        return marker;
    }

    // ------------------------------------------------------------------ 作成

    private void SetActive(bool active)
    {
        if (_root != null && !_root.WasCollected && _root.activeSelf != active)
            _root.SetActive(active);
    }

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
        {
            _root = null;
            _markers.Clear();
        }
        if (_root != null)
            return true;
        if (HudStyle.Font == null)
            return false;

        EnsureSprite();
        _root = HudStyle.CreateCanvas("SprocketHitReport", 30003, out _group);
        var t = _root.transform;
        _panel = HudStyle.CreateImage(t, "Panel", true);
        _accent = HudStyle.CreateImage(t, "Accent", true);
        _divider = HudStyle.CreateImage(t, "Divider", false);
        _header = HudStyle.CreateText(t, "Header", TextAlignmentOptions.MidlineLeft);
        _count = HudStyle.CreateText(t, "Hits", TextAlignmentOptions.MidlineRight);
        _note = HudStyle.CreateText(t, "Note", TextAlignmentOptions.MidlineLeft);
        _rows.Clear();
        _rowCount = 0;
        _markers.Clear();
        _shown = null;
        return true;
    }

    /// <summary>黒い縁取り付きの白い菱形（色は Image.color で付ける）。</summary>
    private static unsafe void EnsureSprite()
    {
        if (_diamond != null && !_diamond.WasCollected)
            return;
        const int size = 32;
        var pixels = new byte[size * size * 4];
        var c = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Mathf.Abs(x - c) + Mathf.Abs(y - c); // 菱形の距離
                var outer = Mathf.Clamp01(c - d + 0.5f);
                var inner = Mathf.Clamp01(c - 4f - d + 0.5f);
                var i = (y * size + x) * 4;
                var v = (byte)Mathf.RoundToInt(inner * 255f);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = (byte)Mathf.RoundToInt(outer * 255f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        _diamond = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _diamond.hideFlags = HideFlags.HideAndDontSave;
    }
}
