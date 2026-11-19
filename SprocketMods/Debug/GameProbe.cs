using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Scripting;
using Object = UnityEngine.Object;

namespace SprocketMods.Debug;

/// <summary>
/// 開発用（perf game）: ゲーム本体の重さの手がかり。GC の方式、画質・物理・地形と草木の設定、シーンの物の数
/// （描画・影を落とす描画・光・物理の物体・パーティクル）、CPU（メイン・描画スレッド）と GPU の 1 フレームの時間。
/// 1 項目ずつ try で囲む（ゲームによって削られている API がある）。
/// </summary>
internal static class GameProbe
{
    private static readonly FrameTiming[] Timings = new FrameTiming[1];
    private static double _cpuMain, _cpuRender, _gpu, _cpuTotal;
    private static int _samples;

    /// <summary>毎フレーム（計測中だけ）。FrameTimingManager の値を足していく。</summary>
    public static void Sample()
    {
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, Timings) < 1)
                return;
            var t = Timings[0];
            _cpuTotal += t.cpuFrameTime;
            _cpuMain += t.cpuMainThreadFrameTime;
            _cpuRender += t.cpuRenderThreadFrameTime;
            _gpu += t.gpuFrameTime;
            _samples++;
        }
        catch (Exception)
        {
        }
    }

    public static void ResetTimings()
    {
        _cpuMain = _cpuRender = _gpu = _cpuTotal = 0;
        _samples = 0;
    }

    public static string TimingLine() => _samples == 0
        ? "  frame timing: not available"
        : $"  frame timing ({_samples} frames): cpu {_cpuTotal / _samples:0.00} ms, main thread {_cpuMain / _samples:0.00} ms, " +
          $"render thread {_cpuRender / _samples:0.00} ms, gpu {_gpu / _samples:0.00} ms";

    public static List<string> Report()
    {
        var lines = new List<string> { "game probe:" };
        void Add(string label, Func<object> read)
        {
            try
            {
                lines.Add($"  {label}: {read()}");
            }
            catch (Exception e)
            {
                lines.Add($"  {label}: n/a ({e.GetType().Name})");
            }
        }

        lines.Add(TimingLine());
        Add("frame timing feature", () => FrameTimingManager.IsFeatureEnabled());
        Add("gc", () => $"mode {GarbageCollector.GCMode}, incremental {GarbageCollector.isIncremental}, slice {GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.0} ms");
        Add("quality", () => $"{QualitySettings.names[QualitySettings.GetQualityLevel()]} ({QualitySettings.GetQualityLevel()}), lodBias {QualitySettings.lodBias}, maxLOD {QualitySettings.maximumLODLevel}, " +
                             $"vSync {QualitySettings.vSyncCount}, targetFrameRate {Application.targetFrameRate}, shadowDistance {QualitySettings.shadowDistance}, " +
                             $"pixelLights {QualitySettings.pixelLightCount}, realtimeReflectionProbes {QualitySettings.realtimeReflectionProbes}, " +
                             $"mipStreaming {QualitySettings.streamingMipmapsActive}, globalTextureMipmapLimit {QualitySettings.globalTextureMipmapLimit}");
        Add("screen", () => $"{Screen.width}x{Screen.height} {Screen.fullScreenMode}, refresh {Screen.currentResolution.refreshRateRatio.value:0.#} Hz");
        Add("time", () => $"fixedDeltaTime {Time.fixedDeltaTime * 1000f:0.0} ms, maximumDeltaTime {Time.maximumDeltaTime * 1000f:0} ms, timeScale {Time.timeScale}");
        Add("physics", () => $"solver {Physics.defaultSolverIterations}/{Physics.defaultSolverVelocityIterations}, simulationMode {Physics.simulationMode}, " +
                             $"autoSyncTransforms {Physics.autoSyncTransforms}, reuseCollisionCallbacks {Physics.reuseCollisionCallbacks}, defaultContactOffset {Physics.defaultContactOffset}");

        // 地形と草木
        try
        {
            var terrains = Terrain.activeTerrains;
            lines.Add($"  terrains: {terrains?.Length ?? 0}");
            for (var i = 0; terrains != null && i < terrains.Length && i < 2; i++)
            {
                var t = terrains[i];
                var d = t.terrainData;
                lines.Add($"    [{i}] pixelError {t.heightmapPixelError}, basemapDistance {t.basemapDistance}, drawInstanced {t.drawInstanced}, " +
                          $"drawTreesAndFoliage {t.drawTreesAndFoliage}, treeDistance {t.treeDistance}, billboard {t.treeBillboardDistance}, " +
                          $"treeCrossFade {t.treeCrossFadeLength}, maxMeshTrees {t.treeMaximumFullLODCount}, detailDistance {t.detailObjectDistance}, " +
                          $"detailDensity {t.detailObjectDensity}, shadows {t.shadowCastingMode}, " +
                          $"heightmap {d.heightmapResolution}, detail {d.detailResolution}, trees {d.treeInstanceCount}, size {d.size}");
            }
        }
        catch (Exception e)
        {
            lines.Add($"  terrains: n/a ({e.GetType().Name})");
        }

        // シーンの物の数
        Count(lines, "renderers", Il2CppType.Of<Renderer>(), o =>
        {
            var r = o.Cast<Renderer>();
            var on = r.enabled && r.gameObject.activeInHierarchy;
            var shadow = on && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            return (on ? 1 : 0, shadow ? 1 : 0);
        }, "enabled", "cast shadows");
        Count(lines, "lights", Il2CppType.Of<Light>(), o =>
        {
            var l = o.Cast<Light>();
            var on = l.enabled && l.gameObject.activeInHierarchy;
            return (on ? 1 : 0, on && l.shadows != LightShadows.None ? 1 : 0);
        }, "enabled", "with shadows");
        Count(lines, "rigidbodies", Il2CppType.Of<Rigidbody>(), o =>
        {
            var b = o.Cast<Rigidbody>();
            return (b.IsSleeping() ? 0 : 1, b.isKinematic ? 1 : 0);
        }, "awake", "kinematic");
        Count(lines, "colliders", Il2CppType.Of<Collider>(), o =>
        {
            var c = o.Cast<Collider>();
            return (c.enabled ? 1 : 0, c.TryCast<MeshCollider>() != null ? 1 : 0);
        }, "enabled", "mesh colliders");
        Count(lines, "particle systems", Il2CppType.Of<ParticleSystem>(), o =>
        {
            var p = o.Cast<ParticleSystem>();
            return (p.isPlaying ? 1 : 0, p.particleCount);
        }, "playing", "particles");
        Count(lines, "audio sources", Il2CppType.Of<AudioSource>(), o =>
        {
            var a = o.Cast<AudioSource>();
            return (a.isPlaying ? 1 : 0, 0);
        }, "playing", "-");
        Count(lines, "cameras", Il2CppType.Of<Camera>(), o =>
        {
            var c = o.Cast<Camera>();
            return (c.enabled && c.gameObject.activeInHierarchy ? 1 : 0, 0);
        }, "enabled", "-");
        Count(lines, "reflection probes", Il2CppType.Of<ReflectionProbe>(), o =>
        {
            var p = o.Cast<ReflectionProbe>();
            return (p.enabled ? 1 : 0, p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime ? 1 : 0);
        }, "enabled", "realtime");
        Count(lines, "lod groups", Il2CppType.Of<LODGroup>(), o => (1, 0), "-", "-");
        Count(lines, "mono behaviours", Il2CppType.Of<MonoBehaviour>(), o =>
        {
            var m = o.Cast<MonoBehaviour>();
            return (m.isActiveAndEnabled ? 1 : 0, 0);
        }, "active", "-");
        return lines;
    }

    private static void Count(List<string> lines, string label, Il2CppSystem.Type type, Func<Object, (int A, int B)> classify, string a, string b)
    {
        try
        {
            var found = Object.FindObjectsByType(type, FindObjectsSortMode.None);
            int sumA = 0, sumB = 0;
            for (var i = 0; found != null && i < found.Length; i++)
            {
                try
                {
                    var (x, y) = classify(found[i]);
                    sumA += x;
                    sumB += y;
                }
                catch (Exception)
                {
                }
            }
            lines.Add($"  {label}: {found?.Length ?? 0} ({a} {sumA}, {b} {sumB})");
        }
        catch (Exception e)
        {
            lines.Add($"  {label}: n/a ({e.GetType().Name})");
        }
    }
}
