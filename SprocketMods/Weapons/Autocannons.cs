using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.WeaponFramework;
using UnityEngine;

namespace SprocketMods.Weapons;

/// <summary>機関砲の設定（[Autocannon] セクション）。</summary>
public class AutocannonConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<float> MagazineReloadTime;
    public readonly ConfigEntry<bool> NoRecoil;
    public readonly ConfigEntry<bool> ShowHud;
    public readonly ConfigEntry<bool> NoMuzzleSmoke;
    public readonly ConfigEntry<float> MuzzleFlashTime;

    public AutocannonConfig(ConfigFile cfg)
    {
        const string s = "Autocannon";
        Enabled = cfg.Bind(s, "Enabled", true,
            "名前に [AC 弾倉サイズ/毎分発射数]（例: [AC 10/450]）を含む砲を機関砲にする。砲の設計名・配置した砲の名前のどちらに書いてもよい");
        MagazineReloadTime = cfg.Bind(s, "MagazineReloadTime", 5f,
            new ConfigDescription("弾倉の交換時間（秒）。装填手が作業している間だけ進む", new AcceptableValueRange<float>(0.5f, 60f)));
        NoRecoil = cfg.Bind(s, "NoRecoil", true, "機関砲の反動（車体への衝撃・画面の揺れ・砲身の後退）をなくす");
        ShowHud = cfg.Bind(s, "ShowHud", true, "自車の機関砲の残弾と弾倉交換の進み具合を画面に表示する");
        NoMuzzleSmoke = cfg.Bind(s, "NoMuzzleSmoke", true, "機関砲の発砲煙を出さない（発砲音と一瞬の閃光は残す）");
        MuzzleFlashTime = cfg.Bind(s, "MuzzleFlashTime", 0.05f,
            new ConfigDescription("機関砲の閃光を見せる時間（秒）。この後に発砲の VFX を隠して煙を消す。0 なら閃光も出さない",
                new AcceptableValueRange<float>(0f, 0.3f)));
    }
}

/// <summary>機関砲 1 門ぶんの状態。</summary>
internal sealed class AutocannonState
{
    public string Name;
    public int MagazineSize;
    public float RoundsPerMinute;
    public int Rounds;
    public float NextShotTime;
    public float ReloadElapsed;

    public float Interval => 60f / RoundsPerMinute;
}

/// <summary>
/// 機関砲。ゲームの砲（Cannon / CannonBehaviour）をそのまま使い、装填の扱いだけを変える。
///
/// ゲームの仕組み（調査結果）:
///  - 砲手は射撃ボタンが押されている間、毎回「CanFire なら Fire()」を行う。CanFire は装填済みかどうかだけを見る。
///  - Fire → CannonBehaviour.FireInternal が弾を撃ち、WeaponBehaviour.RequestReload で LoadTask に再装填を頼む。
///  - LoadTask.Update が装填手の作業量で準備→取り出し→運搬→装填と進め、終わると SetState(Loaded) で弾薬庫の弾を込める。
///
/// 機関砲では:
///  - 弾倉に残弾がある間は RequestReload を止める（装填済みのまま）。CanFire に発射間隔の制限を足して連射速度にする。
///  - 弾倉が空になったら通常どおり再装填させる。弾薬庫の砲弾 1 発が 1 弾倉で、装填時間は固定（既定 5 秒）。
///  - 反動は、発射イベントを受けて車体に衝撃を与える Cannon.OnFire と画面を揺らす VehicleController.OnWeaponFire を飛ばし、
///    砲身の後退アニメーションを止めて消す。
/// AI の砲手も同じ処理を通るので、AI 車両の機関砲も同じルールで動く。
/// </summary>
internal static unsafe class Autocannons
{
    // [AC 10/450]、[AC:10/450rpm]、【AC 10／450】 など。大文字小文字は区別しない
    private static readonly Regex Tag = new(
        @"[\[【［]\s*AC\s*[:：]?\s*(\d{1,4})\s*[/／]\s*(\d{1,5})\s*(?:rpm)?\s*[\]】］]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaxMagazine = 1000;
    private const float MinRpm = 1f;
    private const float MaxRpm = 3000f;
    private const float RecheckInterval = 2f;

    // LoadTask.target（ILoadable、実体は WeaponBehaviour）
    private const int LoadTaskTarget = 0x18;

    private sealed class Entry
    {
        public AutocannonState State;
        public float NextCheck;
    }

    private static readonly Dictionary<IntPtr, Entry> Entries = new();
    private static IntPtr _firing;

    private static AutocannonConfig Cfg => Plugin.Autocannon;

    /// <summary>いま FireInternal の中で発射している砲が機関砲か（発射に伴う効果の処理から使う）。</summary>
    public static bool IsFiringAutocannon => _firing != IntPtr.Zero && Cfg.Enabled.Value && Get(_firing, null) != null;

    /// <summary>機関砲ならその状態、そうでなければ null。名前は数秒ごとに読み直す（設計画面での変更に追従）。</summary>
    public static AutocannonState Get(WeaponBehaviour weapon)
    {
        if (weapon == null || !Cfg.Enabled.Value)
            return null;
        return Get(weapon.Pointer, weapon);
    }

    private static AutocannonState Get(IntPtr pointer, WeaponBehaviour weapon)
    {
        if (pointer == IntPtr.Zero)
            return null;
        var now = Time.unscaledTime;
        if (Entries.TryGetValue(pointer, out var entry) && now < entry.NextCheck)
            return entry.State;

        if (entry == null)
        {
            if (Entries.Count > 512)
                Entries.Clear();
            entry = new Entry();
            Entries[pointer] = entry;
        }
        entry.NextCheck = now + RecheckInterval;
        weapon ??= new Il2CppSystem.Object(pointer).TryCast<WeaponBehaviour>();
        entry.State = Evaluate(weapon, entry.State);
        return entry.State;
    }

    private static AutocannonState Evaluate(WeaponBehaviour weapon, AutocannonState previous)
    {
        var cannon = weapon?.mount?.TryCast<Cannon>();
        if (cannon == null)
            return null;

        string name = null;
        Match match = null;
        foreach (var candidate in Names(cannon))
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            var m = Tag.Match(candidate);
            if (m.Success)
            {
                name = candidate;
                match = m;
                break;
            }
        }
        if (match == null)
            return null;

        var magazine = Math.Clamp(int.Parse(match.Groups[1].Value), 1, MaxMagazine);
        var rpm = Mathf.Clamp(float.Parse(match.Groups[2].Value), MinRpm, MaxRpm);
        if (previous != null && previous.MagazineSize == magazine && Mathf.Approximately(previous.RoundsPerMinute, rpm))
            return previous;

        var state = previous ?? new AutocannonState { Rounds = magazine };
        state.Name = name;
        state.MagazineSize = magazine;
        state.RoundsPerMinute = rpm;
        state.Rounds = Math.Min(state.Rounds, magazine);
        Plugin.Logger.LogInfo($"Autocannon: '{name}' magazine {magazine}, {rpm:0} rpm");
        return state;
    }

    private static IEnumerable<string> Names(Cannon cannon)
    {
        string instanceName = null, designName = null;
        try
        {
            instanceName = cannon.InstanceBlueprint?.Name;
        }
        catch (Exception)
        {
            // 配置情報の無い砲
        }
        try
        {
            designName = cannon.Blueprint?.Name;
        }
        catch (Exception)
        {
            // 設計情報の無い砲
        }
        yield return instanceName;
        yield return designName;
    }

    private static AutocannonState ForLoadTask(LoadTask task)
    {
        if (task == null || !Cfg.Enabled.Value)
            return null;
        return Get(*(IntPtr*)(task.Pointer + LoadTaskTarget), null);
    }

    // ------------------------------------------------------------------ 発射

    /// <summary>CannonBehaviour.FireInternal の前。中で呼ばれる RequestReload がどの砲の発射によるものかを覚える。</summary>
    public static void BeforeFire(WeaponBehaviour weapon, out IntPtr previous)
    {
        previous = _firing;
        _firing = weapon.Pointer;
    }

    /// <summary>CannonBehaviour.FireInternal の後。次弾の発射時刻を決め、砲身の後退アニメーションを消す。</summary>
    public static void AfterFire(CannonBehaviour weapon, IntPtr previous)
    {
        _firing = previous;
        var state = Get(weapon);
        if (state == null)
            return;

        // 毎フレームの判定でも平均の連射速度が保たれるように、予定時刻に間隔を足していく（遅れすぎたらやり直す）
        var now = Time.time;
        var interval = state.Interval;
        state.NextShotTime = now - state.NextShotTime <= Mathf.Max(interval, 0.1f)
            ? state.NextShotTime + interval
            : now + interval;

        if (Cfg.NoRecoil.Value)
        {
            var routine = weapon.recoilRoutine;
            if (routine != null)
                weapon.StopCoroutine(routine);
            weapon.recoilRoutine = null;
            weapon.ResetRecoil();
        }
    }

    /// <summary>WeaponBehaviour.RequestReload の前。弾倉に残弾があれば再装填させない（false で元の処理を飛ばす）。</summary>
    public static bool BeforeRequestReload(WeaponBehaviour weapon)
    {
        if (weapon.Pointer != _firing)
            return true;
        var state = Get(weapon);
        if (state == null)
            return true;
        state.Rounds--;
        if (state.Rounds > 0)
            return false;
        state.Rounds = 0;
        state.ReloadElapsed = 0f;
        return true;
    }

    /// <summary>WeaponBehaviour.CanFire の後。発射間隔が空くまでは撃てないことにする。</summary>
    public static bool FilterCanFire(WeaponBehaviour weapon, bool canFire)
    {
        if (!canFire)
            return false;
        var state = Get(weapon);
        return state == null || Time.time >= state.NextShotTime;
    }

    /// <summary>
    /// 反動を与える発射イベントの受け手（車体の衝撃・画面の揺れ）を飛ばすか。
    /// 発射イベントは FireInternal の中で同期的に流れるので、まず発射中の砲で判断する
    /// （車両側で中継されたイベントは sender が砲ではないことがある）。
    /// </summary>
    public static bool SkipRecoil(Il2CppSystem.Object sender)
    {
        if (!Cfg.NoRecoil.Value || !Cfg.Enabled.Value)
            return false;
        if (_firing != IntPtr.Zero && Get(_firing, null) != null)
            return true;
        return sender != null && Get(sender.Pointer, null) != null;
    }

    // ------------------------------------------------------------------ 弾倉の交換

    /// <summary>
    /// LoadTask.Update の代わり。装填手の作業（運搬・操作の速さ）がある間だけ時間を進め、固定時間で装填を終える。
    /// 弾薬庫からの取り出しと弾種の設定はゲームの UpdateRequest / SetState がそのまま行う。
    /// </summary>
    public static bool ReplaceLoadTaskUpdate(LoadTask task, float travelSpeed, float manipulationSpeed, float deltaTime)
    {
        var state = ForLoadTask(task);
        if (state == null)
            return true;

        if (task.currentRequest != LoadRequest.None)
            task.UpdateRequest();
        if (task.state != LoadState.Loading)
            return false;

        if (travelSpeed > 0f || manipulationSpeed > 0f)
            state.ReloadElapsed += deltaTime;
        if (state.ReloadElapsed >= Cfg.MagazineReloadTime.Value)
            task.SetState(LoadState.Loaded);
        return false;
    }

    /// <summary>LoadTask.SetState の後。装填が終わったら弾倉を満たす。</summary>
    public static void AfterSetState(LoadTask task, LoadState newState)
    {
        var state = ForLoadTask(task);
        if (state == null)
            return;
        if (newState == LoadState.Loaded)
        {
            state.Rounds = state.MagazineSize;
            state.ReloadElapsed = 0f;
        }
        else if (newState == LoadState.Loading)
        {
            state.ReloadElapsed = 0f;
        }
    }

    /// <summary>ゲームの装填バーと AI が見る装填の進み具合を、弾倉交換の時間に合わせる。</summary>
    public static float FilterLoadFraction(LoadTask task, float fraction)
    {
        var state = ForLoadTask(task);
        if (state == null || task.state != LoadState.Loading)
            return fraction;
        return Mathf.Clamp01(state.ReloadElapsed / Cfg.MagazineReloadTime.Value);
    }

    public static float FilterRemainingTime(LoadTask task, float remaining)
    {
        var state = ForLoadTask(task);
        if (state == null || task.state != LoadState.Loading)
            return remaining;
        return Mathf.Max(0f, Cfg.MagazineReloadTime.Value - state.ReloadElapsed);
    }
}
