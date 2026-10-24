using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.WeaponFramework;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.Weapons;

/// <summary>
/// 自車の選択中の砲が機関砲のとき、照準の右下に半透明の黒いパネルで残弾を出す。
///   ┃ 弾倉            7 / 10
///   ┃ ▮▮▮▮▮▮▮▯▯▯
/// 弾倉が小さいとき（MaxPips 発以下）は 1 発ずつの目盛り、大きいときは 1 本のバー。
/// 弾倉交換中は「弾倉交換 3.2 秒」と交換の進み具合、弾薬庫が空なら「弾切れ」。
/// </summary>
internal sealed class AutocannonHud
{
    private const int MaxPips = 30;

    private VehicleController _controller;
    private float _nextControllerSearch;

    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private Image _accent;
    private TextMeshProUGUI _caption;
    private TextMeshProUGUI _value;
    private Image _track;
    private Image _bar;
    private readonly List<Image> _pips = new();
    private string _lastValue;

    public void LateUpdate()
    {
        var cfg = Plugin.Autocannon;
        AutocannonState state = null;
        WeaponBehaviour weapon = null;
        if (cfg.Enabled.Value && cfg.ShowHud.Value && Time.timeScale > 0f)
        {
            weapon = GetPlayerWeapon();
            state = Autocannons.Get(weapon);
        }
        if (state == null || !Ensure())
        {
            SetActive(false);
            return;
        }
        SetActive(true);
        Draw(weapon, state);
    }

    private WeaponBehaviour GetPlayerWeapon()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        if (_controller == null && Time.unscaledTime >= _nextControllerSearch)
        {
            _nextControllerSearch = Time.unscaledTime + 1f;
            _controller = Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        }
        if (_controller == null || _controller.ControlledVehicle == null)
            return null;
        var gunner = _controller.activeGunner?.TryCast<Gunner>();
        return gunner?.weapon?.TryCast<WeaponBehaviour>();
    }

    private void Draw(WeaponBehaviour weapon, AutocannonState state)
    {
        var loadState = weapon.LoadState;
        var reloadTime = Plugin.Autocannon.MagazineReloadTime.Value;
        string value;
        string hex;
        float fill;
        var reloading = false;
        if (loadState == LoadState.Loading)
        {
            var remaining = Mathf.Max(0f, reloadTime - state.ReloadElapsed);
            value = T("RELOADING " + remaining.ToString("0.0") + " s");
            hex = HudStyle.Orange;
            fill = Mathf.Clamp01(state.ReloadElapsed / reloadTime);
            reloading = true;
        }
        else if (loadState != LoadState.Loaded)
        {
            value = T("NO AMMO");
            hex = HudStyle.Red;
            fill = 0f;
        }
        else
        {
            value = "<b>" + state.Rounds + "</b><size=75%><color=#" + HudStyle.Grey + "> / " + state.MagazineSize + "</color></size>";
            hex = state.Rounds * 4 <= state.MagazineSize ? HudStyle.Yellow : HudStyle.White;
            fill = state.Rounds / (float)state.MagazineSize;
        }
        value = "<color=#" + hex + ">" + value + "</color>";
        if (value != _lastValue)
        {
            _value.text = value;
            _lastValue = value;
        }
        var colour = HudStyle.Hex(hex);

        var scale = HudStyle.Scale;
        var font = 20f * scale;
        var pad = font * 0.6f;
        var accentWidth = Mathf.Max(2f, 4f * scale);
        var width = 190f * scale;
        var headerHeight = font * 1.4f;
        var barHeight = Mathf.Max(4f, 7f * scale);
        var height = pad + headerHeight + font * 0.25f + barHeight + pad;
        var left = Screen.width * 0.5f + 180f * scale;
        var top = Screen.height * 0.5f - 40f * scale;
        var bottom = top - height;

        HudStyle.StylePanel(_panel, scale);
        HudStyle.Place(_panel, left, bottom, width, height);
        _accent.color = colour;
        _accent.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
        HudStyle.Place(_accent, left + pad * 0.45f, bottom + pad * 0.6f, accentWidth, height - pad * 1.2f);

        var x = left + pad + accentWidth;
        var inner = width - pad * 2f - accentWidth;
        var y = top - pad - headerHeight;
        _caption.fontSize = font * 0.7f;
        HudStyle.Place(_caption, x, y, inner * 0.4f, headerHeight);
        _value.fontSize = reloading || loadState != LoadState.Loaded ? font * 0.85f : font * 1.15f;
        HudStyle.Place(_value, x + inner * 0.25f, y, inner * 0.75f, headerHeight);

        var barY = bottom + pad;
        var pips = !reloading && loadState == LoadState.Loaded && state.MagazineSize <= MaxPips ? state.MagazineSize : 0;
        HudStyle.SetActive(_track, pips == 0);
        HudStyle.SetActive(_bar, pips == 0 && fill > 0.001f);
        if (pips == 0)
        {
            _track.color = HudStyle.TrackColor;
            _track.pixelsPerUnitMultiplier = _bar.pixelsPerUnitMultiplier = 4f / Mathf.Max(0.1f, scale);
            _bar.color = colour;
            HudStyle.Place(_track, x, barY, inner, barHeight);
            HudStyle.Place(_bar, x, barY, inner * fill, barHeight);
        }
        DrawPips(pips, state.Rounds, x, barY, inner, barHeight, colour, scale);
    }

    /// <summary>1 発 = 1 目盛り（残っている弾は色付き、撃った弾は暗く）。</summary>
    private void DrawPips(int count, int rounds, float x, float y, float width, float height, Color colour, float scale)
    {
        if (count > 0)
        {
            var gap = Mathf.Max(1f, (count > 15 ? 2f : 3f) * scale);
            var pipWidth = (width - gap * (count - 1)) / count;
            for (var i = 0; i < count; i++)
            {
                var pip = GetPip(i);
                pip.color = i < rounds ? colour : HudStyle.TrackColor;
                HudStyle.Place(pip, x + i * (pipWidth + gap), y, pipWidth, height);
            }
        }
        for (var i = count; i < _pips.Count; i++)
            HudStyle.SetActive(_pips[i], false);
    }

    private Image GetPip(int index)
    {
        while (_pips.Count <= index)
            _pips.Add(HudStyle.CreateImage(_root.transform, "Pip", false));
        var pip = _pips[index];
        HudStyle.SetActive(pip, true);
        return pip;
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
        _root = HudStyle.CreateCanvas("SprocketAutocannonHUD", 30001, out _group);
        var t = _root.transform;
        _panel = HudStyle.CreateImage(t, "Panel", true);
        _accent = HudStyle.CreateImage(t, "Accent", true);
        _track = HudStyle.CreateImage(t, "MagazineTrack", true);
        _bar = HudStyle.CreateImage(t, "Magazine", true);
        _caption = HudStyle.CreateText(t, "Caption", TextAlignmentOptions.MidlineLeft);
        _caption.text = "<color=#" + HudStyle.Grey + ">" + T("Magazine") + "</color>";
        _value = HudStyle.CreateText(t, "Rounds", TextAlignmentOptions.MidlineRight);
        _pips.Clear();
        _lastValue = null;
        return true;
    }
}
