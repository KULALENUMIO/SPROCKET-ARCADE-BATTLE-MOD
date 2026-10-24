using System;
using System.Collections.Generic;
using Sprocket.Vehicles.Fires;
using UnityEngine;

namespace SprocketMods.Weapons;

/// <summary>
/// 機関砲の発砲煙を消す。
///
/// 発砲時の効果は MuzzleFlashEffect（Sprocket.Vehicles.Fires）1 つで、発砲音（AudioSource）と
/// 閃光・煙をまとめた 1 つの VFX グラフ（MediumCannonFire。外から変えられるのは Calibre / Propellant Volume /
/// Bore Length / High Detail / Wind / Inherited Velocity だけで、煙だけを止める値は無い）でできている。
/// 効果を丸ごと飛ばすと発砲音も消えるので、効果はそのまま再生させ、閃光の一瞬（MuzzleFlashTime）だけ見せた後に
/// VFX のレンダラーを隠す。煙は閃光より遅く広がって長く残るので、これで煙だけが見えなくなる。
///
/// 効果のインスタンスはプールされて他の砲でも使い回されるため、Setup の前に必ず表示を戻し、
/// 隠す予約は「その再生のとき」の番号が変わっていなければ実行する。
/// Setup は CannonBehaviour.FireInternal の中から同期的に呼ばれるので、どの砲の発射かは Autocannons の発射中の砲で分かる。
/// </summary>
internal static class AutocannonMuzzle
{
    private sealed class EffectState
    {
        public MuzzleFlashEffect Effect;
        public Renderer[] Renderers;
        public int Generation;
        public bool Hidden;
    }

    private struct PendingHide
    {
        public EffectState State;
        public int Generation;
        public float At;
    }

    private static readonly Dictionary<IntPtr, EffectState> States = new();
    private static readonly List<PendingHide> Pending = new();

    private static AutocannonConfig Cfg => Plugin.Autocannon;

    /// <summary>MuzzleFlashEffect.Setup の前。前回機関砲で隠していたら表示を戻す。</summary>
    public static void BeforeSetup(MuzzleFlashEffect effect)
    {
        if (effect == null || !States.TryGetValue(effect.Pointer, out var state))
            return;
        state.Generation++;
        if (state.Hidden)
            SetVisible(state, true);
    }

    /// <summary>MuzzleFlashEffect.Setup の後。機関砲の発射なら、閃光の後に隠す予約をする。</summary>
    public static void AfterSetup(MuzzleFlashEffect effect)
    {
        if (effect == null || !Cfg.NoMuzzleSmoke.Value || !Autocannons.IsFiringAutocannon)
            return;
        var state = GetState(effect);
        if (state.Renderers.Length == 0)
            return;

        var flashTime = Cfg.MuzzleFlashTime.Value;
        if (flashTime <= 0f)
        {
            SetVisible(state, false);
            return;
        }
        if (Pending.Count > 512)
            Pending.Clear();
        Pending.Add(new PendingHide { State = state, Generation = state.Generation, At = Time.time + flashTime });
    }

    /// <summary>毎フレーム。予約の時刻が来たものを隠す。</summary>
    public static void Update()
    {
        if (Pending.Count == 0)
            return;
        var now = Time.time;
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var p = Pending[i];
            if (now < p.At)
                continue;
            Pending.RemoveAt(i);
            // その後に別の発射で再利用されていたら、そちらの表示を邪魔しない
            if (p.State.Generation == p.Generation && p.State.Effect != null && !p.State.Effect.WasCollected)
                SetVisible(p.State, false);
        }
    }

    private static EffectState GetState(MuzzleFlashEffect effect)
    {
        if (States.TryGetValue(effect.Pointer, out var state) && state.Effect != null && !state.Effect.WasCollected)
            return state;
        if (States.Count > 512)
            States.Clear();

        Renderer[] renderers;
        try
        {
            var visual = effect.visualEffect;
            var found = visual != null
                ? visual.GetComponentsInChildren<Renderer>(true)
                : effect.GetComponentsInChildren<Renderer>(true);
            renderers = new Renderer[found?.Length ?? 0];
            for (var i = 0; i < renderers.Length; i++)
                renderers[i] = found[i];
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Autocannon: could not find muzzle effect renderers: {e.Message}");
            renderers = Array.Empty<Renderer>();
        }
        state = new EffectState { Effect = effect, Renderers = renderers };
        States[effect.Pointer] = state;
        return state;
    }

    private static void SetVisible(EffectState state, bool visible)
    {
        state.Hidden = !visible;
        foreach (var renderer in state.Renderers)
        {
            if (renderer != null && !renderer.WasCollected)
                renderer.enabled = visible;
        }
    }
}
