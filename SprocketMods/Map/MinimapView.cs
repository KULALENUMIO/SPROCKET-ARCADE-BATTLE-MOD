using System;
using System.Collections.Generic;
using SprocketMods.KillFeed;
using SprocketMods.Teams;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.Map;

/// <summary>
/// War Thunder 風のミニマップ（画面左下）。半透明の黒いパネルに地図（MapTerrain）を入れ、上に列番号（1, 2, …）、
/// 左に行の記号（A, B, …）のマス目を付ける。車両は向きの分かる矢印: 自車 = 金、味方 = 青、敵 = 赤。
/// 撃破された車両は暗い色の ×。自車からはカメラの向きと視野を 2 本の線で出す。北が上。
/// </summary>
internal sealed class MinimapView
{
    private static Sprite _arrow;
    private static Sprite _cross;

    private readonly MapTerrain _map = new();
    private readonly List<MapUnit> _units = new();
    private readonly List<Vector3> _positions = new();
    private readonly List<Image> _markers = new();
    private readonly List<Image> _gridLines = new();
    private readonly List<TextMeshProUGUI> _gridLabels = new();

    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private RawImage _image;
    private Transform _gridLayer;
    private Transform _markerLayer;
    private Image _viewLeft;
    private Image _viewRight;
    private TextMeshProUGUI _status;
    private TextMeshProUGUI _scale;
    private Texture2D _shownTexture;
    private float _nextErrorLog;

    public void LateUpdate()
    {
        var cfg = Plugin.Minimap;
        var tracker = TeamStrengthTracker.Instance;
        if (!cfg.Enabled.Value || tracker == null || !tracker.HasPlayer || Time.timeScale <= 0f || !Ensure())
        {
            SetActive(false);
            return;
        }
        tracker.CollectUnits(_units);
        _positions.Clear();
        foreach (var u in _units)
            _positions.Add(u.Position);
        try
        {
            _map.Update(_positions);
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Minimap terrain failed: {e}");
            }
        }
        if (_map.Texture == null && !_map.Building)
        {
            SetActive(false);
            return;
        }
        SetActive(true);
        Draw(cfg);
    }

    private void Draw(MinimapConfig cfg)
    {
        var s = Screen.height / 1080f;
        var size = cfg.Size.Value * s;
        var margin = 14f * s;
        var pad = 5f * s;
        var grid = cfg.ShowGrid.Value;
        var band = grid ? 14f * s : 0f;
        var width = pad * 2f + band + size;
        var height = pad * 2f + band + size;
        var mapX = margin + pad + band;
        var mapY = margin + pad;

        HudStyle.StylePanel(_panel, HudStyle.Scale);
        var pc = _panel.color;
        pc.a = Mathf.Min(1f, pc.a * 1.3f);
        _panel.color = pc;
        HudStyle.Place(_panel, margin, margin, width, height);

        if (_map.Texture != _shownTexture)
        {
            _shownTexture = _map.Texture;
            _image.texture = _shownTexture;
        }
        HudStyle.SetActive(_image, _map.Texture != null);
        HudStyle.Place(_image, mapX, mapY, size, size);

        var ready = _map.Ready;
        HudStyle.SetActive(_status, !ready);
        if (!ready)
        {
            _status.fontSize = 15f * s;
            _status.text = "<color=#" + HudStyle.Grey + ">" + T("Generating map…") + "</color>";
            HudStyle.Place(_status, mapX, mapY, size, size);
        }

        var region = _map.Region;
        var cell = DrawGrid(grid && ready, region, mapX, mapY, size, band, s);
        HudStyle.SetActive(_scale, ready);
        if (ready)
        {
            _scale.fontSize = 12f * s;
            _scale.text = "<color=#" + HudStyle.White + ">" + (grid ? cell : MapTerrain.GridCell(region.width)).ToString("0") + " m</color>";
            HudStyle.Place(_scale, mapX, mapY + 2f * s, size - 4f * s, 16f * s);
        }

        DrawUnits(cfg, ready, region, mapX, mapY, size, s);
    }

    // ------------------------------------------------------------------ マス目

    private float DrawGrid(bool visible, Rect region, float mapX, float mapY, float size, float band, float s)
    {
        var cell = MapTerrain.GridCell(region.width);
        var count = visible ? Mathf.Clamp(Mathf.CeilToInt(region.width / cell - 0.01f), 1, 26) : 0;
        var lines = 0;
        var labels = 0;
        var pixelsPerMetre = size / Mathf.Max(1f, region.width);
        for (var i = 0; i < count; i++)
        {
            var offset = i * cell * pixelsPerMetre;
            var next = Mathf.Min(size, (i + 1) * cell * pixelsPerMetre);
            if (i > 0)
            {
                Line(lines++, mapX + offset, mapY, Mathf.Max(1f, s), size); // 縦線
                Line(lines++, mapX, mapY + size - offset, size, Mathf.Max(1f, s)); // 横線
            }
            // 列番号（上）と行の記号（左、北から A）
            Label(labels++, (i + 1).ToString(), mapX + offset, mapY + size, next - offset, band, s);
            Label(labels++, ((char)('A' + i)).ToString(), mapX - band, mapY + size - next, band, next - offset, s);
        }
        for (var i = lines; i < _gridLines.Count; i++)
            HudStyle.SetActive(_gridLines[i], false);
        for (var i = labels; i < _gridLabels.Count; i++)
            HudStyle.SetActive(_gridLabels[i], false);
        return cell;
    }

    private void Line(int index, float x, float y, float w, float h)
    {
        while (_gridLines.Count <= index)
            _gridLines.Add(HudStyle.CreateImage(_gridLayer, "GridLine", false));
        var line = _gridLines[index];
        HudStyle.SetActive(line, true);
        line.color = new Color(0f, 0f, 0f, 0.28f);
        HudStyle.Place(line, x, y, w, h);
    }

    private void Label(int index, string text, float x, float y, float w, float h, float s)
    {
        while (_gridLabels.Count <= index)
            _gridLabels.Add(HudStyle.CreateText(_gridLayer, "GridLabel", TextAlignmentOptions.Center));
        var label = _gridLabels[index];
        HudStyle.SetActive(label, true);
        label.fontSize = 11f * s;
        label.text = "<color=#" + HudStyle.Grey + ">" + text + "</color>";
        HudStyle.Place(label, x, y, w, h);
    }

    // ------------------------------------------------------------------ 車両

    private void DrawUnits(MinimapConfig cfg, bool ready, Rect region, float mapX, float mapY, float size, float s)
    {
        var used = 0;
        Image playerMarker = null;
        Vector2 playerPos = default;
        if (ready)
        {
            // 下から: 残骸 → 敵 → 味方 → 自車 の順に重ねる
            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var u in _units)
                {
                    var p = u.Side == Side.Player ? 3 : !u.Alive ? 0 : u.Side == Side.Enemy ? 1 : 2;
                    if (p != pass)
                        continue;
                    if (u.Side == Side.Enemy && !cfg.ShowEnemies.Value)
                        continue;
                    var fx = Mathf.Clamp01((u.Position.x - region.x) / region.width);
                    var fy = Mathf.Clamp01((u.Position.z - region.y) / region.height);
                    var pos = new Vector2(mapX + fx * size, mapY + fy * size);
                    var marker = GetMarker(used++);
                    var colour = HudStyle.Hex(u.Side switch
                    {
                        Side.Player => HudStyle.Gold,
                        Side.Friendly => HudStyle.Blue,
                        Side.Enemy => HudStyle.Red,
                        _ => HudStyle.White,
                    });
                    float markerSize;
                    var rt = marker.rectTransform;
                    if (u.Alive)
                    {
                        marker.sprite = _arrow;
                        markerSize = (u.Side == Side.Player ? 17f : 13f) * s;
                        var heading = Mathf.Atan2(u.Forward.x, u.Forward.z) * Mathf.Rad2Deg;
                        rt.localEulerAngles = new Vector3(0f, 0f, -heading);
                    }
                    else
                    {
                        marker.sprite = _cross;
                        markerSize = 10f * s;
                        colour = Color.Lerp(colour, new Color(0.15f, 0.15f, 0.15f), 0.45f);
                        rt.localEulerAngles = Vector3.zero;
                    }
                    marker.color = colour;
                    rt.anchoredPosition = pos;
                    rt.sizeDelta = new Vector2(markerSize, markerSize);
                    if (u.Side == Side.Player)
                    {
                        playerMarker = marker;
                        playerPos = pos;
                    }
                }
            }
        }
        for (var i = used; i < _markers.Count; i++)
            HudStyle.SetActive(_markers[i], false);
        DrawView(cfg.ShowViewDirection.Value && playerMarker != null, playerPos, size, s);
    }

    /// <summary>自車からカメラの視野の左右の端の 2 本の線。</summary>
    private void DrawView(bool visible, Vector2 origin, float size, float s)
    {
        var camera = Hits.HitTracker.Instance?.ActiveCamera;
        visible &= camera != null;
        HudStyle.SetActive(_viewLeft, visible);
        HudStyle.SetActive(_viewRight, visible);
        if (!visible)
            return;
        var forward = camera.transform.forward;
        var heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        var halfFov = Mathf.Atan(Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * camera.aspect) * Mathf.Rad2Deg;
        halfFov = Mathf.Clamp(halfFov, 1.5f, 60f);
        var length = size * 0.2f;
        foreach (var (line, angle) in new[] { (_viewLeft, heading - halfFov), (_viewRight, heading + halfFov) })
        {
            line.color = new Color(1f, 1f, 1f, 0.55f);
            var rt = line.rectTransform;
            rt.anchoredPosition = origin;
            rt.sizeDelta = new Vector2(length, Mathf.Max(1f, 1.5f * s));
            // 画像は右（東）向き。北から時計回りの角度を UI の回転（反時計回り、東 = 0）に直す
            rt.localEulerAngles = new Vector3(0f, 0f, 90f - angle);
        }
    }

    private Image GetMarker(int index)
    {
        while (_markers.Count <= index)
        {
            var go = new GameObject("MapMarker");
            go.transform.SetParent(_markerLayer, false);
            var image = go.AddComponent<Image>();
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
        EnsureSprites();
        _root = HudStyle.CreateCanvas("SprocketMinimap", 29990, out _group);
        var t = _root.transform;
        _panel = HudStyle.CreateImage(t, "Panel", true);
        var go = new GameObject("Map");
        go.transform.SetParent(t, false);
        _image = go.AddComponent<RawImage>();
        _image.raycastTarget = false;
        var irt = _image.rectTransform;
        irt.anchorMin = irt.anchorMax = Vector2.zero;
        irt.pivot = Vector2.zero;
        _gridLayer = CreateLayer(t, "Grid");
        _status = HudStyle.CreateText(t, "Status", TextAlignmentOptions.Center);
        _scale = HudStyle.CreateText(t, "Scale", TextAlignmentOptions.BottomRight);
        _viewLeft = CreateViewLine(t);
        _viewRight = CreateViewLine(t);
        _markerLayer = CreateLayer(t, "Markers");
        _markers.Clear();
        _gridLines.Clear();
        _gridLabels.Clear();
        _shownTexture = null;
        return true;
    }

    /// <summary>描く順番をまとめるための空の層（左下が画面の原点）。</summary>
    private static Transform CreateLayer(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
        return rt;
    }

    private static Image CreateViewLine(Transform parent)
    {
        var line = HudStyle.CreateImage(parent, "ViewLine", false);
        line.rectTransform.pivot = new Vector2(0f, 0.5f);
        return line;
    }

    // ------------------------------------------------------------------ 絵

    private static void EnsureSprites()
    {
        if (_arrow != null && !_arrow.WasCollected)
            return;
        // 上向きの矢じり（白い中身と黒い縁。色は Image.color で付ける）
        var arrow = new[] { new Vector2(0f, 0.92f), new Vector2(0.7f, -0.78f), new Vector2(0f, -0.38f), new Vector2(-0.7f, -0.78f) };
        _arrow = MakeSprite(64, (x, y) => Polygon(arrow, x, y));
        _cross = MakeSprite(64, Cross);
    }

    /// <summary>多角形の内側までの距離（内側が正、単位は画像の半分 = 1）。</summary>
    private static float Polygon(Vector2[] poly, float x, float y)
    {
        var p = new Vector2(x, y);
        var inside = false;
        var dist = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j];
            var b = poly[i];
            if ((b.y > y) != (a.y > y) && x < (a.x - b.x) * (y - b.y) / (a.y - b.y) + b.x)
                inside = !inside;
            var ab = a - b;
            var t = Mathf.Clamp01(Vector2.Dot(p - b, ab) / ab.sqrMagnitude);
            dist = Mathf.Min(dist, (p - (b + ab * t)).magnitude);
        }
        return inside ? dist : -dist;
    }

    private static float Cross(float x, float y)
    {
        // 2 本の棒の × （太さ 0.22）
        var d1 = Mathf.Abs(x - y) / 1.4142f;
        var d2 = Mathf.Abs(x + y) / 1.4142f;
        var inBox = 0.75f - Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
        return Mathf.Min(0.22f - Mathf.Min(d1, d2), inBox);
    }

    /// <summary>距離関数から、白い中身＋黒い縁の絵を作る。</summary>
    private static unsafe Sprite MakeSprite(int size, Func<float, float, float> shape)
    {
        var pixels = new byte[size * size * 4];
        var texel = 2f / size;
        const float border = 0.16f;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = (px + 0.5f) * texel - 1f;
                var y = (py + 0.5f) * texel - 1f;
                var d = shape(x, y);
                var outer = Mathf.Clamp01((d + border) / texel + 0.5f);
                var inner = Mathf.Clamp01(d / texel + 0.5f);
                var i = (py * size + px) * 4;
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
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
