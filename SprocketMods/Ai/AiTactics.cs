using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.Orders;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace SprocketMods.Ai;

/// <summary>
/// AI 改善の段階 2: 戦術（docs/ai-design.md §3.1〜3.3・3.8）。
///  - ゲームの作戦選び（CombatUpdate に埋め込み）は、直前に集める戦況（CollectCombatInfo）を書き換えて常に
///    「その場で撃ち合う」（HoldPosition = 目標を向いて止まる）にする。突撃（CloseDistance: 目標の手前 100 m へ）と、
///    目標の真後ろ 20 m への回り込み（Flank）はさせない。
///  - 動く判断は MOD が車両ごとに 1 秒ごとにする。動くときはゲームの MoveToPositionTask を、攻撃命令より高い優先度で
///    操縦手の作業に積む（着いたら終わり、下のゲームの作業に戻る）。状態:
///      交戦（撃ち合う）→ 同じ場所で 2〜4 発撃った・撃たれた・射線が通らない → 陣地転換（横か少し後ろの、射線の通る位置へ。
///      ハルダウンになる位置を好む）→ 交戦
///      乗員が半分以下・短い間に何度も貫通された・数で大きく負けている → 後退（敵を向いたまま、射線の切れる後ろへバック）
///      → 立て直し（その場で修理などを待つ）→ 交戦
///      目標の今見えている面を抜けないと分かった（ゲームが 3 発の非貫通で付ける印）・味方が正面を押さえている → 回り込み
///      （目標の横へ。陣営ごとに 1〜2 両まで）→ 交戦
///  - 敵が見えないとき（見失って 8 秒、誰も見ていなければ 15 秒）は、味方が見ている敵 → 最後に見た位置 → 敵陣の方へ
///    100 m ずつ前進して探す。隊の一番前の車両（プレイヤーを含む）より 80 m 以上は前に出ない（交互に前進する）。
///  - 動くのは前方 ±40° へ前進するか、後方 ±40° へ正面を向けたまま後退するかだけ（横へ走って側面や背面を見せない）。
///  - 目標の選び方: ゲームが並べた優先順位（燃えていない・近い順）の後で、撃ってきた相手・抜ける相手・味方が撃っている相手・
///    今の目標を優先して、一番を先頭へ入れ替える。
/// ゲームの構造体（ShortTargetInfo など参照を含むもの）は interop で受け渡すと壊れるので、メモリから直接読む。
/// </summary>
internal sealed partial class AiDirector
{
    private enum Tactic : byte
    {
        Engage,
        Reposition,
        FallBack,
        Recover,
        Flank,
        Advance,
    }

    private sealed partial class Unit
    {
        public Tactic Tactic;
        public float TacticSince;
        public float NextTactic;
        public MoveToPositionTask Move;
        public Vector3 MoveGoal;
        public float MoveDeadline;
        public int OriginalApproach = -1; // 攻撃命令の進み方（書き換える前）
        public bool ApproachOverridden;
        public int HostilesNearby;
        public int AlliesNearby;
        public readonly Queue<float> Penetrations = new();
        public float HitAt = float.NegativeInfinity;
        public IntPtr ThreatBody;
        public float FallBackCooldown;
        public Vector3 PreviousPosition = new(float.NaN, 0f, 0f);
        public bool HasTarget;
        public float TargetSeen = float.NegativeInfinity;
        public float LastContact;
        public bool Advancing; // 敵を探して前進中（見つけるまで続けて進む）
        public float WaitingSince = float.NaN; // 味方が追いつくのを待ち始めた時刻
        public string AdvanceNote = "";
        public string TacticReason = ""; // 今の戦術に移った理由（数字を除いたもの。開発用）
        public int TargetEntity;
        public Vector3 TargetPos;
        public IntPtr TargetTransform;
    }

    // 攻撃命令の進み方（Sprocket.Orders.AttackApproachType）
    private const int ApproachPassive = 0;
    private const int ApproachDefensive = 2;

    private readonly Dictionary<IntPtr, Unit> _byTargetBuffer = new();
    private int _layerMask;
    private NavMeshPath _path;

    private bool Tactics => Active && Cfg.Tactics.Value;

    // ------------------------------------------------------------------ ゲームの作戦を「その場で撃ち合う」に（CollectCombatInfo の後）

    /// <summary>
    /// CombatUpdate は集めた戦況から作戦を選ぶ。進み方を Defensive にして、後退の条件（味方の損失・数の不利・貫通された）を
    /// 0 にすると、選ばれるのは HoldPosition になる。消す前の値は自分の判断に使う。
    /// </summary>
    public void AfterCollectCombatInfo(CommanderAI commander, CombatStrategy combat)
    {
        if (combat == null || !_units.TryGetValue(commander.Pointer, out var unit))
            return;
        var approach = (int)combat.AttackApproachType;
        if (!unit.ApproachOverridden || approach != ApproachDefensive)
            unit.OriginalApproach = approach;
        if (!Tactics || unit.OriginalApproach == ApproachPassive)
        {
            RestoreApproach(unit, combat);
            return;
        }
        unit.HostilesNearby = combat.NearbyActiveHostileCount;
        unit.AlliesNearby = combat.NearbyActiveAllyCount;
        // 貫通された回数（OnRecievedHit で 1 ずつ増え、毎秒 0.1 減る）。毎回 0 にするので、残っていればこの 1 秒の分
        var penetrations = Mathf.RoundToInt(combat.ReceivedPenetrations);
        for (var i = 0; i < penetrations; i++)
            unit.Penetrations.Enqueue(Time.time);

        combat.AttackApproachType = (AttackApproachType)ApproachDefensive;
        combat.NearbyAllyCount = 0;
        combat.NearbyActiveHostileCount = 0;
        combat.ReceivedPenetrations = 0f;
        unit.ApproachOverridden = true;
    }

    private static void RestoreApproach(Unit unit, CombatStrategy combat)
    {
        if (!unit.ApproachOverridden)
            return;
        unit.ApproachOverridden = false;
        if (unit.OriginalApproach >= 0 && combat != null)
            combat.AttackApproachType = (AttackApproachType)unit.OriginalApproach;
    }

    private void RestoreTactics(Unit unit)
    {
        CancelMove(unit);
        unit.Tactic = Tactic.Engage;
        try
        {
            RestoreApproach(unit, unit.Commander.combat);
        }
        catch (Exception)
        {
            // 車両が消えた
        }
    }

    // ------------------------------------------------------------------ 状態の見直し（毎秒）

    private void UpdateTactics(float now)
    {
        if (!Tactics)
        {
            foreach (var unit in _units.Values)
            {
                if (unit.Move != null || unit.ApproachOverridden)
                    RestoreTactics(unit);
            }
            return;
        }
        foreach (var unit in _units.Values)
        {
            if (now < unit.NextTactic)
                continue;
            unit.NextTactic = now + 1f;
            var body = unit.Body;
            if (body == null || body.WasCollected || unit.Abandoned || unit.Driver == null)
                continue;
            var combat = unit.Commander.combat;
            if (combat == null || unit.OriginalApproach == ApproachPassive)
                continue;
            while (unit.Penetrations.Count > 0 && now - unit.Penetrations.Peek() > 20f)
                unit.Penetrations.Dequeue();
            // 撃破済み（乗員全滅など）の車両は何もしない
            if (unit.Vehicle != null && KillFeed.KillTracker.Instance?.IsKnockedOut(unit.Vehicle.Pointer) == true || CrewFraction(unit) <= 0f)
            {
                CancelMove(unit);
                continue;
            }
            ReadTarget(unit);
            if (combat.Immobile || unit.Driver.Immobilized)
            {
                if (unit.Move != null)
                    SetTactic(unit, Tactic.Engage, now, "immobilized");
                CancelMove(unit);
                continue;
            }
            if (unit.Move != null && !UpdateMove(unit, now))
                continue;

            switch (unit.Tactic)
            {
                case Tactic.Recover:
                    if (now - unit.TacticSince >= 12f && !IsRepairing(unit))
                        SetTactic(unit, Tactic.Engage, now, "recovered");
                    break;
                case Tactic.Engage:
                    Engage(unit, combat, now);
                    break;
                default:
                    SetTactic(unit, Tactic.Engage, now, "arrived");
                    break;
            }
        }
    }

    /// <summary>移動中の確認。終わったら true（次の判断へ進む）。</summary>
    private bool UpdateMove(Unit unit, float now)
    {
        var move = unit.Move;
        var flat = unit.Body.position - unit.MoveGoal;
        flat.y = 0f;
        var arrived = move.Finished || flat.magnitude < 6f;
        // 回り込みの途中で貫通されたら、そこで陣地転換に切り替える
        if (!arrived && unit.Tactic == Tactic.Flank && unit.Penetrations.Count > 0 && unit.HitAt > unit.TacticSince)
        {
            CancelMove(unit);
            SetTactic(unit, Tactic.Engage, now, "flank under fire");
            return true;
        }
        if (!arrived && now < unit.MoveDeadline)
            return false;
        CancelMove(unit);
        if (unit.Tactic == Tactic.FallBack)
        {
            SetTactic(unit, Tactic.Recover, now, arrived ? "in cover" : $"fall back timed out {flat.magnitude:0} m short");
            return false;
        }
        SetTactic(unit, Tactic.Engage, now, arrived ? "arrived" : $"move timed out {flat.magnitude:0} m short");
        return false;
    }

    private void Engage(Unit unit, CombatStrategy combat, float now)
    {
        SubscribeDeaths();
        var visible = unit.HasTarget && now - unit.TargetSeen < 3f;
        if (IsAlive(unit))
            RecordBattleSecond(unit, visible, 1f);
        if (visible)
        {
            unit.LastContact = now;
            unit.Advancing = false;
        }

        // 後退: 乗員が半分以下・20 秒で 2 回以上貫通された・近くの敵が味方より 3 両以上多い
        if (unit.HasTarget && now >= unit.FallBackCooldown)
        {
            var crew = CrewFraction(unit);
            var reason = crew <= 0.5f ? $"crew {crew:P0}"
                : unit.Penetrations.Count >= 2 ? $"penetrated x{unit.Penetrations.Count}"
                : unit.HostilesNearby >= unit.AlliesNearby + 3 ? $"outnumbered {unit.HostilesNearby}:{unit.AlliesNearby}"
                : null;
            if (reason != null && TryFallBack(unit, now, reason))
                return;
        }

        // 見えていない: しばらくしたら前進して探す（その場で見えない目標を向いて待ち続けない）
        if (!visible)
        {
            // 前進の途中なら 2 秒見回してすぐ次へ（止まっては進むを繰り返さない）
            var quiet = now - Mathf.Max(unit.LastContact, unit.TacticSince);
            if (quiet >= (unit.Advancing ? 2f : unit.HasTarget ? 8f : 15f))
                TryAdvance(unit, now);
            return;
        }

        if (Level == 0)
            return; // 新兵は陣地を変えない

        var distance = Flat(unit.TargetPos - unit.Body.position).magnitude;
        // 回り込み: 見えている面を抜けない目標で、近くの味方が正面を押さえている
        if (now - unit.TacticSince > 8f && distance > 120f && distance < 800f && FacingImpenetrable(unit) && TryFlank(unit, now, distance))
            return;

        // 陣地転換
        var shots = combat.AttacksFromCurrentPosition;
        // 遠い撃ち合い（450 m 超）で撃たれていなければ、同じ位置から倍まで撃つ（横に数十 m 動いても相手の狙いはほぼ変わらず、
        // 動く間は撃てないので損になる）
        var maxShots = combat.MaxAttacksFromCurrentPosition;
        if (distance > 450f && now - unit.HitAt > 15f)
            maxShots *= 2;
        if (shots > 0 && shots >= maxShots)
            TryReposition(unit, now, $"{shots} shots from here", false);
        else if (unit.HitAt > unit.TacticSince && now - unit.HitAt < 3f && !ShooterInSight(unit) && (Level >= 2 || _random.NextDouble() < 0.5))
            TryReposition(unit, now, "hit by unseen", false); // 撃ってきた相手が見えていれば、動かずに撃ち返す（動くと当たらず、狙われ続ける）
        else if (shots == 0 && combat.TimeSincePositionEnter > 10f && now - unit.TacticSince > 10f)
            TryReposition(unit, now, "no line of fire", true);
    }

    /// <summary>撃ってきた相手（弾の出どころ）が、今見えている目標か。</summary>
    private static bool ShooterInSight(Unit unit) =>
        unit.HasTarget && Time.time - unit.TargetSeen < 2f && Flat(unit.ThreatPos - unit.TargetPos).magnitude < 12f;

    /// <summary>車両ごとの状態（調整用、Log = true のとき 1 分ごと）。</summary>
    private void LogTactics()
    {
        foreach (var line in DescribeUnits())
            Plugin.Logger.LogInfo($"AI unit: {line}");
    }

    /// <summary>生きている AI の車両ごとの状態（開発用のコマンド status からも使う）。</summary>
    public IEnumerable<string> DescribeUnits()
    {
        var now = Time.time;
        var lines = new List<string>();
        foreach (var unit in _units.Values)
        {
            if (!IsAlive(unit))
                continue;
            var combat = unit.Commander.combat;
            var task = unit.Driver?.CurrentTask;
            lines.Add($"{unit.Name} team={unit.Team} tactic={unit.Tactic} {now - unit.TacticSince:0}s approach={unit.OriginalApproach} " +
                      $"order prio={combat?.AttackOrderPriority} target={(unit.HasTarget ? $"{Flat(unit.TargetPos - unit.Body.position).magnitude:0}m seen {now - unit.TargetSeen:0}s ago" : "none")} " +
                      $"task={task?.Name ?? "none"}({task?.Priority}) speed={unit.Body.velocity.magnitude:0.0} immobile={unit.Driver?.Immobilized} crew={CrewFraction(unit):P0}" +
                      (unit.AdvanceNote.Length > 0 ? $" advance: {unit.AdvanceNote}" : ""));
        }
        return lines;
    }

    /// <summary>開発用（hitcam proxy）: 陣営の番号がいちばん小さい陣営の、生きている AI の車両。</summary>
    public List<IntPtr> ProxyTeam()
    {
        var bodies = new List<IntPtr>();
        int? team = null;
        foreach (var unit in _units.Values)
        {
            if (IsAlive(unit) && (team == null || (int)unit.Team < team))
                team = (int)unit.Team;
        }
        foreach (var unit in _units.Values)
        {
            if (IsAlive(unit) && (int)unit.Team == team)
                bodies.Add(unit.BodyPtr);
        }
        return bodies;
    }

    private static bool IsAlive(Unit unit) =>
        unit.Body != null && !unit.Body.WasCollected && !unit.Abandoned && unit.Vehicle != null && !unit.Vehicle.WasCollected
        && !SprocketMods.Debug.DebugConsole.IsHidden(unit.Body)
        && KillFeed.KillTracker.Instance?.IsKnockedOut(unit.Vehicle.Pointer) != true && CrewFraction(unit) > 0f;

    /// <summary>陣営ごとの生きている AI の数（開発用）。</summary>
    public Dictionary<string, int> CountAlive()
    {
        var counts = new Dictionary<string, int>();
        foreach (var unit in _units.Values)
        {
            var key = unit.Team.ToString();
            counts.TryGetValue(key, out var n);
            counts[key] = n + (IsAlive(unit) ? 1 : 0);
        }
        return counts;
    }

    private readonly Dictionary<string, int> _tacticCounts = new();

    public void ResetTacticCounts()
    {
        _tacticCounts.Clear();
        _deathCounts.Clear();
        ResetBattleStats();
    }

    // 撃破されたときに何をしていたか（開発用: 自動対戦の評価）
    private readonly Dictionary<string, int> _deathCounts = new();
    private bool _deathsSubscribed;

    private void SubscribeDeaths()
    {
        if (_deathsSubscribed || KillFeed.KillTracker.Instance == null)
            return;
        KillFeed.KillTracker.Instance.Killed += OnUnitKilled;
        _deathsSubscribed = true;
    }

    private void OnUnitKilled(KillFeed.KillEvent e)
    {
        if (e.Kind == KillFeed.KillKind.Immobilized)
            return;
        foreach (var unit in _units.Values)
        {
            if (unit.Vehicle == null || unit.Vehicle.WasCollected || unit.Vehicle.Pointer != e.VictimPointer || unit.Body == null)
                continue;
            var now = Time.time;
            var visible = unit.HasTarget && now - unit.TargetSeen < 3f;
            var allies = 0;
            foreach (var other in _units.Values)
            {
                if (other != unit && other.Team == unit.Team && IsAlive(other) && Flat(other.Body.position - unit.Body.position).magnitude < 150f)
                    allies++;
            }
            var key = $"{unit.Tactic} ({unit.TacticReason}) {(unit.Body.velocity.magnitude > 1f ? "moving" : "still")}, " +
                      $"{(visible ? "saw enemy" : "no enemy in sight")}, allies<150m {(allies >= 2 ? "2+" : allies.ToString())}";
            _deathCounts.TryGetValue(key, out var n);
            _deathCounts[key] = n + 1;
            break;
        }
    }

    public IEnumerable<string> DescribeDeaths() =>
        _deathCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Value,3} x died while {p.Key}");

    public IEnumerable<string> DescribeTacticCounts() =>
        _tacticCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Value,4} x {p.Key}");

    private void SetTactic(Unit unit, Tactic tactic, float now, string reason)
    {
        if (unit.Tactic != tactic)
        {
            // 理由の先頭（数字より前）でまとめて数える
            // 理由の数字（距離・秒・割合）を除いてまとめる
            var key = $"{unit.Tactic} -> {tactic} ({Regex.Replace(Regex.Replace(reason, @"[\d.%:x]+(m|s)?\b", string.Empty), @"\s+", " ").Trim(' ', ',')})";
            _tacticCounts.TryGetValue(key, out var n);
            _tacticCounts[key] = n + 1;
        }
        unit.TacticReason = Regex.Replace(Regex.Replace(reason, @"[\d.%:x]+(m|s)?\b", string.Empty), @"\s+", " ").Trim(' ', ',');
        if (Cfg.Log.Value && unit.Tactic != tactic)
            Plugin.Logger.LogInfo($"AI tactic: {unit.Name} {unit.Tactic} -> {tactic} ({reason})");
        unit.Tactic = tactic;
        unit.TacticSince = now;
    }

    // ------------------------------------------------------------------ 動き

    /// <summary>
    /// 射線の通る別の位置へ移る。横へ走ると側面を見せるので、前方 ±40° へ前進するか、後方 ±40° へ正面を向けたまま
    /// 後退する位置だけを候補にする（どちらも横へずれる）。射線が通らないときは前方だけ。
    /// </summary>
    private bool TryReposition(Unit unit, float now, string reason, bool forwardOnly)
    {
        var position = unit.Body.position;
        var forward = Flat(unit.TargetPos - position).normalized;
        var right = new Vector3(forward.z, 0f, -forward.x);
        var best = Vector3.zero;
        var bestScore = float.MinValue;
        var bestHullDown = false;
        var bestReverse = false;
        var angles = new[] { -40f, -25f, 25f, 40f };
        var distances = forwardOnly ? new[] { 20f, 40f } : new[] { 15f, 28f };
        foreach (var reverse in forwardOnly ? new[] { false } : new[] { true, false })
        {
            foreach (var angle in angles)
            {
                foreach (var step in distances)
                {
                    var dir = Quaternion.Euler(0f, angle, 0f) * (reverse ? -forward : forward);
                    if (!TrySample(unit, position + dir * step, out var ground))
                        continue;
                    if (!HasLineOfFire(unit, ground, unit.TargetPos, out var hullDown))
                        continue;
                    var offset = Flat(ground - position);
                    var score = 100f + (hullDown ? 35f : 0f) + Mathf.Abs(Vector3.Dot(offset, right)) * 0.8f - offset.magnitude * 0.3f
                                + (reverse ? 10f : 0f) + (float)_random.NextDouble() * 8f;
                    score -= CrowdPenalty(unit, ground);
                    if (!float.IsNaN(unit.PreviousPosition.x) && Flat(ground - unit.PreviousPosition).magnitude < 10f)
                        score -= 40f; // さっきの陣地へ戻らない
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = ground;
                        bestHullDown = hullDown;
                        bestReverse = reverse;
                    }
                }
            }
        }
        if (bestScore == float.MinValue)
            return false;
        unit.PreviousPosition = position;
        StartMove(unit, best, AngledFacing(unit, best), bestReverse, 25f, now);
        SetTactic(unit, Tactic.Reposition, now, $"{reason}{(bestReverse ? ", backing" : "")}{(bestHullDown ? ", hull-down spot" : "")}");
        return true;
    }

    /// <summary>
    /// 敵を探して前進する。行き先は 味方が今見ている敵 → 自分が最後に見た位置 → 敵陣の方向。1 回に 100 m まで、
    /// 隊の一番前の味方より 80 m 以上は前に出ない。
    /// </summary>
    private bool TryAdvance(Unit unit, float now)
    {
        var position = unit.Body.position;
        Vector3 goal;
        string what;
        if (TryTeamContact(unit, out goal))
            what = "ally contact";
        else if (unit.HasTarget)
        {
            goal = unit.TargetPos;
            what = $"last seen {now - unit.TargetSeen:0}s ago";
        }
        else if (TryEnemySide(unit, out goal))
            what = "enemy side";
        else
        {
            unit.AdvanceNote = "no destination";
            return false;
        }
        var to = Flat(goal - position);
        var distance = to.magnitude;
        if (distance < 60f)
        {
            // 着いたのに見えない（丘・建物の陰）: 敵のいる位置が見える場所へ回り込む
            if (what != "enemy side" && TrySightOf(unit, goal, now, what))
                return true;
            unit.AdvanceNote = $"{what} reached";
            return false;
        }
        var dir = to / distance;
        var step = Mathf.Min(what == "enemy side" ? 150f : 100f, distance - 40f);
        var room = LeadRoom(unit, dir);
        if (room < 25f)
        {
            // 味方が追いつくのを待つ。来ない（交戦中・走行不能）なら 20 秒で待つのをやめる
            if (float.IsNaN(unit.WaitingSince))
                unit.WaitingSince = now;
            if (now - unit.WaitingSince < 20f)
            {
                unit.AdvanceNote = $"waiting for team ({now - unit.WaitingSince:0}s)";
                return false;
            }
        }
        else
            step = Mathf.Min(step, room);
        // 混んでいない場所を優先し、どこも混んでいれば混雑を許す。届かなければ半分の距離でも試す
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var length in new[] { step, step * 0.5f })
            {
                if (length < 20f)
                    continue;
                foreach (var angle in new[] { 0f, -20f, 20f, -40f, 40f, -60f, 60f })
                {
                    var candidate = position + Quaternion.Euler(0f, angle, 0f) * dir * length;
                    if (!TrySample(unit, candidate, out var ground) || pass == 0 && CrowdPenalty(unit, ground) > 0f)
                        continue;
                    StartMove(unit, ground, dir, false, 15f + length / 3f, now);
                    unit.Advancing = true;
                    unit.WaitingSince = float.NaN;
                    unit.AdvanceNote = "";
                    SetTactic(unit, Tactic.Advance, now, $"{what}, {distance:0} m away");
                    return true;
                }
            }
        }
        unit.AdvanceNote = $"no path toward {what}";
        return false;
    }

    /// <summary>
    /// 敵のいる位置 goal（味方が見ている・最後に見た）が見える場所へ動く。goal の周り 40〜90 m の輪の上で、
    /// 自分に近い側から順に、地面があって goal までの射線が通る点を探す。
    /// </summary>
    private bool TrySightOf(Unit unit, Vector3 goal, float now, string what)
    {
        var position = unit.Body.position;
        var away = Flat(position - goal);
        var bearing = away.sqrMagnitude > 1f ? Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg : unit.Body.rotation.eulerAngles.y;
        foreach (var radius in new[] { 45f, 70f, 95f })
        {
            foreach (var offset in new[] { 0f, -30f, 30f, -60f, 60f, -90f, 90f })
            {
                var candidate = goal + Quaternion.Euler(0f, bearing + offset, 0f) * Vector3.forward * radius;
                if (!TrySample(unit, candidate, out var ground) || !HasLineOfFire(unit, ground, goal, out _))
                    continue;
                if (CrowdPenalty(unit, ground) > 0f && offset != 0f)
                    continue;
                var face = Flat(goal - ground).normalized;
                StartMove(unit, ground, face, false, 12f + Flat(ground - position).magnitude / 3f, now);
                unit.Advancing = true;
                unit.AdvanceNote = "";
                SetTactic(unit, Tactic.Advance, now, $"sight of {what}");
                return true;
            }
        }
        return false;
    }

    /// <summary>味方（同じ陣営の AI）が今見ている敵のうち、自分に一番近いもの。</summary>
    private bool TryTeamContact(Unit unit, out Vector3 goal)
    {
        goal = default;
        var now = Time.time;
        var best = float.MaxValue;
        foreach (var other in _units.Values)
        {
            if (other == unit || other.Team != unit.Team || !other.HasTarget || now - other.TargetSeen > 3f)
                continue;
            var d = Flat(other.TargetPos - unit.Body.position).magnitude;
            if (d < best)
            {
                best = d;
                goal = other.TargetPos;
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>
    /// 方向 dir へ何 m 進んでよいか。隊の一番前の味方（プレイヤーを含む、撃破されていない車両）より 120 m 以上は前に出ない。
    /// 味方がいなければ制限なし。
    /// </summary>
    private float LeadRoom(Unit unit, Vector3 dir)
    {
        var position = unit.Body.position;
        var lead = float.NegativeInfinity;
        var tracker = KillFeed.KillTracker.Instance;
        foreach (var vehicle in _vehicles.Values)
        {
            if (vehicle == null || vehicle.WasCollected || vehicle.Pointer == unit.Vehicle?.Pointer)
                continue;
            TeamID team;
            try
            {
                team = vehicle.ID.TeamID;
            }
            catch (Exception)
            {
                continue;
            }
            if (team != unit.Team || tracker?.IsKnockedOut(vehicle.Pointer) == true || SprocketMods.Debug.DebugConsole.IsHidden(vehicle.Rigidbody))
                continue;
            // 走行不能の味方は前に来られないので、隊の位置に数えない
            var body = vehicle.Rigidbody;
            if (body != null && _byBody.TryGetValue(body.Pointer, out var ally) && ally.Driver != null && ally.Driver.Immobilized)
                continue;
            lead = Mathf.Max(lead, Vector3.Dot(Flat(vehicle.transform.position - position), dir));
        }
        return float.IsNegativeInfinity(lead) ? float.MaxValue : lead + 120f;
    }

    private bool TryFallBack(Unit unit, float now, string reason)
    {
        var position = unit.Body.position;
        var threat = unit.HitAt > now - 20f ? unit.ThreatPos : unit.TargetPos;
        var away = -Flat(threat - position).normalized;
        if (away.sqrMagnitude < 0.5f)
            return false;
        var right = new Vector3(away.z, 0f, -away.x);
        var best = Vector3.zero;
        var bestScore = float.MinValue;
        foreach (var back in new[] { 25f, 40f })
        {
            foreach (var lateral in new[] { -15f, 0f, 15f })
            {
                if (!TrySample(unit, position + away * back + right * lateral, out var ground))
                    continue;
                // 射線の切れる（撃ってきた方から見えない）位置を好む
                var hidden = !HasLineOfFire(unit, ground, threat, out _);
                var score = (hidden ? 80f : 0f) - Flat(ground - position).magnitude * 0.5f - CrowdPenalty(unit, ground);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = ground;
                }
            }
        }
        if (bestScore == float.MinValue)
            return false;
        unit.FallBackCooldown = now + 40f;
        // 敵を向いたまま下がる
        StartMove(unit, best, Flat(threat - best).normalized, true, 30f, now);
        SetTactic(unit, Tactic.FallBack, now, reason);
        return true;
    }

    private bool TryFlank(Unit unit, float now, float distance)
    {
        // 回り込むのは陣営ごとに 1 両（エースは 2 両）まで、近くに撃ち合っている味方がいるときだけ
        var limit = Level >= 3 ? 2 : 1;
        var flanking = 0;
        var holding = false;
        var position = unit.Body.position;
        foreach (var other in _units.Values)
        {
            if (other == unit || other.Team != unit.Team || other.Body == null || other.Body.WasCollected)
                continue;
            if (other.Tactic == Tactic.Flank)
                flanking++;
            else if (other.HasTarget && Flat(other.Body.position - position).magnitude < 300f)
                holding = true;
        }
        if (flanking >= limit || !holding)
            return false;
        if (!TryGetTargetFrame(unit, out var targetForward))
            return false;
        var targetRight = new Vector3(targetForward.z, 0f, -targetForward.x);
        var side = Vector3.Dot(position - unit.TargetPos, targetRight) >= 0f ? 1f : -1f;
        var radius = Mathf.Clamp(distance * 0.6f, 120f, 300f);
        foreach (var angle in new[] { 75f, 60f, 90f })
        {
            var dir = Quaternion.Euler(0f, side * angle, 0f) * targetForward;
            var candidate = unit.TargetPos + dir * radius;
            if (Flat(candidate - position).magnitude > 400f || !TrySample(unit, candidate, out var ground))
                continue;
            if (!HasLineOfFire(unit, ground, unit.TargetPos, out _))
                continue;
            StartMove(unit, ground, Flat(unit.TargetPos - ground).normalized, false, 70f, now);
            SetTactic(unit, Tactic.Flank, now, $"front not penetrable, flanking {angle:0}°");
            return true;
        }
        return false;
    }

    private void StartMove(Unit unit, Vector3 goal, Vector3 face, bool reverse, float timeout, float now)
    {
        CancelMove(unit);
        var objective = new PositionTarget(goal, face, 4f, 0.5f);
        var task = new MoveToPositionTask(ref objective);
        task.ForceReverse = reverse;
        // 攻撃命令より上、かつ地図の移動命令（標準 128）より上にする（攻撃命令が届く前は AttackOrderPriority が 0）
        var priority = unit.Commander.combat?.AttackOrderPriority ?? 0;
        task.Priority = (byte)Math.Min(AITask.TopPriority, Math.Max(priority + 10, AITask.NeutralPriority + 20));
        unit.Driver.AddTask(task);
        unit.Move = task;
        unit.MoveGoal = goal;
        unit.MoveDeadline = now + timeout;
    }

    private static void CancelMove(Unit unit)
    {
        if (unit.Move == null)
            return;
        try
        {
            if (!unit.Move.WasCollected)
                unit.Move.Finished = true;
        }
        catch (Exception)
        {
            // 車両ごと消えた
        }
        unit.Move = null;
    }

    /// <summary>止まって撃つ向き。正面を 20° ずらして構え、装甲の実質の厚さを稼ぐ（新兵はしない）。</summary>
    private Vector3 AngledFacing(Unit unit, Vector3 from)
    {
        var dir = Flat(unit.TargetPos - from).normalized;
        if (Level == 0)
            return dir;
        return Quaternion.Euler(0f, _random.NextDouble() < 0.5 ? -20f : 20f, 0f) * dir;
    }

    // ------------------------------------------------------------------ 地形と射線

    /// <summary>
    /// 候補の位置の地面を求め、今の位置から NavMesh の経路で行けるか確かめる
    /// （このビルドでは NavMesh.SamplePosition が削られているので、地面への光線と CalculatePath で代わりにする）。
    /// </summary>
    private bool TrySample(Unit unit, Vector3 point, out Vector3 ground)
    {
        ground = point;
        var mask = LayerMask();
        if (mask == 0 || !Physics.Raycast(point + Vector3.up * 60f, Vector3.down, out var hit, 160f, mask, QueryTriggerInteraction.Ignore))
            return false;
        ground = hit.point;
        _path ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(unit.Body.position, ground, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
            return false;
        var corners = _path.corners;
        return corners != null && corners.Length > 0 && Flat(corners[corners.Length - 1] - ground).magnitude < 4f;
    }

    /// <summary>位置 ground（地面）の砲塔の高さから目標へ射線が通るか。車体の下半分が目標から隠れるならハルダウン。</summary>
    private bool HasLineOfFire(Unit unit, Vector3 ground, Vector3 target, out bool hullDown)
    {
        hullDown = false;
        var mask = LayerMask();
        if (mask == 0)
            return true;
        var height = Mathf.Clamp(unit.Driver.LocalBounds.size.y, 1.5f, 4f);
        var eye = ground + Vector3.up * (height * 0.85f);
        var aim = target + Vector3.up * 1.2f;
        if (Blocked(eye, aim, mask))
            return false;
        hullDown = Blocked(aim, ground + Vector3.up * (height * 0.35f), mask);
        return true;
    }

    /// <summary>地形・建物で遮られているか（車両に当たったものは一時的なので遮りとみなさない）。</summary>
    private static bool Blocked(Vector3 from, Vector3 to, int mask)
    {
        return Physics.Linecast(from, to, out var hit, mask, QueryTriggerInteraction.Ignore) && hit.rigidbody == null;
    }

    private int LayerMask()
    {
        if (_layerMask == 0)
        {
            var main = Object.FindAnyObjectByType(Il2CppType.Of<Sprocket.DamageModelling.ProjectilesMain>())?.TryCast<Sprocket.DamageModelling.ProjectilesMain>();
            if (main != null)
                _layerMask = main.ProjectileLayerMask.value;
        }
        return _layerMask;
    }

    /// <summary>味方と 12 m 以内に寄らない。</summary>
    private float CrowdPenalty(Unit unit, Vector3 point)
    {
        var penalty = 0f;
        foreach (var other in _units.Values)
        {
            if (other == unit || other.Team != unit.Team || other.Body == null || other.Body.WasCollected)
                continue;
            var near = Flat(other.Body.position - point).magnitude;
            if (other.Move != null)
                near = Mathf.Min(near, Flat(other.MoveGoal - point).magnitude);
            if (near < 12f)
                penalty += 60f;
        }
        return penalty;
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    // ------------------------------------------------------------------ 乗員・修理

    private static float CrewFraction(Unit unit)
    {
        var repair = Repair.RepairSystem.Instance;
        if (repair == null || unit.Vehicle == null || !repair.TryGetCrew(unit.Vehicle, out var alive, out var total) || total == 0)
            return 1f;
        return alive / (float)total;
    }

    private static bool IsRepairing(Unit unit) =>
        unit.Vehicle != null && Repair.RepairSystem.Instance?.IsRepairing(unit.Vehicle) == true;

    /// <summary>AI が修理を始めてよいか（Repair/RepairSystem.cs から）。撃たれている最中や移動中は、先に下がってから直す。</summary>
    public bool AllowRepair(VehicleBehaviour vehicle)
    {
        if (!Tactics || vehicle == null)
            return true;
        var body = vehicle.Rigidbody;
        if (body == null || !_byBody.TryGetValue(body.Pointer, out var unit))
            return true;
        if (unit.Driver != null && unit.Driver.Immobilized)
            return true; // 走れないなら、その場で直すしかない
        if (unit.Move != null)
            return false;
        return unit.Tactic == Tactic.Recover || Time.time - unit.HitAt > 8f;
    }

    // ------------------------------------------------------------------ 目標（メモリから読む）

    // IL2CPP の配列: 要素数 +0x18、要素 +0x20
    private const int ArrayLength = 0x18;
    private const int ArrayData = 0x20;
    // PrioritizedTargetBuffer: buffer（ShortTargetInfo[]）+0x10、validTargetCount +0x18
    private const int PrioritizedBuffer = 0x10;
    private const int PrioritizedCount = 0x18;
    // ShortTargetInfo（0x20 バイト）: associatedObjects（DetectedObject[]）+0、Index +8、VisibleCentroid +0x10
    private const int ShortSize = 0x20;
    private const int ShortObjects = 0x0;
    private const int ShortIndex = 0x8;
    private const int ShortTime = 0xC; // 最後に見た時刻（Time.time）
    private const int ShortCentroid = 0x10;
    // DetectedObject（0x20 バイト）: Transform +0、EntityID +0x18
    private const int DetectedSize = 0x20;
    private const int DetectedTransform = 0x0;
    private const int DetectedEntity = 0x18;
    // LongtermTargetBuffer: buffer（LongTargetInfo[]）+0x18。LongTargetInfo（0x28 バイト）: entityID +8、features +0x20
    private const int LongBuffer = 0x18;
    private const int LongSize = 0x28;
    private const int LongEntity = 0x8;
    private const int LongFeatures = 0x20;
    // CombatStrategy.PlanTargetID
    private const int CombatPlanTarget = 0x49;

    private const byte FeatureOnFire = 4;
    private const byte FeatureFrontImpenetrable = 8;
    private const byte FeatureSideImpenetrable = 16;
    private const byte FeatureRearImpenetrable = 32;

    private static unsafe bool TryReadEntry(byte* buffer, int index, out int entity, out Vector3 centroid, out IntPtr transform)
    {
        entity = 0;
        transform = IntPtr.Zero;
        var entry = buffer + ArrayData + index * ShortSize;
        centroid = *(Vector3*)(entry + ShortCentroid);
        var objects = *(byte**)(entry + ShortObjects);
        var objectIndex = *(int*)(entry + ShortIndex);
        if (objects == null || objectIndex < 0 || objectIndex >= *(int*)(objects + ArrayLength))
            return false;
        var detected = objects + ArrayData + objectIndex * DetectedSize;
        entity = *(int*)(detected + DetectedEntity);
        transform = *(IntPtr*)(detected + DetectedTransform);
        return true;
    }

    /// <summary>今の目標（優先順位の先頭）を読む。</summary>
    private static unsafe void ReadTarget(Unit unit)
    {
        unit.HasTarget = false;
        var targets = unit.Commander.prioritizedTargets;
        if (targets == null)
            return;
        var owner = (byte*)targets.Pointer;
        var buffer = *(byte**)(owner + PrioritizedBuffer);
        if (buffer == null || *(int*)(owner + PrioritizedCount) <= 0 || *(int*)(buffer + ArrayLength) <= 0)
            return;
        if (!TryReadEntry(buffer, 0, out var entity, out var centroid, out var transform))
            return;
        unit.HasTarget = true;
        unit.TargetSeen = *(float*)(buffer + ArrayData + ShortTime);
        unit.TargetEntity = entity;
        unit.TargetTransform = transform;
        unit.TargetPos = transform != IntPtr.Zero ? new Transform(transform).position : centroid;
    }

    private static bool TryGetTargetFrame(Unit unit, out Vector3 forward)
    {
        forward = Vector3.forward;
        if (unit.TargetTransform == IntPtr.Zero)
            return false;
        forward = Flat(new Transform(unit.TargetTransform).forward).normalized;
        return forward.sqrMagnitude > 0.5f;
    }

    /// <summary>目標の、自分に向いている面（正面 / 側面 / 背面）を、ゲームが「抜けない」と記録しているか。</summary>
    private static bool FacingImpenetrable(Unit unit)
    {
        if (!TryGetTargetFrame(unit, out var targetForward))
            return false;
        var features = ReadFeatures(unit, unit.TargetEntity);
        var angle = Vector3.Angle(targetForward, Flat(unit.Body.position - unit.TargetPos));
        var face = angle < 45f ? FeatureFrontImpenetrable : angle > 135f ? FeatureRearImpenetrable : FeatureSideImpenetrable;
        return (features & face) != 0;
    }

    private static unsafe byte ReadFeatures(Unit unit, int entity)
    {
        var memory = unit.Commander.longtermTargetBuffer;
        if (memory == null)
            return 0;
        var buffer = *(byte**)((byte*)memory.Pointer + LongBuffer);
        if (buffer == null)
            return 0;
        var length = *(int*)(buffer + ArrayLength);
        for (var i = 0; i < length; i++)
        {
            var entry = buffer + ArrayData + i * LongSize;
            if (*(int*)(entry + LongEntity) == entity)
                return entry[LongFeatures];
        }
        return 0;
    }

    private static unsafe int PlanTarget(Unit unit)
    {
        var combat = unit.Commander.combat;
        return combat == null ? int.MinValue : *(int*)((byte*)combat.Pointer + CombatPlanTarget);
    }

    /// <summary>ゲームが優先順位を並べた後（PrioritizedTargetBuffer.Update の後）に、一番狙うべき目標を先頭へ入れ替える。</summary>
    public unsafe void AfterPrioritize(PrioritizedTargetBuffer targets)
    {
        if (!Tactics || !Cfg.TargetPriority.Value || targets == null || !_byTargetBuffer.TryGetValue(targets.Pointer, out var unit))
            return;
        var owner = (byte*)targets.Pointer;
        var buffer = *(byte**)(owner + PrioritizedBuffer);
        if (buffer == null)
            return;
        var count = Math.Min(*(int*)(owner + PrioritizedCount), *(int*)(buffer + ArrayLength));
        if (count < 2 || unit.Body == null || unit.Body.WasCollected)
            return;
        var now = Time.time;
        var current = PlanTarget(unit);
        var best = 0;
        var bestScore = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadEntry(buffer, i, out var entity, out var centroid, out var transform))
                continue;
            var score = ScoreTarget(unit, entity, transform != IntPtr.Zero ? new Transform(transform) : null, centroid, current, now);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        if (best != 0)
            SwapEntries(buffer, 0, best);
    }

    private float ScoreTarget(Unit unit, int entity, Transform transform, Vector3 centroid, int current, float now)
    {
        var position = transform != null ? transform.position : centroid;
        var score = -Flat(position - unit.Body.position).magnitude * 0.25f;
        var features = ReadFeatures(unit, entity);
        if ((features & FeatureOnFire) != 0)
            score -= 150f;
        if (entity == current)
            score += 120f; // ころころ変えない
        // 撃ってきた相手
        if (unit.ThreatBody != IntPtr.Zero && now - unit.HitAt < 20f && _vehicles.TryGetValue(entity, out var vehicle)
            && vehicle != null && !vehicle.WasCollected && vehicle.Rigidbody?.Pointer == unit.ThreatBody)
            score += 300f;
        // 自分に向いている面を抜けない相手は後回し
        if (transform != null)
        {
            var angle = Vector3.Angle(Flat(transform.forward), Flat(unit.Body.position - position));
            var face = angle < 45f ? FeatureFrontImpenetrable : angle > 135f ? FeatureRearImpenetrable : FeatureSideImpenetrable;
            if ((features & face) != 0)
                score -= 250f;
        }
        // 味方が撃っている相手（集中射撃）
        var focus = 0;
        foreach (var other in _units.Values)
        {
            if (other != unit && other.Team == unit.Team && other.HasTarget && other.TargetEntity == entity)
                focus++;
        }
        score += Mathf.Min(focus, 2) * 50f;
        return score;
    }

    /// <summary>ShortTargetInfo を 2 つ入れ替える。参照（associatedObjects）は GC の書き込みバリアを通す。</summary>
    private static unsafe void SwapEntries(byte* buffer, int a, int b)
    {
        var pa = buffer + ArrayData + a * ShortSize;
        var pb = buffer + ArrayData + b * ShortSize;
        var objectsA = *(IntPtr*)pa;
        var objectsB = *(IntPtr*)pb;
        for (var i = sizeof(IntPtr); i < ShortSize; i++)
            (pa[i], pb[i]) = (pb[i], pa[i]);
        IL2CPP.il2cpp_gc_wbarrier_set_field((IntPtr)buffer, (IntPtr)pa, objectsB);
        IL2CPP.il2cpp_gc_wbarrier_set_field((IntPtr)buffer, (IntPtr)pb, objectsA);
    }

    private void RegisterTargetBuffer(Unit unit)
    {
        var targets = unit.Commander.prioritizedTargets;
        if (targets != null)
            _byTargetBuffer[targets.Pointer] = unit;
    }
}
