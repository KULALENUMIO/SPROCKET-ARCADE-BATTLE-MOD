using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace SprocketMods.Localization;

/// <summary>
/// 翻訳先の言語の文字を表示するためのフォント。ゲームのフォント（LiberationSans）は欧文しか持たないので、
/// Languages/&lt;lang&gt;/font.txt に書かれたフォントファイル（OS のフォントフォルダか絶対パス）から
/// TMP のダイナミックフォントアセットを作り、ゲーム中のすべての TMP フォントのフォールバックに加える。
/// </summary>
internal sealed class FontManager
{
    private readonly HashSet<IntPtr> _patchedFonts = new();
    private TMP_FontAsset _fallback;
    private string _fontPath;

    public TMP_FontAsset Fallback => _fallback != null && !_fallback.WasCollected ? _fallback : null;

    /// <summary>言語に合ったフォントを用意する。font.txt が無い・見つからない場合はフォールバック無し。</summary>
    public void Prepare(string languageDirectory)
    {
        var path = FindFont(languageDirectory);
        if (path == _fontPath && Fallback != null)
            return;
        _fontPath = path;
        _fallback = null;
        _patchedFonts.Clear();
        if (path == null)
            return;

        try
        {
            var asset = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9, GlyphRenderMode.SDFAA, 2048, 2048,
                AtlasPopulationMode.Dynamic, true);
            if (asset == null)
            {
                Plugin.Logger.LogError($"Localization: could not create a font from {path}");
                return;
            }
            asset.name = "SprocketMods Fallback (" + Path.GetFileNameWithoutExtension(path) + ")";
            asset.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(asset);
            _fallback = asset;
            Plugin.Logger.LogInfo($"Localization: fallback font {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Localization: font creation failed ({path}): {e}");
        }
    }

    public void Clear()
    {
        _fallback = null;
        _fontPath = null;
        _patchedFonts.Clear();
    }

    /// <summary>テキストが使っているフォントにフォールバックを登録する（OnEnable や text 設定のたびに呼ばれる）。</summary>
    public void Attach(TMP_Text text)
    {
        var fallback = Fallback;
        if (fallback == null || text == null)
            return;
        var font = text.font;
        if (font == null || _patchedFonts.Contains(font.Pointer))
            return;
        _patchedFonts.Add(font.Pointer);
        if (font.Pointer == fallback.Pointer)
            return;

        try
        {
            // シェーダーはゲームのフォントと同じものを使う（ビルドに含まれていないシェーダーだと表示されないため）
            var gameMaterial = font.material;
            var ownMaterial = fallback.material;
            if (gameMaterial != null && ownMaterial != null && ownMaterial.shader != gameMaterial.shader)
                ownMaterial.shader = gameMaterial.shader;

            var table = font.fallbackFontAssetTable;
            if (table == null)
            {
                table = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
                font.fallbackFontAssetTable = table;
            }
            if (!table.Contains(fallback))
                table.Add(fallback);

            var global = TMP_Settings.fallbackFontAssets;
            if (global != null && !global.Contains(fallback))
                global.Add(fallback);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Localization: could not attach fallback font to {font.name}: {e.Message}");
        }
    }

    /// <summary>font.txt の各行（ファイル名か絶対パス）から最初に存在するフォントファイルを返す。</summary>
    private static string FindFont(string languageDirectory)
    {
        var list = Path.Combine(languageDirectory, LocalizationManager.FontListFile);
        if (!File.Exists(list))
            return null;

        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            languageDirectory,
        };
        foreach (var raw in File.ReadAllLines(list))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            if (Path.IsPathRooted(line))
            {
                if (File.Exists(line))
                    return line;
                continue;
            }
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, line);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        Plugin.Logger.LogWarning($"Localization: none of the fonts in {list} were found");
        return null;
    }
}
