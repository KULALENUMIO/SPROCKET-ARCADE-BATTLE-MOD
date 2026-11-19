using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.UI;

/// <summary>
/// 自車が撃破されたときの画面（War Thunder 風）。0.8 秒かけて入り、自車が戻れば（次の試合など）0.4 秒で消える。
///  - 画面の周りを暗い赤で落とす（周辺減光）＋全体を少し赤く（UI の画像なので必ず見える）
///  - HDRP の Volume（既定のレイヤー、優先度を高く）: 彩度を落として暗くし、色収差で画面の周りをにじませる（周辺のぼけ）、赤い周辺減光
/// 自車が撃破されたかは KillTracker（乗員の全滅・誘爆）で決める。
/// </summary>
internal sealed class DeathScreen
{
    private static Sprite _vignetteSprite;
    private GameObject _root;
    private CanvasGroup _group;
    private Image _tint;
    private Image _vignette;
    private GameObject _volumeObject;
    private Volume _volume;
    private float _weight;

    public void LateUpdate()
    {
        var dead = Plugin.Style.DeathEffect.Value && Ammo.AmmoSystem.Instance?.PlayerDead == true;
        var target = dead ? 1f : 0f;
        _weight = Mathf.MoveTowards(_weight, target, Time.unscaledDeltaTime / (dead ? 0.8f : 0.4f));
        if (_weight <= 0f)
        {
            SetActive(false);
            return;
        }
        if (!Ensure())
            return;
        SetActive(true);
        var k = _weight * _weight * (3f - 2f * _weight);
        _group.alpha = k;
        HudStyle.Place(_tint, 0f, 0f, Screen.width, Screen.height);
        HudStyle.Place(_vignette, 0f, 0f, Screen.width, Screen.height);
        if (_volume != null && !_volume.WasCollected)
            _volume.weight = k;
    }

    private void SetActive(bool active)
    {
        if (_root != null && !_root.WasCollected && _root.activeSelf != active)
            _root.SetActive(active);
        if (_volumeObject != null && !_volumeObject.WasCollected && _volumeObject.activeSelf != active)
            _volumeObject.SetActive(active);
    }

    private bool Ensure()
    {
        if (_root != null && _root.WasCollected)
            _root = null;
        if (_root == null)
        {
            EnsureSprite();
            // ほかの HUD より下（キルログ・ヒットカメラなどは上に見える）
            _root = HudStyle.CreateCanvas("SprocketDeathScreen", 29000, out _group);
            _tint = HudStyle.CreateImage(_root.transform, "Tint", false);
            _tint.color = new Color(0.45f, 0.02f, 0.02f, 0.14f);
            _vignette = HudStyle.CreateImage(_root.transform, "Vignette", false);
            _vignette.sprite = _vignetteSprite;
            _vignette.color = Color.white;
        }
        if (_volumeObject != null && _volumeObject.WasCollected)
            _volumeObject = null;
        if (_volumeObject == null)
            CreateVolume();
        return true;
    }

    private void CreateVolume()
    {
        try
        {
            _volumeObject = new GameObject("SprocketDeathScreenVolume");
            Object.DontDestroyOnLoad(_volumeObject);
            _volumeObject.hideFlags = HideFlags.HideAndDontSave;
            _volumeObject.layer = 0;
            _volume = _volumeObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 900f;
            _volume.weight = 0f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            var colour = ScriptableObject.CreateInstance<ColorAdjustments>();
            colour.active = true;
            colour.saturation.overrideState = true;
            colour.saturation.value = -65f;
            colour.postExposure.overrideState = true;
            colour.postExposure.value = -0.4f;
            colour.colorFilter.overrideState = true;
            colour.colorFilter.value = new Color(1f, 0.8f, 0.78f, 1f);
            profile.components.Add(colour);

            var aberration = ScriptableObject.CreateInstance<ChromaticAberration>();
            aberration.active = true;
            aberration.intensity.overrideState = true;
            aberration.intensity.value = 1f;
            profile.components.Add(aberration);

            var vignette = ScriptableObject.CreateInstance<Vignette>();
            vignette.active = true;
            vignette.color.overrideState = true;
            vignette.color.value = new Color(0.45f, 0f, 0f, 1f);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.5f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.9f;
            profile.components.Add(vignette);

            _volume.sharedProfile = profile;
            _volumeObject.SetActive(false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Death screen: volume failed (the overlay still works): {e.Message}");
            _volume = null;
        }
    }

    /// <summary>真ん中が透明で、周りへ行くほど暗い赤になる画像（画面に引き伸ばす）。</summary>
    private static unsafe void EnsureSprite()
    {
        if (_vignetteSprite != null && !_vignetteSprite.WasCollected)
            return;
        const int size = 256;
        var pixels = new byte[size * size * 4];
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = (px + 0.5f) / size * 2f - 1f;
                var y = (py + 0.5f) / size * 2f - 1f;
                var r = Mathf.Sqrt(x * x + y * y) / 1.41421f; // 角で 1
                var t = Mathf.Clamp01((r - 0.38f) / 0.62f);
                var a = t * t * (3f - 2f * t);
                var i = (py * size + px) * 4;
                pixels[i] = (byte)Mathf.RoundToInt(Mathf.Lerp(120f, 40f, a));
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = (byte)Mathf.RoundToInt(a * 235f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        _vignetteSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
    }
}
