using System;
using BepInEx.Configuration;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Colliders;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Tracks.Belts;
using UnityEngine;

namespace SprocketMods.Tracks;

/// <summary>履帯のダメージの設定（[TrackDamage] セクション）。</summary>
public class TrackDamageConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<float> ArmourFactor;

    public TrackDamageConfig(ConfigFile cfg)
    {
        const string s = "TrackDamage";
        Enabled = cfg.Bind(s, "Enabled", true,
            "履帯の帯（履板）にも当たり判定を付ける。当たると履帯の体力が減り、0 で履帯が切れる（ゲームでは転輪・サスペンションにしか判定が無い）。車両を作り直したときに反映");
        ArmourFactor = cfg.Bind(s, "ArmourFactor", 1f,
            new ConfigDescription("履板の装甲としての強さ（予備履帯と同じ値に掛ける）。0 にすると砲弾を減衰させず、履帯の体力だけ減る",
                new AcceptableValueRange<float>(0f, 3f)));
    }
}

/// <summary>
/// 履帯の帯に当たり判定を足す。
///
///  - ゲームの履帯（TrackAssembly、左右 1 つずつ）には体力があり、0 になると Broken（走行不能）になる。その当たり判定は
///    TrackAssembly.BuildDamageModelColliders で転輪の軸・転輪の並び（WheelMount / WheelArray / AxleRod）にだけ作られ、
///    それぞれ TrackAssemblyVuid（その履帯の VUID）を付けて builder に渡している。この ID が、当たったときに減る体力を決める。
///  - その後処理で、帯（TrackBelt）の静止時のリンクの位置（segmentPoints、帯の平面の 2 次元: x = 前後、y = 高さ。
///    TrackBelt.ToLocalSpace と同じく (0, y, x) が帯の Transform の座標）ごとに、履板 1 枚の箱を同じ ID で足す。
///    箱の向きは前後のリンクを結ぶ向き、大きさは帯の幅 × 厚さ × リンクの間隔。
///  - 装甲の値は、車体に付ける予備履帯（TrackBeltSegmentArray）と同じ DamageModelParameters に ArmourFactor を掛けたもの。
/// </summary>
internal static class TrackDamage
{
    private static TrackDamageConfig Cfg => Plugin.TrackDamage;
    private static int _logged;

    public static void AfterBuildColliders(TrackAssembly track, IVehicleComponentDamageModelBuilder builder)
    {
        if (!Cfg.Enabled.Value || track == null || builder == null)
            return;
        var belt = track.Belt;
        var points = belt?.segmentPoints;
        var count = points?.Length ?? 0;
        if (count < 3)
        {
            if (_logged++ < 4)
                Plugin.Logger.LogInfo($"Track damage: {track.name} has no belt points yet ({count})");
            return;
        }
        var vehicleObject = ((VehicleComponent)track).vehicleObject;
        if (vehicleObject == null)
            return;
        var id = ((VehiclePart)track).VUID;
        var entity = vehicleObject.entityID;
        var links = TrackBeltSegmentArray.DamageModelParameters;
        var parameters = new DamageModelParameters(links.RhaFactor * Cfg.ArmourFactor.Value, links.SpallFactor, links.Density, links.Priority);
        var size = new Vector3(Mathf.Max(0.05f, belt.Width), Mathf.Max(0.01f, belt.Thickness), Mathf.Max(0.02f, belt.SegmentPitch) * 1.05f);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var matrix = belt.transform.localToWorldMatrix;
        // リンクの位置は 1 回だけ読み、向きは C# の float で計算する（Vector3 / Quaternion の演算はゲーム側の呼び出しで遅い）
        var bx = new float[count];
        var by = new float[count];
        for (var i = 0; i < count; i++)
        {
            var point = points[i].Position;
            bx[i] = point.x;
            by[i] = point.y;
        }
        var added = 0;
        for (var i = 0; i < count; i++)
        {
            // 帯の座標 (0, y, x)。前後のリンクを結ぶ向き（y-z 平面）を前、それと帯の横（x 軸）に直交する向きを上にした回転は、
            // x 軸まわりの回転になる: 前 = (0, -sin θ, cos θ)
            var next = (i + 1) % count;
            var previous = (i - 1 + count) % count;
            var fy = by[next] - by[previous];
            var fz = bx[next] - bx[previous];
            var length = MathF.Sqrt(fy * fy + fz * fz);
            if (length < 1e-4f)
                continue;
            var half = MathF.Atan2(-fy / length, fz / length) * 0.5f;
            var rotation = new Quaternion { x = MathF.Sin(half), y = 0f, z = 0f, w = MathF.Cos(half) };
            var position = new Vector3 { x = 0f, y = by[i], z = bx[i] };
            builder.AddBox(id, ref matrix, entity, position, rotation, size, ref parameters);
            added++;
        }
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (_logged++ < 4 || Plugin.Cfg.DebugLog.Value && _logged < 40)
            Plugin.Logger.LogInfo($"Track damage: {track.name} {ms:0.00} ms, belt boxes {added} size {size} rha {parameters.RhaFactor:0.###} " +
                                  $"density {parameters.Density:0.###} spall {parameters.SpallFactor:0.###} priority {parameters.Priority} vuid {id.Value} entity {entity}");
    }

    /// <summary>
    /// 開発用（tracks）: 車両ごとに、足した箱の中心（静止時のリンクの位置）と実際に描かれているリンクの位置のずれ、履帯の体力。
    /// </summary>
    public static System.Collections.Generic.IEnumerable<string> Describe()
    {
        var lines = new System.Collections.Generic.List<string>();
        var found = UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<Vehicle>(), FindObjectsSortMode.None);
        lines.Add($"vehicles {found?.Length ?? 0}");
        var shown = 0;
        for (var v = 0; found != null && v < found.Length; v++)
        {
            var vehicle = found[v]?.TryCast<Vehicle>();
            if (vehicle == null || shown >= 4)
                continue;
            foreach (var track in Assemblies(vehicle))
            {
                var belt = track?.Belt;
                var points = belt?.segmentPoints;
                var links = belt?.beltRenderer?.TryCast<Sprocket.ContinuousTracks.TrackBeltRenderer>()?.models;
                if (points == null || links == null || points.Length == 0)
                {
                    lines.Add($"{vehicle.name} {track.name}: belt {(belt != null)} points {points?.Length ?? -1} links {links?.Length ?? -1}");
                    continue;
                }
                var sum = 0f;
                var worst = 0f;
                for (var i = 0; i < points.Length; i++)
                {
                    var box = belt.transform.TransformPoint(Local(points[i].Position));
                    var best = float.PositiveInfinity;
                    foreach (var link in links)
                    {
                        if (link != null && link.gameObject.activeInHierarchy)
                            best = Mathf.Min(best, (link.transform.position - box).magnitude);
                    }
                    sum += best;
                    worst = Mathf.Max(worst, best);
                }
                lines.Add($"{vehicle.name} {track.name}: boxes {points.Length} links {links.Length} offset mean {sum / points.Length:0.000} m max {worst:0.000} m " +
                          $"health {track.HealthFraction:P0} broken {track.Broken}");
            }
            shown++;
        }
        if (lines.Count == 0)
            lines.Add("no tracks");
        return lines;
    }

    /// <summary>車両の履帯（Transform の階層は平らにされているので、部品の登録 Vehicle.objects から集める）。</summary>
    internal static System.Collections.Generic.List<TrackAssembly> Assemblies(Vehicle vehicle)
    {
        var list = new System.Collections.Generic.List<TrackAssembly>();
        var objects = vehicle?.objects?.register;
        for (var i = 0; objects != null && i < objects.Count; i++)
        {
            var behaviours = objects[i]?.behaviours;
            for (var b = 0; behaviours != null && b < behaviours.Length; b++)
            {
                var track = behaviours[b]?.TryCast<TrackAssembly>();
                if (track != null)
                    list.Add(track);
            }
        }
        return list;
    }

    /// <summary>帯の平面の点を帯の Transform の座標にする（TrackBelt.ToLocalSpace と同じ）。</summary>
    internal static Vector3 Local(Vector2 belt) => new() { x = 0f, y = belt.y, z = belt.x };
}
