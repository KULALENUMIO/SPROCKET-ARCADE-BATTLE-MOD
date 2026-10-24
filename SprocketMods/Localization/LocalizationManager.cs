using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Localization;

/// <summary>
/// ゲーム全体（MOD を含む）の表示テキストの翻訳。
///
/// ゲーム固有のクラスには依存せず、TextMeshPro / uGUI の Text に表示される瞬間の文字列を英語原文をキーにした辞書で置き換える。
/// そのためゲームのアップデートで画面や文言が増えても MOD の再ビルドは不要で、新しい文言は _untranslated.txt に
/// 自動で集まるので、訳を書き足すだけで対応できる。言語は Languages/&lt;言語コード&gt;/ フォルダを足すだけで増やせる。
/// </summary>
internal sealed class LocalizationManager
{
    public const string UntranslatedFile = "_untranslated.txt";
    public const string FontListFile = "font.txt";
    public const string Off = "off";

    public static LocalizationManager Instance { get; private set; }

    public readonly ConfigEntry<string> Language;
    public readonly ConfigEntry<bool> CollectUntranslated;
    public readonly ConfigEntry<KeyboardShortcut> ReloadKey;

    private readonly FontManager _fonts = new();
    private readonly Dictionary<IntPtr, string> _originals = new();
    private readonly Dictionary<IntPtr, bool> _excluded = new();
    private Translator _translator;
    private float _nextFlush;

    public string RootDirectory { get; }
    public string ActiveLanguage { get; private set; } = Off;
    public bool Active => _translator != null;

    public LocalizationManager(ConfigFile cfg)
    {
        Instance = this;
        RootDirectory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "Languages");

        const string s = "Localization";
        Language = cfg.Bind(s, "Language", "auto",
            "表示言語。Languages フォルダ内のフォルダ名（ja など）。auto は Windows の表示言語、off で翻訳しない");
        CollectUntranslated = cfg.Bind(s, "CollectUntranslated", true,
            "翻訳が見つからなかったテキストを Languages/<言語>/_untranslated.txt に集める（アップデートで増えた文言の翻訳用）");
        ReloadKey = cfg.Bind(s, "ReloadKey", new KeyboardShortcut(KeyCode.F11), "翻訳ファイルを読み直して画面のテキストを訳し直すキー");
        Language.SettingChanged += (_, _) => Reload();
    }

    /// <summary>Languages フォルダにある言語の一覧。</summary>
    public string[] AvailableLanguages()
    {
        if (!Directory.Exists(RootDirectory))
            return Array.Empty<string>();
        return Directory.GetDirectories(RootDirectory).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    }

    public void Reload()
    {
        _translator?.FlushUntranslated();
        var language = ResolveLanguage(Language.Value);
        ActiveLanguage = language ?? Off;
        if (language == null)
        {
            _translator = null;
            _fonts.Clear();
            Plugin.Logger.LogInfo("Localization: off");
        }
        else
        {
            var directory = Path.Combine(RootDirectory, language);
            var table = TranslationTable.Load(language, directory);
            _translator = new Translator(table, CollectUntranslated.Value);
            _fonts.Prepare(directory);
            Plugin.Logger.LogInfo($"Localization: {language} ({table.Count} entries)");
        }
        RetranslateScene();
    }

    private string ResolveLanguage(string setting)
    {
        var value = (setting ?? string.Empty).Trim();
        if (value.Length == 0 || value.Equals(Off, StringComparison.OrdinalIgnoreCase))
            return null;
        var available = AvailableLanguages();
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var culture = CultureInfo.CurrentUICulture;
            return available.FirstOrDefault(l => l.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
                   ?? available.FirstOrDefault(l => l.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
        }
        var match = available.FirstOrDefault(l => l.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            Plugin.Logger.LogWarning($"Localization: language folder '{value}' not found in {RootDirectory}");
        return match;
    }

    public void Update()
    {
        if (ReloadKey.Value.IsDown())
            Reload();
        if (_translator != null && Time.unscaledTime >= _nextFlush)
        {
            _nextFlush = Time.unscaledTime + 5f;
            _translator.FlushUntranslated();
        }
    }

    public void Shutdown() => _translator?.FlushUntranslated();

    // ---------------------------------------------------------------- hooks から呼ばれる

    /// <summary>TMP のテキストが設定される直前。翻訳した文字列を返す。</summary>
    public string OnTextSet(TMP_Text text, string value)
    {
        var translator = _translator;
        if (translator == null || string.IsNullOrEmpty(value) || text == null || IsExcluded(text))
            return value;
        _fonts.Attach(text);
        var translated = translator.Translate(value);
        if (translated != value)
            RememberOriginal(text.Pointer, value);
        return translated;
    }

    /// <summary>シーンやプレハブに保存されているテキストは set_text を通らないので、表示開始時に訳す。</summary>
    public void OnTextEnabled(TMP_Text text)
    {
        if (_translator == null || text == null || IsExcluded(text))
            return;
        _fonts.Attach(text);
        var current = text.text;
        var translated = _translator.Translate(current);
        if (translated != current)
        {
            RememberOriginal(text.Pointer, current);
            text.text = translated;
        }
    }

    public string OnLegacyTextSet(string value) => _translator == null ? value : _translator.Translate(value);

    /// <summary>MOD のコードから文言を訳す（言語がオフなら原文のまま）。</summary>
    public string Translate(string value) => _translator == null ? value : _translator.Translate(value);

    /// <summary>
    /// このテキストを自動翻訳の対象から外す。車両名などを含む文を MOD 側で組み立てて表示するときに使う
    /// （フォントのフォールバックは付ける）。
    /// </summary>
    public void Exclude(TMP_Text text)
    {
        if (text == null)
            return;
        _excluded[text.Pointer] = true;
        _fonts.Attach(text);
    }

    private void RememberOriginal(IntPtr instance, string original)
    {
        if (_originals.Count > 50000)
            _originals.Clear();
        _originals[instance] = original;
    }

    /// <summary>入力欄の文字（プレイヤーが打った車両名など）は訳さない。</summary>
    private bool IsExcluded(TMP_Text text)
    {
        var ptr = text.Pointer;
        if (_excluded.TryGetValue(ptr, out var excluded))
            return excluded;
        try
        {
            excluded = text.GetComponentInParent<TMP_InputField>() != null;
        }
        catch (Exception)
        {
            excluded = false;
        }
        if (_excluded.Count > 50000)
            _excluded.Clear();
        _excluded[ptr] = excluded;
        return excluded;
    }

    /// <summary>言語の切り替え・再読み込みのときに、表示中のテキストを元の英語から訳し直す。</summary>
    private void RetranslateScene()
    {
        try
        {
            var objects = Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (objects == null)
                return;
            foreach (var obj in objects)
            {
                var text = obj?.TryCast<TMP_Text>();
                if (text == null)
                    continue;
                if (_originals.TryGetValue(text.Pointer, out var original))
                    text.text = original; // set_text のフックで新しい言語に訳される（off なら原文に戻る）
                else if (_translator != null)
                    OnTextEnabled(text);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Localization: could not refresh texts: {e.Message}");
        }
    }
}
