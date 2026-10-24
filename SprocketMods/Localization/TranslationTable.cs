using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SprocketMods.Localization;

/// <summary>
/// 1 言語ぶんの翻訳辞書。Languages/&lt;lang&gt;/ 以下の *.txt をすべて読む。
///
/// 書式（1 行 1 エントリ、UTF-8）:
///   # コメント
///   原文=訳文                     完全一致。数字は {0} {1} … と書くと任意の数値に一致する
///   r:正規表現=置換               正規表現（$1 などで参照）。完全一致・数字テンプレートで見つからなかったときに使う
///   原文=                         訳文が空の行は無視される（未翻訳の印）
/// エスケープ: \n 改行, \t タブ, \= イコール, \\ バックスラッシュ, \# 行頭の #
///
/// ファイルはファイル名順に読み、同じ原文は後から読んだものが優先される。
/// user*.txt と _untranslated.txt は最後に読む（ユーザーの修正が常に勝つ）。
/// </summary>
internal sealed class TranslationTable
{
    private static readonly Regex NumberPattern = new(@"(?<![A-Za-z_\d.,])-?\d+(?:[.,]\d+)*(?![\d])", RegexOptions.Compiled);
    private static readonly Regex FormatSpecifier = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);
    private readonly List<(Regex pattern, string replacement)> _regexes = new();

    public string Language { get; }
    public string Directory { get; }
    public int Count => _exact.Count + _templates.Count + _regexes.Count;

    /// <summary>辞書ファイルに書かれている原文（訳文が空のものも含む）。未翻訳の収集で重複を避けるのに使う。</summary>
    public HashSet<string> KnownKeys { get; } = new(StringComparer.Ordinal);

    public TranslationTable(string language, string directory)
    {
        Language = language;
        Directory = directory;
    }

    public static TranslationTable Load(string language, string directory)
    {
        var table = new TranslationTable(language, directory);
        if (!System.IO.Directory.Exists(directory))
            return table;

        var files = System.IO.Directory.GetFiles(directory, "*.txt", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(LocalizationManager.FontListFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => IsUserFile(f) ? 1 : 0)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                table.LoadFile(file);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Localization: failed to read {file}: {e.Message}");
            }
        }
        return table;
    }

    private static bool IsUserFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("user", StringComparison.OrdinalIgnoreCase)
               || name.Equals(LocalizationManager.UntranslatedFile, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadFile(string path)
    {
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || line.Trim().Length == 0)
                continue;
            if (!TrySplit(line, out var key, out var value))
            {
                Plugin.Logger.LogWarning($"Localization: {Path.GetFileName(path)}:{lineNumber}: missing '='");
                continue;
            }

            if (key.StartsWith("r:", StringComparison.Ordinal))
            {
                if (value.Length == 0)
                    continue;
                try
                {
                    _regexes.Add((new Regex(key.Substring(2), RegexOptions.Compiled | RegexOptions.Singleline), value));
                }
                catch (ArgumentException e)
                {
                    Plugin.Logger.LogWarning($"Localization: {Path.GetFileName(path)}:{lineNumber}: bad regex: {e.Message}");
                }
                continue;
            }

            key = NormalizeFormat(key);
            KnownKeys.Add(key);
            if (value.Length == 0)
                continue;
            value = NormalizeFormat(value);
            if (key.Contains("{0}"))
                _templates[key] = value;
            else
                _exact[key] = value;
        }
    }

    /// <summary>"key=value" を最初のエスケープされていない = で分け、エスケープを戻す。</summary>
    private static bool TrySplit(string line, out string key, out string value)
    {
        var sb = new StringBuilder(line.Length);
        key = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                var n = line[++i];
                sb.Append(n switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => n,
                });
                continue;
            }
            if (c == '=' && key == null)
            {
                key = sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        value = sb.ToString();
        return key != null && key.Length > 0;
    }

    /// <summary>"{0:F2}" "{1,5}" のような書式指定を "{0}" "{1}" にそろえる。</summary>
    public static string NormalizeFormat(string text) => FormatSpecifier.Replace(text, m => "{" + m.Groups[1].Value + "}");

    /// <summary>テキスト中の数値を {0} {1} … に置き換えたテンプレートと、取り出した数値。</summary>
    public static string ToTemplate(string text, List<string> numbers)
    {
        numbers.Clear();
        if (text.IndexOfAny("0123456789".ToCharArray()) < 0)
            return null;
        var template = NumberPattern.Replace(text, m =>
        {
            numbers.Add(m.Value);
            return "{" + (numbers.Count - 1) + "}";
        });
        return numbers.Count > 0 ? template : null;
    }

    public bool TryExact(string text, out string result) => _exact.TryGetValue(text, out result);

    public bool TryTemplate(string template, List<string> numbers, out string result)
    {
        result = null;
        if (template == null || !_templates.TryGetValue(template, out var value))
            return false;
        result = FormatSpecifier.Replace(value, m =>
        {
            var index = int.Parse(m.Groups[1].Value);
            return index < numbers.Count ? numbers[index] : m.Value;
        });
        return true;
    }

    public bool TryRegex(string text, out string result)
    {
        foreach (var (pattern, replacement) in _regexes)
        {
            var m = pattern.Match(text);
            if (m.Success && m.Index == 0 && m.Length == text.Length)
            {
                result = m.Result(replacement);
                return true;
            }
        }
        result = null;
        return false;
    }
}
