using System;
using HarmonyLib;
using TMPro;

namespace SprocketMods.Localization;

/// <summary>
/// テキスト表示のフック。ゲーム固有のクラスではなく TextMeshPro / uGUI の API だけに当てるので、ゲームのアップデートに強い。
/// </summary>
internal static class LocalizationHooks
{
    private static bool _failed;

    public static void Apply(Harmony harmony)
    {
        Patch(harmony, AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), nameof(TextSetPrefix));
        Patch(harmony, AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string) }), nameof(SetTextPrefix));
        Patch(harmony, AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string), typeof(bool) }), nameof(SetTextPrefix));
        Patch(harmony, AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"), null, nameof(EnablePostfix));
        Patch(harmony, AccessTools.Method(typeof(TextMeshPro), "OnEnable"), null, nameof(EnablePostfix));
        Patch(harmony, AccessTools.PropertySetter(typeof(UnityEngine.UI.Text), nameof(UnityEngine.UI.Text.text)), nameof(LegacyTextSetPrefix));
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodBase original, string prefix, string postfix = null)
    {
        if (original == null)
        {
            Plugin.Logger.LogWarning($"Localization: hook target not found ({prefix ?? postfix})");
            return;
        }
        try
        {
            harmony.Patch(original,
                prefix: prefix != null ? new HarmonyMethod(typeof(LocalizationHooks), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(LocalizationHooks), postfix) : null);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Localization: failed to hook {original.DeclaringType?.Name}.{original.Name}: {e}");
        }
    }

    private static void TextSetPrefix(TMP_Text __instance, ref string value)
    {
        try
        {
            var manager = LocalizationManager.Instance;
            if (manager != null && manager.Active)
                value = manager.OnTextSet(__instance, value);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private static void SetTextPrefix(TMP_Text __instance, ref string sourceText)
    {
        try
        {
            var manager = LocalizationManager.Instance;
            if (manager != null && manager.Active)
                sourceText = manager.OnTextSet(__instance, sourceText);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private static void EnablePostfix(TMP_Text __instance)
    {
        try
        {
            var manager = LocalizationManager.Instance;
            if (manager != null && manager.Active)
                manager.OnTextEnabled(__instance);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private static void LegacyTextSetPrefix(ref string value)
    {
        try
        {
            var manager = LocalizationManager.Instance;
            if (manager != null && manager.Active)
                value = manager.OnLegacyTextSet(value);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    /// <summary>フック内の例外でゲームのテキスト表示を壊さない（最初の 1 回だけログ）。</summary>
    private static void Report(Exception e)
    {
        if (_failed)
            return;
        _failed = true;
        Plugin.Logger.LogError($"Localization hook error: {e}");
    }
}
