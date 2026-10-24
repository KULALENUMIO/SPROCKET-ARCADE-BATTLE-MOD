# 翻訳ファイル（Languages）

Sprocket 本体と Sprocket Mods の表示テキストは、このフォルダの翻訳ファイルで置き換えられます。
ゲームのコードには手を加えず、画面に文字が表示される瞬間（TextMeshPro / uGUI）に英語の原文を辞書で置き換える方式なので、
**ゲームがアップデートされても MOD を作り直す必要はありません。**

```
Languages/
  ja/                    ← 言語コードのフォルダ（フォルダ名が言語の ID）
    font.txt             ← この言語で使うフォント
    game_ui.txt          ← ゲームの UI（メニュー・設定・デザイナー・戦闘画面）
    game_parts.txt       ← 部品名・部品の説明・シナリオ
    sprocketmods.txt     ← このMODの文言
    user.txt             ← 自分用の修正（任意。MOD を更新しても上書きされない）
    _untranslated.txt    ← プレイ中に見つかった未翻訳の文言（自動で作られる）
```

## 書式

UTF-8 のテキストで、1 行に 1 つ `原文=訳文` を書きます。

```
# コメント
Settings=設定
Speed {0} km/h=速度 {0} km/h          ← 数値は {0} {1} … と書くと、どんな数値にも一致する
Enable track trails.\nHeavily reduces performance.=履帯跡を有効にします。\n重くなります。
r:^(.+) destroyed (.+)$=$1 が $2 を撃破   ← r: で始まる行は正規表現（$1 などで参照）
Untranslated text=                    ← 訳文が空の行は無視される
```

| 書き方 | 意味 |
|---|---|
| `\n` | 改行 |
| `\t` | タブ |
| `\=` | `=`（原文や訳文の中の `=`） |
| `\\` | `\` |
| `\#` | 行頭の `#`（コメントではない） |

- 原文は**英語の表示テキストそのまま**です（大文字小文字・記号・空白も一致させる）。
- 前後の空白や `<b>` などの装飾タグ、末尾の `:` は自動で外して探すので、書かなくても大抵一致します。
- 複数行のテキストは、全体で見つからなければ 1 行ずつ探します。
- 同じ原文が複数あるときは、**後から読んだファイルが優先**されます。ファイルはファイル名順に読み、
  `user*.txt` と `_untranslated.txt` は最後に読みます。

## 訳を直す・足す

1. 直したい訳は `user.txt`（無ければ作る）に同じ原文で書く。配布されている `game_ui.txt` などは MOD の更新で上書きされるため。
2. ゲーム中に **F11** を押すと翻訳ファイルを読み直し、表示中の文字も訳し直します（キーは設定で変更可）。

## アップデートで増えた文言に対応する

訳が見つからなかった文言は、プレイ中に自動で `_untranslated.txt` に `原文=` の形で追記されます（数値は `{0}` に置き換え済み）。

1. ゲームを遊んで、新しい画面を一通り開く。
2. `_untranslated.txt` を開き、`=` の右に訳を書く（そのままでも有効。F11 で反映）。
3. 整理したいときは、訳を書いた行だけを `user.txt` に移せます:
   ```
   python _ModSource/tools/translations.py merge-untranslated BepInEx/plugins/SprocketMods/Languages/ja
   ```

## 新しい言語を追加する

1. 既存の言語から、訳文が空のテンプレートを作ります:
   ```
   python _ModSource/tools/translations.py template Languages/ja Languages/zh-CN
   ```
2. 各ファイルの `=` の右に訳を書きます。
3. 欧文以外の文字を使う言語は `font.txt` にフォントファイルを書きます（上から順に探し、最初に見つかったものを使う）。
   ファイル名だけなら Windows のフォントフォルダとこのフォルダを探します。絶対パスも使えます。
   ```
   # 簡体字中国語の例
   msyh.ttc
   simsun.ttc
   ```
   フォントファイルを言語フォルダに同梱することもできます。
4. ゲームの **設定 → MODS → Language** で選ぶか、`BepInEx/config/sprocketmods.cfg` の `[Localization] Language` にフォルダ名を書きます。
   `auto` は Windows の表示言語（`ja-JP` → `ja-JP` か `ja` のフォルダ）、`off` で翻訳しません。

## チェック

```
python _ModSource/tools/translations.py check Languages/ja
```
書式エラー、同じ原文に違う訳、`{0}` などの数の食い違いを表示します。

## 注意

- 入力欄（車両名など）に打った文字は翻訳しません。
- 翻訳で表示がおかしくなったときは、設定で Language を `off` にすると英語に戻ります。
