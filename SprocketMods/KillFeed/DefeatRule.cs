using System;
using Sprocket.MissionFramework;
using UnityEngine;

namespace SprocketMods.KillFeed;

/// <summary>
/// 対戦の勝敗を「撃破（乗員の全滅・誘爆で消えた・放棄）」だけで決める（[KillFeed] DestroyedOnly）。
///
/// ゲームの対戦（DeathmatchGameMode）は ImmobilizeObjective で、陣営の車両のうち走行可能（VehicleFlags.Mobile）な台数が
/// minMobileCount を下回ったら完了（負け）にする（数え方: 合計が 1 台以上で、走行可能な台数 &lt; minMobileCount なら Completed）。
/// 走行不能は修理（Repair）で直るので、その後処理で、KillTracker が知っている同じ陣営の車両を「残っている / 撃破された」に分けて数え直す。
///  ※ ゲームの車両の一覧（IObjectiveEntityRegister.GetEntity）は out の値型（ObjectiveEntity、Transform を含む）を返すため、
///    こちらから呼ぶとランタイムごと落ちる。呼ばずに KillTracker の記録で数える。
///  ※ 陣営以外の絞り込み（GroupID / BuildID / SpawnerID）があるもの（シナリオなど）はゲームの判定のまま。
/// </summary>
internal static class DefeatRule
{
    private const int Incomplete = 1;
    private const int Completed = 2;
    private static float _nextLog;

    public static void AfterUpdate(ImmobilizeObjective objective, ref ObjectiveResult result)
    {
        if (!Plugin.KillFeed.DestroyedOnly.Value || objective == null || KillTracker.Instance == null)
            return;
        var filter = objective.filter;
        if ((int)filter.GroupID != 0 || filter.BuildID != -1 || filter.SpawnerID != -1)
            return;
        KillTracker.Instance.CountTeam((int)filter.TeamID, out var standing, out var fallen);
        // まだ車両を見つけていない（試合の始め）ならゲームの判定のまま
        if (standing + fallen == 0)
            return;
        var game = (int)result;
        var ours = standing < objective.minMobileCount ? Completed : Incomplete;
        result = (ObjectiveResult)ours;
        if (ours != game && Time.unscaledTime >= _nextLog)
        {
            _nextLog = Time.unscaledTime + 10f;
            Plugin.Logger.LogInfo($"Defeat rule: team {filter.TeamID} {(game == Completed ? "not defeated yet" : "defeated")} " +
                                  $"(standing {standing}, destroyed {fallen}, needed {objective.minMobileCount})");
        }
    }
}
