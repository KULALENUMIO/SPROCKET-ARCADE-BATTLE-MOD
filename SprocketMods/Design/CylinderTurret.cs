using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.MeshGeneration;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.ProceduralMeshGeneration.ThreeSplitTurret;
using Sprocket.Vehicles.Turrets;
using UnityEngine;

namespace SprocketMods.Design;

internal sealed class CylinderTurretConfig
{
    public readonly ConfigEntry<bool> Enabled;

    public CylinderTurretConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Design", "CylinderTurret", true, "砲塔の設計画面に円柱（32 角）の形状を追加する");
    }
}

/// <summary>
/// 砲塔の形状「円柱（32 角）」。ゲームの生成形式を増やすと MOD なしで設計図が開けなくなるので、
/// 中身は普通のフリーフォームのメッシュとして作る（MOD なしでも開ける）。
/// 寸法は頂点の並びから読み取るので、保存して開き直しても続きから編集できる。
/// 反映はゲームの PlateStructureEditor.LoadBlueprint を通すので、元に戻す（Undo）が効く。
/// </summary>
internal static class CylinderTurret
{
    private const int Sides = 32;
    private const float SmoothAngle = 20f; // 面どうしの角度（11.25°）より大きく、天板の縁（90°）より小さい
    private const float ApplyDelay = 0.25f; // スライダーを動かすたびに Undo の履歴が増えないよう、止まってから反映する

    private struct Shape
    {
        public float Radius;
        public float Height;
        public float HoleRadius;
        public int SideArmour;
        public int RoofArmour;
        public int BottomArmour;
    }

    private static PlateStructureEditor _pendingEditor;
    private static Shape _pendingShape;
    private static float _applyAt = float.NaN;
    private static IntPtr _shapeMesh;
    private static Shape _shape;
    private static IntPtr _armourMesh;
    private static float _nextArmourRead;
    private static (int Side, int Roof, int Bottom) _armour;

    private static CylinderTurretConfig Cfg => Plugin.CylinderTurret;

    /// <summary>砲塔の構造物の編集欄に項目を足す（Patches.cs から PlateStructureEditor.OnGUI の後に呼ばれる）。</summary>
    public static void DrawGui(PlateStructureEditor editor, IGUILayout layout)
    {
        if (!Cfg.Enabled.Value || editor == null || layout == null)
            return;
        var drawer = layout.TryCast<IGUIElementDrawer>();
        var mesh = editor.mesh;
        if (drawer == null || mesh == null)
            return;

        if (TryRead(mesh, out var current))
        {
            // 反映待ちの間は手元の値を正とする。それ以外はメッシュの値（Undo で戻った場合も含む）
            if (_shapeMesh != mesh.Pointer || float.IsNaN(_applyAt))
            {
                _shapeMesh = mesh.Pointer;
                _shape = current;
            }
            // 砲塔リングの大きさを変えた場合、フリーフォームには自動で反映されないので穴を合わせる
            var ring = GetRingRadius(editor);
            if (ring > 0f && Math.Abs(ring - _shape.HoleRadius) > 0.002f && _shape.HoleRadius > 0f)
            {
                _shape.HoleRadius = ring;
                _shape.Radius = Math.Max(_shape.Radius, ring + 0.01f);
                Request(editor);
            }
            DrawShapeSliders(drawer, editor);
        }
        else if (editor.TryCast<ThreeSplitTurretEditor>() != null)
        {
            drawer.AddSpace(8f);
            drawer.Header("Cylinder turret (32 sides)");
            // UITooltip は参照を含む構造体で ref 渡しになり、interop では壊れるので Button は使わずトグルにする
            var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(new Action<bool>(on =>
            {
                if (!on)
                    return;
                try
                {
                    Convert(editor);
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"Cylinder conversion failed: {e}");
                }
            }));
            drawer.ToggleField("Use cylinder shape", false, callback, "Replaces this turret with a 32-sided cylinder. Saved as a freeform mesh, so the design also opens without the mod. Undo restores the old shape.");
        }
    }

    private static void DrawShapeSliders(IGUIElementDrawer drawer, PlateStructureEditor editor)
    {
        drawer.AddSpace(8f);
        drawer.Header("Cylinder turret (32 sides)");
        var minDiameter = Math.Max(0.4f, _shape.HoleRadius * 2f + 0.02f);
        Slider(drawer, editor, "Diameter (m)", _shape.Radius * 2f, minDiameter, 6f, (ref Shape s, float v) => s.Radius = v / 2f);
        Slider(drawer, editor, "Height (m)", _shape.Height, 0.2f, 3f, (ref Shape s, float v) => s.Height = v);
        ArmourSlider(drawer, editor, "Side armour (mm)", _shape.SideArmour, (ref Shape s, int v) => s.SideArmour = v);
        ArmourSlider(drawer, editor, "Roof armour (mm)", _shape.RoofArmour, (ref Shape s, int v) => s.RoofArmour = v);
        ArmourSlider(drawer, editor, "Bottom armour (mm)", _shape.BottomArmour, (ref Shape s, int v) => s.BottomArmour = v);
    }

    private delegate void Setter<T>(ref Shape shape, T value);

    private static void Slider(IGUIElementDrawer drawer, PlateStructureEditor editor, string label, float value, float min, float max, Setter<float> set)
    {
        var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(new Action<float>(v =>
        {
            set(ref _shape, (float)Math.Round(v, 3));
            Request(editor);
        }));
        drawer.Slider(label, value, min, max, callback);
    }

    private static void ArmourSlider(IGUIElementDrawer drawer, PlateStructureEditor editor, string label, int value, Setter<int> set)
    {
        var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(new Action<float>(v =>
        {
            set(ref _shape, (int)Math.Round(v));
            Request(editor);
        }));
        drawer.Slider(label, value, 1, 500, callback);
    }

    private static void Request(PlateStructureEditor editor)
    {
        _pendingEditor = editor;
        _pendingShape = _shape;
        _applyAt = Core.Fast.Unscaled + ApplyDelay;
    }

    /// <summary>反映待ちを処理する（HudBehaviour から毎フレーム）。</summary>
    public static void Update()
    {
        if (float.IsNaN(_applyAt) || Core.Fast.Unscaled < _applyAt)
            return;
        _applyAt = float.NaN;
        var editor = _pendingEditor;
        _pendingEditor = null;
        if (editor == null || editor.WasCollected || editor.mesh == null)
            return;
        Apply(editor, _pendingShape);
    }

    private static void Convert(PlateStructureEditor editor)
    {
        var mesh = editor.mesh;
        var bounds = mesh.LocalBounds;
        var holeRadius = GetRingRadius(editor);
        var radius = Math.Max(Math.Max(bounds.extents.x, bounds.extents.z), holeRadius + 0.05f);
        var shape = new Shape
        {
            Radius = radius,
            Height = Math.Max(bounds.size.y, 0.3f),
            HoleRadius = holeRadius,
            SideArmour = 50,
            RoofArmour = 20,
            BottomArmour = 20,
        };
        _shapeMesh = mesh.Pointer;
        _shape = shape;
        Apply(editor, shape);
    }

    private static float GetRingRadius(PlateStructureEditor editor)
    {
        var ring = editor.Component?.GetComponentInParent<TurretRing>();
        return ring != null ? ring.ringRadius : 0f;
    }

    private static void Apply(PlateStructureEditor editor, Shape shape)
    {
        var editMesh = new EditMesh();
        var blueprint = Sprocket.SprocketIO.DeserializeJSON<MeshBlueprint>(BuildJson(shape));
        blueprint.ToMesh(editMesh);
        editMesh.smoothAngleThreshold = SmoothAngle;
        editor.LoadBlueprint(new PlateStructureMeshBlueprint(null, editMesh));
        Plugin.Logger.LogInfo($"Cylinder turret: d={shape.Radius * 2f:F2} h={shape.Height:F2} ring={shape.HoleRadius:F2} armour={shape.SideArmour}/{shape.RoofArmour}/{shape.BottomArmour}");
    }

    // ------------------------------------------------------------------ メッシュ

    // 頂点: 0..31 底の外周 / 32..63 天板の外周 / 64 天板の中心 /
    //       穴あり: 65..96 底の穴の周（砲塔リングの半径）、穴なし: 65 底の中心
    // 面:   0..31 側面（四角形）/ 32..63 天板（三角形）/ 64..95 底（穴ありは四角形、穴なしは三角形）
    // 面の表は外向き。側面の 1 枚が真正面を向くよう半ピッチずらす。

    private static string BuildJson(Shape shape)
    {
        var hasHole = shape.HoleRadius > 0.001f;
        var vertices = new List<Vector3>();
        var faces = new List<int[]>();
        var thickness = new List<int>();
        void Ring(float radius, float y)
        {
            for (var i = 0; i < Sides; i++)
            {
                var angle = (i + 0.5f) * Mathf.PI * 2f / Sides;
                vertices.Add(new Vector3(radius * Mathf.Cos(angle), y, radius * Mathf.Sin(angle)));
            }
        }
        Ring(shape.Radius, 0f);
        Ring(shape.Radius, shape.Height);
        vertices.Add(new Vector3(0f, shape.Height, 0f));
        if (hasHole)
            Ring(shape.HoleRadius, 0f);
        else
            vertices.Add(Vector3.zero);

        const int bottom = 0, top = Sides, roofCentre = Sides * 2, inner = Sides * 2 + 1;
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            faces.Add(new[] { bottom + next, bottom + i, top + i, top + next });
            thickness.Add(shape.SideArmour);
        }
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            faces.Add(new[] { roofCentre, top + next, top + i });
            thickness.Add(shape.RoofArmour);
        }
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            faces.Add(hasHole
                ? new[] { bottom + i, bottom + next, inner + next, inner + i }
                : new[] { inner, bottom + i, bottom + next });
            thickness.Add(shape.BottomArmour);
        }

        var edges = new List<(int A, int B)>();
        var edgeIds = new HashSet<long>();
        foreach (var face in faces)
        {
            for (var i = 0; i < face.Length; i++)
            {
                var a = face[i];
                var b = face[(i + 1) % face.Length];
                if (edgeIds.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b)))
                    edges.Add((a, b));
            }
        }

        var c = CultureInfo.InvariantCulture;
        var json = new StringBuilder();
        json.Append("{\"majorVersion\":0,\"minorVersion\":3,\"vertices\":[");
        for (var i = 0; i < vertices.Count; i++)
        {
            if (i > 0)
                json.Append(',');
            var v = vertices[i];
            json.Append(v.x.ToString("R", c)).Append(',').Append(v.y.ToString("R", c)).Append(',').Append(v.z.ToString("R", c));
        }
        json.Append("],\"edges\":[");
        for (var i = 0; i < edges.Count; i++)
        {
            if (i > 0)
                json.Append(',');
            json.Append(edges[i].A).Append(',').Append(edges[i].B);
        }
        json.Append("],\"edgeFlags\":[");
        for (var i = 0; i < edges.Count; i++)
            json.Append(i > 0 ? ",0" : "0");
        json.Append("],\"faces\":[");
        for (var i = 0; i < faces.Count; i++)
        {
            if (i > 0)
                json.Append(',');
            var face = faces[i];
            json.Append("{\"v\":[").Append(string.Join(",", face)).Append("],\"t\":[");
            for (var j = 0; j < face.Length; j++)
                json.Append(j > 0 ? "," : string.Empty).Append(Math.Clamp(thickness[i], 1, 65535));
            // 角の厚み付けは Auto（各バイトが 1 角ぶん）
            json.Append("],\"tm\":").Append(face.Length == 4 ? 0x01010101 : 0x00010101).Append(",\"te\":0}");
        }
        json.Append("]}");
        return json.ToString();
    }

    /// <summary>メッシュがこの円柱なら寸法を読み取る。</summary>
    private static bool TryRead(PlateStructureMesh mesh, out Shape shape)
    {
        shape = default;
        var vertexCount = mesh.VertexCount;
        var hasHole = vertexCount == Sides * 3 + 1;
        if (mesh.IsGenerated || mesh.FaceCount != Sides * 3 || (!hasHole && vertexCount != Sides * 2 + 2))
            return false;
        var reader = mesh.TryCast<IReadOnlyMesh>();
        if (reader == null)
            return false;

        var first = reader.GetVertexPosition(0);
        var radius = new Vector2(first.x, first.z).magnitude;
        var height = reader.GetVertexPosition(Sides).y - first.y;
        if (radius <= 0.001f || height <= 0.001f)
            return false;
        for (var i = 0; i < Sides; i++)
        {
            var low = reader.GetVertexPosition(i);
            var high = reader.GetVertexPosition(Sides + i);
            if (Math.Abs(new Vector2(low.x, low.z).magnitude - radius) > 0.002f
                || Math.Abs(low.y - first.y) > 0.002f
                || Math.Abs(high.x - low.x) > 0.002f
                || Math.Abs(high.z - low.z) > 0.002f
                || Math.Abs(high.y - low.y - height) > 0.002f)
                return false;
        }

        var hole = 0f;
        if (hasHole)
        {
            var inner = reader.GetVertexPosition(Sides * 2 + 1);
            hole = new Vector2(inner.x, inner.z).magnitude;
        }
        if (mesh.Pointer != _armourMesh || Core.Fast.Unscaled >= _nextArmourRead)
        {
            _armourMesh = mesh.Pointer;
            _nextArmourRead = Core.Fast.Unscaled + 0.5f;
            _armour = ReadArmour(mesh);
        }
        var armour = _armour;
        shape = new Shape
        {
            Radius = radius,
            Height = height,
            HoleRadius = hole,
            SideArmour = armour.Side,
            RoofArmour = armour.Roof,
            BottomArmour = armour.Bottom,
        };
        return true;
    }

    private static (int Side, int Roof, int Bottom) ReadArmour(PlateStructureMesh mesh)
    {
        var values = new List<int>();
        foreach (Match match in Regex.Matches(mesh.ToJson(false), "\"t\"\\s*:\\s*\\[\\s*(\\d+)"))
            values.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return values.Count == Sides * 3 ? (values[0], values[Sides], values[Sides * 2]) : (50, 20, 20);
    }
}
