using System;
using System.Collections.Generic;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.KillFeed;

/// <summary>
/// 画面右上のキルログ。新しいものが一番上に入り、Duration 秒たつとフェードして消える。
/// 1 行ずつ角丸の半透明の黒いパネルに入れ、左端に色の帯（撃破 = 赤、行動不能 = 橙、自車が関わる行 = 金）を付ける。
/// 名前の色: 自車 = 金、味方 = 青、敵 = 赤、不明 = 灰。
/// 文の形は翻訳ファイルの「$K destroyed $V」などで言語ごとに変えられる（$K = 撃破した車両、$V = 撃破された車両）。
/// </summary>
internal sealed class KillFeedView
{
    private const float FadeTime = 0.6f;
    private const float SlideTime = 0.18f;

    private const string TemplateDestroyed = "$K destroyed $V";
    private const string TemplateDestroyedUnknown = "$V destroyed";
    private const string TemplateImmobilized = "$K immobilized $V";
    private const string TemplateImmobilizedUnknown = "$V immobilized";
    private const string TemplateAbandoned = "$V abandoned";

    private sealed class Line
    {
        public GameObject Root;
        public RectTransform Rect;
        public Image Background;
        public Image Accent;
        public string AccentHex;
        public TextMeshProUGUI Text;
        public float Born;
        public bool InvolvesPlayer;
        public float Y;
    }

    private readonly List<Line> _lines = new();
    private readonly Queue<KillEvent> _queued = new();
    private GameObject _root;
    private bool _subscribed;

    public void LateUpdate()
    {
        var cfg = Plugin.KillFeed;
        Subscribe();
        if (!cfg.Enabled.Value)
        {
            Clear();
            return;
        }
        if (!Ensure())
            return;

        while (_queued.Count > 0)
            Add(_queued.Dequeue());
        Layout();
    }

    private void Subscribe()
    {
        if (_subscribed || KillTracker.Instance == null)
            return;
        KillTracker.Instance.Killed += e =>
        {
            if (_queued.Count < 32)
                _queued.Enqueue(e);
        };
        _subscribed = true;
    }

    // ------------------------------------------------------------------ 行

    private void Add(KillEvent e)
    {
        var line = new Line
        {
            Born = Time.unscaledTime,
            InvolvesPlayer = e.InvolvesPlayer,
            Y = float.NaN,
            AccentHex = e.InvolvesPlayer ? HudStyle.Gold : e.Kind == KillKind.Destroyed ? HudStyle.Red
                : e.Kind == KillKind.Abandoned ? HudStyle.Grey : HudStyle.Orange,
        };
        line.Root = new GameObject("KillFeedLine");
        line.Root.transform.SetParent(_root.transform, false);
        var rootRt = line.Root.AddComponent<RectTransform>();
        line.Rect = rootRt;
        rootRt.anchorMin = rootRt.anchorMax = Vector2.zero;
        rootRt.pivot = new Vector2(1f, 1f);

        line.Background = HudStyle.CreateImage(line.Root.transform, "Background", true);
        line.Accent = HudStyle.CreateImage(line.Root.transform, "Accent", true);
        var text = HudStyle.CreateText(line.Root.transform, "Text", TextAlignmentOptions.MidlineRight);
        text.text = Compose(e);
        line.Text = text;

        _lines.Insert(0, line);
        var max = Plugin.KillFeed.MaxEntries.Value;
        while (_lines.Count > max)
            Remove(_lines.Count - 1);
    }

    private static string Compose(in KillEvent e)
    {
        var known = !string.IsNullOrEmpty(e.Attacker);
        var template = e.Kind == KillKind.Abandoned ? TemplateAbandoned
            : e.Kind == KillKind.Destroyed
                ? known ? TemplateDestroyed : TemplateDestroyedUnknown
                : known ? TemplateImmobilized : TemplateImmobilizedUnknown;
        var translated = Plugin.Localization?.Translate(template) ?? template;

        // 動詞などの文字は灰白、名前は陣営の色
        var body = "<color=#D8D8D8>" + translated + "</color>";
        body = body.Replace("$V", Name(e.Victim, e.VictimSide));
        if (known)
            body = body.Replace("$K", Name(e.Attacker, e.AttackerSide));
        if (e.Kind == KillKind.Destroyed)
            body = "<color=#FF5A4A><b>×</b></color>  " + body;
        return body;
    }

    private static string Name(string name, Side side)
    {
        var colour = side switch
        {
            Side.Player => "#FFD24A",
            Side.Friendly => "#6EB8FF",
            Side.Enemy => "#FF6A5A",
            _ => "#E0E0E0",
        };
        var weight = side == Side.Player ? "<b>" : string.Empty;
        var weightEnd = side == Side.Player ? "</b>" : string.Empty;
        return $"<color={colour}>{weight}<noparse>{name}</noparse>{weightEnd}</color>";
    }

    private void Remove(int index)
    {
        var line = _lines[index];
        _lines.RemoveAt(index);
        if (line.Root != null && !line.Root.WasCollected)
            Object.Destroy(line.Root);
    }

    // ------------------------------------------------------------------ 配置

    private void Layout()
    {
        var cfg = Plugin.KillFeed;
        var scale = Screen.height / 1080f;
        var fontSize = cfg.FontSize.Value * scale * (Plugin.Style?.Scale.Value ?? 1f);
        var lineHeight = fontSize * 1.75f;
        var gap = 4f * scale;
        var accentWidth = Mathf.Max(2f, 3f * scale);
        var paddingX = 12f * scale;
        var right = Screen.width - 24f * scale;
        var top = Screen.height - 24f * scale;
        var now = Time.unscaledTime;
        var duration = cfg.Duration.Value;
        var slide = Mathf.Clamp01(Time.unscaledDeltaTime / SlideTime);

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i];
            var age = now - line.Born;
            if (age > duration || line.Root == null || line.Root.WasCollected)
            {
                Remove(i);
                continue;
            }
        }

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var age = now - line.Born;
            var alpha = Mathf.Clamp01((cfg.Duration.Value - age) / FadeTime) * Mathf.Clamp01(age / 0.15f);

            var text = line.Text;
            text.fontSize = fontSize;
            text.alpha = alpha;
            var textWidth = text.preferredWidth;
            var width = textWidth + paddingX * 2f + accentWidth;
            HudStyle.Place(text, paddingX + accentWidth, 0f, textWidth, lineHeight);

            var targetY = top - i * (lineHeight + gap);
            line.Y = float.IsNaN(line.Y) ? targetY : Mathf.Lerp(line.Y, targetY, slide);
            var rt = line.Rect;
            rt.sizeDelta = new Vector2(width, lineHeight);
            rt.anchoredPosition = new Vector2(right, line.Y);

            // 自車が関わる行は少し金色がかった背景
            HudStyle.StylePanel(line.Background, scale);
            var bg = line.Background.color;
            if (line.InvolvesPlayer)
                bg = new Color(0.16f, 0.12f, 0.02f, Mathf.Min(1f, bg.a + 0.1f));
            bg.a *= alpha;
            line.Background.color = bg;
            HudStyle.Place(line.Background, 0f, 0f, width, lineHeight);

            var accent = HudStyle.Hex(line.AccentHex);
            accent.a = alpha;
            line.Accent.color = accent;
            line.Accent.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
            HudStyle.Place(line.Accent, paddingX * 0.5f, lineHeight * 0.2f, accentWidth, lineHeight * 0.6f);
        }
    }

    // ------------------------------------------------------------------ 作成・破棄

    private void Clear()
    {
        _queued.Clear();
        for (var i = _lines.Count - 1; i >= 0; i--)
            Remove(i);
    }

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
        {
            _root = null;
            _lines.Clear();
        }
        if (_root == null)
        {
            _root = new GameObject("SprocketKillFeed");
            Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30002;
        }
        return HudStyle.Font != null;
    }
}
