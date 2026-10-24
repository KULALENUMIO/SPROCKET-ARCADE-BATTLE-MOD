# SPROCKET ARCADE BATTLE MOD

Sprocket を War Thunder / World of Tanks 風のアーケードバトルにする BepInEx 6 (IL2CPP) MOD。

> このフォルダ（`_ModSource`）は Sprocket のインストール先（`Sprocket/_ModSource`）に置いてビルドします。ゲーム本体のファイルは含まれません。

Sprocket 向けの BepInEx 6 (IL2CPP) MOD。設定はゲームの設定画面に追加される **MODS** タブから変更できる。

- **Ballistics HUD** — War Thunder のような弾道予測と貫通インジケーター
- **Arcade Battle Mode** — World of Tanks のようなストレスフリーな操縦（下記）
- **War Thunder 風の右クリック操作** — 照準器の最大/最低倍率切り替え・キューポラ視点（下記）
- **機関砲** — 名前にタグを書いた砲を弾倉式の連射砲にする（下記）
- **キルログ** — 画面右上に誰が誰を撃破したかを表示（下記）
- **命中結果** — War Thunder のように、自分の弾の貫通・跳弾と損傷を与えたモジュールを表示（下記）
- **照準した車両の情報** — 照準の先の車両の名前・敵味方・撃破済みか（残骸か）・乗員・エンジンなどを表示（下記）
- **日本語化（多言語対応）** — ゲーム本体と MOD の表示テキストを翻訳（下記）

## Ballistics HUD

- 主砲の現在の向きから弾道をリアルタイムで計算し、**着弾点に円形のクロスヘア**（War Thunder AB 風）を表示
- 着弾点で **ゲーム本体の貫通シミュレーター** を実行し、クロスヘアの色と文字で判定を表示
- クロスヘアの横に着弾点までの距離。弾の寿命（Lifetime）いっぱいまで計算するので遠距離でも判定できる
- 予測軌跡の線は既定で非表示（F9 で表示）

| 表示 | 色 | 意味 |
|---|---|---|
| PEN / 貫通可 | 緑 | 敵車両に命中し、装甲を貫通できる |
| PEN? n/m / 一部貫通 | 黄 | 着弾点周辺のサンプルの一部だけ貫通（装甲の境目・弱点の縁など） |
| NO PEN / 貫通不可 | 赤 | 敵車両に命中するが貫通できない |
| RICOCHET / 跳弾 | 橙 | 跳弾する |
| HE (no AP sim) / 榴弾 | 黄 | AP 機能を持たない弾（貫通計算の対象外） |
| FRIENDLY / 味方 | 青 | 味方車両に当たる |
| NOT ENEMY / 敵戦車ではない | 灰 | 地形・建物などに当たる |
| OWN VEHICLE / 自車に命中 | 灰 | 自車に当たる |
| NO IMPACT / 着弾なし | 灰 | 弾の寿命内に何にも当たらない（クロスヘアは寿命の位置、距離は「>」付き） |

`ShowDetails = true` にすると、判定の下に着弾速度・飛翔時間・入射角も表示します。

## 構成

| フォルダ | 出力先 | 内容 |
|---|---|---|
| `Unity6InteropFix/` | `BepInEx/patchers/` | **必須**。Unity 6000.3 で BepInEx が起動直後にクラッシュする問題の回避パッチャー |
| `SprocketMods/` | `BepInEx/plugins/SprocketMods/` | 本体（Ballistics HUD・Arcade Battle Mode・右クリック操作・機関砲・キルログ・日本語化・MODS 設定タブ） |
| `docs/` | （配布しない） | 調査メモ: `ai-research.md`（ゲームの AI の仕組み・数値・弱点・MOD から変える方法）、`ai-design.md`（AI 改善の設計: 目指す振る舞い・作り方・確かめ方） |
| `tools/` | （配布しない） | 開発用: `translations.py`（翻訳ファイルの検査）、`disasm/`（GameAssembly.dll の逆アセンブラ、下記「調査用ツール」） |

### Unity6InteropFix が必要な理由

BepInEx 6 BE #788 同梱の Il2CppInterop 1.5.3 は、起動時に `Class::GetDefaultFieldValue` をネイティブフックします。
Sprocket（Unity 6000.3.21 / IL2CPP metadata v39）ではこの関数の探索が別の関数を掴み、プラグインが 0 個でも
`AccessViolationException at Class_GetFieldDefaultValue_Hook.Hook` でゲームが落ちます。
このフックは独自 enum の注入（EnumInjector）にしか使われないため、パッチャーで `InjectorHelpers.Setup()` を
このフック抜きで実行するように差し替えています。

また Unity 6000.3 では `il2cpp_format_exception` 自体も AccessViolation を起こすため、IL2CPP 側の例外が 1 つでも
発生すると（本来 MOD 内で catch できるものでも）ゲームごと落ちます。`Il2CppException.ParseMessageHook` で、
クラス名と `_message` だけを読む安全な実装に差し替えています。

## 操作

| キー | 動作 |
|---|---|
| F8 | HUD の表示/非表示 |
| F9 | 弾道線の表示/非表示 |
| F10 | Arcade Battle Mode の ON/OFF |
| F11 | 翻訳ファイルの再読み込み |
| 右クリック | 照準器: 最大倍率 ⇔ 最低倍率 / 三人称: キューポラ視点 ⇔ 三人称視点 |

主な項目はゲームの **設定 → MODS** タブで変更できます（即時反映・自動保存）。
MODS タブは上のページ切り替え（一般 / 操縦 / 武装・カメラ / 戦闘 HUD / マップ / 照準情報 / 弾道 HUD / AI）で分かれる。
キーや色などすべての項目は `BepInEx/config/sprocketmods.cfg` にあります。

## Arcade Battle Mode

ギアや回転数を気にせず、素早く加速・旋回し、安定して接地し、傾かずに止まれる操縦モード。**自車にだけ**働きます。
ゲームの駆動系（エンジン・変速機・履帯の物理）はそのまま動かし、その上で次のことをします。

**駆動系チューニング**（自車のインスタンスデータだけを書き換え、OFF・乗り換え時に元へ戻す）
- トルクカーブを最大トルクで平らにする（`EngineBehaviour.physicsJob.torqueCurveLookupTable` の y 成分）
- 乗員の変速待ちを無くす: `Driver` は毎回 `GearShiftDelay`（`PowertrainBehaviour+0x70`）に「1〜3 秒（練度次第）」を書き込むため、
  Harmony で `PowertrainBehaviour.FixedUpdate` の直前に 0 で上書き
- 変速機の切断/接続時間を短縮（`TransmissionBehaviour.updateJob.gearDisengagePeriod / gearEngagePeriod`、元は 0.1〜0.3 秒）
- ※ クラッチ速度や変速時間の多くは C# の const でネイティブコードに埋め込まれていて変更できない。残る変速時の駆動力の途切れは下の補助が埋める

**操作アシスト**（閉ループ。補助力は重心に加えるので、それ自体は車体を傾けない）
- 前後: 入力から目標速度を作り、`最大トルク × 最大回転数 × 倍率`（＝エンジンの最大出力）由来の加速度で追従。
  離すと一定の減速度で、逆入力でブレーキの減速度で滑らかに止まる。ギアチェンジ中も加速が途切れない
- エンジン回転が最大の 94% を超えると前進方向の補助を絞る（過回転ダメージ `max(rpm − maxSpeed, 0) × 0.01 × dt` を起こさず、
  トップギアでは設計上の最高速度で頭打ちになる）
- 旋回: 目標の旋回角速度へ角速度を直接寄せる（このビルドでは `Rigidbody.AddTorque` が削除されている）。低速ほど速く回れる
- 安定: 横滑りの打ち消し、ピッチ・ロールの減衰（加減速での前後の傾きを抑える）、接地中の押し付け
- エンジン停止・履帯破損・操縦手不在・空中では推進と旋回の補助をしない

**砲塔の旋回・俯仰**（Snappy turret and gun）
- 加減速（イージング）なしで最高速度のまま目標へ向かい、到達したらその場で止まる。マウスに吸い付くような照準
- 砲塔（`TurretBehaviour`）と砲架（`LayingDriveBehaviour`）の軸は `RotationAxisPhysicsRegister.axes`（`RotationAxisPhysics` の値型配列、要素 0x38 バイト）
  にあり、`RotationAxisPhysicsRegister.Update` が 0.01 秒刻みで加速度制限・目標付近の精密モード（減速）付きで動かす。
  Harmony で自車の軸だけ Update の間 Active フラグを外してゲームの物理をスキップさせ、直後に角度を進めて `apply` を呼ぶ。
  角度制限・360° 旋回の折り返しはゲームと同じ規則（`ConstrainRotation`）
- 速度 = max(年代別の速度, 設計上の速度 × 倍率) × 乗員係数。年代は車両の設計年（`Vehicle.info.Date`）。
  既定は 〜1918: 旋回 12°/s・俯仰 6°/s、〜1935: 20・10、〜1945: 30・15、〜1960: 40・20、1961〜: 50・25（`TurretEraYears` / `TraverseRates` / `ElevationRates`）
- 乗員係数はゲームが毎回軸の `maxSpeed` に書く「設計速度 × 練度などの係数」から求めるので、砲手不在や損傷時はゲーム同様に動かない
- 照準ロック（AimFlags.Lock）中はゲーム本来の処理に任せる

## War Thunder 風の右クリック操作

| 状態 | 右クリック | もう一度 |
|---|---|---|
| 照準器を覗いている | 最大倍率へ | 最低倍率へ |
| 三人称視点 | 車長キューポラ視点へ | 元の三人称視点へ |

- イージング（easeInOutCubic）付きで `TransitionTime` 秒（既定 0.35、0.05〜0.5）かけて切り替える。途中で押すとそこから折り返す
- 照準器: `ScopeCameraController` の焦点距離（targetFocalLength / appliedFocalLength）を最小/最大ズームの間で補間。ホイール操作があればそちらを優先
- キューポラ視点: 三人称カメラ（`OrbitalMovementController`）の注視点オフセットをキューポラ位置へ、距離を 0 へ補間する。
  視点操作・照準・揺れはゲームの処理がそのまま働く。位置は車長キューポラ部品のカメラ位置（無ければ一番高い視察口、それも無ければ車体上部）＋ `CupolaHeightOffset`
- 右クリックはゲームの「命令の取り消し」でもあるので、命令を出している最中やカーソル表示中は反応しない
- 設定は `[Camera]` セクションと MODS タブ

## 機関砲

砲の**名前**に `[AC 弾倉サイズ/毎分発射数]` を書くと、その砲が機関砲になる。砲の設計名（砲の設計画面）と、
車両に配置した砲の名前のどちらに書いてもよい（配置した砲の名前が優先）。

```
20mm Flak [AC 10/450]      ← 弾倉 10 発・毎分 450 発
37mm [AC:6/160rpm]         ← 区切りの「:」や末尾の「rpm」、全角の 【】／ も可
```

- 砲・砲弾・見た目はゲームのものをそのまま使う。砲弾の設計も通常どおり
- 射撃ボタンを押している間、毎分発射数の間隔で連射する。**弾薬庫の砲弾 1 発 = 1 弾倉**（弾薬庫の残数表示は残り弾倉数）
- 弾倉が空になると装填手が弾薬庫から次の弾倉を運ぶ。交換時間は固定（既定 5 秒、`MagazineReloadTime`）で、装填手が作業している間だけ進む
- 反動なし（車体への衝撃・画面の揺れ・砲身の後退。`NoRecoil`）
- 発砲煙なし（`NoMuzzleSmoke`）。発砲音と一瞬の閃光（`MuzzleFlashTime`、既定 0.05 秒）は残す
- 画面中央の右下に残弾（`7 / 10`）と弾倉のバー、交換中は残り時間を表示（`ShowHud`）。ゲームの装填バーも弾倉交換の進み具合を示す
- AI の砲手も同じ処理を通るので、AI 車両の機関砲も同じように撃つ
- 設定は `[Autocannon]` セクションと MODS タブ

仕組み（逆アセンブルで確認したゲームの処理）:
- 砲手（`Gunner.ExecutePerform`）は射撃要求が続く間、毎回「`CanFire` なら `Fire()`」を行う。`CanFire` は装填済みかどうかだけを見る
- `CannonBehaviour.FireInternal` が弾を撃った後 `WeaponBehaviour.RequestReload` → `LoadTask.RequestReload(Consumed)` で再装填に入る
- 機関砲では、弾倉に残弾がある間は `RequestReload` を飛ばして装填済みのままにし、`CanFire` に発射間隔の制限を足す
- 弾倉交換は `LoadTask.Update` を置き換え、装填手の作業（運搬・操作の速さ）がある間だけ時間を進めて `SetState(Loaded)` する。
  弾薬庫からの取り出しと弾種の設定はゲームの `UpdateRequest` / `SetState` がそのまま行う。`LoadFraction` / `RemainingTime` も交換時間に合わせる
- 反動は、発射イベントの受け手のうち `Cannon.OnFire`（`VehicleObject.AddImpulse`）と `VehicleController.OnWeaponFire`（画面の揺れ）を飛ばし、
  砲身の後退アニメーション（`VisualizeRecoil`）を止める
- 発砲煙: 発砲時の効果 `MuzzleFlashEffect` は発砲音と、閃光・煙をまとめた 1 つの VFX グラフ（煙だけを止める公開値は無い）。
  効果は再生させたまま、閃光の一瞬の後に VFX のレンダラーを隠して煙を見えなくする。効果はプールされて使い回されるので、
  次の `Setup` の前に必ず表示を戻す

注意:
- 1 発ごとにゲーム本来の弾道・貫通・破片の計算が走るので、高い発射速度で多数が撃ち合うと重くなることがある
- 弾倉の途中で弾種を切り替えると、新しい弾種の弾倉への交換になる（残弾は引き継がない）。
  装填済みの弾倉が弾薬庫に戻るかどうかはゲームの弾種切り替えの処理に従う（未検証）

## キルログ

画面右上に「A が B を撃破」を表示する。新しいものが一番上に入り、`Duration` 秒（既定 8）でフェードして消える。

- 名前の色: 自車 = 金、味方 = 青、敵 = 赤（チームが無いテスト走行などでは灰）。自車が関わる行は背景が明るい
- 撃破に加えて「行動不能」（走行できなくなったが武装は生きている）も出す（`ShowImmobilized`）
- 文の形は翻訳ファイルの `$K destroyed $V=$K が $V を撃破` などで言語ごとに変えられる
- 設定は `[KillFeed]` セクションと MODS タブ

仕組み（このビルドのゲームには撃破イベントが無いため自前で判定する。`KillEventArgs` は一度も作られず、`KillLogger` / `KillObjective` は中身が無い）:
- **撃破**: 全車両の `VehicleFlags` を 0.25 秒ごとに見て、最初に持っていた `Mobile`（走行可能）と `Armed`（武装）を両方失ったら撃破。
  ゲームの対戦モードの勝利条件（`ImmobilizeObjective`）も `Mobile` を失った車両を倒したと数える。
  命中直後に車両ごと消えた（誘爆など）場合も撃破
- **撃破者**: `CannonBehaviour.FireInternal` が返す弾の ID と撃った車両を覚え、`ProjectileRegister.projectileHistory`（弾 ID → 最新の弾イベント）に
  `HitDamageModelProjectileEvent` が出たら、命中したダメージモデルの Rigidbody から被弾車両を求める。
  撃破の `AttributionWindow` 秒（既定 120）以内に最後に**貫通**弾（無ければ命中弾）を与えた車両を撃破者にする。誰も当てていなければ「B 撃破」
- ※ `IProjectileSimulation.TryGetProjectileProjectileInfo` は参照型を含む構造体の out 引数なので使わない（interop 経由でクラッシュする）

## 命中結果（War Thunder 風）

自分の弾が当たると、照準の下に結果を出す。

```
貫通
砲手 戦死
エンジン 破壊
弾薬庫 損傷 42%
火災
```

- 見出し: 撃破（赤）/ 貫通（橙）/ 非貫通（灰）/ 跳弾（青灰）。機関砲などで同じ目標に続けて当てたものはまとめ、`×命中数` を付ける
- モジュール: 破壊・戦死は赤、損傷・負傷は橙〜黄（残り体力 %）。同じ種類はまとめる（転輪 ×3 など）。
  名前は部品の種類（エンジン・主砲・砲尾・弾薬庫・履帯…）で、乗員は今している役割（車長・砲手・装填手・操縦手・無線手）
- 損傷したモジュールの位置に菱形のマーカーを一瞬出す（`ShowMarkers`）
- 設定は `[HitReport]` セクションと MODS タブ

仕組み:
- 弾: 自車の `CannonBehaviour.FireInternal` が返す弾 ID を追い、`ProjectileRegister.projectileHistory` の `HitDamageModelProjectileEvent.Type`
  （Penetrated / Absorbed / Deflected）で 貫通 / 非貫通 / 跳弾
- 損害: 各車両の `VehicleBehaviour.healthRegister.entries`（部品と体力プール `HealthPool` の組）をメモリから直接読み、命中前（命中を見つけた
  フレームの 1 つ前、至近距離なら発射の瞬間）の体力と比べる。命中後 3 秒は追い続け、火災（`VehicleFlags.OnFire`）・誘爆などの損害も足す
- 乗員の役割: 乗員モジュール（`VehicleCrewWorkspaceModule`）の operator の `ActiveRoleID` と、同じ RoleID の task の `IconIdentifier`
  （commander / gunner / loader / driver / radio）
- 撃破はキルログと同じ判定（乗員全滅など）

## HUD の見た目

MOD の HUD（命中結果・照準した車両の情報・機関砲の残弾・キルログ・弾道 HUD の距離と判定）は、背景が明るくても読めるよう
角丸の半透明の黒いパネルに入れ、左端の色の帯で種類を示す（敵 = 赤、味方 = 青、撃破 = 赤、貫通 = 橙、自車が関わるキルログ = 金 など）。
モジュール・乗員は「名前 ─ 残りのバー ─ 値」の行で揃える。機関砲の残弾は 30 発以下の弾倉なら 1 発ずつの目盛り。

- `[HudStyle] PanelOpacity` — パネルの濃さ（0 でパネルなし）
- `[HudStyle] Scale` — パネル全体の大きさ
- MODS タブの「HUD の見た目」でも変えられる

## 勢力ゲージ（War Thunder 風）

カスタムバトル・シナリオで、画面上部の中央に味方（左・青）と敵（右・赤）の残りの車両数をゲージで出す。

```
 10/12  ■■■■■■■■■■□□ ◆ ■■■■■■■■■■■□□□  11/14
```

- ゲージは中央から外へ伸び、撃破されると外側の目盛りから暗くなる（一瞬白く光る）。40 両を超えると 1 本のバー
- 目安なので、0 になっても試合は終わらない
- 撃破はキルログと同じ判定（乗員全滅など）。誘爆などで消えた車両も撃破として数える。一度に半分以上が消えたら（試合のリセット・退出）数え直す
- 設定は `[TeamBar]` セクションと MODS タブの「マップ」

## ミニマップ（War Thunder 風）

画面左下に地図を出す（北が上）。自車を操作しているときだけ。

- 地図: 地形（Unity Terrain。MapMagic のタイル）から作る
  - 色: 地面の素材。Terrain の splat（各 TerrainLayer の割合）で各レイヤーのテクスチャの平均色を混ぜ、地図らしく彩度を落として明るくする
  - 陰影: 高さの傾きから北西の光で陰を付ける（起伏が小さい地形でも見えるよう高さを強調）
  - 等高線: 高さの幅から間隔（2/5/10/20 m…）を決め、5 本ごとに濃く
  - 範囲: 地形全体（一辺 4.2 km まで）。それより広いマップは車両の集まっている所を中心にした正方形
  - 作るのに数フレームかかる（その間は「地図を作成中…」）
- マス目: 上に列番号（1, 2 …）、左に行の記号（A, B …）。右下に 1 マスの大きさ
- 車両: 向きの分かる矢印。自車 = 金、味方 = 青、敵 = 赤。撃破された車両は暗い ×。地図の外の車両は縁に出す
- 自車からカメラの向きと視野を 2 本の線で出す
- 設定は `[Minimap]` セクションと MODS タブの「マップ」（大きさ、敵を出すか、マス目、視線の方向、等高線）
- `DebugLog = true` のときは作った地図を `BepInEx/minimap_debug.png` に書き出す

## 修理（War Thunder 風）

`F`（`[Repair] Key`）を 1 秒押し続けると修理が始まり、あとはキーを離しても修理が進む。修理時間に達するとエンジン以外の損傷をすべて直す。

- 押している 1 秒の間、画面中央の下の白い円がぎゅるんと一周して修理が始まる
- 修理中は WASD（とクルーズコントロール・AI の操縦）を一切受け付けない。操縦手の入力（`Driver.Solution`）を `Driver.ExecutePerform` の前に 0 にするので、
  エンジンも変速機も反応しない
- 修理中はその場に止まる（物理ステップごとに水平方向の速度と左右の回転を打ち消し、始めた場所・向きへ少しずつ戻す。
  `Rigidbody.constraints` で固定すると履帯の物理が壊れて吹き飛ぶので使わない）。終わったら元どおり走れる
- 修理中は白い円のタイマー（直し終わると一周）と残り秒数、「修理中」を出す。修理中にもう一度 1 秒押すと中止
- 修理時間 = `BaseTime`（初期 20 秒）× √(4 / 満席の乗員数) × (満席の乗員数 / 生きている乗員数)。
  乗員が多い車両ほど早く、乗員が減ると長くなる（例: 5 人乗り 17.9 秒 → 2 人倒れると 29.8 秒）。乗員が全滅していたら直せない
- 直すもの: 部品の体力（ゲームの `HealthUtils.FullyHeal`。体力変化の処理が走る）と切れた履帯。履帯は `TrackAssembly.Broken` に加えて、
  帯のシミュレーション（`TrackBeltBehaviour`）の切れた状態（`broken` と各リンクの `SegmentFlags.Broken`）を消し、`ResetPositions` で車輪の周りに履き直す。
  エンジン（`CombustionEngine` / `EngineVent` / `EngineExhaust`）と乗員は直さない。火災も消さない
- AI も損傷に気付いてから `AiDelay` 秒（初期 3 秒）後に修理を始める（`AiRepair`）。修理中は AI もその場に止まる
- 修理できる損傷があるときは画面下に「[F] 長押しで修理（18 秒）」と出す（`ShowHint`）
- ゲームの初期設定では F はクルーズコントロールの減速にも割り当てられているので、気になるならキーを変える
- 設定は `[Repair]` セクションと MODS タブの「武装・カメラ」

## AI の改善（段階 1）

ゲームの AI の明らかに間抜けな動きを止める。設計は [docs/ai-design.md](docs/ai-design.md)、ゲームの AI の仕組みは [docs/ai-research.md](docs/ai-research.md)。
処理は `Ai/AiDirector.cs`。MODS タブの「AI」ページと `[AI]` セクションで切り替え・強さ（新兵 / 標準 / 熟練 / エース）を選べる。

- **人間らしい照準**: ゲームの距離・速さの誤差と夾叉の表（6 発でほぼ誤差 0）をやめ、`FindAndAimAtTargetGunLayerTask.Update` の後で
  照準の向きに自前の誤差を足す。誤差（標準偏差）= max(下限, 初弾の誤差 ×（1 − 修正率）^撃った数) × 強さの倍率
  - 初弾の誤差 = 0.3 + 0.0019 × 距離 m（300 m で約 0.9 m、1000 m で約 2.2 m）、下限 = 0.25 + 0.00075 × 距離
  - 修正率（1 発ごと）: 新兵 20% / 標準 30% / 熟練 40% / エース 50%。倍率: 1.8 / 1 / 0.75 / 0.5
  - 自分が動いていると 3 倍、撃たれた直後 2 秒は 1.5 倍。自分が 10 m 以上動いた・目標が急に速さを変えたら修正が半分戻る。毎発ランダム
  - 反応: 目標を捕まえてから撃てるまで ゲームの 1 秒 + 新兵 1〜2 / 標準 0.2〜0.6 / 熟練 0〜0.2 / エース 0 秒。
    照準が合ってから撃つまで ゲームの 0.1〜0.5 秒 + 新兵 0.6〜1 / 標準 0.2〜0.35 / 熟練 0.05〜0.15 / エース 0 秒
  - 撃つ条件の照準のずれをゲームの 0.6° から 0.25° に（0.6° では 600 m で 6 m ずれていても撃ってしまい、砲が落ち着く前に撃つ）
  - 砲が複数ある車両は、撃った砲に一番近い照準の修正だけを進める
  - 実測（標準、カスタムバトル 234 発）: 400 m 未満 23 / 83 / 83 / 93%、400〜800 m 22 / 67 / 82 / 84%、800 m 以上 11 / 33 / 50%（初弾 / 2 / 3 / 4 発目以降）
- **砲塔で脅威を見張る**: 目標がいないとき、砲塔を 撃たれた方向（15 秒）→ 最後に目標がいた方向（30 秒）→ 敵陣（敵の出現位置の平均）の方向 へ向ける
- **発砲音**: プレイヤーの発砲も `AINoiseRegister`（1 m で 160 dB）に登録。AI の見回しは音の方向を優先する
- **撃たれたら振り向く**: 撃ってきた位置を脅威として覚え、車長の視線をそちらへ向ける
- **安全な操縦**: 自動の後退を禁止（`DriverAI.reverseAllowed = false`。目的地が後ろでもその場で向きを変えて前進）。
  押された（3.5 m/秒 以上の横滑りが 0.5 秒、またはその場旋回でもハンドルも切らずに 80°/秒 以上の回転が 0.3 秒続いた）ら
  1 秒止まって経路を計算し直す。6 秒アクセルを踏んでも 2 m 動かなければ 2.5 秒下がって経路を計算し直す（3 回まで。走行不能の車両は除く）
- **転覆・詰まりで放棄**（`AutoAbandon`）: 70° 以上傾いて止まったまま `FlipTime`（10）秒、または動こうとしているのに 4 m 進めない時間の合計が
  `StuckTime`（30）秒の AI の車両は、乗員が車両を捨てる（乗員の座席の体力をゲームの `HealthUtils.Kill` で 0 に ＝ 撃破扱い）。
  キルログには撃破者なしで「〇〇 放棄」と出る。走行不能（履帯が切れたなど）でも撃てる車両と、止まって撃つ・修理中の時間は数えない
- `Log = true` で、AI の命中率（距離 400 m 未満 / 400〜800 m / 800 m 以上 × 何発目か）を 60 秒ごと、操縦の立て直しをその都度ログに出す

## 被弾方向インジケーター（War Thunder / WoT 風）

自車が被弾すると、撃ってきた方向を照準の周りの弧で示す（自分が向きを変えれば弧も回る）。

- 色: 貫通 = 赤、非貫通 = 橙、跳弾 = 灰青。出た瞬間は大きく明るく、`Duration` 秒かけて消える
- 同じ方向（±12°）からの続けての被弾は 1 つにまとめる（機関砲など）
- 仕組み: 他の車両の `CannonBehaviour.FireInternal` が返す弾 ID と発射した瞬間の砲口の位置を覚え、
  `ProjectileRegister.projectileHistory` にその弾の `HitDamageModelProjectileEvent` が出て、当たった Rigidbody が自車なら被弾
- 設定は `[DamageIndicator]` セクションと MODS タブの「戦闘 HUD」（非貫通も出すか、表示時間、照準からの距離）

## 照準した車両の情報

照準（画面の中央）を車両に合わせると、照準の上にその車両の情報を出す。照準から外すと消える。

```
敵  Tiger II
稼働中  ·  850 m
乗員 4/5  装填手 戦死
エンジン 62%   履帯 破壊
```

- 1 行目: 敵（赤）/ 味方（青）と車両名。撃破済みなら「敵の残骸」「味方の残骸」
- 2 行目: 稼働中 / 行動不能（走行不能）/ 撃破済み、火災、距離
- 乗員: 生存数 / 全員、負傷者数、戦死した乗員の役割
- モジュール: エンジンは常に、変速機・主砲・閉鎖機・弾薬庫・燃料タンク・履帯・砲塔旋回装置は損傷しているときだけ
- 自車は対象外。手前に地形や建物があるときは出さない。設定は `[TargetInfo]` セクションと MODS タブ（味方・残骸を出すか、車両名・状態・距離・乗員数・乗員の詳細・エンジン・損傷したモジュール・火災のそれぞれを出すか、文字の大きさ、最大距離）

仕組み: 画面中央からのレイを各車両のダメージモデル（`CompoundStructure.Raycast`）に当てて車両を探す。撃破はキルログの判定か乗員全滅、
乗員・モジュールの状態は命中結果と同じく体力プールを直接読む。

## 日本語化（多言語対応）

ゲームのコードではなく、**表示される瞬間のテキスト**を英語原文をキーにした辞書で置き換える。

- フック: `TMP_Text.text` / `TMP_Text.SetText` / `TextMeshProUGUI.OnEnable`（シーンに保存された文字）/ uGUI `Text.text`。
  ゲーム固有のクラスに依存しないので、**アップデートで画面や文言が増えても MOD の再ビルドは不要**
- 照合: 完全一致 → 数値をテンプレート化（`Speed 12 km/h` → `Speed {0} km/h`）→ 正規表現 → 前後の空白・装飾タグ・末尾の `:` を外して → 複数行は行ごと
- 未翻訳の文言は `Languages/<言語>/_untranslated.txt` に自動で集まる（アップデート対応用。訳を書いて F11 で反映）
- フォント: ゲームのフォント（LiberationSans）は欧文のみなので、`font.txt` に書いたフォント（日本語は BIZ UDゴシック → 游ゴシック → メイリオ → MS ゴシック）から
  `TMP_FontAsset.CreateFontAsset` でダイナミックフォントを作り、全 TMP フォントのフォールバックに登録
- 言語は `Languages/<言語コード>/` フォルダを足すだけで増える。設定の MODS タブで選択（auto = Windows の表示言語）
- 入力欄の文字は翻訳しない
- 初期の日本語訳（約 1,300 件）は、IL2CPP メタデータからゲームのコードが参照している UI 文字列と、
  `StreamingAssets/Localization/en-UK` の部品・シナリオの XML を抽出して作成

書式・言語の追加方法は [SprocketMods/Languages/README.md](SprocketMods/Languages/README.md)。
補助ツール `tools/translations.py`（`check` / `template` / `merge-untranslated`）。

## 設定画面の MODS タブ

言語 / Arcade Battle Mode / 砲塔のキビキビ旋回 / War Thunder 風の右クリック操作 / 機関砲 / キルログ / 命中結果 / 照準した車両の情報 / Ballistics HUD の各項目。

`SettingsMenu` は `subMenus` 配列からタブを作り、各タブは `DisplayGUI(IGUILayout)` で `IGUIElementDrawer` のトグルやスライダーを描く。
`SettingsSubMenu` を継承したクラスを IL2CPP に注入し、Harmony で `SetEditProfile` / `SetupMenuButtons` のときに配列の末尾へ追加している。

## 仕組み

ゲームのネイティブコード（GameAssembly.dll）を逆アセンブルして、ゲームと同じ計算をしています。

**弾道**（`ProjectileInstance.Update` と同じ）
- 発射位置 `WeaponBehaviour.ShellSpawnPosition`、方向は最後の砲身トランスフォームの forward
- 初速 `ShellProperties.CalculateFireInfo(砲身長, 口径, 弾長, 装薬長, 最大圧力)`（`DetonationPropellantProjectileFunction` と同じ引数）＋ 車両速度の継承
- 毎ステップ `a = g − v̂·CalculateDragForce(|v|)/質量`、`v += a·dt`、`p += v·dt`（dt = FixedUpdate 間隔）
- 当たり判定: 車両は全ダメージモデルの AABB → `CompoundStructure.Raycast`、地形は `Physics.Raycast`（`ProjectilesMain.ProjectileLayerMask`）。
  0.04 秒ぶんのステップをまとめた弦で判定し、当たった弦だけ 1 ステップずつ判定し直す
- ※ `DamageModel.Raycast` は使わない。戻り値 `DamageModelHit` は参照型を含む構造体で、Il2CppInterop 経由の `out` 受け渡しがメモリを壊してクラッシュする
- 空気抵抗は弾種ごとに `CalculateDragForce` を 1 m/s 刻みで前計算した表から補間
- 弾速が `MinSpeed` を下回るか寿命 `Lifetime` を超えたら終了
- ※ 発射時のランダムな散布は含みません（予測は散布の中心）

**貫通判定**（`ArmourPiercingProjectileFunction.HitDamageModel` と同じ入力）
- `PenetratorInfo(質量, 直径, ProjectileConstants.MetalDensity, 5°, PenetratorConstant, 着弾点, 着弾速度)` を
  着弾した車両の `CompoundStructure.Penetrate()` に渡し、最初の破片のフラグ（Penetrated / Deflected / Embedded）で判定。
  結果は `GetFragment()`（同じく参照型を含む構造体の out 引数）を使わず、`fragments` 配列をメモリから直接読む
  （オフセットは IL2CPP メタデータから実行時に取得）
- `Penetrate()` は最後に `SimulationRan` イベントを発火し、その購読側で被弾処理（ダメージ・カメラ揺れ・AI の反応）が走るため、
  予測の呼び出し中だけイベントを外して、すぐ元に戻しています（予測で敵に損傷が入ることはありません）
- 着弾点の周囲（既定: 1 口径の円周上 4 点 + 中心）でも判定し、結果が割れたら「一部貫通」

**陣営**: `CompoundStructure.ID.TeamID` を自車（Rigidbody が一致するダメージモデル）のチームと比較。
自車のチームが `None`（設計画面のテスト走行など）の場合は、他の車両をすべて敵として扱います。

## ビルド

.NET SDK 6 以上が必要です。BepInEx が一度ゲームを起動して `BepInEx/interop/` を生成した後にビルドしてください。

```
dotnet build Unity6InteropFix -c Release
dotnet build SprocketMods -c Release
```

ビルド後、DLL は自動でゲームの `BepInEx/patchers/` と `BepInEx/plugins/SprocketMods/` にコピーされます。
ゲームが別の場所にある場合は `Directory.Build.props` の `GameDir` を変更してください。

## 調査用ツール（tools/disasm）

ゲームの処理を読むための GameAssembly.dll の逆アセンブラ。BepInEx/core に入っている LibCpp2IL と Iced を使う。
使い回すツールは一時フォルダではなく必ずここ（`tools/`）に置く。

```
dotnet build tools/disasm -c Release
tools/disasm/bin/Release/disasm.exe <出力フォルダ> <検索> [<検索> ...]
```

| 検索 | 内容 |
|---|---|
| `<正規表現>` | `名前空間.型::メソッド` が一致するメソッドを逆アセンブルして `<型>__<メソッド>.asm` に書く。call の飛び先はメソッド名、RIP 相対の参照はメタデータ（型・メソッド・フィールド）か文字列リテラルの注釈付き |
| `X:<16進>` | そのアドレスを呼んでいる・参照している命令を全コードから探す |
| `U:<正規表現>` | メタデータや文字列リテラルの参照が一致する命令を全コードから探す（例 `U:Value=Sprocket.HealthPool}`） |
| `F:<正規表現>` | 型のフィールドとオフセット |
| `T:<正規表現>` | 型の基底・フィールド（オフセット）・プロパティ・メソッド（アドレス） |
| `G:<正規表現>` | 文字列リテラルを探す |

全コードの検索（X: / U:）も数秒で終わる。結果は `<出力フォルダ>/_results.txt` と `_methods.txt` にも書く。

## 注意

- ゲームのアップデートでクラスやネイティブコードが変わると動かなくなる可能性があります。
  その場合は `BepInEx/interop/` を削除して再生成させ、再ビルドしてください。
- 不具合時は `BepInEx/LogOutput.log` を確認してください。設定の `DebugLog = true` で砲弾パラメータと判定結果がログに出ます。
