using System;
using SprocketMods.UI;
using Sprocket.WeaponFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMods.Ammo;

/// <summary>
/// 砲弾の種類の表示（画面の下の中央、War Thunder 風）。
///  - 3 つの枠（キーの数字・略号・名前）。選んだ枠は種類の色の縁取りで、選んだ瞬間に少し大きく弾む
///  - 装填中の種類の枠の後ろで扇形が時計回りに広がる（装填の進み具合）。装填済みの種類の枠には丸い印
///  - 枠の上に、選んだ種類の名前と「装填中 2.4 秒」「装填済み」
/// </summary>
internal sealed class AmmoView
{
    private static readonly string[] Colours = { HudStyle.Gold, HudStyle.Orange, HudStyle.Red };
    private const float PopTime = 0.22f;

    private sealed class Slot
    {
        public Image[] Border; // 上・下・左・右の線（選んだ枠は種類の色、ほかは薄い白）
        public Image Panel;
        public Image Pie;
        public TextMeshProUGUI Key;
        public TextMeshProUGUI Name;
        public TextMeshProUGUI Sub;
        public TextMeshProUGUI Mark;
    }

    private static Sprite _disc;
    private GameObject _root;
    private CanvasGroup _group;
    private readonly Slot[] _slots = new Slot[3];
    private TextMeshProUGUI _status;
    private string _lastStatus;
    private float _retract; // 自車が撃破されたら下へ引っ込める（0 = 出ている、1 = 引っ込んだ）

    public void LateUpdate()
    {
        var system = AmmoSystem.Instance;
        var cfg = Plugin.Ammo;
        _retract = Mathf.MoveTowards(_retract, system?.PlayerDead == true ? 1f : 0f, Time.unscaledDeltaTime / 0.35f);
        if (system == null || !cfg.Enabled.Value || !cfg.ShowHud.Value || !system.HasGun || _retract >= 1f || !Ensure())
        {
            SetActive(false);
            return;
        }
        SetActive(true);
        var hide = _retract * _retract;
        _group.alpha = 1f - hide;

        var s = HudStyle.Scale;
        var slotW = 92f * s;
        var slotH = 58f * s;
        var gap = 10f * s;
        var total = slotW * 3f + gap * 2f;
        var x0 = Screen.width * 0.5f - total * 0.5f;
        var y0 = 22f * s - (slotH + 70f * s) * hide;
        var keys = new[] { KeyLabel(cfg.KeyAP.Value), KeyLabel(cfg.KeyAPHE.Value), KeyLabel(cfg.KeyHE.Value) };
        var pop = 1f - Mathf.Clamp01((Core.Fast.Unscaled - system.SelectedAt) / PopTime);

        for (var i = 0; i < 3; i++)
        {
            var type = (AmmoType)i;
            var slot = _slots[i];
            var selected = system.Selected == type;
            var colour = HudStyle.Hex(Colours[i]);
            var grow = selected ? 1f + 0.12f * pop * pop : 1f;
            var w = slotW * grow;
            var h = slotH * grow;
            var cx = x0 + slotW * 0.5f + i * (slotW + gap);
            var cy = y0 + slotH * 0.5f;
            var x = cx - w * 0.5f;
            var y = cy - h * 0.5f;

            HudStyle.StylePanel(slot.Panel, s);
            var panel = HudStyle.PanelColor;
            panel.a = Mathf.Max(0.62f, panel.a);
            slot.Panel.color = panel;
            HudStyle.Place(slot.Panel, x, y, w, h);
            // 縁: 選んだ枠は種類の色で太く、ほかは薄い白で細く
            var line = selected ? 2.5f * s : Mathf.Max(1f, 1f * s);
            var border = selected ? colour : new Color(1f, 1f, 1f, 0.22f);
            foreach (var bar in slot.Border)
                bar.color = border;
            HudStyle.Place(slot.Border[0], x, y + h - line, w, line);
            HudStyle.Place(slot.Border[1], x, y, w, line);
            HudStyle.Place(slot.Border[2], x, y, line, h);
            HudStyle.Place(slot.Border[3], x + w - line, y, line, h);

            // 扇形: 装填中の種類はその進み具合、装填済みの種類は薄く一杯
            var loading = system.State == LoadState.Loading && system.LoadingType == type;
            var ready = system.State == LoadState.Loaded && system.Loaded == type;
            HudStyle.SetActive(slot.Pie, loading || ready);
            if (loading || ready)
            {
                var d = h * 0.9f;
                var pie = colour;
                pie.a = loading ? 0.5f : 0.16f;
                slot.Pie.color = pie;
                slot.Pie.fillAmount = loading ? system.LoadFraction : 1f;
                HudStyle.Place(slot.Pie, cx - d * 0.5f, cy - d * 0.5f, d, d);
            }

            HudStyle.SetText(slot.Key, keys[i]);
            HudStyle.SetFontSize(slot.Key, 12f * s);
            slot.Key.color = selected ? Color.white : HudStyle.Hex(HudStyle.Grey);
            HudStyle.Place(slot.Key, x + 6f * s, y + h - 18f * s, 30f * s, 16f * s);

            HudStyle.SetActive(slot.Mark, ready);
            slot.Mark.color = colour;
            HudStyle.SetFontSize(slot.Mark, 12f * s);
            HudStyle.Place(slot.Mark, x + w - 36f * s, y + h - 18f * s, 30f * s, 16f * s);

            HudStyle.SetText(slot.Name, "<b>" + AmmoSystem.ShortName(type) + "</b>");
            HudStyle.SetFontSize(slot.Name, (selected ? 22f : 19f) * s);
            slot.Name.color = selected ? colour : HudStyle.Hex(HudStyle.White);
            HudStyle.Place(slot.Name, x, y + h * 0.36f, w, h * 0.42f);

            HudStyle.SetText(slot.Sub, T(AmmoSystem.LongName(type)));
            HudStyle.SetFontSize(slot.Sub, 11f * s);
            slot.Sub.color = selected ? Color.white : HudStyle.Hex(HudStyle.Grey);
            HudStyle.Place(slot.Sub, x, y + 5f * s, w, h * 0.3f);
        }

        // 選んだ種類と装填の状態
        var chosen = system.Selected;
        string state;
        if (system.State == LoadState.Loading)
            state = "<color=#" + HudStyle.Orange + ">" + T("Loading") + " " + system.RemainingTime.ToString("0.0") + " s</color>";
        else if (system.State == LoadState.Loaded)
            state = system.Loaded == chosen
                ? "<color=#" + HudStyle.Green + ">" + T("Loaded") + "</color>"
                : "<color=#" + HudStyle.Yellow + ">" + T("Loaded") + ": " + AmmoSystem.ShortName(system.Loaded) + "</color>";
        else
            state = "<color=#" + HudStyle.Red + ">" + T("Not loaded") + "</color>";
        var text = "<b><color=#" + Colours[(int)chosen] + ">" + T(AmmoSystem.LongName(chosen)) + "</color></b>   " + state;
        if (text != _lastStatus)
        {
            HudStyle.SetText(_status, text);
            _lastStatus = text;
        }
        HudStyle.SetFontSize(_status, 15f * s);
        HudStyle.Place(_status, x0 - 100f * s, y0 + slotH + 8f * s, total + 200f * s, 22f * s);
    }

    private static string T(string english) => Plugin.Localization?.Translate(english) ?? english;

    /// <summary>キーの表示（Alpha1 → 1、Keypad1 → Num 1）。</summary>
    private static string KeyLabel(BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut shortcut)
    {
        var key = shortcut.MainKey;
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            return ((int)(key - KeyCode.Alpha0)).ToString();
        if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            return "Num " + (int)(key - KeyCode.Keypad0);
        return key.ToString();
    }

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
        EnsureSprite();
        _root = HudStyle.CreateCanvas("SprocketAmmo", 30004, out _group);
        var t = _root.transform;
        for (var i = 0; i < 3; i++)
        {
            var slot = new Slot
            {
                Panel = HudStyle.CreateImage(t, "Panel", true),
                Pie = HudStyle.CreateImage(t, "Pie", false),
            };
            slot.Border = new Image[4];
            for (var b = 0; b < 4; b++)
                slot.Border[b] = HudStyle.CreateImage(t, "Border", false);
            slot.Pie.sprite = _disc;
            slot.Pie.type = Image.Type.Filled;
            slot.Pie.fillMethod = Image.FillMethod.Radial360;
            slot.Pie.fillOrigin = (int)Image.Origin360.Top;
            slot.Pie.fillClockwise = true;
            slot.Key = HudStyle.CreateText(t, "Key", TextAlignmentOptions.Left);
            slot.Mark = HudStyle.CreateText(t, "Mark", TextAlignmentOptions.Right);
            HudStyle.SetText(slot.Mark, "●");
            slot.Name = HudStyle.CreateText(t, "Name", TextAlignmentOptions.Center);
            slot.Sub = HudStyle.CreateText(t, "Sub", TextAlignmentOptions.Center);
            _slots[i] = slot;
        }
        _status = HudStyle.CreateText(t, "Status", TextAlignmentOptions.Center);
        _lastStatus = null;
        return true;
    }

    /// <summary>白い円（縁を少しぼかす）。扇形の塗りに使う。</summary>
    private static unsafe void EnsureSprite()
    {
        if (_disc != null && !_disc.WasCollected)
            return;
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var texel = 2f / size;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = (px + 0.5f) * texel - 1f;
                var y = (py + 0.5f) * texel - 1f;
                var r = Mathf.Sqrt(x * x + y * y);
                var a = Mathf.Clamp01((0.96f - r) / texel + 0.5f);
                var i = (py * size + px) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Mathf.RoundToInt(a * 255f);
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
        _disc = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _disc.hideFlags = HideFlags.HideAndDontSave;
    }
}
