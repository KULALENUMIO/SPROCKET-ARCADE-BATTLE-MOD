using System;
using UnityEngine;

namespace SprocketMods.Core;

/// <summary>
/// ゲーム側にごみを作らない小道具。
///
/// IL2CPP の上では、値（float・bool・Vector3・Color など）を返すゲーム側の呼び出しは、返り値がゲーム側のヒープに箱詰めされる
/// （1 回 32 バイト。Mathf.Sqrt・Vector3.Lerp・Vector3 の演算・transform.position の読み取り・activeSelf・Time.unscaledTime・
/// Unity のオブジェクトの == null まで全部）。毎フレーム数千回になると、ゲーム側の GC（1 回 20〜30 ms 止まる）が頻繁に走る。
/// 毎フレームの処理では、ここの C# だけの計算・1 フレームに 1 回読んだ値・メモリを直接見る生存確認を使う。
/// コンストラクタと値を返さない呼び出し（setter）は箱詰めされない。
/// </summary>
internal static unsafe class Fast
{
    private static int _screenWidth;
    private static int _screenHeight;

    /// <summary>Time.unscaledTime（フレームの初めに 1 回読んだ値）。</summary>
    public static float Unscaled { get; private set; }

    /// <summary>Time.time（フレームの初めに 1 回読んだ値。FixedUpdate の中の Time.time とは違うので、Update の処理だけで使う）。</summary>
    public static float GameTime { get; private set; }

    /// <summary>Time.timeScale（フレームの初めに 1 回読んだ値）。</summary>
    public static float TimeScale { get; private set; } = 1f;

    /// <summary>Screen.width / height（フレームの初めに 1 回読んだ値）。</summary>
    public static int ScreenWidth => _screenWidth > 0 ? _screenWidth : _screenWidth = Screen.width;

    public static int ScreenHeight => _screenHeight > 0 ? _screenHeight : _screenHeight = Screen.height;

    /// <summary>フレームの初め（HudBehaviour.Update）に呼ぶ。</summary>
    public static void Tick()
    {
        Unscaled = Time.unscaledTime;
        GameTime = Time.time;
        TimeScale = Time.timeScale;
        _screenWidth = Screen.width;
        _screenHeight = Screen.height;
    }

    /// <summary>
    /// Unity のオブジェクトが生きているか（== null と同じ意味）。UnityEngine.Object の最初の値 m_CachedPtr（中身のオブジェクト）を
    /// メモリから見る。破棄されると 0 になる。
    /// </summary>
    public static bool Alive(UnityEngine.Object o) =>
        o is not null && !o.WasCollected && *(IntPtr*)(o.Pointer + 0x10) != IntPtr.Zero;

    /// <summary>位置と向き（out で受け取るので箱詰めされない）。</summary>
    public static void Pose(Transform t, out Vector3 position, out Quaternion rotation) =>
        t.GetPositionAndRotation(out position, out rotation);

    /// <summary>位置（out で受け取るので箱詰めされない）。</summary>
    public static Vector3 Position(Transform t)
    {
        t.GetPositionAndRotation(out var position, out _);
        return position;
    }

    /// <summary>回転の前向き（rotation * Vector3.forward）。</summary>
    public static Vector3 Forward(Quaternion q) => new()
    {
        x = 2f * (q.x * q.z + q.w * q.y),
        y = 2f * (q.y * q.z - q.w * q.x),
        z = 1f - 2f * (q.x * q.x + q.y * q.y),
    };

    /// <summary>回転の右向き（rotation * Vector3.right）。</summary>
    public static Vector3 Right(Quaternion q) => new()
    {
        x = 1f - 2f * (q.y * q.y + q.z * q.z),
        y = 2f * (q.x * q.y + q.w * q.z),
        z = 2f * (q.x * q.z - q.w * q.y),
    };

    /// <summary>回転の上向き（rotation * Vector3.up）。</summary>
    public static Vector3 Up(Quaternion q) => new()
    {
        x = 2f * (q.x * q.y - q.w * q.z),
        y = 1f - 2f * (q.x * q.x + q.z * q.z),
        z = 2f * (q.y * q.z + q.w * q.x),
    };

    /// <summary>a × b（外積）。</summary>
    public static Vector3 Cross(Vector3 a, Vector3 b) => new()
    {
        x = a.y * b.z - a.z * b.y,
        y = a.z * b.x - a.x * b.z,
        z = a.x * b.y - a.y * b.x,
    };

    /// <summary>長さ 1 に（短すぎるときは fallback）。</summary>
    public static Vector3 Normalize(Vector3 v, Vector3 fallback)
    {
        var length = Length(v);
        return length > 1e-6f ? Mul(v, 1f / length) : fallback;
    }

    public static Vector2 V2(float x, float y) => new() { x = x, y = y };

    public static Vector3 V3(float x, float y, float z) => new() { x = x, y = y, z = z };

    public static Color Rgba(float r, float g, float b, float a = 1f) => new() { r = r, g = g, b = b, a = a };

    public static Vector3 Add(Vector3 a, Vector3 b) => new() { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };

    public static Vector3 Sub(Vector3 a, Vector3 b) => new() { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };

    public static Vector3 Mul(Vector3 a, float k) => new() { x = a.x * k, y = a.y * k, z = a.z * k };

    /// <summary>Vector3.Lerp と同じ（t は 0..1 に切る）。</summary>
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Vector3 { x = a.x + (b.x - a.x) * t, y = a.y + (b.y - a.y) * t, z = a.z + (b.z - a.z) * t };
    }

    /// <summary>Mathf.Lerp と同じ（t は 0..1 に切る）。</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    public static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    public static float Length(Vector3 v) => MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

    public static float Length(Vector2 v) => MathF.Sqrt(v.x * v.x + v.y * v.y);

    public static float Distance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

    /// <summary>Color.Lerp と同じ（t は 0..1 に切る）。</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color { r = a.r + (b.r - a.r) * t, g = a.g + (b.g - a.g) * t, b = a.b + (b.b - a.b) * t, a = a.a + (b.a - a.a) * t };
    }

    /// <summary>z 軸まわりの回転（Quaternion.Euler(0, 0, degrees) と同じ）。</summary>
    public static Quaternion RotZ(float degrees)
    {
        var half = degrees * (MathF.PI / 360f);
        return new Quaternion { x = 0f, y = 0f, z = MathF.Sin(half), w = MathF.Cos(half) };
    }

    /// <summary>行列で点を移す（Matrix4x4.MultiplyPoint3x4 と同じ）。</summary>
    public static Vector3 Point(in Matrix4x4 m, Vector3 p) => new()
    {
        x = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
        y = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
        z = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23,
    };

    /// <summary>行列の積（a * b）。</summary>
    public static Matrix4x4 Multiply(in Matrix4x4 a, in Matrix4x4 b)
    {
        var r = new Matrix4x4();
        r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
        r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
        r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
        r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
        r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
        r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
        r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
        r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
        r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
        r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
        r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
        r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
        r.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
        r.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
        r.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
        r.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
        return r;
    }

    /// <summary>
    /// カメラの投影（1 フレームに 1 回 Set してから、WorldToViewportPoint と同じ計算を C# で行う）。
    /// </summary>
    public struct Projector
    {
        private Matrix4x4 _viewProjection;

        private float _px, _py, _pw, _ph;

        public void Set(Camera camera)
        {
            _viewProjection = Multiply(camera.projectionMatrix, camera.worldToCameraMatrix);
            var rect = camera.pixelRect;
            _px = rect.x;
            _py = rect.y;
            _pw = rect.width;
            _ph = rect.height;
        }

        /// <summary>画面の座標（WorldToScreenPoint と同じ。x, y はピクセル、z はカメラからの奥行き m）。</summary>
        public Vector3 Screen(Vector3 world)
        {
            var v = Viewport(world);
            return new Vector3 { x = _px + v.x * _pw, y = _py + v.y * _ph, z = v.z };
        }

        /// <summary>ビューポート座標（x, y は 0..1、z はカメラからの奥行き m）。</summary>
        public Vector3 Viewport(Vector3 world)
        {
            ref var m = ref _viewProjection;
            var x = m.m00 * world.x + m.m01 * world.y + m.m02 * world.z + m.m03;
            var y = m.m10 * world.x + m.m11 * world.y + m.m12 * world.z + m.m13;
            var w = m.m30 * world.x + m.m31 * world.y + m.m32 * world.z + m.m33;
            if (MathF.Abs(w) < 1e-6f)
                w = 1e-6f;
            return new Vector3 { x = (x / w + 1f) * 0.5f, y = (y / w + 1f) * 0.5f, z = w };
        }
    }
}
