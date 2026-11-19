using BepInEx;
using BepInEx.Unity.IL2CPP.Configuration;
using UnityEngine;

namespace SprocketMods.UI;

/// <summary>
/// キーの判定。BepInEx の KeyboardShortcut.IsDown / IsPressed は「ほかのキーが 1 つも押されていない」ことも条件にするため、
/// WASD で走りながら修理キー・砲弾の切り替えキーを押しても反応しない。ここでは本体のキーと、指定した修飾キーだけを見る。
/// </summary>
internal static class KeyInput
{
    /// <summary>このフレームで押された（修飾キーは押されたまま）。</summary>
    public static bool Down(KeyboardShortcut shortcut) =>
        shortcut.MainKey != KeyCode.None && UnityInput.Current.GetKeyDown(shortcut.MainKey) && Modifiers(shortcut);

    /// <summary>押されている（修飾キーも）。</summary>
    public static bool Held(KeyboardShortcut shortcut) =>
        shortcut.MainKey != KeyCode.None && UnityInput.Current.GetKey(shortcut.MainKey) && Modifiers(shortcut);

    private static bool Modifiers(KeyboardShortcut shortcut)
    {
        foreach (var key in shortcut.Modifiers)
        {
            if (!UnityInput.Current.GetKey(key))
                return false;
        }
        return true;
    }
}
