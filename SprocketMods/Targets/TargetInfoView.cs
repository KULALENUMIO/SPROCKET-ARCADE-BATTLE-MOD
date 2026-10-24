using System.Collections.Generic;
using System.Text;
using SprocketMods.Hits;
using SprocketMods.KillFeed;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMods.Targets;

/// <summary>
/// 照準した車両の情報。照準の上に半透明の黒いパネルを出し、その中に
///   ┃ 敵                         稼働中
///   ┃ Tiger II                    850 m
///   ┃ ───────────────────────────────
///   ┃ 乗員       ■■■■■■■■□□   4/5
///   ┃ エンジン   ■■■■■■□□□□   62%
///   ┃ 履帯       □□□□□□□□□□   破壊
///   ┃ 装填手 戦死 · 火災
/// を並べる（左端の帯は 敵 = 赤 / 味方 = 青、残骸は灰）。照準から外れると消える。
/// 文言は英語の原文を Localization で訳す（このテキストは自動翻訳の対象から外す）。
/// </summary>
internal sealed class TargetInfoView
{
    private const float FadeTime = 0.2f;

    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private Image _accent;
    private Image _divider;
    private TextMeshProUGUI _tag;
    private TextMeshProUGUI _name;
    private TextMeshProUGUI _status;
    private TextMeshProUGUI _note;
    private readonly List<StatRow> _rows = new();
    private int _rowCount;
    private bool _hasNote;
    private bool _hasName;
    private int _statusLines;
    private string _accentHex = HudStyle.White;

    private TargetSnapshot _shown;
    private int _shownVersion = -1;
    private readonly StringBuilder _sb = new();

    public void LateUpdate()
    {
        var cfg = Plugin.TargetInfo;
        var tracker = TargetInfoTracker.Instance;
        var data = cfg.Enabled.Value ? tracker?.Current : null;
        if (data != null && !cfg.ShowWrecks.Value && data.State == TargetState.KnockedOut)
            data = null;
        var alpha = data == null ? 0f : Mathf.Clamp01(1f - (Time.unscaledTime - tracker.LastSeen - 0.15f) / FadeTime);
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
    }

    // ------------------------------------------------------------------ 内容

    private void Compose(TargetSnapshot data)
    {
        var cfg = Plugin.TargetInfo;
        var wreck = data.State == TargetState.KnockedOut;
        var sideHex = data.Side switch
        {
            Side.Enemy => HudStyle.Red,
            Side.Friendly => HudStyle.Blue,
            _ => HudStyle.White,
        };
        var sideText = data.Side switch
        {
            Side.Enemy => wreck ? "Enemy wreck" : "Enemy",
            Side.Friendly => wreck ? "Friendly wreck" : "Friendly",
            _ => wreck ? "Wreck" : "Vehicle",
        };
        _accentHex = wreck ? HudStyle.Grey : sideHex;

        _tag.text = "<b><color=#" + sideHex + ">" + T(sideText) + "</color></b>";
        _hasName = cfg.ShowName.Value;
        HudStyle.SetActive(_name, _hasName);
        if (_hasName)
            _name.text = "<b><color=#" + (wreck ? HudStyle.Grey : HudStyle.White) + "><noparse>"
                         + (string.IsNullOrEmpty(data.Name) ? "?" : data.Name) + "</noparse></color></b>";

        var (state, stateHex) = data.State switch
        {
            TargetState.KnockedOut => ("Knocked out", HudStyle.Red),
            TargetState.Immobilized => ("Immobilized", HudStyle.Orange),
            _ => ("Operational", HudStyle.Green),
        };
        _sb.Clear();
        _statusLines = 0;
        if (cfg.ShowStatus.Value)
        {
            _sb.Append("<b><color=#").Append(stateHex).Append('>').Append(T(state)).Append("</color></b>");
            _statusLines++;
        }
        if (cfg.ShowDistance.Value)
        {
            if (_statusLines > 0)
                _sb.Append('\n');
            _sb.Append("<size=85%><color=#").Append(HudStyle.Grey).Append('>').Append(Mathf.RoundToInt(data.Distance)).Append(" m</color></size>");
            _statusLines++;
        }
        _status.text = _sb.ToString();
        HudStyle.SetActive(_status, _statusLines > 0);

        // 行: 乗員、エンジン（常に）、ほかのモジュール（損傷しているときだけ）
        _rowCount = 0;
        var d = data.Details;
        if (cfg.ShowCrew.Value && d.CrewTotal > 0)
        {
            var fraction = d.CrewAlive / (float)d.CrewTotal;
            Row().Set(T("Crew"), HudStyle.White, d.CrewAlive + "/" + d.CrewTotal,
                d.CrewAlive == 0 ? HudStyle.Red : d.CrewAlive < d.CrewTotal ? HudStyle.Orange : HudStyle.Green, fraction);
        }
        foreach (var m in d.Modules)
        {
            var destroyed = m.State == ModuleState.Destroyed;
            var engine = m.Label == "Engine";
            if (engine ? !cfg.ShowEngine.Value : !cfg.ShowDamagedModules.Value || !destroyed && m.WorstFraction >= 0.98f)
                continue;
            var value = destroyed ? T("destroyed") : Mathf.RoundToInt(m.WorstFraction * 100f) + "%";
            Row().Set(T(m.Label), HudStyle.White, value, HudStyle.HealthHex(m.WorstFraction, destroyed), destroyed ? 0f : m.WorstFraction);
        }
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].SetActive(i < _rowCount);

        // 補足: 戦死した乗員・負傷・火災
        _sb.Clear();
        if (cfg.ShowFire.Value && data.OnFire)
            _sb.Append("<b><color=#").Append(HudStyle.Fire).Append('>').Append(T("On fire")).Append("</color></b>");
        if (cfg.ShowCrewDetails.Value && d.DeadCrew.Count > 0 && d.CrewAlive > 0)
        {
            Separator();
            _sb.Append("<color=#").Append(HudStyle.Red).Append('>');
            for (var i = 0; i < d.DeadCrew.Count; i++)
            {
                if (i > 0)
                    _sb.Append('・');
                _sb.Append(T(d.DeadCrew[i]));
            }
            _sb.Append(' ').Append(T("killed")).Append("</color>");
        }
        if (cfg.ShowCrewDetails.Value && d.CrewWounded > 0)
        {
            Separator();
            _sb.Append("<color=#").Append(HudStyle.Yellow).Append('>').Append(T("wounded")).Append(' ').Append(d.CrewWounded).Append("</color>");
        }
        _hasNote = _sb.Length > 0;
        _note.text = _sb.ToString();
        HudStyle.SetActive(_note, _hasNote);
    }

    private void Separator()
    {
        if (_sb.Length > 0)
            _sb.Append("<color=#").Append(HudStyle.Grey).Append(">  ·  </color>");
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
        var font = Plugin.TargetInfo.FontSize.Value * scale;
        var pad = font * 0.75f;
        var width = font * 17f;
        var accentWidth = Mathf.Max(2f, 4f * scale);
        var inner = width - pad * 2f - accentWidth;

        var tagHeight = font * 1.2f;
        var nameHeight = _hasName ? font * 1.35f : 0f;
        var headerHeight = Mathf.Max(tagHeight + nameHeight, _statusLines * font * 1.15f);
        var rowHeight = font * 1.5f;
        var noteHeight = _hasNote ? font * 0.9f * 1.4f : 0f;
        var dividerGap = font * 0.45f;
        var body = _rowCount * rowHeight + noteHeight;
        var height = pad + headerHeight + (body > 0f ? dividerGap * 2f + 1f : 0f) + body + pad * 0.8f;

        var left = Screen.width * 0.5f - width * 0.5f;
        var bottom = Screen.height * 0.5f + 120f * scale;
        var top = bottom + height;

        HudStyle.StylePanel(_panel, scale);
        HudStyle.Place(_panel, left, bottom, width, height);
        _accent.color = HudStyle.Hex(_accentHex);
        _accent.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
        HudStyle.Place(_accent, left + pad * 0.45f, bottom + pad * 0.6f, accentWidth, height - pad * 1.2f);

        var x = left + pad + accentWidth;
        var y = top - pad;
        var statusWidth = font * 6.5f;
        _tag.fontSize = font * 0.85f;
        HudStyle.Place(_tag, x, y - tagHeight, _statusLines > 0 ? inner - statusWidth : inner, tagHeight);
        _name.fontSize = font * 1.2f;
        HudStyle.Place(_name, x, y - headerHeight, inner - font * 5f, nameHeight);
        _status.fontSize = font;
        HudStyle.Place(_status, x + inner - statusWidth, y - headerHeight, statusWidth, headerHeight);
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
            _rows[i].Layout(x, y, inner, rowHeight, font, font * 6.2f, font * 3.6f, scale);
            y -= rowHeight;
        }
        if (_hasNote)
        {
            _note.fontSize = font * 0.9f;
            HudStyle.Place(_note, x, y - noteHeight, inner, noteHeight);
        }
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
            _root = null;
        if (_root != null)
            return true;
        if (HudStyle.Font == null)
            return false;

        _root = HudStyle.CreateCanvas("SprocketTargetInfo", 30002, out _group);
        var t = _root.transform;
        _panel = HudStyle.CreateImage(t, "Panel", true);
        _accent = HudStyle.CreateImage(t, "Accent", true);
        _divider = HudStyle.CreateImage(t, "Divider", false);
        _tag = HudStyle.CreateText(t, "Side", TextAlignmentOptions.BottomLeft);
        _name = HudStyle.CreateText(t, "Name", TextAlignmentOptions.TopLeft);
        _name.overflowMode = TextOverflowModes.Ellipsis;
        _status = HudStyle.CreateText(t, "Status", TextAlignmentOptions.TopRight);
        _status.lineSpacing = -4f;
        _note = HudStyle.CreateText(t, "Note", TextAlignmentOptions.MidlineLeft);
        _rows.Clear();
        _rowCount = 0;
        _shown = null;
        return true;
    }
}
