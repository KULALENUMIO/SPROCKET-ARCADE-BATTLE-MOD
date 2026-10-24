using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket.DamageModelling;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.WeaponFramework;
using SprocketMods.UI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.Hits;

/// <summary>被弾方向インジケーターの設定（[DamageIndicator] セクション）。</summary>
public class DamageIndicatorConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<float> Duration;
    public readonly ConfigEntry<float> Radius;
    public readonly ConfigEntry<bool> ShowNonPenetrating;

    public DamageIndicatorConfig(ConfigFile cfg)
    {
        const string s = "DamageIndicator";
        Enabled = cfg.Bind(s, "Enabled", true, "自車が被弾したとき、撃ってきた方向を照準の周りの赤い弧で示す（War Thunder / WoT 風）");
        Duration = cfg.Bind(s, "Duration", 3.5f, new ConfigDescription("表示しておく時間（秒）", new AcceptableValueRange<float>(1f, 10f)));
        Radius = cfg.Bind(s, "Radius", 170f, new ConfigDescription("照準からの距離（1080p 基準の px）", new AcceptableValueRange<float>(80f, 400f)));
        ShowNonPenetrating = cfg.Bind(s, "ShowNonPenetrating", true, "貫通しなかった被弾（非貫通・跳弾）も表示する（色を変える）");
    }
}

/// <summary>
/// War Thunder / World of Tanks 風の被弾方向インジケーター。
///  - 弾: 他の車両の CannonBehaviour.FireInternal が返す弾 ID と、発射した瞬間の砲口の位置を覚える。
///    ProjectileRegister.projectileHistory にその弾の HitDamageModelProjectileEvent が出て、当たったダメージモデルの
///    Rigidbody が自車なら被弾。
///  - 方向: 撃った位置（砲口）。今のカメラの向きに対する方位で、照準の周りの円の上に弧を出す（自分が向きを変えれば弧も回る）。
///  - 色: 貫通 = 赤、非貫通 = 橙、跳弾 = 灰青。出た瞬間は大きく明るく、Duration 秒かけて消える。
///    同じ方向（±12°）からの続けての被弾は 1 つにまとめる（機関砲など）。
/// </summary>
internal sealed class DamageIndicator
{
    public static DamageIndicator Instance { get; private set; }

    private const float ProjectileTimeout = 20f;
    private const float MergeAngle = 12f;
    private const int MaxIndicators = 12;

    private sealed class Shot
    {
        public Vector3 Origin;
        public float Time;
        public IntPtr LastEvent;
    }

    private sealed class Indicator
    {
        public Vector3 Source;
        public HitDamageModelProjectileEvent.HitType Type;
        public float Time;
        public Image Image;
    }

    private static Sprite _arc;

    private readonly Dictionary<int, Shot> _shots = new();
    private readonly List<int> _finished = new();
    private readonly List<Indicator> _indicators = new();
    private readonly Stack<Image> _pool = new();
    private VehicleController _controller;
    private float _nextControllerSearch;
    private GameObject _root;
    private CanvasGroup _group;
    private float _nextErrorLog;

    public DamageIndicator() => Instance = this;

    private static DamageIndicatorConfig Cfg => Plugin.DamageIndicator;

    // ------------------------------------------------------------------ 発射（FireInternal の後）

    public void OnShot(WeaponBehaviour weapon, int projectileId)
    {
        if (!Cfg.Enabled.Value || weapon == null || projectileId <= 0)
            return;
        var transform = weapon.Transform;
        if (transform == null)
            return;
        if (_shots.Count > 2048)
            _shots.Clear();
        _shots[projectileId] = new Shot { Origin = transform.position, Time = Time.time };
    }

    // ------------------------------------------------------------------ 毎フレーム

    public void Update()
    {
        if (!Cfg.Enabled.Value)
        {
            _shots.Clear();
            Clear();
            return;
        }
        try
        {
            var player = GetPlayerBody();
            ProcessShots(player);
            Draw(player);
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Damage indicator failed: {e}");
            }
        }
    }

    private void ProcessShots(Rigidbody player)
    {
        if (_shots.Count == 0)
            return;
        var history = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>()?.projectileHistory;
        if (history == null)
            return;
        var now = Time.time;
        _finished.Clear();
        foreach (var pair in _shots)
        {
            var shot = pair.Value;
            if (now - shot.Time > ProjectileTimeout)
            {
                _finished.Add(pair.Key);
                continue;
            }
            if (!history.TryGetValue(pair.Key, out var latest) || latest == null || latest.Pointer == shot.LastEvent)
                continue;
            shot.LastEvent = latest.Pointer;
            var hit = latest.TryCast<HitDamageModelProjectileEvent>();
            if (hit != null && player != null)
            {
                var body = hit.DamageModel?.RigidBody;
                if (body != null && body.Pointer == player.Pointer)
                    OnHit(shot.Origin, hit.Type);
            }
            if (latest.Terminal)
                _finished.Add(pair.Key);
        }
        foreach (var id in _finished)
            _shots.Remove(id);
    }

    private void OnHit(Vector3 source, HitDamageModelProjectileEvent.HitType type)
    {
        var penetrated = (type & HitDamageModelProjectileEvent.HitType.Penetrated) != 0;
        if (!penetrated && !Cfg.ShowNonPenetrating.Value)
            return;
        var now = Time.unscaledTime;
        var player = GetPlayerBody();
        var origin = player != null ? player.position : source;
        if (Plugin.Cfg.DebugLog.Value)
            Plugin.Logger.LogInfo($"Damage indicator: {type} from bearing {Bearing(origin, source):0}, {(source - origin).magnitude:0} m");

        // 同じ方向からの続けての被弾はまとめる（重い方の色にする）
        foreach (var existing in _indicators)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(Bearing(origin, existing.Source), Bearing(origin, source))) <= MergeAngle)
            {
                existing.Source = source;
                existing.Time = now;
                if (Severity(type) > Severity(existing.Type))
                    existing.Type = type;
                return;
            }
        }
        if (_indicators.Count >= MaxIndicators)
            Remove(0);
        _indicators.Add(new Indicator { Source = source, Type = type, Time = now });
    }

    private static int Severity(HitDamageModelProjectileEvent.HitType type) =>
        (type & HitDamageModelProjectileEvent.HitType.Penetrated) != 0 ? 2
        : (type & HitDamageModelProjectileEvent.HitType.Deflected) != 0 ? 0 : 1;

    /// <summary>北から時計回りの方位（度）。</summary>
    private static float Bearing(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
    }

    // ------------------------------------------------------------------ 描画

    private void Draw(Rigidbody player)
    {
        var now = Time.unscaledTime;
        var duration = Cfg.Duration.Value;
        for (var i = _indicators.Count - 1; i >= 0; i--)
        {
            if (now - _indicators[i].Time > duration)
                Remove(i);
        }
        var camera = HitTracker.Instance?.ActiveCamera;
        var visible = _indicators.Count > 0 && player != null && camera != null && Time.timeScale > 0f && Ensure();
        if (_root != null && !_root.WasCollected && _root.activeSelf != visible)
            _root.SetActive(visible);
        if (!visible)
            return;

        var scale = Screen.height / 1080f;
        var radius = Cfg.Radius.Value * scale;
        var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var forward = camera.transform.forward;
        var cameraBearing = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        var origin = player.position;

        foreach (var indicator in _indicators)
        {
            var image = indicator.Image ??= GetImage();
            var age = now - indicator.Time;
            // 出た瞬間は大きく明るく、最後の 1 秒で消える
            var pop = 1f + 0.18f * Mathf.Clamp01(1f - age / 0.3f);
            var alpha = Mathf.Clamp01((duration - age) / 1f) * (0.75f + 0.25f * Mathf.Clamp01(1f - age / 0.3f));
            var colour = HudStyle.Hex(Severity(indicator.Type) switch
            {
                2 => "FF3B30",
                1 => "FFA040",
                _ => "B8CCE0",
            });
            colour.a = alpha;
            image.color = colour;

            var relative = Mathf.DeltaAngle(cameraBearing, Bearing(origin, indicator.Source));
            var rt = image.rectTransform;
            rt.anchoredPosition = centre;
            var size = radius * 2f / ArcOuter * pop;
            rt.sizeDelta = new Vector2(size, size);
            rt.localEulerAngles = new Vector3(0f, 0f, -relative); // 画像は上（前方）向き。右から来たら右へ回す
        }
    }

    private Image GetImage()
    {
        if (_pool.Count > 0)
        {
            var pooled = _pool.Pop();
            pooled.gameObject.SetActive(true);
            return pooled;
        }
        var go = new GameObject("DamageArc");
        go.transform.SetParent(_root.transform, false);
        var image = go.AddComponent<Image>();
        image.sprite = _arc;
        image.raycastTarget = false;
        var rt = image.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        return image;
    }

    private void Remove(int index)
    {
        var indicator = _indicators[index];
        _indicators.RemoveAt(index);
        if (indicator.Image != null && !indicator.Image.WasCollected)
        {
            indicator.Image.gameObject.SetActive(false);
            _pool.Push(indicator.Image);
        }
    }

    private void Clear()
    {
        for (var i = _indicators.Count - 1; i >= 0; i--)
            Remove(i);
        if (_root != null && !_root.WasCollected && _root.activeSelf)
            _root.SetActive(false);
    }

    private Rigidbody GetPlayerBody()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        return _controller?.ControlledVehicle?.Rigidbody;
    }

    // ------------------------------------------------------------------ 作成

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
        {
            _root = null;
            _pool.Clear();
            foreach (var indicator in _indicators)
                indicator.Image = null;
        }
        if (_root != null)
            return true;
        EnsureSprite();
        _root = HudStyle.CreateCanvas("SprocketDamageIndicator", 30004, out _group);
        return true;
    }

    // 弧の形（画像の半分 = 1 とした半径と、中心から左右への角度）
    private const float ArcInner = 0.77f;
    private const float ArcOuter = 0.91f;
    private const float ArcHalfAngle = 26f;

    /// <summary>上（前方）向きの弧と、外向きの小さな矢じり。白（色は Image.color）で、縁はぼかす。</summary>
    private static unsafe void EnsureSprite()
    {
        if (_arc != null && !_arc.WasCollected)
            return;
        const int size = 256;
        var pixels = new byte[size * size * 4];
        var texel = 2f / size;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = (px + 0.5f) * texel - 1f;
                var y = (py + 0.5f) * texel - 1f;
                var r = Mathf.Sqrt(x * x + y * y);
                var angle = Mathf.Abs(Mathf.Atan2(x, y) * Mathf.Rad2Deg); // 上から左右への角度

                // 弧: 中央が太く濃く、両端に向かって細く薄く
                var t = Mathf.Clamp01(angle / ArcHalfAngle);
                var halfWidth = (ArcOuter - ArcInner) * 0.5f * (1f - 0.6f * t * t);
                var mid = (ArcInner + ArcOuter) * 0.5f;
                const float border = 0.014f; // 明るい背景でも見えるよう黒い縁
                var distance = halfWidth - Mathf.Abs(r - mid);
                var inner = Mathf.Clamp01(distance / texel + 0.5f);
                var outer = Mathf.Clamp01((distance + border) / texel + 0.5f);
                var fade = 1f - Mathf.SmoothStep(0.55f, 1f, t);
                var inArc = angle <= ArcHalfAngle ? fade : 0f;
                var a = outer * inArc;
                var v = inner * inArc;

                // 外向きの矢じり（弧の外側、中央）
                var ty = (y - ArcOuter - 0.005f) / 0.07f; // 0 = 根元, 1 = 先
                if (ty >= -0.2f && ty <= 1.2f)
                {
                    var edge = (Mathf.Clamp01(1f - ty) * 0.06f - Mathf.Abs(x));
                    var inside = ty >= 0f && ty <= 1f ? 1f : 0f;
                    v = Mathf.Max(v, Mathf.Clamp01(edge / texel + 0.5f) * inside);
                    a = Mathf.Max(a, Mathf.Clamp01((edge + border) / texel + 0.5f) * (ty >= -0.15f && ty <= 1.15f ? 1f : 0f));
                }

                var i = (py * size + px) * 4;
                var value = (byte)Mathf.RoundToInt(Mathf.Clamp01(a > 0f ? v / a : 0f) * 255f);
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        _arc = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _arc.hideFlags = HideFlags.HideAndDontSave;
    }
}
