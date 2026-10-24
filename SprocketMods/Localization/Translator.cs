using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SprocketMods.Localization;

/// <summary>
/// 表示テキストを翻訳する。探す順番:
///   1. 完全一致
///   2. 数値をテンプレート化して一致（"Speed 12 km/h" → "Speed {0} km/h"）
///   3. 正規表現
///   4. 前後の空白・リッチテキストタグを外した中身で 1〜3
///   5. 複数行なら行ごとに 1〜4
/// 結果はキャッシュする。見つからなかった文字列は（数値をテンプレート化して）_untranslated.txt に追記する。
/// </summary>
internal sealed class Translator
{
    private const int CacheLimit = 40000;
    private const int MaxLength = 2000;

    private static readonly Regex Wrapped = new(@"^(\s*(?:<[^>]+>\s*)*)(.*?)((?:\s*</?[^>]+>)*\s*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HasLatin = new(@"[A-Za-z]{2}", RegexOptions.Compiled);
    private static readonly Regex OnlyTags = new(@"^(\s*<[^>]*>\s*)*$", RegexOptions.Compiled);

    private readonly TranslationTable _table;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dumped;
    private readonly List<string> _pendingDump = new();
    private readonly List<string> _numbers = new();
    private readonly bool _dump;

    public Translator(TranslationTable table, bool dump)
    {
        _table = table;
        _dump = dump;
        _dumped = new HashSet<string>(table.KnownKeys, StringComparer.Ordinal);
    }

    public string Translate(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
            return text;
        if (_cache.TryGetValue(text, out var cached))
            return cached;
        if (_outputs.Contains(text))
            return text;

        var result = TranslateLines(text, out var found);
        if (!found)
        {
            result = text;
            if (text.IndexOf('\n') < 0)
                CollectUntranslated(text);
        }
        else
        {
            if (_outputs.Count > CacheLimit)
                _outputs.Clear();
            _outputs.Add(result);
        }

        if (_cache.Count > CacheLimit)
            _cache.Clear();
        _cache[text] = result;
        return result;
    }

    private string TranslateLines(string text, out bool found)
    {
        if (TryTranslateSegment(text, out var whole))
        {
            found = true;
            return whole;
        }
        found = false;
        if (text.IndexOf('\n') < 0)
            return text;

        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            var line = lines[i];
            var trailingCr = line.EndsWith("\r", StringComparison.Ordinal);
            var core = trailingCr ? line.Substring(0, line.Length - 1) : line;
            if (TryTranslateSegment(core, out var translated))
            {
                found = true;
                sb.Append(translated);
            }
            else
            {
                sb.Append(core);
                CollectUntranslated(core);
            }
            if (trailingCr)
                sb.Append('\r');
        }
        return sb.ToString();
    }

    private bool TryTranslateSegment(string text, out string result)
    {
        if (TryLookup(text, out result))
            return true;

        // 前後の空白・タグを外した中身で探す（"<b>Engine</b>" や " Engine: " など）
        var m = Wrapped.Match(text);
        if (m.Success)
        {
            var core = m.Groups[2].Value;
            if (core.Length > 0 && core.Length != text.Length && TryLookup(core, out var inner))
            {
                result = m.Groups[1].Value + inner + m.Groups[3].Value;
                return true;
            }
            // 末尾のコロンや記号を外す（"Mass:" → "Mass" の訳 + ":"）
            var trimmed = core.TrimEnd(':', '：', ' ');
            if (trimmed.Length > 0 && trimmed.Length < core.Length && TryLookup(trimmed, out var colon))
            {
                result = m.Groups[1].Value + colon + core.Substring(trimmed.Length) + m.Groups[3].Value;
                return true;
            }
        }
        result = null;
        return false;
    }

    private bool TryLookup(string text, out string result)
    {
        if (_table.TryExact(text, out result))
            return true;
        var template = TranslationTable.ToTemplate(text, _numbers);
        if (template != null && _table.TryTemplate(template, _numbers, out result))
            return true;
        return _table.TryRegex(text, out result);
    }

    // ---------------------------------------------------------------- untranslated

    private void CollectUntranslated(string text)
    {
        if (!_dump || !HasLatin.IsMatch(text) || OnlyTags.IsMatch(text))
            return;
        var key = Wrapped.Match(text) is { Success: true } m && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : text;
        key = TranslationTable.ToTemplate(key, _numbers) ?? key;
        if (!HasLatin.IsMatch(key) || !_dumped.Add(key))
            return;
        lock (_pendingDump)
            _pendingDump.Add(key);
    }

    /// <summary>集めた未翻訳を _untranslated.txt に追記する（数秒おきに呼ぶ）。</summary>
    public void FlushUntranslated()
    {
        string[] lines;
        lock (_pendingDump)
        {
            if (_pendingDump.Count == 0)
                return;
            lines = _pendingDump.ToArray();
            _pendingDump.Clear();
        }
        try
        {
            System.IO.Directory.CreateDirectory(_table.Directory);
            var path = Path.Combine(_table.Directory, LocalizationManager.UntranslatedFile);
            var sb = new StringBuilder();
            if (!File.Exists(path))
            {
                sb.Append("# Untranslated texts collected while playing. Fill in the right side of '=' and press the reload key.\n");
                sb.Append("# プレイ中に見つかった未翻訳のテキスト。'=' の右に訳を書いて再読み込みキーを押すと反映される。\n");
            }
            foreach (var line in lines)
                sb.Append(Escape(line)).Append("=\n");
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Localization: could not write untranslated texts: {e.Message}");
        }
    }

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '=': sb.Append("\\="); break;
                case '#' when i == 0: sb.Append("\\#"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
