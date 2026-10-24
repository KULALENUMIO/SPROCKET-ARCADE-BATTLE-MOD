# Sprocket の AI 調査メモ

ゲームのバージョン 0.2.55.5（Unity 6000.3.21 / IL2CPP）で、`GameAssembly.dll` を `tools/disasm` で逆アセンブルして読んだ結果と、
カスタムバトル（AI 19 両）での実測をまとめたもの。アドレスはこのバージョンのもので、ゲームの更新で変わる。

- 「確定」= 逆アセンブルで処理を追って確認した。「推定」= 処理の一部からの推測。
- 値の「種類」: **const** = コンパイル時定数でコードに埋め込まれている（MOD から値を書き換えても効かない。変えるならメソッドを差し替える）、
  **static** = 静的フィールド（書き換えれば全 AI に効く）、**インスタンス** = AI ごとのフィールド（書き換えればその AI だけに効く）。

---

## 1. 全体の構成

AI は車両 1 両ごとに、乗員の役割に対応した 4 つの部品でできている（`Sprocket.Vehicles.AI.dll`）。

| 部品 | クラス | 役割 |
|---|---|---|
| 車長 | `CommanderAI` | 索敵、目標の記憶と優先順位、作戦（Plan）の選択、ほかの 3 つへの指示 |
| 操縦手 | `DriverAI` | 作業（DriverAITask）のスタックから目的地・向きを受け取り、NavMesh の経路、衝突回避、スロットル・ステア |
| 照準手 | `GunLayerAI` | 作業（GunLayerAITask）から照準の向きを受け取る。誤差・見越し・弾道計算 |
| 砲手 | `GunnerAI` | 作業（GunnerAITask）で撃つかどうかを決め、撃った弾の結果を記録 |

- 登録: `AIRegister.register`（static の `List<AI>`）。MOD からは `AIRegister.register` で全 AI に届く。
- 更新の頻度（`AIRegister`、const）: **FastTick = 0.05 秒（20 Hz）**、**SlowTick = 1 秒**、AI ごとに 1/30 秒ずつずらす（TickSpacing）。
- 作成: `VehicleAIAssemblyStage`（車両の組み立て段階）が `DriverAISettings` / `GunnerAISettings` / `GunLayerAISettings` /
  `CommanderAISettings` を各部品に渡す。設定値の元はゲームのアセット（`VehicleAIAssemblyStageDefinition`）。
- 作業はどれも優先度（0〜255、標準 128、`AITask.Priority`）付きのスタックで、一番優先度の高い作業が動く。

### SlowTick（1 秒ごと、`CommanderAI.SlowTick` @ 182011B90）の流れ（確定）

1. 隊列（Formation）の後片付け。車両が操作可能な状態でなければ何もしない
2. `UpdateFormationTasks`（隊列で移動中ならそれに従う）
3. 乗員の指揮効率 `eff`（0〜1）から
   - 見回しの間隔 `ViewDirectionController.ScanPeriod = 1 − 0.5 × eff`（0.5〜1 秒）
   - 認識の角度の倍率 `RecognizedTargetBuffer.DetectionAngleMultiplier = 0.5 + 0.5 × eff`（0.5〜1）
4. 視線の状態機械（見回し / 目標を見る / 見失った目標を探す）を進める
5. 索敵の流れ: 見える車両 → 視野 → 認識 → 長期記憶 → 優先順位（[3. 索敵](#3-索敵)）
6. 味方の把握（200 m 以内）
7. **目標がいる** → `CombatUpdate`（作戦の選び直し）
8. **目標がいないが、生きている敵を覚えている** → 一番近い記憶の敵の最後の位置へ移動（作戦 Scout。半径 10 m、優先度 128）
9. **何も覚えていない** → 何もしない（地図の命令に従って動くか、止まっている）
10. 自分の位置の記録: `CombatStrategy.Position` から `PositionRadius` より離れたら「新しい陣地」として、その陣地での射撃数を 0 に、
    その陣地で撃ってよい数 `MaxAttacksFromCurrentPosition` を **2〜4 の乱数**にし直す

### FastTick（0.05 秒ごと、`CommanderAI.FastTick` @ 1820114C0）の流れ（確定）

1. 照準手ごとに `GunLayerAI.Step(車両の速度)`
2. 砲手ごとに `GunnerAI.Step`。どれかが「止まって撃ちたい」（RequestVehicleStop）なら
   - 交戦の仕方が StopToFire で、乗員の効率 > 0.5 で、前回の停止から **5 秒以上**（`MinStopToFireInterval`、const）なら停止（Halt）
3. 操縦手に `DriverAI.Step(フラグ = その場旋回を許可 | 停止中なら Halt)`

---

## 2. 作戦（CommanderPlan）

### 選び方（`CommanderAI.GetIdealCombatPlan` @ 1820151C0、`CombatUpdate` に同じ処理が埋め込み。確定）

入力は `CombatStrategy`（`CollectCombatInfo` が毎秒集める）:

| フィールド | 中身 |
|---|---|
| `NearbyActiveHostileCount` | 優先順位の付いた敵のうち **200 m 以内**（`NearbyRange`、const）の数 |
| `VisibleHostileVehicleCount` | 優先順位の付いた敵の数 |
| `NearbyAllyCount` / `NearbyActiveAllyCount` / `NearbyInactiveAllyCount` | 200 m 以内の味方 / そのうち生きている / 撃破された |
| `ReceivedPenetrations` / `ReceivedBlocks` | 被弾で貫通された / 防いだ回数（毎秒 0.1 ずつ減る） |
| `AllyLosses` | 味方の損失（毎秒 0.05 ずつ減る。**増やしている処理は見つからなかった**＝実質未使用、推定） |
| `Immobile` | 走行できない |
| `AttackApproachType` | 攻撃命令の「進み方」（Passive / Aggressive / Defensive / Balanced） |
| `AttacksFromCurrentPosition` / `MaxAttacksFromCurrentPosition` | 今の陣地で撃った数 / 撃ってよい数（2〜4） |
| `PositionEnterTime` | 今の陣地に着いた時刻 |

選択ルール（上から順に）:

| 条件 | 作戦 |
|---|---|
| 走行不能 | HoldPosition（その場で撃ち合う） |
| Passive | None（何もしない） |
| **Aggressive** | 200 m 以内に敵 → **Flank**、いない → **CloseDistance** |
| **Defensive** | 次のどれか → **FallBack**: ①味方がいて、撃破された味方 > 生きている味方 ②200 m 以内の敵 > 生きている味方 + 1 ③貫通されたことがある（減衰中の値 > 0）。それ以外 → HoldPosition |
| **Balanced**（カスタムバトルで実測） | 200 m 以内に敵 → **Flank**。今の陣地で撃った数 > 撃ってよい数（2〜4）→ **CloseDistance**。今の陣地で 1 発も撃たずに **5 秒**たった → **CloseDistance**。それ以外 → **HoldPosition** |

- 作戦か目標が変わったときだけ、新しい操縦の作業を作る（`CombatStrategy.NewPlan` → `CreateDriverEngagementTask` → `DriverAI.AddTask`）。
- 隊列に加わっている間は作戦を立てない（CancelPlan）。
- 実測（Balanced、4 分）: 敵を見つけた AI のほとんどが HoldPosition。CloseDistance が時々、Flank は 1 両、FallBack は 0。
  交戦距離が 200 m を超えることが多く、Flank の条件（200 m 以内）にほとんど入らないため。

### 作戦ごとの操縦（`CommanderAI.CreateDriverEngagementTask` @ 182013550。確定）

| 作戦 | 操縦の作業 | 数値 |
|---|---|---|
| HoldPosition | `FaceTargetTask`（目標の方を向いて止まる） | 向きの許容 **25°** |
| FallBack | `GetFallbackPosition` → `MoveToPositionTask`（**バックで**） | 敵と反対方向に **100 m**、今の車体の向きのまま（正面を敵に向けたまま後退）、到着半径 10 m |
| Flank | `FlankTargetTask` | 目標から見て **横 10 m・後ろ 20 m**（自分が今いる側）の地点へ移動、着いたら（5 m 以内）目標を向く（許容 **30°**）。移動前にまず目標の方を向く |
| CloseDistance | `MoveToPositionTask` | 目標の手前 **100 m** の地点（自分と目標を結ぶ線上）へ、到着半径 10 m、着いたら目標の方を向く |
| Scout | `MoveToPosition`（SlowTick から） | 記憶にある一番近い生きている敵の最後の位置へ、半径 10 m |

---

## 3. 索敵

### 流れ（確定）

1. **見える車両**（`DetectionBuffer.Update`）: 検出サービス（`DetectionRegister`）から範囲内の車両を集め、視線（LOS）を調べる。
   - 範囲 `ViewRange` = **1000 m**（インスタンス）
   - 車両ごとに外形に沿った「検出点」があり、距離に応じた数（LOD）の点へ視線を飛ばし、**見えた点の割合**が `VisibilityFraction`。
     地形や建物（遮蔽レイヤー）で隠れた部分は見えない ＝ 稜線射撃（ハルダウン）の車両は見えにくい
2. **視野**（`FieldOfVisionDetectionBuffer`）: 今見ている方向の **90°**（インスタンス）以内、1000 m 以内
3. **認識**（`RecognizedTargetBuffer.Update` @ 18201F4B0）: 見ている方向と敵の方向の角度（水平）が、次のしきい値より小さければ認識する

   しきい値 = 敵の速さによる角度 × `VisibilityFraction` × `DetectionAngleMultiplier`（0.5〜1、乗員の効率）

   | 敵の速さ | 角度（`ComputeDetectionAngleThreshold`） |
   |---|---|
   | 1 m/s 未満（ほぼ停止） | **5°** |
   | 5 m/s 未満 | 30° |
   | 10 m/s 未満 | 45° |
   | 10 m/s（36 km/h）以上 | 90° |

   止まっている敵は、ほぼ真正面を見たときにしか見つからない。動いている敵は視野の端でも見つかる。
   最近見た位置の履歴（15° 以内・60 秒、`AlertConeAngle` / `AlertPeriod`、const）も記録している。
4. **長期記憶**（`LongtermTargetBuffer`）: 見た敵の最後の位置・時刻、撃った弾の結果（命中・貫通・当たった面）を覚える。
   生きている / 活動中 / 燃えている（`TargetFeatures`）を推定。ある面への非貫通が **3 回**（const）で「その面は抜けない」の印を付けるが、
   **この印を読む処理は AI の中に見つからなかった**（目標の選び方にも作戦にも使われていない）
5. **優先順位**（`PrioritizedTargetBuffer` + `AdvancedTargetComparer.Compare` @ 18201F1B0）: 記憶で生きている敵だけを、
   **①燃えている敵を後回し ②近い順** に並べる。脅威度・損傷・貫通できるかは考えていない

### どこを見るか（`ViewDirectionController`。確定）

状態: Idle / Scanning（見回し）/ Targeting（目標を見る）/ TargetReacquisition（見失った目標を探す）

- **見回し**: 12 方向（30° 刻み）から、①最近見た方向ほど低く（ヒートマップ）②音のした方向を高く ③方向ごとの重み
  （`ScanDirectionWeights`、static）で一番高い方向を選び、なめらかに向ける。`ScanPeriod`（0.5〜1 秒）ごとに選び直す
- **目標を見る**: 一番優先度の高い目標の方向。見失ったら Reacquisition
- **探す**: 最後に見た位置を **5 秒**見て、だめなら見回しへ

### 音（`AINoiseRegister`。確定）

- 音を出すのは **AI の砲手が撃ったとき**（`GunnerAI.OnGunnerFired` に埋め込み）だけ。1 m で **160 dB**、
  距離で `160 − 20·log10(距離)`、時間で `− 20·log10(経過秒 + 1)` 減る
- **プレイヤーの車両には GunnerAI が無いので、プレイヤーの発砲は音にならない**（推定。プレイヤー側で音を登録する処理は見つからなかった）
- 被弾しても撃ってきた方を向く処理は無い（`OnRecievedHit` は貫通 / 非貫通の回数を数えるだけ）

### 使われていないもの（確定）

- `Concealment`（植生などによる隠蔽）: 呼び出し元が無い ＝ 草木は AI の索敵に影響しない

---

## 4. 照準（`FindAndAimAtTargetGunLayerTask.Update` @ 1820207B0。確定）

1. 目標が変わったら「捕捉からの時間」を 0 に。**捕捉から 1 秒**（`TargetAquireTime`、const）は照準しない（反応の遅れ）
2. この目標への「捕捉以降の射撃数」n を記憶から数え、**夾叉の誤差** z = `ZeroingErrors[min(n, 5)]`
   （static の配列 **[1, 0.5, 0.3, 0.2, 0.1, 0.025]**）。撃つたびに狙いが詰まる
3. 乱数の種 = 目標の ID と、その目標への命中数 → **当たるまでは同じ方向に外し続け、当たると誤差が変わる**
4. 距離の誤差 = 乱数(−z〜z) × `maxRangeError`（**0.1** = 10%、インスタンス）× `AccuracyModifier`（攻撃命令の値）。
   見積もった距離 = 実際の距離 × (1 + 距離の誤差)。視線方向に沿って狙う点がずれる
5. 速さの誤差 = 乱数(−z〜z) × `maxVelocityError`（**0.1**）× `AccuracyModifier`。相対速度 × (1 + 誤差) で見越す
6. 狙う点 = 最後に見た位置 + 速度 × 見てからの時間 + **半径 0.25 m（`MaxAimOffset`、const）の球内の乱数**
7. `Ballistics.CalculateInterceptPoint`（3 回反復）で見越し点、弾道（重力）を補正した照準の向きを出す

---

## 5. 射撃（`FireWhenAimedGunnerAITask.Update` @ 18201CA20、`GunnerAI.Step` @ 18201BC10。確定）

- **撃つ条件**: 照準のずれ < **0.6°**（`MaxFireAngleDeviation`、インスタンス。初期値は const 0.6）、かつ砲口から目標へのヒットスキャンで
  間に何も無い（地形・物・**別の敵・味方**に遮られていない）
- **反応時間**: 条件がそろってから **0.1〜0.5 秒の乱数**待って撃つ（`reactionFireTime`）
- **止まって撃つ要求**: 交戦の仕方が StopToFire のとき、ずれ < **20°**（const）で装填が半分以上進んでいれば停止を要求
  （実際に止まるかは FastTick の 5 秒ルール）
- 撃った弾は 16 発分（const）の記録に入れ、後で結果（外れ・貫通・非貫通・当たった面）を長期記憶に返す（`UpdatePreviousShotStates`）

---

## 6. 操縦（`DriverAI.Step` @ 182018200。確定）

1. 作業のスタックから一番優先度の高い作業を動かし、指示（目的地・向き・フラグ: Halt / ForceReverse / StopAndRotate / FaceDirection /
   MoveToDestination / PreferReverse）を受け取る
2. 目的地が変わったら NavMesh で経路（`NavMeshPath`）を計算。経路から 10 m（const）以上外れたら計算し直す
3. 次の角へ向かう。衝突回避は **16 本の光線・20 m**（const）を **8 ティックごと**（const）
4. 動き方: 通常 / **その場旋回**（目標の方向とのずれ > 80° で入り、< 10° で抜ける。旋回中の最高速度 1 m/s）
5. スロットル・ステアは感度カーブ（AnimationCurve）から。ステア角 5〜50°、スロットル距離 0.1〜5 m、予測 1 秒、
   後退は 120° 以上後ろ・20 m まで（すべてインスタンス、設定から）
6. 出力は操縦手の入力（`Driver.Solution` の Throttle / Steer）
- **詰まりからの回復が無い**: 設定に `stuckTime` / `stuckDistance` / `maxDestinationSpeed` があるが、`DriverAI.SetSettings` はこれらを
  コピーしていない（確定）。引っかかった AI は動けないまま

---

## 7. 命令とゲームモード

- AI が戦うのは**攻撃命令**を受けたとき（`CommanderAI.Attack` @ 182014BD0）。攻撃命令は
  - 進み方（`AttackApproachType`: Passive / Aggressive / Defensive / Balanced）と交戦の仕方（`AttackEngageMode`: FireOnTheMove / StopToFire）、
    優先度を `CombatStrategy` に入れ
  - 照準手に「Find and Aim」（`AccuracyModifier` = 命令の精度の倍率）、砲手に「Firing when aimed」の作業を渡す
- カスタムバトル・シナリオでは、攻撃命令は地図のミッションの段階イベント `AttackOrderStageEvent`
  （進み方・交戦の仕方・精度の倍率・目標の陣営）から出る。値は地図のアセットにある。カスタムバトルの実測は Balanced / StopToFire
- 開始時の「Moving to position」は地図の移動命令。敵を見つけるまでそのまま
- `CommanderAISettings.fireMode` は実測で HoldFire だったが、攻撃命令の作業はこれを見ずに撃つ（推定）

---

## 8. 値の一覧

| 値 | 初期値 | 種類 | 場所 |
|---|---|---|---|
| FastTick / SlowTick の間隔 | 0.05 / 1 秒 | const | `AIRegister` |
| 近くとみなす距離 | 200 m | const | `CommanderAI.NearbyRange` |
| 停止して撃つ最短の間隔 | 5 秒 | const | `CommanderAI.MinStopToFireInterval` |
| 陣地で撃ってよい数 | 2〜4（乱数） | インスタンス | `CombatStrategy.MaxAttacksFromCurrentPosition` |
| 撃たずに陣地を変えるまで | 5 秒 | コード内の値 | `GetIdealCombatPlan` |
| 前進・後退の距離 | 100 m | コード内の値 | `CreateDriverEngagementTask` |
| 回り込む位置 | 横 10 m・後ろ 20 m | コード内の値 | 同上（`FlankTargetTask.FlankOffset` はインスタンス） |
| 向きの許容 HoldPosition / Flank | 25° / 30° | コード内の値 | 同上 |
| 被弾の記憶の減り方 | 0.1 / 秒 | const | `CombatStrategy.ReceivedPenetrationDecay` |
| 視界・視野 | 1000 m / 90° | インスタンス | `DetectionBuffer.ViewRange` ほか |
| 認識の角度（速さ別） | 5 / 30 / 45 / 90° | コード内の値 | `RecognizedTargetBuffer.ComputeDetectionAngleThreshold` |
| 認識の角度の倍率 | 0.5〜1（乗員の効率） | インスタンス（毎秒上書き） | `RecognizedTargetBuffer.DetectionAngleMultiplier` |
| 見回しの方向の重み | — | static 配列 | `ViewDirectionController.ScanDirectionWeights` |
| 抜けない面とみなす非貫通数 | 3 | const | `LongtermTargetBuffer.ImpenetrableIneffectiveShotThreshold` |
| 捕捉から照準まで | 1 秒 | const | `FindAndAimAtTargetGunLayerTask.TargetAquireTime` |
| 夾叉の誤差 | [1, 0.5, 0.3, 0.2, 0.1, 0.025] | **static 配列** | `FindAndAimAtTargetGunLayerTask.ZeroingErrors` |
| 距離・速さの誤差 | 0.1 / 0.1 | インスタンス | `GunLayerAI.maxRangeError` / `maxVelocityError` |
| 狙う点のばらつき | 0.25 | const | `FindAndAimAtTargetGunLayerTask.MaxAimOffset` |
| 精度の倍率 | 1（命令による） | インスタンス | `GunLayerAITask.AccuracyModifier` |
| 撃つ照準のずれ | 0.6° | インスタンス | `FireWhenAimedGunnerAITask.MaxFireAngleDeviation` |
| 反応時間 | 0.1〜0.5 秒 | コード内の値 | `FireWhenAimedGunnerAITask.Update` |
| 停止要求のずれ・装填 | 20° / 半分 | const | 同上 |
| 衝突回避 | 16 本・20 m・8 ティック | const | `DriverAI` |
| その場旋回 入る / 抜ける | 80° / 10° | インスタンス | `DriverAI.stopAndRotateEnter` / `Exit` |
| 砲声 | 160 dB（1 m） | コード内の値 | `GunnerAI.OnGunnerFired` |

---

## 9. 見つかった弱点

1. **作戦がほぼ「その場で撃ち合う」だけ**（Balanced）。回り込みは 200 m 以内でしか起きず、後退は Defensive でしか選ばれない
2. **目標は「近い順」だけ**。脅威（自分を狙っている）、損傷、貫通できるかを考えない。「抜けない面」の判定は作っているのに使っていない
3. **撃たれても撃ってきた方を見ない**。プレイヤーの発砲は音にもならない（推定）ので、側面から撃たれても気付きにくい
4. **止まっている敵はほぼ真正面でしか見つからない**（5°）。見回しの向き次第で、近くの停止した敵を見落とす
5. **詰まりから回復しない**（stuck の設定が使われていない）
6. **人間離れした点**: 照準が合えば 0.1〜0.5 秒で撃ち、5 発で誤差がほぼ 0 になる。一方で動きは単調
7. 敵を見つけていない AI は地図の移動命令が終わると何もしない（記憶に敵がいなければ探しに行かない）
8. 草木（Concealment）は索敵に影響しない

---

## 10. MOD から変えるには

| やりたいこと | 方法 |
|---|---|
| 狙いの誤差・夾叉の速さ | インスタンス `GunLayerAI.maxRangeError` / `maxVelocityError`、static `FindAndAimAtTargetGunLayerTask.ZeroingErrors`、作業の `AccuracyModifier` を書き換え。全 AI は `AIRegister.register` → `CommanderAI.gunLayers` |
| 反応時間 | `FireWhenAimedGunnerAITask.Update` の後（Harmony の postfix）で `reactionFireTime` を延ばす、または `MaxFireAngleDeviation` を変える |
| 作戦の選び方 | `CommanderAI.CombatUpdate` を差し替え（`GetIdealCombatPlan` は CombatUpdate に埋め込まれているので、GetIdealCombatPlan だけを差し替えても効かない）。または `CombatStrategy.AttackApproachType` を書き換えて既存のルールを切り替え |
| 作戦ごとの動き | `CommanderAI.CreateDriverEngagementTask` の後で、できた作業（`FlankTargetTask.FlankOffset` など）を書き換え、または自前の作業を `DriverAI.AddTask` |
| 目標の選び方 | `AdvancedTargetComparer.Compare` を差し替え（`ITargetDetailSource` から長期記憶 `LongTargetInfo` が引ける） |
| 撃たれた方を向く | `CommanderAI.OnRecievedHit` の後で `ViewDirectionController` の状態・向き（`lookDirection` / `PrimaryTarget`）を変える |
| プレイヤーの発砲を音にする | プレイヤーの `CannonBehaviour.FireInternal` の後で `AINoiseRegister.Register(位置, 160)` を呼ぶ |
| 索敵 | `DetectionBuffer.ViewRange`、`FieldOfVisionDetectionBuffer.FieldOfVision` を書き換え。認識の角度は `RecognizedTargetBuffer.Update` の差し替えが必要（const） |
| 詰まりからの回復 | 自前で位置の変化を見て、詰まったら `DriverAI.AddTask` で後退・別ルートの作業を足す |
| AI に命令を出す | `CommanderAI.Order(Order)`（移動・攻撃・停止）。`MoveToPosition` / `Attack` / `Halt` |

注意:
- const の値はコードに埋め込まれているので、変えるにはそのメソッドを Harmony で差し替える
- ref / out の構造体引数（`DriverInstructions&` など）を持つメソッドへのパッチは、参照型を含まない構造体（blittable）ならよいが、
  参照型を含む構造体（`ShortTargetInfo` など）を interop で受け渡すとメモリが壊れる（`sprocket-modding-setup` のメモ参照）

---

## 11. 調べ方（再現）

```
dotnet build tools/disasm -c Release
tools/disasm/bin/Release/disasm.exe <出力> "^Sprocket\.ArtificialIntelligence\."    # AI の全メソッド（約 2.4 万行）
tools/disasm/bin/Release/disasm.exe <出力> "T:^Sprocket\.ArtificialIntelligence\."  # フィールドの位置・定数
tools/disasm/bin/Release/disasm.exe <出力> "D:183FAC660,1"                          # コード中の定数（例: 5.0）
tools/disasm/bin/Release/disasm.exe <出力> "X:<アドレス>"                            # 呼び出し元
```

主なメソッドのアドレス: `CommanderAI.SlowTick` 182011B90 / `FastTick` 1820114C0 / `CombatUpdate` 182012660 /
`GetIdealCombatPlan` 1820151C0 / `CreateDriverEngagementTask` 182013550 / `CollectCombatInfo` 182012990 /
`FindAndAimAtTargetGunLayerTask.Update` 1820207B0 / `FireWhenAimedGunnerAITask.Update` 18201CA20 / `GunnerAI.Step` 18201BC10 /
`DriverAI.Step` 182018200 / `RecognizedTargetBuffer.Update` 18201F4B0 / `AdvancedTargetComparer.Compare` 18201F1B0 /
`FlankTargetTask.Update` 1820212C0 / `CommanderAI.Attack` 182014BD0
