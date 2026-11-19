using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
///  - 陰影: 高さの傾きから北西の光で陰を付ける（起伏が見えるよう高さを強調）。
///  - 等高線: 高さの幅から間隔（2/5/10/20/50 m…）を決め、5 本ごとに太く。
///
/// 読み込み直後の引っかかりを避けるため、何フレームかに分けて作る:
///   1. 準備: 1 フレームに地形 1 枚ずつ、高さ（TerrainData.GetHeights でまとめて）と splat を CPU に読む
///   2. 標本: 1 フレームに RowsPerFrame 行ずつ、高さと地面の色を拾う
///   3. 仕上げ: 1 フレームに ComposeRowsPerFrame 行ずつ、陰影・等高線を付けて画素にする
/// 計算は C# の float だけで行う（Vector3 / Color / Mathf はゲーム側の呼び出しになり、26 万画素では非常に遅い）。
/// </summary>
internal sealed unsafe class MapTerrain
{
    public const int Resolution = 512;
    private const float MaxFullMap = 4200f;
    private const float MinRegion = 1000f;
    private const int RowsPerFrame = 64;
    private const int ComposeRowsPerFrame = 128;
    private const int MaxHeightmap = 2049;

    private static readonly float[] GridCells = { 50f, 100f, 125f, 200f, 250f, 500f, 1000f };
    private static readonly float[] Steps = { 1f, 2f, 5f, 10f, 20f, 25f, 50f, 100f, 200f };

    private sealed class TerrainInfo
    {
        public Terrain Terrain;
        public TerrainData Data;
        public float X, Y, Z; // 位置
        public float SizeX, SizeY, SizeZ;
        public float[] Heights; // 高さマップ（0..1、[z * HeightRes + x]）。読めなければ null（1 点ずつゲームに聞く）
        public int HeightRes;
        public Color32[][] Splat; // alphamapTextures を CPU に写したもの
        public int SplatWidth;
        public int SplatHeight;
        public float[] LayerColours; // レイヤーごとに r, g, b
    }

    private enum Stage
    {
        Idle,
        Prepare,
        Rows,
        Compose,
    }

    private readonly List<TerrainInfo> _terrains = new();
    private readonly Dictionary<IntPtr, (float R, float G, float B)> _layerColours = new(); // TerrainLayer ごとの平均色（地形をまたいで共通）
    private long _signature;
    private float[] _heights;
    private float[] _colours; // 画素ごとに r, g, b（r が NaN なら素材の色なし）
    private byte[] _pixels;
    private Stage _stage;
    private int _index;
    private TerrainInfo _last;
    private float _nextCheck;
    private bool _contours;
    private float _rx, _ry, _rw; // 地図の範囲（西端・南端・一辺）
    private float _min, _max;

    /// <summary>地図画像（作り終わるまで null）。</summary>
    public Texture2D Texture { get; private set; }

    /// <summary>地図の範囲（x = 西端, y = 南端, 幅 = 高さ, m）。</summary>
    public Rect Region { get; private set; }

    // 地図の範囲（C# の値。Rect の x・width などは読むたびにゲーム側の呼び出しになり、ごみが出る）
    public float RegionX => _rx;
    public float RegionY => _ry;
    public float RegionSize => _rw;

    public bool Ready => Texture is not null && _stage == Stage.Idle;
    public bool Building => _stage != Stage.Idle;
    public float ContourStep { get; private set; }

    /// <summary>毎フレーム。地形が変わったら作り直し、作っている途中なら続きを作る。</summary>
    public void Update(IReadOnlyList<Vector3> vehicles)
    {
        var now = Core.Fast.Unscaled;
        if (now >= _nextCheck && _stage == Stage.Idle)
        {
            _nextCheck = now + 2f;
            CheckTerrains(vehicles);
        }
        var perf = Debug.Perf.Begin();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var stage = _stage;
        var index = _index;
        switch (_stage)
        {
            case Stage.Prepare:
                PrepareNext();
                Debug.Perf.End("Minimap/Prepare", perf);
                break;
            case Stage.Rows:
                BuildRows();
                Debug.Perf.End("Minimap/Rows", perf);
                break;
            case Stage.Compose:
                ComposeRows();
                Debug.Perf.End("Minimap/Compose", perf);
                break;
        }
        if (stage != Stage.Idle && Plugin.Cfg.DebugLog.Value)
        {
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= 4.0)
                Plugin.Logger.LogInfo($"Minimap: {stage} {index} took {ms:0.0} ms");
        }
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
        var perf = Debug.Perf.Begin();
        Start(active, vehicles);
        Debug.Perf.End("Minimap/Start", perf);
    }

    private void Clear()
    {
        if (Texture != null)
            Object.Destroy(Texture);
        Texture = null;
        _terrains.Clear();
        _stage = Stage.Idle;
        _heights = null;
        _colours = null;
        _pixels = null;
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
            var position = terrain.GetPosition();
            var size = data.size;
            var info = new TerrainInfo
            {
                Terrain = terrain, Data = data,
                X = position.x, Y = position.y, Z = position.z,
                SizeX = size.x, SizeY = size.y, SizeZ = size.z,
            };
            _terrains.Add(info);
            minX = MathF.Min(minX, info.X);
            minZ = MathF.Min(minZ, info.Z);
            maxX = MathF.Max(maxX, info.X + info.SizeX);
            maxZ = MathF.Max(maxZ, info.Z + info.SizeZ);
        }
        if (_terrains.Count == 0)
            return;

        // 範囲: 地形全体が収まるならそれ、広すぎるなら車両の周り
        float rx, ry, side;
        var width = maxX - minX;
        var depth = maxZ - minZ;
        if (MathF.Max(width, depth) <= MaxFullMap)
        {
            side = MathF.Max(width, depth);
            rx = (minX + maxX - side) * 0.5f;
            ry = (minZ + maxZ - side) * 0.5f;
        }
        else
        {
            float vx0 = float.MaxValue, vz0 = float.MaxValue, vx1 = float.MinValue, vz1 = float.MinValue;
            foreach (var p in vehicles)
            {
                vx0 = MathF.Min(vx0, p.x);
                vz0 = MathF.Min(vz0, p.z);
                vx1 = MathF.Max(vx1, p.x);
                vz1 = MathF.Max(vz1, p.z);
            }
            side = Math.Clamp(MathF.Max(vx1 - vx0, vz1 - vz0) * 1.5f + 400f, MinRegion, MaxFullMap);
            var cx = Math.Clamp((vx0 + vx1) * 0.5f, minX + side * 0.5f, maxX - side * 0.5f);
            var cz = Math.Clamp((vz0 + vz1) * 0.5f, minZ + side * 0.5f, maxZ - side * 0.5f);
            rx = cx - side * 0.5f;
            ry = cz - side * 0.5f;
        }
        // マス目がちょうど収まるよう、一辺をマスの大きさの倍数に切り上げる（中心は保つ）
        var cell = GridCell(side);
        var rounded = MathF.Ceiling(side / cell - 0.001f) * cell;
        var centreX = rx + side * 0.5f;
        var centreY = ry + side * 0.5f;
        _rx = centreX - rounded * 0.5f;
        _ry = centreY - rounded * 0.5f;
        _rw = rounded;
        Region = new Rect(_rx, _ry, _rw, _rw);
        // 地図の範囲にかかる地形だけ読む（広い地形では範囲は車両の周りだけで、残りのタイルを読むのは無駄）
        var total = _terrains.Count;
        _terrains.RemoveAll(t => t.X > _rx + _rw || t.X + t.SizeX < _rx || t.Z > _ry + _rw || t.Z + t.SizeZ < _ry);

        _heights = new float[Resolution * Resolution];
        _colours = new float[Resolution * Resolution * 3];
        _stage = Stage.Prepare;
        _index = 0;
        _last = null;
        Plugin.Logger.LogInfo($"Minimap: {_terrains.Count} of {total} terrain(s), area {width:0}x{depth:0} m, map {_rw:0} m at ({_rx:0}, {_ry:0})");
    }

    /// <summary>地形 1 枚分の高さと splat を読む（1 フレームに 1 枚）。</summary>
    private void PrepareNext()
    {
        if (_index < _terrains.Count)
        {
            var info = _terrains[_index++];
            if (info.Data == null || info.Data.WasCollected)
            {
                Restart();
                return;
            }
            ReadHeights(info);
            ReadSplat(info);
            return;
        }
        _stage = Stage.Rows;
        _index = 0;
    }

    /// <summary>地形が途中で消えた（シーンが変わった）ら、次の確認で作り直す。</summary>
    private void Restart()
    {
        Clear();
        _signature = -1;
    }

    /// <summary>高さマップを 1 回でまとめて読む（float[,] の中身をそのまま写す）。</summary>
    private static void ReadHeights(TerrainInfo info)
    {
        try
        {
            var res = info.Data.heightmapResolution;
            if (res < 2 || res > MaxHeightmap)
                return;
            var array = info.Data.GetHeights(0, 0, res, res);
            if (array == null)
                return;
            // il2cpp の配列: 先頭 0x18 に要素数、0x20 から中身（多次元でも同じ。[z, x] の行優先）
            var length = *(long*)(array.Pointer + 0x18);
            if (length != (long)res * res)
                return;
            var heights = new float[res * res];
            Marshal.Copy(array.Pointer + 0x20, heights, 0, heights.Length);
            info.Heights = heights;
            info.HeightRes = res;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Minimap: heightmap not readable, sampling instead ({e.Message})");
            info.Heights = null;
        }
    }

    /// <summary>splat（各レイヤーの割合）と各レイヤーの平均色を読む。読めなければ高さで色を付ける。</summary>
    private void ReadSplat(TerrainInfo info)
    {
        try
        {
            var data = info.Data;
            var layers = data.terrainLayers;
            var textures = data.alphamapTextures;
            if (layers == null || layers.Length == 0 || textures == null || textures.Length == 0)
                return;
            var count = layers.Length;
            var colours = new float[count * 3];
            for (var i = 0; i < count; i++)
            {
                var layer = layers[i];
                (float R, float G, float B) c = (0.55f, 0.55f, 0.45f);
                if (layer != null && !_layerColours.TryGetValue(layer.Pointer, out c))
                {
                    var diffuse = layer.diffuseTexture;
                    c = diffuse != null ? AverageColour(diffuse) : (0.55f, 0.55f, 0.45f);
                    _layerColours[layer.Pointer] = c;
                }
                colours[i * 3] = c.R;
                colours[i * 3 + 1] = c.G;
                colours[i * 3 + 2] = c.B;
            }
            // 草地・土・岩などの色の差が小さいので、平均からの差を強調して見分けやすくする
            for (var ch = 0; ch < 3; ch++)
            {
                var mean = 0f;
                for (var i = 0; i < count; i++)
                    mean += colours[i * 3 + ch];
                mean /= count;
                for (var i = 0; i < count; i++)
                    colours[i * 3 + ch] = Math.Clamp(mean + (colours[i * 3 + ch] - mean) * 1.8f, 0f, 1f);
            }
            info.LayerColours = colours;
            var w = Math.Min(textures[0].width, Resolution);
            var h = Math.Min(textures[0].height, Resolution);
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

    private static (float R, float G, float B) AverageColour(Texture texture)
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
        return (r / n, g / n, b / n);
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
            // 生の画素（RGBA の 4 バイトずつ）をまとめて写す
            var raw = copy.GetRawTextureData();
            var result = new Color32[width * height];
            fixed (Color32* dst = result)
            {
                var bytes = Math.Min((long)result.Length * 4, raw.Length);
                Buffer.MemoryCopy((void*)(raw.Pointer + 0x20), dst, (long)result.Length * 4, bytes);
            }
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
        var step = _rw / Resolution;
        var end = Math.Min(Resolution, _index + RowsPerFrame);
        for (var y = _index; y < end; y++)
        {
            var z = _ry + (y + 0.5f) * step;
            for (var x = 0; x < Resolution; x++)
            {
                var wx = _rx + (x + 0.5f) * step;
                var i = y * Resolution + x;
                var info = Find(wx, z);
                if (info == null)
                {
                    _heights[i] = float.NaN;
                    _colours[i * 3] = float.NaN;
                    continue;
                }
                var u = (wx - info.X) / info.SizeX;
                var v = (z - info.Z) / info.SizeZ;
                _heights[i] = SampleHeight(info, u, v) + info.Y;
                GroundColour(info, u, v, i * 3);
            }
        }
        _index = end;
        if (_index >= Resolution)
            BeginCompose();
    }

    private static float SampleHeight(TerrainInfo info, float u, float v)
    {
        var heights = info.Heights;
        if (heights == null)
            return info.Data.GetInterpolatedHeight(u, v);
        var last = info.HeightRes - 1;
        var fx = Math.Clamp(u, 0f, 1f) * last;
        var fy = Math.Clamp(v, 0f, 1f) * last;
        var x0 = Math.Min((int)fx, last - 1);
        var y0 = Math.Min((int)fy, last - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var row0 = y0 * info.HeightRes;
        var row1 = row0 + info.HeightRes;
        var a = heights[row0 + x0] + (heights[row0 + x0 + 1] - heights[row0 + x0]) * tx;
        var b = heights[row1 + x0] + (heights[row1 + x0 + 1] - heights[row1 + x0]) * tx;
        return (a + (b - a) * ty) * info.SizeY;
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
        x >= t.X && x <= t.X + t.SizeX && z >= t.Z && z <= t.Z + t.SizeZ;

    /// <summary>地面の素材の色（splat の割合でレイヤーの平均色を混ぜる）。無ければ r に NaN（後で高さから付ける）。</summary>
    private void GroundColour(TerrainInfo info, float u, float v, int o)
    {
        _colours[o] = float.NaN;
        if (info.Splat == null || info.LayerColours == null)
            return;
        var layers = info.LayerColours.Length / 3;
        var sx = Math.Clamp((int)(u * info.SplatWidth), 0, info.SplatWidth - 1);
        var sy = Math.Clamp((int)(v * info.SplatHeight), 0, info.SplatHeight - 1);
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
                if (layer >= layers)
                    break;
                var w = (c == 0 ? p.r : c == 1 ? p.g : c == 2 ? p.b : p.a) / 255f;
                if (w <= 0f)
                    continue;
                r += info.LayerColours[layer * 3] * w;
                g += info.LayerColours[layer * 3 + 1] * w;
                b += info.LayerColours[layer * 3 + 2] * w;
                total += w;
            }
        }
        if (total <= 0.001f)
            return;
        _colours[o] = r / total;
        _colours[o + 1] = g / total;
        _colours[o + 2] = b / total;
    }

    // ------------------------------------------------------------------ 仕上げ

    private void BeginCompose()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var h in _heights)
        {
            if (float.IsNaN(h))
                continue;
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }
        if (min > max)
        {
            min = 0f;
            max = 1f;
        }
        _min = min;
        _max = max;
        ContourStep = NiceStep(MathF.Max(1f, max - min) / 14f);
        _pixels = new byte[Resolution * Resolution * 4];
        _stage = Stage.Compose;
        _index = 0;
    }

    private void ComposeRows()
    {
        const int n = Resolution;
        var cell = _rw / n;
        var range = MathF.Max(1f, _max - _min);
        // 起伏が小さい地形でも陰が見えるよう、高さを強調する
        var exaggeration = Math.Clamp(120f / range, 1.5f, 6f);
        // 北西の上からの光 (-1, 1.4, 1) を正規化
        var ll = MathF.Sqrt(1f + 1.96f + 1f);
        float lx = -1f / ll, ly = 1.4f / ll, lz = 1f / ll;
        var step = ContourStep;
        var end = Math.Min(n, _index + ComposeRowsPerFrame);
        for (var y = _index; y < end; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = y * n + x;
                var h = _heights[i];
                float r, g, b;
                if (float.IsNaN(h))
                {
                    Put(i, 0.17f, 0.19f, 0.2f);
                    continue;
                }
                var hx0 = Height(x - 1, y, h);
                var hx1 = Height(x + 1, y, h);
                var hy0 = Height(x, y - 1, h);
                var hy1 = Height(x, y + 1, h);

                // 色: 素材の色（無ければ高さ）を War Thunder 風に彩度を落として明るく
                r = _colours[i * 3];
                if (float.IsNaN(r))
                    HeightColour((h - _min) / range, out r, out g, out b);
                else
                    Stylize(r, _colours[i * 3 + 1], _colours[i * 3 + 2], out r, out g, out b);

                // 陰影
                var nx = -(hx1 - hx0) / (2f * cell) * exaggeration;
                var nz = -(hy1 - hy0) / (2f * cell) * exaggeration;
                var dot = (nx * lx + ly + nz * lz) / MathF.Sqrt(nx * nx + 1f + nz * nz);
                var shade = Math.Clamp(1f + (dot - ly) * 1.4f, 0.55f, 1.25f);
                r *= shade;
                g *= shade;
                b *= shade;

                // 等高線（右か上の画素と段が違えば線）
                if (_contours)
                {
                    var band = (int)MathF.Floor(h / step);
                    var bandR = (int)MathF.Floor(hx1 / step);
                    var bandU = (int)MathF.Floor(hy1 / step);
                    if (band != bandR || band != bandU)
                    {
                        var top = Math.Max(band, Math.Max(bandR, bandU));
                        var k = top % 5 == 0 ? 0.55f : 0.3f;
                        r += (0.28f - r) * k;
                        g += (0.2f - g) * k;
                        b += (0.12f - b) * k;
                    }
                }
                Put(i, r, g, b);
            }
        }
        _index = end;
        if (_index >= n)
            Finish();
    }

    private void Put(int i, float r, float g, float b)
    {
        var o = i * 4;
        _pixels[o] = ToByte(r);
        _pixels[o + 1] = ToByte(g);
        _pixels[o + 2] = ToByte(b);
        _pixels[o + 3] = 255;
    }

    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

    private void Finish()
    {
        const int n = Resolution;
        var texture = new Texture2D(n, n, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.LoadRawTextureData(_pixels);
        texture.Apply(false, false);
        Texture = texture;
        _heights = null;
        _colours = null;
        _pixels = null;
        _stage = Stage.Idle;
        foreach (var info in _terrains)
        {
            info.Heights = null; // 作り終えたら大きな配列は手放す
            info.Splat = null;
        }
        Latest = texture;
        Plugin.Logger.LogInfo($"Minimap: map ready, height {_min:0}-{_max:0} m, contour every {ContourStep:0} m");
    }

    /// <summary>最後に作った地図（開発用のコマンド minimap png で書き出す）。</summary>
    private static Texture2D Latest;

    /// <summary>
    /// 開発用（minimap png）: 地図を BepInEx/minimap_debug.png に書き出す。
    /// PNG への変換は数十 ms かかるので、地図を作るたびには行わない。
    /// </summary>
    public static string SaveDebugImage()
    {
        if (Latest == null || Latest.WasCollected)
            return "minimap: no map yet";
        try
        {
            var path = Path.Combine(Paths.BepInExRootPath, "minimap_debug.png");
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(Latest));
            return $"minimap: wrote {path}";
        }
        catch (Exception e)
        {
            return $"minimap: debug image failed: {e.Message}";
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
        foreach (var c in GridCells)
        {
            if (width / c <= 10f)
                return c;
        }
        return 2000f;
    }

    private static float NiceStep(float raw)
    {
        foreach (var s in Steps)
        {
            if (s >= raw)
                return s;
        }
        return 500f;
    }

    /// <summary>素材の色を地図らしく: 彩度を落とし、紙のような明るい色に寄せる。</summary>
    private static void Stylize(float r, float g, float b, out float or, out float og, out float ob)
    {
        var grey = r * 0.3f + g * 0.59f + b * 0.11f;
        r += (grey - r) * 0.3f;
        g += (grey - g) * 0.3f;
        b += (grey - b) * 0.3f;
        // 暗いテクスチャでも見やすいよう明るさをそろえる
        var lift = Math.Clamp(0.62f / MathF.Max(0.05f, grey), 0.8f, 2.2f);
        r *= lift;
        g *= lift;
        b *= lift;
        or = r + (0.82f - r) * 0.22f;
        og = g + (0.78f - g) * 0.22f;
        ob = b + (0.64f - b) * 0.22f;
    }

    /// <summary>素材が分からないときの高さの色（低い = 緑、中 = 黄土、高い = 灰）。</summary>
    private static void HeightColour(float t, out float r, out float g, out float b)
    {
        if (t < 0.5f)
        {
            var k = t * 2f;
            r = 0.56f + (0.74f - 0.56f) * k;
            g = 0.64f + (0.68f - 0.64f) * k;
            b = 0.44f + (0.5f - 0.44f) * k;
        }
        else
        {
            var k = (t - 0.5f) * 2f;
            r = 0.74f + (0.7f - 0.74f) * k;
            g = 0.68f + (0.67f - 0.68f) * k;
            b = 0.5f + (0.62f - 0.5f) * k;
        }
    }
}
