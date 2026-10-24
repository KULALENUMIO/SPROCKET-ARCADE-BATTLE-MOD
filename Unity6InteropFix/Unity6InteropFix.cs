using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Preloader.Core.Patching;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;

namespace SprocketMods.Unity6InteropFix;

/// <summary>
/// Sprocket (Unity 6000.3, IL2CPP metadata v39) で Il2CppInterop 1.5.3 が起動直後にクラッシュする問題の回避パッチャー。
///
/// Il2CppInterop は InjectorHelpers.Setup() で Class::GetDefaultFieldValue をネイティブフックするが、
/// Unity 6000.3 では関数の探索（シグネチャ/xref）が別の関数を掴んでしまい、
/// ゲームがそこを呼んだ瞬間に AccessViolationException で落ちる。
/// このフックは EnumInjector（独自 enum の注入）専用なので、スキップしても通常の MOD には影響しない。
/// </summary>
[PatcherPluginInfo(Guid, "Sprocket Unity6 Il2CppInterop Fix", "1.1.0")]
public class Unity6InteropFixPatcher : BasePatcher
{
    public const string Guid = "sprocketmods.unity6interopfix";

    private static Unity6InteropFixPatcher _instance;
    private ConfigEntry<bool> _skipFieldDefaultValueHook;
    private ConfigEntry<bool> _safeExceptionMessages;

    public override void Initialize()
    {
        _instance = this;
        _skipFieldDefaultValueHook = Config.Bind("General", "SkipGetFieldDefaultValueHook", true,
            "Il2CppInterop の Class::GetDefaultFieldValue フックをスキップする（Unity 6000.3 でのクラッシュ回避）");
        _safeExceptionMessages = Config.Bind("General", "SafeExceptionMessages", true,
            "IL2CPP 例外のメッセージを il2cpp_format_exception を使わずに作る。" +
            "Unity 6000.3 では il2cpp_format_exception が AccessViolation を起こし、MOD 内で捕捉できるはずの例外でゲームごと落ちるため");

        if (_safeExceptionMessages.Value)
            Il2CppException.ParseMessageHook = SafeExceptionMessage;

        if (!_skipFieldDefaultValueHook.Value)
        {
            Log.LogInfo("Disabled by config");
            return;
        }

        var setup = AccessTools.Method("Il2CppInterop.Runtime.Injection.InjectorHelpers:Setup");
        if (setup == null)
        {
            Log.LogError("InjectorHelpers.Setup not found; Il2CppInterop version may have changed. Fix not applied.");
            return;
        }

        new Harmony(Guid).Patch(setup, prefix: new HarmonyMethod(typeof(Unity6InteropFixPatcher), nameof(SetupPrefix)));
        Log.LogInfo("Patched InjectorHelpers.Setup (Class::GetDefaultFieldValue hook will be skipped)");
    }

    /// <summary>
    /// 例外オブジェクトのクラス名と Exception._message だけを IL2CPP のエクスポート関数で読む。
    /// </summary>
    private static unsafe string SafeExceptionMessage(IntPtr exception)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(exception);
            var name = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass));
            var ns = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_namespace(klass));
            var message = string.Empty;
            for (var k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                var field = IL2CPP.il2cpp_class_get_field_from_name(k, "_message");
                if (field == IntPtr.Zero)
                    continue;
                var str = *(IntPtr*)(exception + (int)IL2CPP.il2cpp_field_get_offset(field));
                if (str != IntPtr.Zero)
                    message = IL2CPP.Il2CppStringToManaged(str);
                break;
            }
            return string.IsNullOrEmpty(ns) ? $"{name}: {message}" : $"{ns}.{name}: {message}";
        }
        catch (Exception)
        {
            return "IL2CPP exception (message unavailable)";
        }
    }

    /// <summary>
    /// InjectorHelpers.Setup() を、GetFieldDefaultValueHook を除いて再実装する。
    /// 反射に失敗した場合はオリジナルを実行する。
    /// </summary>
    private static bool SetupPrefix(MethodBase __originalMethod)
    {
        var t = __originalMethod.DeclaringType;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        try
        {
            object Field(string name) =>
                (t.GetField(name, flags) ?? throw new MissingFieldException(t.FullName, name)).GetValue(null);

            void Apply(string hookField)
            {
                var hook = Field(hookField);
                hook.GetType().GetMethod("ApplyHook", BindingFlags.Instance | BindingFlags.Public)!.Invoke(hook, null);
            }

            if (Field("InjectedAssembly") == null)
                t.GetMethod("CreateInjectedAssembly", flags)!.Invoke(null, null);

            if (Il2CppInteropRuntime.Instance.UnityVersion.Major >= 6000)
                Apply("GenericMethodGetMethodHook_Unity6");
            else
                Apply("GenericMethodGetMethodHook");
            Apply("GetTypeInfoFromTypeDefinitionIndexHook");
            // Apply("GetFieldDefaultValueHook");  <- Unity 6000.3 でクラッシュするためスキップ

            var classInitField = t.GetField("ClassInit", flags)!;
            if (classInitField.GetValue(null) == null)
                classInitField.SetValue(null, t.GetMethod("FindClassInit", flags)!.Invoke(null, null));

            Apply("FromIl2CppTypeHook");
            Apply("FromNameHook");

            _instance?.Log.LogInfo("Il2CppInterop injector set up without Class::GetDefaultFieldValue hook");
            return false;
        }
        catch (Exception e)
        {
            _instance?.Log.LogError($"Replacement Setup failed, falling back to original: {e}");
            return true;
        }
    }
}
