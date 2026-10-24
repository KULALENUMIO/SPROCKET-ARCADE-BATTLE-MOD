# Translation files (Languages)

The display text of Sprocket and of SPROCKET ARCADE BATTLE MOD is replaced using the translation files in this folder.
The game's code is not modified: at the moment text is displayed (TextMeshPro / uGUI) the English original is looked up in a dictionary and replaced.
**A game update therefore does not require rebuilding the mod.**

The bundled Japanese translation (`ja/`) doubles as a **template for other languages**; see [Adding a new language](#adding-a-new-language).

```
Languages/
  ja/                    language code folder (the folder name is the language ID)
    font.txt             fonts used for this language
    game_ui.txt          the game's UI (menus, settings, designer, battle screen)
    game_parts.txt       part names, part descriptions, scenarios
    sprocketmods.txt     text of this mod
    user.txt             your own corrections (optional; not overwritten by mod updates)
    _untranslated.txt    untranslated text found while playing (created automatically)
```

## Format

UTF-8 text, one `original=translation` pair per line.

```
# comment
Settings=設定
Speed {0} km/h=速度 {0} km/h          numbers are written {0} {1} ... and match any number
Enable track trails.\nHeavily reduces performance.=履帯跡を有効にします。\n重くなります。
r:^(.+) destroyed (.+)$=$1 が $2 を撃破   lines starting with r: are regular expressions ($1 etc. refer to groups)
Untranslated text=                    a line with an empty translation is ignored
```

| Syntax | Meaning |
|---|---|
| `\n` | newline |
| `\t` | tab |
| `\=` | a literal `=` inside an original or translation |
| `\\` | `\` |
| `\#` | a `#` at the start of a line (not a comment) |

- The original is **the English display text exactly as shown** (case, punctuation and spacing must match).
- Leading/trailing whitespace, rich-text tags such as `<b>` and a trailing `:` are stripped automatically before lookup, so you usually do not need to write them.
- Multi-line text that is not found as a whole is looked up line by line.
- When the same original appears more than once, **the file read last wins**. Files are read in file name order, with `user*.txt` and `_untranslated.txt` read last.

## Fixing or adding translations

1. Put corrections in `user.txt` (create it if missing) using the same original. The bundled `game_ui.txt` and others are overwritten when the mod is updated.
2. Press **F11** in game to reload the translation files and re-translate what is on screen (the key can be changed in the settings).

## Handling text added by a game update

Text without a translation is appended to `_untranslated.txt` as `original=` while you play (numbers are already replaced with `{0}`).

1. Play the game and open the new screens.
2. Open `_untranslated.txt` and write the translation to the right of `=` (it is effective as is; F11 applies it).
3. To tidy up, move only the lines you translated into `user.txt`:
   ```
   python _ModSource/tools/translations.py merge-untranslated BepInEx/plugins/SprocketMods/Languages/ja
   ```

## Adding a new language

1. Create an empty template from an existing language:
   ```
   python _ModSource/tools/translations.py template Languages/ja Languages/zh-CN
   ```
2. Write the translation to the right of `=` in each file.
3. For languages that need non-Latin characters, list font files in `font.txt` (searched from the top; the first one found is used).
   A bare file name is searched in the Windows fonts folder and in this folder; absolute paths also work.
   ```
   # Simplified Chinese example
   msyh.ttc
   simsun.ttc
   ```
   Font files can also be shipped inside the language folder.
4. Select the language in game under **Settings → MODS → Language**, or write the folder name to `[Localization] Language` in `BepInEx/config/sprocketmods.cfg`.
   `auto` follows the Windows display language (`ja-JP` uses a `ja-JP` or `ja` folder); `off` disables translation.

Translations contributed to this repository are welcome.

## Checking

```
python _ModSource/tools/translations.py check Languages/ja
```
Reports format errors, different translations for the same original, and mismatched `{0}` numbers.

## Notes

- Text typed into input fields (vehicle names, etc.) is not translated.
- If a translation breaks the display, set Language to `off` in the settings to return to English.
