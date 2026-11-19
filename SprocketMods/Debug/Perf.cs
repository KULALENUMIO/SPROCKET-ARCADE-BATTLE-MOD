using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SprocketMods.Debug;

/// <summary>
/// 開発用の計測（コマンド perf on / off / reset / report）。
///  - MOD の各システム（HudBehaviour から呼ぶ Update など）と、Harmony のパッチの処理の時間を、名前ごとに 1 フレームあたりで集計する
///    （平均・1 フレームでの最大・呼ばれた回数）。パッチはゲームとの境界をまたぐ分（interop の出入り）は含まない。回数で見積もる
///  - フレームの時間（平均・95 / 99 パーセンタイル・最大）、MOD の処理が占める割合、メモリの使い捨て量（.NET 側）と GC の回数
///  - 長いフレーム（50 ms 以上）を、そのフレームに時間を使った MOD の処理と一緒に記録する（読み込み・命中・撃破の引っかかり）
///  - シーンが変わった時刻（読み込みの長さ）
///  - ゲーム側（il2cpp）のヒープの増え方を名前ごとに数える（il2cpp_gc_get_used_size の差。GC が走った区間は数えない）。
///    MOD がゲーム側に作るもの（文字列・配列・Unity のオブジェクト）は il2cpp の GC（1 回 20〜30 ms 止まる）の原因になる
///  - 名前に "/" を含むもの（"HUD/Solve" など）は、ほかの計測の内側の内訳。MOD の合計には足さない
/// 計測していない間は Begin が 0 を返し、End は何もしない（ほぼ費用なし）。
/// </summary>
internal static class Perf
{
    public static bool Enabled { get; private set; }

    private sealed class Stat
    {
        public long Ticks;
        public long FrameTicks;
        public long MaxFrameTicks;
        public long Calls;
        public long FrameCalls;
        public long MaxFrameCalls;
        public long Il2cppBytes;
        public long LastFrameTicks;
    }

    // 1 フレームの中で重ならないゲームの処理（Game/Other を出すのに使う）
    private static readonly string[] TopGameKeys =
    {
        "Game/GameController.SceneFixedUpdate", "Game/GameController.SceneUpdate", "Game/VehiclesGamePlugin.GameLateUpdate",
    };

    private readonly struct Spike
    {
        public readonly float Time;
        public readonly float Milliseconds;
        public readonly float ModMilliseconds;
        public readonly string Scene;
        public readonly string Top;

        public Spike(float time, float ms, float mod, string scene, string top)
        {
            Time = time;
            Milliseconds = ms;
            ModMilliseconds = mod;
            Scene = scene;
            Top = top;
        }
    }

    private static readonly Dictionary<string, Stat> Stats = new();
    private static readonly List<float> Frames = new();
    private static readonly List<Spike> Spikes = new();
    private static readonly List<string> SceneChanges = new();
    private static readonly List<(string Key, long Ticks)> FrameTop = new();
    private static long _modTicks;
    private static long _allocStart;
    private static int _gcStart;
    private static int _il2cppGcStart = -1;
    private static float _started;
    private static string _scene = "";
    private static int _lastFrame = -1;
    private static readonly Dictionary<long, long> Heap = new(); // Begin の時刻 → その時の il2cpp ヒープの使用量
    private static long _frameHeap = -1;
    private static long _heapGrowth;
    private static bool _heapWorks = true;
    private static int _lastGcCount = -1;
    private static readonly List<float> GcFrames = new(); // il2cpp の GC が走ったフレームの長さ（次のフレームの deltaTime）
    private static bool _gcPending;
    private static float _gcCandidate;

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern long il2cpp_gc_get_used_size();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern long il2cpp_gc_get_heap_size();

    public static long HeapSize()
    {
        try
        {
            return il2cpp_gc_get_heap_size();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static long HeapUsed() => UsedHeap();

    private static long UsedHeap()
    {
        if (!_heapWorks)
            return -1;
        try
        {
            return il2cpp_gc_get_used_size();
        }
        catch (Exception)
        {
            _heapWorks = false;
            return -1;
        }
    }

    public static void Start()
    {
        Reset();
        Enabled = true;
    }

    public static void Stop() => Enabled = false;

    public static void Reset()
    {
        Stats.Clear();
        Frames.Clear();
        Spikes.Clear();
        SceneChanges.Clear();
        _modTicks = 0;
        _allocStart = GC.GetTotalAllocatedBytes(false);
        _gcStart = GC.CollectionCount(0);
        _il2cppGcStart = Il2cppCollections();
        _started = Time.realtimeSinceStartup;
        _scene = SceneName();
        Heap.Clear();
        _frameHeap = -1;
        _heapGrowth = 0;
        GameProbe.ResetTimings();
        GcFrames.Clear();
        _lastGcCount = -1;
        _gcPending = false;
    }

    public static long Begin()
    {
        if (!Enabled)
            return 0;
        var ts = Stopwatch.GetTimestamp();
        var used = UsedHeap();
        if (used >= 0)
            Heap[ts] = used;
        return ts;
    }

    public static void End(string key, long start)
    {
        if (start == 0 || !Enabled)
            return;
        var ticks = Stopwatch.GetTimestamp() - start;
        if (!Stats.TryGetValue(key, out var stat))
            Stats[key] = stat = new Stat();
        stat.Ticks += ticks;
        stat.FrameTicks += ticks;
        stat.Calls++;
        stat.FrameCalls++;
        if (Heap.Remove(start, out var before))
        {
            var grown = UsedHeap() - before;
            if (grown > 0)
                stat.Il2cppBytes += grown;
        }
    }

    /// <summary>フレームの終わり（HudBehaviour.LateUpdate の最後）。</summary>
    public static void Frame()
    {
        if (!Enabled || Time.frameCount == _lastFrame)
            return;
        _lastFrame = Time.frameCount;
        GameProbe.Sample();
        // GC が走ったフレームの長さ: このフレームで回数が増えたら、次のフレームの deltaTime（このフレームの長さ）を記録
        var ms0 = Time.unscaledDeltaTime * 1000f;
        // GC がフレームの終わり（前のフレーム）に走ったなら今の deltaTime に、今のフレームの中なら次の deltaTime に出るので、大きい方
        if (_gcPending)
        {
            GcFrames.Add(Math.Max(_gcCandidate, ms0));
            _gcPending = false;
        }
        var gcCount = Il2cppCollections();
        if (_lastGcCount >= 0 && gcCount > _lastGcCount)
        {
            _gcPending = true;
            _gcCandidate = ms0;
        }
        _lastGcCount = gcCount;
        Heap.Clear();
        var heap = UsedHeap();
        if (_frameHeap >= 0 && heap > _frameHeap)
            _heapGrowth += heap - _frameHeap;
        _frameHeap = heap;
        var ms = Time.unscaledDeltaTime * 1000f;
        Frames.Add(ms);
        long frameMod = 0;
        FrameTop.Clear();
        foreach (var pair in Stats)
        {
            var stat = pair.Value;
            if (pair.Key.IndexOf('/') < 0)
                frameMod += stat.FrameTicks;
            if (stat.FrameTicks > stat.MaxFrameTicks)
                stat.MaxFrameTicks = stat.FrameTicks;
            if (stat.FrameCalls > stat.MaxFrameCalls)
                stat.MaxFrameCalls = stat.FrameCalls;
            if (stat.FrameTicks > 0)
                FrameTop.Add((pair.Key, stat.FrameTicks));
            stat.LastFrameTicks = stat.FrameTicks;
            stat.FrameTicks = 0;
            stat.FrameCalls = 0;
        }
        _modTicks += frameMod;
        // フレームの残り（描画・入力・アニメーション・ゲームの MonoBehaviour の Update など）
        long game = 0;
        foreach (var key in TopGameKeys)
        {
            if (Stats.TryGetValue(key, out var gameStat))
                game += gameStat.LastFrameTicks;
        }
        var other = (long)(Time.unscaledDeltaTime * Stopwatch.Frequency) - game - frameMod;
        if (!Stats.TryGetValue("Game/Other", out var otherStat))
            Stats["Game/Other"] = otherStat = new Stat();
        otherStat.Ticks += other;
        otherStat.Calls++;
        if (other > otherStat.MaxFrameTicks)
            otherStat.MaxFrameTicks = other;
        var scene = SceneName();
        if (scene != _scene)
        {
            SceneChanges.Add($"{Time.realtimeSinceStartup - _started:0.0} s: {_scene} -> {scene}");
            _scene = scene;
        }
        if (ms >= 50f)
        {
            var top = string.Join(", ", FrameTop.OrderByDescending(t => t.Ticks).Take(4).Select(t => $"{t.Key} {Ms(t.Ticks):0.0}"));
            Spikes.Add(new Spike(Time.realtimeSinceStartup - _started, ms, Ms(frameMod), scene, top));
            if (Spikes.Count > 400)
                Spikes.RemoveAt(0);
        }
    }

    public static List<string> Report()
    {
        var lines = new List<string>();
        var count = Frames.Count;
        if (count == 0)
        {
            lines.Add("perf: no frames recorded (perf on)");
            return lines;
        }
        var sorted = Frames.OrderBy(f => f).ToList();
        var avg = Frames.Average();
        var seconds = Time.realtimeSinceStartup - _started;
        var alloc = GC.GetTotalAllocatedBytes(false) - _allocStart;
        var il2cppGc = Il2cppCollections();
        lines.Add($"perf: {count} frames in {seconds:0.0} s, scene {_scene}");
        var over25 = Frames.Count(f => f >= 25f);
        var over33 = Frames.Count(f => f >= 33.4f);
        lines.Add($"  frames >= 25 ms: {over25}, >= 33.4 ms: {over33}");
        lines.Add($"  frame ms: avg {avg:0.00} ({1000f / avg:0} fps)  p95 {sorted[(int)(count * 0.95f)]:0.0}  p99 {sorted[Math.Min(count - 1, (int)(count * 0.99f))]:0.0}  max {sorted[count - 1]:0.0}");
        lines.Add($"  mod ms/frame: {Ms(_modTicks) / count:0.000} ({Ms(_modTicks) / count / avg * 100f:0.0} % of the frame)");
        lines.Add($"  managed alloc: {alloc / 1024f / Math.Max(1, count):0.0} KB/frame ({alloc / 1048576f:0.0} MB total), .NET GC gen0 {GC.CollectionCount(0) - _gcStart}" +
                  (il2cppGc >= 0 && _il2cppGcStart >= 0 ? $", il2cpp GC {il2cppGc - _il2cppGcStart}" : ""));
        lines.Add(GameProbe.TimingLine());
        lines.Add($"  il2cpp heap: {HeapSize() / 1048576f:0} MB, used {UsedHeap() / 1048576f:0} MB");
        if (GcFrames.Count > 0)
            lines.Add($"  il2cpp GC frames: {GcFrames.Count}, avg {GcFrames.Average():0.0} ms, max {GcFrames.Max():0.0} ms");
        lines.Add($"  il2cpp heap growth (game + mod, frames without GC): {_heapGrowth / 1024f / Math.Max(1, count):0.0} KB/frame");
        foreach (var change in SceneChanges)
            lines.Add($"  scene {change}");
        lines.Add("  -- systems and patches (ms per frame avg / max in one frame, calls per frame avg / max, il2cpp heap KB per frame)");
        foreach (var pair in Stats.OrderByDescending(p => p.Value.Ticks))
        {
            var s = pair.Value;
            lines.Add($"  {pair.Key,-34} {Ms(s.Ticks) / count,8:0.000} / {Ms(s.MaxFrameTicks),7:0.00}   calls {s.Calls / (float)count,7:0.0} / {s.MaxFrameCalls,-4}  il2cpp {s.Il2cppBytes / 1024f / count,6:0.00} KB");
        }
        lines.Add($"  -- long frames (>= 50 ms): {Spikes.Count}");
        foreach (var spike in Spikes.OrderByDescending(s => s.Milliseconds).Take(20))
            lines.Add($"  {spike.Time,7:0.0} s  {spike.Milliseconds,6:0} ms  mod {spike.ModMilliseconds,5:0.0} ms  [{spike.Scene}]  {spike.Top}");
        try
        {
            File.WriteAllLines(Path.Combine(BepInEx.Paths.BepInExRootPath, "sprocketmods_perf.txt"), lines);
        }
        catch (Exception)
        {
        }
        return lines;
    }

    /// <summary>開発用（perf boxtest）: ゲーム側の呼び方ごとに、1 万回でゲーム側のヒープがどれだけ増えるか。</summary>
    public static List<string> BoxTest()
    {
        var lines = new List<string>();
        var go = new GameObject("SprocketBoxTest");
        var t = go.transform;
        var camera = Camera.main;
        var a = new Vector3 { x = 1f, y = 2f, z = 3f };
        var b = new Vector3 { x = 4f, y = 5f, z = 6f };
        float sink = 0f;
        void Run(string name, Action action)
        {
            const int n = 10000;
            var before = UsedHeap();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                action();
            watch.Stop();
            var grown = UsedHeap() - before;
            lines.Add($"  {name,-34} {grown / (float)n,7:0.0} bytes/call  {watch.Elapsed.TotalMilliseconds * 1000.0 / n,6:0.00} us/call");
        }
        Run("Mathf.Sqrt (float return)", () => sink += Mathf.Sqrt(2f));
        Run("Vector3.Lerp (struct return)", () => sink += Vector3.Lerp(a, b, 0.5f).x);
        Run("Vector3 - Vector3", () => sink += (a - b).x);
        Run("Vector3.magnitude", () => sink += a.magnitude);
        Run("new Vector3(x, y, z)", () => sink += new Vector3(1f, 2f, 3f).x);
        Run("new Color(r, g, b, a)", () => sink += new Color(1f, 2f, 3f, 4f).r);
        Run("transform.position (get)", () => sink += t.position.x);
        Run("transform.position = (set)", () => t.position = a);
        Run("gameObject.activeSelf", () => sink += go.activeSelf ? 1f : 0f);
        Run("Object != null", () => sink += t != null ? 1f : 0f);
        Run("transform (ref return)", () => sink += t.gameObject == null ? 0f : 1f);
        Run("string to il2cpp (go.name =)", () => go.name = "x" + (sink > 0 ? "a" : "b"));
        if (camera != null)
            Run("Camera.WorldToViewportPoint", () => sink += camera.WorldToViewportPoint(a).x);
        Run("Time.unscaledTime", () => sink += Time.unscaledTime);
        Run("Core.Fast.Alive", () => sink += Core.Fast.Alive(t) ? 1f : 0f);
        Run("Core.Fast.Pose (out)", () =>
        {
            Core.Fast.Pose(t, out var p, out _);
            sink += p.x;
        });
        var aliveBefore = Core.Fast.Alive(go);
        UnityEngine.Object.DestroyImmediate(go);
        lines.Add($"  Alive check: before destroy {aliveBefore}, after destroy {Core.Fast.Alive(go)} (Unity == null: {go == null})");
        lines.Add($"  (sink {sink:0})");
        return lines;
    }

    private static float Ms(long ticks) => ticks * 1000f / Stopwatch.Frequency;

    private static string SceneName()
    {
        try
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static int Il2cppCollections()
    {
        try
        {
            return Il2CppSystem.GC.CollectionCount(0);
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
