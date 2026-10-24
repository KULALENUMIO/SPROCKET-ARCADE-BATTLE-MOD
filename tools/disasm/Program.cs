// GameAssembly.dll の逆アセンブラ（調査用）。
//
//   dotnet run -c Release -- <出力フォルダ> <検索> [<検索> ...]
//   （ビルド後は bin/Release/disasm.exe <出力フォルダ> <検索> ... でも同じ。全コードの検索も数秒）
//
// 検索の書き方:
//   <正規表現>     "名前空間.型::メソッド" が一致するメソッドを逆アセンブルし、<型>__<メソッド>.asm に書く（オーバーロードは同じファイル）
//                  例: "SettingsMenu::UpdateMenu"  "HealthPool::(HealthDelta|NotifyHealthChanges)"
//   X:<16進>       そのアドレスを呼んでいる（参照している）命令を全コードから探す。例: X:1806A92E0
//   U:<正規表現>   メタデータ（型・メソッド・フィールド・文字列リテラル）の参照が一致する命令を全コードから探す
//                  例: "U:CommanderPlan"  "U:\"Repair\""
//   F:<正規表現>   型の完全名が一致する型のフィールドとオフセット（16進）を出す。例: "F:^Sprocket.HealthPool$"
//   T:<正規表現>   型の完全名が一致する型の基底・フィールド（オフセット）・プロパティ・メソッド（アドレス）を出す
//                  （BepInEx/interop のダンプの代わり）。例: "T:ArtificialIntelligence.DriverAI$"
//   G:<正規表現>   文字列リテラル（コード中の "..."）を探す
//   D:<16進>[,<個数>]  そのアドレスのデータを int32 / float / double として出す（コード中の定数の値を読む）。例: D:183FAC660,4
//
// 注釈: call / jmp の飛び先はメソッド名、RIP 相対のメモリ参照はメタデータ（Metadata Usage {...}）か文字列リテラル。
// 一覧は <出力フォルダ>/_methods.txt、ほかの結果は <出力フォルダ>/_results.txt にも書く。
// ゲームのフォルダは、この exe の場所から上へ GameAssembly.dll を探す（環境変数 SPROCKET_DIR でも指定できる）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetRipper.Primitives;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.Metadata;

internal static class Program
{
    private const int MaxMethodBytes = 0x20000;

    private static byte[] _image;
    private static ulong[] _starts;
    private static readonly Dictionary<ulong, string> Names = new();
    private static readonly StringBuilder Results = new();

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: disasm <outDir> <regex | X:hex | U:regex | F:regex | G:regex> ...");
            return 1;
        }
        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        Console.OutputEncoding = Encoding.UTF8;

        var game = FindGameDir();
        var assembly = Path.Combine(game, "GameAssembly.dll");
        var metadata = Path.Combine(game, "Sprocket_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var version = LibCpp2IlMain.DetermineUnityVersion(Path.Combine(game, "UnityPlayer.dll"), Path.Combine(game, "Sprocket_Data"));
        Console.WriteLine($"Game: {game}  Unity {version}");
        if (!LibCpp2IlMain.LoadFromFile(assembly, metadata, version))
        {
            Console.Error.WriteLine("LibCpp2IL failed to load the game");
            return 2;
        }
        _image = LibCpp2IlMain.Binary.GetRawBinaryContent();
        BuildIndex();
        Console.WriteLine($"{Names.Count} method addresses");

        foreach (var query in args.Skip(1))
        {
            if (query.StartsWith("X:", StringComparison.Ordinal))
                Callers(Convert.ToUInt64(query.Substring(2).Replace("0x", ""), 16));
            else if (query.StartsWith("U:", StringComparison.Ordinal))
                Usages(new Regex(query.Substring(2)));
            else if (query.StartsWith("F:", StringComparison.Ordinal))
                Fields(new Regex(query.Substring(2)), false);
            else if (query.StartsWith("T:", StringComparison.Ordinal))
                Fields(new Regex(query.Substring(2)), true);
            else if (query.StartsWith("D:", StringComparison.Ordinal))
                Data(query.Substring(2));
            else if (query.StartsWith("G:", StringComparison.Ordinal))
                Literals(new Regex(query.Substring(2)));
            else
                Disassemble(new Regex(query), outDir);
        }
        if (Results.Length > 0)
            File.WriteAllText(Path.Combine(outDir, "_results.txt"), Results.ToString(), Encoding.UTF8);
        return 0;
    }

    private static string FindGameDir()
    {
        var env = Environment.GetEnvironmentVariable("SPROCKET_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "GameAssembly.dll")))
            return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GameAssembly.dll")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("GameAssembly.dll not found (set SPROCKET_DIR)");
    }

    // ------------------------------------------------------------------ 名前

    private static string MethodName(Il2CppMethodDefinition m)
    {
        var parameters = m.Parameters == null ? "" : string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.ParameterName}"));
        return $"{m.DeclaringType?.FullName}::{m.Name}({parameters})";
    }

    private static void BuildIndex()
    {
        foreach (var m in LibCpp2IlMain.TheMetadata.methodDefs)
        {
            if (m.MethodPointer != 0 && !Names.ContainsKey(m.MethodPointer))
                Names[m.MethodPointer] = MethodName(m);
        }
        foreach (var pair in LibCpp2IlMain.Binary.ConcreteGenericImplementationsByAddress)
        {
            if (!Names.ContainsKey(pair.Key))
                Names[pair.Key] = pair.Value.FirstOrDefault()?.ToString() ?? "generic";
        }
        _starts = Names.Keys.OrderBy(a => a).ToArray();
    }

    private static string Describe(ulong address)
    {
        if (Names.TryGetValue(address, out var name))
            return name;
        if (LibCpp2IlMain.Binary.TryGetExportedFunctionName(address, out var export))
            return export;
        return null;
    }

    /// <summary>RIP 相対で参照しているアドレスの説明（メタデータ・文字列リテラル）。</summary>
    private static string DescribeData(ulong address)
    {
        try
        {
            var literal = LibCpp2IlMain.GetLiteralByAddress(address);
            if (literal != null)
                return "\"" + literal.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            var usage = LibCpp2IlMain.GetAnyGlobalByAddress(address);
            if (usage != null)
                return usage.ToString(); // "Metadata Usage {type=..., Value=...}"
        }
        catch (Exception)
        {
            // 解決できない参照
        }
        return null;
    }

    // ------------------------------------------------------------------ 逆アセンブル

    private static IEnumerable<Instruction> Decode(ulong start)
    {
        var raw = LibCpp2IlMain.Binary.MapVirtualAddressToRaw(start, false);
        if (raw <= 0 || raw >= _image.Length)
            yield break;
        var index = Array.BinarySearch(_starts, start);
        var end = index >= 0 && index + 1 < _starts.Length ? _starts[index + 1] : start + MaxMethodBytes;
        var length = (int)Math.Min(Math.Min(end - start, MaxMethodBytes), (ulong)(_image.Length - raw));
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(_image, (int)raw, length));
        decoder.IP = start;
        var limit = start + (ulong)length;
        while (decoder.IP < limit)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid)
                yield break;
            yield return instruction;
        }
    }

    private static string Format(MasmFormatter formatter, in Instruction instruction)
    {
        var output = new StringOutput();
        formatter.Format(instruction, output);
        var text = $"{instruction.IP:X} {output.ToStringAndReset()}";
        string note = null;
        if (instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
            && instruction.Op0Kind is OpKind.NearBranch64)
            note = Describe(instruction.NearBranch64);
        if (note == null && instruction.IsIPRelativeMemoryOperand)
            note = Describe(instruction.IPRelativeMemoryAddress) ?? DescribeData(instruction.IPRelativeMemoryAddress);
        return note == null ? text : text.PadRight(48) + "; " + note;
    }

    private static MasmFormatter CreateFormatter()
    {
        var formatter = new MasmFormatter();
        formatter.Options.UppercaseHex = true;
        formatter.Options.HexSuffix = "h";
        formatter.Options.FirstOperandCharIndex = 0;
        return formatter;
    }

    private static void Disassemble(Regex pattern, string outDir)
    {
        var formatter = CreateFormatter();
        var index = new StringBuilder();
        var files = new Dictionary<string, StringBuilder>();
        foreach (var m in LibCpp2IlMain.TheMetadata.methodDefs)
        {
            if (m.MethodPointer == 0)
                continue;
            var name = MethodName(m);
            if (!pattern.IsMatch(name))
                continue;
            var file = Sanitize($"{m.DeclaringType?.FullName}__{m.Name}") + ".asm";
            if (!files.TryGetValue(file, out var sb))
                files[file] = sb = new StringBuilder();
            sb.AppendLine($"; {name} @ {m.MethodPointer:X}");
            foreach (var instruction in Decode(m.MethodPointer))
                sb.AppendLine(Format(formatter, instruction));
            sb.AppendLine();
            index.AppendLine($"{m.MethodPointer:X} {name}");
        }
        foreach (var pair in files)
            File.WriteAllText(Path.Combine(outDir, pair.Key), pair.Value.ToString(), Encoding.UTF8);
        File.AppendAllText(Path.Combine(outDir, "_methods.txt"), index.ToString(), Encoding.UTF8);
        Console.WriteLine($"{pattern}: {files.Count} file(s)");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c is '<' or '>' or '`' or '/' or '|' ? '_' : c).ToArray();
        var result = new string(chars);
        return result.Length > 180 ? result.Substring(0, 180) : result;
    }

    // ------------------------------------------------------------------ 全コードの検索

    /// <summary>すべてのメソッドを逆アセンブルし、条件に合う命令を出す。</summary>
    private static void ScanAll(string title, Func<Instruction, string> match)
    {
        var formatter = CreateFormatter();
        var hits = 0;
        Report($"== {title}");
        foreach (var start in _starts)
        {
            foreach (var instruction in Decode(start))
            {
                var note = match(instruction);
                if (note == null)
                    continue;
                hits++;
                Report($"{Names[start]}\n    {Format(formatter, instruction)}");
            }
        }
        Report($"({hits} hit(s))");
    }

    private static void Callers(ulong target)
    {
        ScanAll($"references to {target:X} {Describe(target)}", i =>
            i.Op0Kind == OpKind.NearBranch64 && i.NearBranch64 == target
            || i.IsIPRelativeMemoryOperand && i.IPRelativeMemoryAddress == target ? "" : null);
    }

    private static void Usages(Regex pattern)
    {
        var cache = new Dictionary<ulong, bool>();
        ScanAll($"metadata usages matching {pattern}", i =>
        {
            if (!i.IsIPRelativeMemoryOperand)
                return null;
            var address = i.IPRelativeMemoryAddress;
            if (!cache.TryGetValue(address, out var ok))
            {
                var data = DescribeData(address);
                ok = data != null && pattern.IsMatch(data);
                cache[address] = ok;
            }
            return ok ? "" : null;
        });
    }

    private static void Fields(Regex pattern, bool members)
    {
        foreach (var type in LibCpp2IlMain.TheMetadata.typeDefs)
        {
            if (type.FullName == null || !pattern.IsMatch(type.FullName))
                continue;
            Report($"== {type.FullName} : {type.BaseType}{(type.IsValueType ? " (value type)" : "")}");
            var infos = type.FieldInfos;
            if (infos != null)
            {
                foreach (var f in infos)
                {
                    var isStatic = (f.Attributes & System.Reflection.FieldAttributes.Static) != 0;
                    var offset = f.FieldOffset;
                    if (isStatic && f.Field != null)
                    {
                        // 静的フィールドは型の静的領域（Il2CppClass.static_fields）の中の位置
                        try
                        {
                            offset = LibCpp2IlMain.Binary.GetFieldOffsetFromIndex(type.TypeIndex, f.IndexInParent, f.Field.FieldIndex, type.IsValueType, true);
                        }
                        catch (Exception)
                        {
                            // 取れなければそのまま
                        }
                    }
                    var literal = (f.Attributes & System.Reflection.FieldAttributes.Literal) != 0;
                    var value = literal && f.DefaultValue != null ? $" = {f.DefaultValue}" : "";
                    Report($"    {(literal ? "const " : isStatic ? "static " : "")}0x{offset:X} {f.Field?.FieldType} {f.Field?.Name}{value}");
                }
            }
            if (!members)
                continue;
            if (type.Properties != null)
            {
                foreach (var prop in type.Properties)
                    Report($"    prop {prop.PropertyType} {prop.Name}{(prop.Getter != null ? " get;" : "")}{(prop.Setter != null ? " set;" : "")}");
            }
            if (type.Methods != null)
            {
                foreach (var m in type.Methods)
                    Report($"    {(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({string.Join(", ", (m.Parameters ?? Array.Empty<LibCpp2IL.Reflection.Il2CppParameterReflectionData>()).Select(p => $"{p.Type} {p.ParameterName}"))}) @ {m.MethodPointer:X}");
            }
        }
    }

    private static void Literals(Regex pattern)
    {
        var metadata = LibCpp2IlMain.TheMetadata;
        Report($"== string literals matching {pattern}");
        for (uint i = 0; i < metadata.stringLiterals.Length; i++)
        {
            var text = metadata.GetStringLiteralFromIndex(i);
            if (text != null && pattern.IsMatch(text))
                Report($"    [{i}] \"{text}\"");
        }
    }

    private static void Data(string spec)
    {
        var parts = spec.Split(',');
        var address = Convert.ToUInt64(parts[0].Replace("0x", ""), 16);
        var count = parts.Length > 1 ? int.Parse(parts[1]) : 4;
        var raw = LibCpp2IlMain.Binary.MapVirtualAddressToRaw(address, false);
        Report($"== data at {address:X}");
        if (raw <= 0)
        {
            Report("    (not mapped)");
            return;
        }
        for (var i = 0; i < count; i++)
        {
            var at = (int)raw + i * 4;
            if (at + 8 > _image.Length)
                break;
            var i32 = BitConverter.ToInt32(_image, at);
            var f32 = BitConverter.ToSingle(_image, at);
            var f64 = BitConverter.ToDouble(_image, at);
            Report($"    {address + (ulong)(i * 4):X}: int {i32}  float {f32:R}  double(8) {f64:R}");
        }
    }

    private static void Report(string line)
    {
        Console.WriteLine(line);
        Results.AppendLine(line);
    }
}
