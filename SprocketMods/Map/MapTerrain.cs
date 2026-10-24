using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Map;

/// <summary>ミニマップの設定（[Minimap] セクション）。</summary>
public class MinimapConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<float> Size;
    public readonly ConfigEntry<bool> ShowEnemies;
    public readonly ConfigEntry<bool> ShowGrid;
    public readonly ConfigEntry<bool> ShowViewDirection;
    public readonly ConfigEntry<bool> ShowContours;

    public MinimapConfig(ConfigFile cfg)
    {
        const string s = "Minimap";
        Enabled = cfg.Bind(s, "Enabled", true, "画面左下に War Thunder 風のミニマップ（地形・等高線・味方と敵の位置）を表示する");
        Size = cfg.Bind(s, "Size", 300f, new ConfigDescription("ミニマップの大きさ（1080p 基準の px）", new AcceptableValueRange<float>(160f, 560f)));
        ShowEnemies = cfg.Bind(s, "ShowEnemies", true, "敵の位置も表示する");
        ShowGrid = cfg.Bind(s, "ShowGrid", true, "マス目（A-1 など）を表示する");
        ShowViewDirection = cfg.Bind(s, "ShowViewDirection", true, "自車の向いている方向（カメラの向き）を表示する");
        ShowContours = cfg.Bind(s, "ShowContours", true, "等高線を引く（変えると地図を作り直す）");
    }
}

/// <summary>
/// ミニマップの地図画像を地形（Unity Terrain。MapMagic が作るタイル）から作る。
///  - 範囲: 有効な Terrain 全体（一辺 MaxFullMap m まで）。それより広いときは車両の集まっている所を中心にした正方形。
///  - 色: 地面の素材。Terrain の splat（alphamapTextures の RGBA = 各 TerrainLayer の割合）で、各レイヤーの
///    diffuseTexture の平均色を混ぜる。取れないときは高さで色を付ける。War Thunder 風に彩度を落として明るくする。
///  - 陰影: 高さ（TerrainData.GetInterpolatedHeight）の傾きから北西の光で陰を付ける（起伏が見えるよう高さを強調）。
///  - 等高線: 高さの幅から間隔（2/5/10/20/50 m…）を決め、5 本ごとに太く。
/// 地形を読むのは重いので、数フレームに分けて行単位で作る。
/// </summary>
internal sealed class MapTerrain
{
    public const int Resolution = 512;
    private const float MaxFullMap = 4200f;
    private const float MinRegion = 1000f;
    private const int RowsPerFrame = 24;

    private sealed class TerrainInfo
    {
        public Terrain Terrain;
        public TerrainData Data;
        public Vector3 Position;
        public Vector3 Size;
        public Color32[][] Splat; // alphamapTextures を CPU に写したもの
        public int SplatWidth;
        public int SplatHeight;
        public Color[] LayerColours;
    }

    private readonly List<TerrainInfo> _terrains = new();
    private long _signature;
    private float[] _heights;
    private Color[] _colours;
    private int _row = -1;
    private TerrainInfo _last;
    private float _nextCheck;
    private bool _contours;

    /// <summary>地図画像（作り終わるまで null）。</summary>
    public Texture2D Texture { get; private set; }

    /// <summary>地図の範囲（x = 西端, y = 南端, 幅 = 高さ, m）。</summary>
    public Rect Region { get; private set; }

    public bool Ready => Texture != null && _row < 0;
    public bool Building => _row >= 0;
    public float ContourStep { get; private set; }

    /// <summary>毎フレーム。地形が変わったら作り直し、作っている途中なら続きを作る。</summary>
    public void Update(IReadOnlyList<Vector3> vehicles)
    {
        var now = Time.unscaledTime;
        if (now >= _nextCheck && _row < 0)
        {
            _nextCheck = now + 2f;
            CheckTerrains(vehicles);
        }
        if (_row >= 0)
            BuildRows();
    }

    private void CheckTerrains(IReadOnlyList<Vector3> vehicles)
    {
        var active = Terrain.activeTerrains;
        long signature = active?.Length ?? 0;
        if (active != null)
        {
            for (var i = 0; i < active.Length; i++)
            {
                if (active[i] != null)
                    signature = signature * 31 + active[i].Pointer.ToInt64();
            }
        }
        var contours = Plugin.Minimap.ShowContours.Value;
        if (signature == _signature && contours == _contours && (Texture != null || signature == 0))
            return;
        if (signature == 0)
        {
            Clear();
            _signature = 0;
            return;
        }
        if (vehicles.Count == 0)
            return; // 車両が出てから範囲を決める
        _signature = signature;
        _contours = contours;
        Start(active, vehicles);
    }

    private void Clear()
    {
        if (Texture != null)
            Object.Destroy(Texture);
        Texture = null;
        _terrains.Clear();
        _row = -1;
    }

    // ------------------------------------------------------------------ 準備

    private void Start(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Terrain> active, IReadOnlyList<Vector3> vehicles)
    {
        Clear();
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (var i = 0; i < active.Length; i++)
        {
            var terrain = active[i];
            var data = terrain != null ? terrain.terrainData : null;
            if (data == null)
                continue;
            var info = new TerrainInfo { Terrain = terrain, Data = data, Position = terrain.GetPosition(), Size = data.size };
            _terrains.Add(info);
            minX = Mathf.Min(minX, info.Position.x);
            minZ = Mathf.Min(minZ, info.Position.z);
            maxX = Mathf.Max(maxX, info.Position.x + info.Size.x);
            maxZ = Mathf.Max(maxZ, info.Position.z + info.Size.z);
        }
        if (_terrains.Count == 0)
            return;

        // 範囲: 地形全体が収まるならそれ、広すぎるなら車両の周り
        Rect region;
        var width = maxX - minX;
        var depth = maxZ - minZ;
        if (Mathf.Max(width, depth) <= MaxFullMap)
        {
            var side = Mathf.Max(width, depth);
            region = new Rect((minX + maxX - side) * 0.5f, (minZ + maxZ - side) * 0.5f, side, side);
        }
        else
        {
            float vx0 = float.MaxValue, vz0 = float.MaxValue, vx1 = float.MinValue, vz1 = float.MinValue;
            foreach (var p in vehicles)
            {
                vx0 = Mathf.Min(vx0, p.x);
                vz0 = Mathf.Min(vz0, p.z);
                vx1 = Mathf.Max(vx1, p.x);
                vz1 = Mathf.Max(vz1, p.z);
            }
            var side = Mathf.Clamp(Mathf.Max(vx1 - vx0, vz1 - vz0) * 1.5f + 400f, MinRegion, MaxFullMap);
            var cx = Mathf.Clamp((vx0 + vx1) * 0.5f, minX + side * 0.5f, maxX - side * 0.5f);
            var cz = Mathf.Clamp((vz0 + vz1) * 0.5f, minZ + side * 0.5f, maxZ - side * 0.5f);
            region = new Rect(cx - side * 0.5f, cz - side * 0.5f, side, side);
        }
        // マス目がちょうど収まるよう、一辺をマスの大きさの倍数に切り上げる（中心は保つ）
        var cell = GridCell(region.width);
        var rounded = Mathf.Ceil(region.width / cell - 0.001f) * cell;
        region = new Rect(region.center.x - rounded * 0.5f, region.center.y - rounded * 0.5f, rounded, rounded);
        Region = region;

        foreach (var info in _terrains)
            ReadSplat(info);

        _heights = new float[Resolution * Resolution];
        _colours = new Color[Resolution * Resolution];
        _row = 0;
        _last = null;
        Plugin.Logger.LogInfo($"Minimap: {_terrains.Count} terrain(s), area {width:0}x{depth:0} m, map {region.width:0} m at ({region.x:0}, {region.y:0}), " +
                              $"layers {(_terrains[0].LayerColours?.Length ?? 0)}");
    }

    /// <summary>splat（各レイヤーの割合）と各レイヤーの平均色を読む。読めなければ高さで色を付ける。</summary>
    private static void ReadSplat(TerrainInfo info)
    {
        try
        {
            var data = info.Data;
            var layers = data.terrainLayers;
            var textures = data.alphamapTextures;
            if (layers == null || layers.Length == 0 || textures == null || textures.Length == 0)
                return;
            info.LayerColours = new Color[layers.Length];
            for (var i = 0; i < layers.Length; i++)
            {
                var diffuse = layers[i]?.diffuseTexture;
                info.LayerColours[i] = diffuse != null ? AverageColour(diffuse) : new Color(0.55f, 0.55f, 0.45f);
            }
            // 草地・土・岩などの色の差が小さいので、平均からの差を強調して見分けやすくする
            var mean = Color.black;
            foreach (var c in info.LayerColours)
                mean += c;
            mean /= info.LayerColours.Length;
            for (var i = 0; i < info.LayerColours.Length; i++)
            {
                var c = mean + (info.LayerColours[i] - mean) * 1.8f;
                info.LayerColours[i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
            }
            var w = Mathf.Min(textures[0].width, Resolution);
            var h = Mathf.Min(textures[0].height, Resolution);
            info.Splat = new Color32[textures.Length][];
            for (var i = 0; i < textures.Length; i++)
                info.Splat[i] = textures[i] != null ? ReadTexture(textures[i], w, h, true) : null;
            info.SplatWidth = w;
            info.SplatHeight = h;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Minimap: splat not readable, using height colours ({e.Message})");
            info.Splat = null;
            info.LayerColours = null;
        }
    }

    private static Color AverageColour(Texture texture)
    {
        const int size = 32;
        var pixels = ReadTexture(texture, size, size, false);
        float r = 0f, g = 0f, b = 0f;
        foreach (var p in pixels)
        {
            r += p.r;
            g += p.g;
            b += p.b;
        }
        var n = pixels.Length * 255f;
        return new Color(r / n, g / n, b / n, 1f);
    }

    /// <summary>GPU で縮小コピーして CPU に読む（読み取り不可のテクスチャでも読める）。</summary>
    private static Color32[] ReadTexture(Texture texture, int width, int height, bool linear)
    {
        var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D copy = null;
        try
        {
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            copy = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            copy.Apply(false);
            var pixels = copy.GetPixels32();
            var result = new Color32[pixels.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = pixels[i];
            return result;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            if (copy != null)
                Object.Destroy(copy);
        }
    }

    // ------------------------------------------------------------------ 標本

    private void BuildRows()
    {
        var region = Region;
        var step = region.width / Resolution;
        var end = Mathf.Min(Resolution, _row + RowsPerFrame);
        for (var y = _row; y < end; y++)
        {
            var z = region.y + (y + 0.5f) * step;
            for (var x = 0; x < Resolution; x++)
            {
                var wx = region.x + (x + 0.5f) * step;
                var i = y * Resolution + x;
                var info = Find(wx, z);
                if (info == null)
                {
                    _heights[i] = float.NaN;
                    continue;
                }
                var u = (wx - info.Position.x) / info.Size.x;
                var v = (z - info.Position.z) / info.Size.z;
                _heights[i] = info.Data.GetInterpolatedHeight(u, v) + info.Position.y;
                _colours[i] = GroundColour(info, u, v);
            }
        }
        _row = end;
        if (_row >= Resolution)
        {
            Compose();
            _row = -1;
        }
    }

    private TerrainInfo Find(float x, float z)
    {
        if (_last != null && Contains(_last, x, z))
            return _last;
        foreach (var info in _terrains)
        {
            if (Contains(info, x, z))
                return _last = info;
        }
        return null;
    }

    private static bool Contains(TerrainInfo t, float x, float z) =>
        x >= t.Position.x && x <= t.Position.x + t.Size.x && z >= t.Position.z && z <= t.Position.z + t.Size.z;

    /// <summary>地面の素材の色（splat の割合でレイヤーの平均色を混ぜる）。無ければ NaN 色（後で高さから付ける）。</summary>
    private static Color GroundColour(TerrainInfo info, float u, float v)
    {
        if (info.Splat == null || info.LayerColours == null)
            return new Color(float.NaN, 0f, 0f, 0f);
        var sx = Mathf.Clamp((int)(u * info.SplatWidth), 0, info.SplatWidth - 1);
        var sy = Mathf.Clamp((int)(v * info.SplatHeight), 0, info.SplatHeight - 1);
        var index = sy * info.SplatWidth + sx;
        float r = 0f, g = 0f, b = 0f, total = 0f;
        for (var t = 0; t < info.Splat.Length; t++)
        {
            var map = info.Splat[t];
            if (map == null)
                continue;
            var p = map[index];
            for (var c = 0; c < 4; c++)
            {
                var layer = t * 4 + c;
                if (layer >= info.LayerColours.Length)
                    break;
                var w = (c == 0 ? p.r : c == 1 ? p.g : c == 2 ? p.b : p.a) / 255f;
                if (w <= 0f)
                    continue;
                var col = info.LayerColours[layer];
                r += col.r * w;
                g += col.g * w;
                b += col.b * w;
                total += w;
            }
        }
        if (total <= 0.001f)
            return new Color(float.NaN, 0f, 0f, 0f);
        return new Color(r / total, g / total, b / total, 1f);
    }

    // ------------------------------------------------------------------ 仕上げ

    private void Compose()
    {
        var n = Resolution;
        var cell = Region.width / n;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var h in _heights)
        {
            if (float.IsNaN(h))
                continue;
            min = Mathf.Min(min, h);
            max = Mathf.Max(max, h);
        }
        if (min > max)
        {
            min = 0f;
            max = 1f;
        }
        var range = Mathf.Max(1f, max - min);
        ContourStep = NiceStep(range / 14f);
        // 起伏が小さい地形でも陰が見えるよう、高さを強調する
        var exaggeration = Mathf.Clamp(120f / range, 1.5f, 6f);
        var light = new Vector3(-1f, 1.4f, 1f).normalized; // 北西の上から
        var flat = light.y;

        var pixels = new Color32[n * n];
        var outside = new Color(0.17f, 0.19f, 0.2f, 1f);
        var contour = new Color(0.28f, 0.2f, 0.12f, 1f);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = y * n + x;
                var h = _heights[i];
                if (float.IsNaN(h))
                {
                    pixels[i] = outside;
                    continue;
                }
                var hx0 = Height(x - 1, y, h);
                var hx1 = Height(x + 1, y, h);
                var hy0 = Height(x, y - 1, h);
                var hy1 = Height(x, y + 1, h);

                // 色: 素材の色（無ければ高さ）を War Thunder 風に彩度を落として明るく
                var c = _colours[i];
                if (float.IsNaN(c.r))
                    c = HeightColour((h - min) / range);
                else
                    c = Stylize(c);

                // 陰影
                var normal = new Vector3(-(hx1 - hx0) / (2f * cell) * exaggeration, 1f, -(hy1 - hy0) / (2f * cell) * exaggeration).normalized;
                var shade = Mathf.Clamp(1f + (Vector3.Dot(normal, light) - flat) * 1.4f, 0.55f, 1.25f);
                c = new Color(c.r * shade, c.g * shade, c.b * shade, 1f);

                // 等高線（右か上の画素と段が違えば線）
                if (_contours)
                {
                    var band = Mathf.FloorToInt(h / ContourStep);
                    var bandR = Mathf.FloorToInt(hx1 / ContourStep);
                    var bandU = Mathf.FloorToInt(hy1 / ContourStep);
                    if (band != bandR || band != bandU)
                    {
                        var top = Mathf.Max(band, Mathf.Max(bandR, bandU));
                        var major = top % 5 == 0;
                        c = Color.Lerp(c, contour, major ? 0.55f : 0.3f);
                    }
                }
                pixels[i] = c;
            }
        }

        var texture = new Texture2D(n, n, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        Texture = texture;
        _heights = null;
        _colours = null;
        Plugin.Logger.LogInfo($"Minimap: map ready, height {min:0}-{max:0} m, contour every {ContourStep:0} m");

        if (Plugin.Cfg.DebugLog.Value)
        {
            try
            {
                var path = Path.Combine(Paths.BepInExRootPath, "minimap_debug.png");
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture));
                Plugin.Logger.LogInfo($"Minimap: wrote {path}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Minimap: debug image failed: {e.Message}");
            }
        }
    }

    private float Height(int x, int y, float fallback)
    {
        if (x < 0 || y < 0 || x >= Resolution || y >= Resolution)
            return fallback;
        var h = _heights[y * Resolution + x];
        return float.IsNaN(h) ? fallback : h;
    }

    /// <summary>マス目 1 マスの大きさ（10 マス以下になる最小のきりのいい値）。</summary>
    public static float GridCell(float width)
    {
        foreach (var c in new[] { 50f, 100f, 125f, 200f, 250f, 500f, 1000f })
        {
            if (width / c <= 10f)
                return c;
        }
        return 2000f;
    }

    private static float NiceStep(float raw)
    {
        foreach (var s in new[] { 1f, 2f, 5f, 10f, 20f, 25f, 50f, 100f, 200f })
        {
            if (s >= raw)
                return s;
        }
        return 500f;
    }

    /// <summary>素材の色を地図らしく: 彩度を落とし、紙のような明るい色に寄せる。</summary>
    private static Color Stylize(Color c)
    {
        var grey = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
        c = Color.Lerp(c, new Color(grey, grey, grey), 0.3f);
        // 暗いテクスチャでも見やすいよう明るさをそろえる
        var lift = Mathf.Clamp(0.62f / Mathf.Max(0.05f, grey), 0.8f, 2.2f);
        c = new Color(c.r * lift, c.g * lift, c.b * lift);
        return Color.Lerp(c, new Color(0.82f, 0.78f, 0.64f), 0.22f);
    }

    /// <summary>素材が分からないときの高さの色（低い = 緑、中 = 黄土、高い = 灰）。</summary>
    private static Color HeightColour(float t)
    {
        var low = new Color(0.56f, 0.64f, 0.44f);
        var mid = new Color(0.74f, 0.68f, 0.5f);
        var high = new Color(0.7f, 0.67f, 0.62f);
        return t < 0.5f ? Color.Lerp(low, mid, t * 2f) : Color.Lerp(mid, high, (t - 0.5f) * 2f);
    }
}
