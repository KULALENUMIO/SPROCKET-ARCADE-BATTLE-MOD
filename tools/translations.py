#!/usr/bin/env python3
"""
Sprocket Mods 翻訳ファイルの補助ツール。

使い方:
  python translations.py check <言語フォルダ>
      書式エラー・同じ原文に違う訳・{0} などの数の食い違いを表示する。
  python translations.py template <元の言語フォルダ> <新しい言語フォルダ>
      元の言語の原文をすべて集め、訳文が空のテンプレートを作る（新しい言語を始めるとき用）。
      既に訳がある原文はそのまま残す（追記のみ）。
  python translations.py merge-untranslated <言語フォルダ> [出力ファイル名]
      _untranslated.txt のうち訳が書き込まれた行を出力ファイル（既定 user.txt）に移し、
      _untranslated.txt からは取り除く。

書式は Languages/README.md を参照。
"""
import os
import re
import sys
from collections import defaultdict

PLACEHOLDER = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")
FONT_FILE = "font.txt"
UNTRANSLATED = "_untranslated.txt"


def split_line(line):
    """'key=value' を最初のエスケープされていない = で分ける。エスケープはそのまま残す。"""
    i = 0
    while i < len(line):
        c = line[i]
        if c == "\\":
            i += 2
            continue
        if c == "=":
            return line[:i], line[i + 1:]
        i += 1
    return None


def entries(path):
    with open(path, encoding="utf-8-sig") as f:
        for number, raw in enumerate(f, 1):
            line = raw.rstrip("\r\n")
            if not line or line.startswith("#") or not line.strip():
                continue
            parts = split_line(line)
            yield number, line, parts


def text_files(folder):
    for root, _, files in os.walk(folder):
        for name in sorted(files):
            if name.endswith(".txt") and name != FONT_FILE:
                yield os.path.join(root, name)


def check(folder):
    problems = 0
    seen = defaultdict(list)
    for path in text_files(folder):
        name = os.path.relpath(path, folder)
        for number, line, parts in entries(path):
            if parts is None:
                print(f"{name}:{number}: '=' がありません: {line}")
                problems += 1
                continue
            key, value = parts
            if not key:
                print(f"{name}:{number}: 原文が空です")
                problems += 1
                continue
            if key.startswith("r:"):
                try:
                    re.compile(key[2:])
                except re.error as e:
                    print(f"{name}:{number}: 正規表現エラー: {e}")
                    problems += 1
                continue
            if value:
                keys = sorted(set(PLACEHOLDER.findall(key)))
                values = sorted(set(PLACEHOLDER.findall(value)))
                if keys != values:
                    print(f"{name}:{number}: プレースホルダが一致しません {keys} → {values}: {line}")
                    problems += 1
                seen[key].append((name, number, value))
    for key, uses in seen.items():
        if len({v for _, _, v in uses}) > 1:
            where = ", ".join(f"{n}:{l}" for n, l, _ in uses)
            print(f"同じ原文に違う訳があります（後から読まれた方が優先）: {key}  [{where}]")
    print(f"{len(seen)} 件の訳、問題 {problems} 件")
    return problems


def template(source, target):
    os.makedirs(target, exist_ok=True)
    existing = set()
    for path in text_files(target):
        for _, _, parts in entries(path):
            if parts:
                existing.add(parts[0])
    for path in text_files(source):
        name = os.path.basename(path)
        if name.startswith("user") or name == UNTRANSLATED:
            continue
        out = os.path.join(target, name)
        added = 0
        with open(out, "a", encoding="utf-8", newline="\n") as f:
            for _, _, parts in entries(path):
                if parts and parts[0] not in existing:
                    f.write(parts[0] + "=\n")
                    existing.add(parts[0])
                    added += 1
        print(f"{name}: {added} 行を追加")
    font = os.path.join(target, FONT_FILE)
    if not os.path.exists(font):
        with open(font, "w", encoding="utf-8", newline="\n") as f:
            f.write("# この言語で使うフォントファイル（上から順に探す）。欧文だけならこのファイルは不要。\n")
        print(f"{FONT_FILE} を作成（フォント名を書いてください）")


def merge_untranslated(folder, output="user.txt"):
    path = os.path.join(folder, UNTRANSLATED)
    if not os.path.exists(path):
        print("_untranslated.txt がありません")
        return
    keep, done = [], []
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            parts = split_line(line) if line and not line.startswith("#") else None
            (done if parts and parts[1] else keep).append(line)
    if done:
        with open(os.path.join(folder, output), "a", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(done) + "\n")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(keep) + "\n")
    print(f"{len(done)} 行を {output} に移しました（未翻訳 {sum(1 for l in keep if l and not l.startswith('#'))} 行）")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "check":
        sys.exit(1 if check(sys.argv[2]) else 0)
    elif len(sys.argv) >= 4 and sys.argv[1] == "template":
        template(sys.argv[2], sys.argv[3])
    elif len(sys.argv) >= 3 and sys.argv[1] == "merge-untranslated":
        merge_untranslated(*sys.argv[2:4])
    else:
        print(__doc__)
        sys.exit(2)
