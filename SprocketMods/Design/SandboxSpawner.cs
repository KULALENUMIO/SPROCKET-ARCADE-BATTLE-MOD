using System;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.GameControl;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Exhausts;
using Sprocket.Vehicles.Missions;
using Sprocket.Vehicles.Serialization;
using Sprocket.Vehicles.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;
using CancellationTokenSource = Il2CppSystem.Threading.CancellationTokenSource;
using Il2CppTasks = Il2CppSystem.Threading.Tasks;

namespace SprocketMods.Design;

internal sealed class SandboxSpawnerConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyboardShortcut> Key;

    public SandboxSpawnerConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Sandbox", "SpawnMenu", true, "サンドボックスでキーを押すと、設計した車両の一覧から標的の車両を選べる");
        Key = cfg.Bind("Sandbox", "SpawnMenuKey", new KeyboardShortcut(KeyCode.Z), "車両の一覧を開くキー");
    }
}

/// <summary>
/// サンドボックスの車両召喚。ゲームには入力（Sandbox/OpenSpawnMenu = Z）だけが残り、処理は無くなっている。
/// 一覧はゲームの設計図選択（VehicleBlueprintSelector）を使い、選んだ車両を「設計中の車両と同じものが自動で出る」
/// 標的（VehicleSource.Mode == EditorExitSave の StageVehicleSpawnEvent）と置き換える。
/// 消す手順は StageVehicleSpawnEvent.StageReset、生成後の手順は SpawnAsync と同じ。
/// ゲームが車両を消すのはリセットのときだけで、そこではエフェクトも全部消している。走行中に 1 両だけ消すので、
/// その車両のエフェクト（排気など）を先に解放する。残すと EffectRegister.Update が毎フレーム例外を出し、
/// GameController.SceneUpdate の続きが止まる。描画の結合（VehicleRendererAtlaser）も走行の開始時に作り、終了時に
/// 片付ける作りなので、消えた車両の分を終了前に取り除き（残すと設計画面へ戻れない）、新しい車両は結合に入れない。
/// </summary>
internal sealed class SandboxSpawner
{
    private Il2CppTasks.Task<IVehicleBlueprint> _selecting;
    private Il2CppTasks.Task<Il2CppReferenceArray<VehicleSpawn>> _spawning;
    private StageVehicleSpawnEvent _target;
    private CancellationTokenSource _cancel;
    private float _timeScale = 1f;
    private CursorLockMode _cursorLock;
    private bool _cursorVisible;
    private float _sweepUntil = float.NegativeInfinity;
    private VehicleRendererAtlaser _atlaser;

    /// <summary>一覧を開いている間と生成の間は VehicleControlPlayerState の更新を止める（Patches.cs）。背景を固定し、途中で走行を終えさせない。</summary>
    public static bool Selecting { get; private set; }

    private static SandboxSpawnerConfig Cfg => Plugin.SandboxSpawner;

    public void Update()
    {
        if (_selecting != null)
        {
            // 走行中はカーソルが固定されているので、選んでいる間は外す（ゲームが戻さないよう毎フレーム）
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (!_selecting.IsCompleted)
                return;
            var task = _selecting;
            _selecting = null;
            Time.timeScale = _timeScale;
            Cursor.lockState = _cursorLock;
            Cursor.visible = _cursorVisible;
            OnSelected(task);
            Selecting = _spawning != null;
            return;
        }
        if (Time.unscaledTime < _sweepUntil)
            SweepDeadEffects();
        if (_spawning != null)
        {
            if (_spawning.IsCompleted)
                OnSpawned();
            return;
        }
        if (Cfg.Enabled.Value && SprocketMods.UI.KeyInput.Down(Cfg.Key.Value) && IsDrivingInSandbox())
            Open();
    }

    private static bool IsDrivingInSandbox()
    {
        if (!SceneManager.GetActiveScene().name.StartsWith("Sandbox", StringComparison.OrdinalIgnoreCase))
            return false;
        var mode = UnityEngine.Object.FindObjectOfType<GameController>()?.gamemode?.TryCast<ScenarioGameMode>();
        return mode?.activeState?.TryCast<MissionScenarioGameState>() != null;
    }

    private static StageVehicleSpawnEvent FindTarget()
    {
        foreach (var e in UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>())
        {
            if (e.blueprintSource != null && e.blueprintSource.Mode == SpawnMode.EditorExitSave)
                return e;
        }
        return null;
    }

    private void Open()
    {
        _target = FindTarget();
        if (_target == null)
        {
            Plugin.Logger.LogWarning("Spawn menu: no vehicle spawner to replace in this scene");
            return;
        }
        _cancel = new CancellationTokenSource();
        var directory = _target.blueprintSource.GetDirectory(GameDataDirectory.CurrentFaction);
        _timeScale = Time.timeScale;
        _cursorLock = Cursor.lockState;
        _cursorVisible = Cursor.visible;
        Time.timeScale = 0f;
        Selecting = true;
        _selecting = VehicleBlueprintSelector.Select(directory, "Spawn vehicle", _cancel.Token);
    }

    private void OnSelected(Il2CppTasks.Task<IVehicleBlueprint> task)
    {
        if (task.IsFaulted || task.IsCanceled)
        {
            Plugin.Logger.LogWarning($"Spawn menu: selection did not finish: {task.Exception?.ToString() ?? "canceled"}");
            return;
        }
        var blueprint = task.Result;
        if (blueprint == null || _target == null || _target.WasCollected || _target.spawner == null)
            return;

        var old = _target.spawns;
        var count = old != null && old.Length > 0 ? old.Length : 1;
        var available = _target.GetSpawnInstances();
        if (available == null || available.Length == 0)
        {
            Plugin.Logger.LogWarning("Spawn menu: no spawn position");
            return;
        }
        count = Math.Min(count, available.Length);
        var instances = new Il2CppStructArray<VehicleSpawnInstance>(count);
        for (var i = 0; i < count; i++)
            instances[i] = available[i];

        if (old != null)
        {
            foreach (var spawn in old)
                Remove(spawn);
            _sweepUntil = Time.unscaledTime + 2f;
        }
        _target.spawns = null;
        Plugin.Logger.LogInfo($"Spawn menu: spawning {blueprint.Header?.Name} x{count}");
        _atlaser = VehicleRendererAtlaser.active;
        VehicleRendererAtlaser.active = null;
        _spawning = _target.spawner.Spawn(blueprint, instances, VehicleSpawnOptions.None, _cancel.Token);
    }

    private void Remove(VehicleSpawn spawn)
    {
        var vehicle = spawn?.Vehicle;
        if (vehicle == null)
            return;
        var behaviour = vehicle.TryCast<IVehicleGateway>()?.Behaviour;
        var body = behaviour?.Rigidbody;
        if (body != null)
            ReleaseEffects(body);
        if (behaviour != null)
            Teams.TeamStrengthTracker.Instance?.Forget(IL2CPP.Il2CppObjectBaseToPtr(behaviour.Cast<Il2CppSystem.Object>()));
        _target.register?.Deregister(vehicle);
        spawn.Dispose();
    }

    // EffectRegister.instanceBuffer は EffectInstance（参照を含む 16 バイトの構造体）の配列なのでメモリから読む
    private const int ArrayData = 0x20;
    private const int ArrayLength = 0x18;
    private const int InstanceSize = 0x10;
    private const int InstanceFlags = 0x8;
    private const byte FlagActive = 1;

    private static unsafe void ForEachEffect(Action<EffectRegister, int, Effect> visit)
    {
        var register = UnityEngine.Object.FindObjectOfType<GameController>()?.effectRegister;
        var buffer = register?.instanceBuffer;
        if (buffer == null)
            return;
        var array = (byte*)IL2CPP.Il2CppObjectBaseToPtrNotNull(buffer);
        var length = *(int*)(array + ArrayLength);
        for (var i = 0; i < length; i++)
        {
            var element = array + ArrayData + i * InstanceSize;
            var effect = *(IntPtr*)element;
            if ((element[InstanceFlags] & FlagActive) != 0 && effect != IntPtr.Zero)
                visit(register, i, new Effect(effect));
        }
    }

    private static unsafe void ClearInstance(EffectRegister register, int index)
    {
        var array = (byte*)IL2CPP.Il2CppObjectBaseToPtrNotNull(register.instanceBuffer);
        array[ArrayData + index * InstanceSize + InstanceFlags] = 0;
    }

    /// <summary>破棄済みのエフェクトなら一覧から取り除いて true を返す（EffectRegister.Release の前に Patches.cs から）。</summary>
    public static unsafe bool SkipDestroyedEffect(EffectRegister register, int index)
    {
        var buffer = register.instanceBuffer;
        if (buffer == null)
            return false;
        var array = (byte*)IL2CPP.Il2CppObjectBaseToPtrNotNull(buffer);
        if (index < 0 || index >= *(int*)(array + ArrayLength))
            return false;
        var pointer = *(IntPtr*)(array + ArrayData + index * InstanceSize);
        if (pointer == IntPtr.Zero)
            return false;
        var effect = new Effect(pointer);
        if (effect != null)
            return false;
        ClearInstance(register, index);
        Plugin.Logger.LogWarning($"Skipped releasing a destroyed effect: {effect.GetIl2CppType().Name}");
        return true;
    }

    private static void ReleaseEffects(Rigidbody body)
    {
        var root = body.transform;
        var count = 0;
        ForEachEffect((register, index, effect) =>
        {
            if (effect == null)
                return;
            if (effect.transform.IsChildOf(root) || effect.TryCast<ExhaustEffect>()?.body == body)
            {
                register.Release(index);
                ClearInstance(register, index);
                count++;
            }
        });
        Plugin.Logger.LogInfo($"Spawn menu: released {count} effect(s)");
    }

    /// <summary>上の条件から漏れて、車両と一緒に消えたエフェクトを取り除く（残すと毎フレーム例外になる）。</summary>
    private static void SweepDeadEffects()
    {
        ForEachEffect((register, index, effect) =>
        {
            if (effect != null)
                return;
            ClearInstance(register, index);
            Plugin.Logger.LogWarning("Spawn menu: removed a destroyed effect");
        });
    }

    /// <summary>走行の終了時、消えた車両の描画を結合の一覧から外す（VehicleRendererAtlaser.Release の前に Patches.cs から）。</summary>
    public static void ForgetDestroyedRenderers(VehicleRendererAtlaser atlaser)
    {
        var users = atlaser.users;
        for (var i = users.Count - 1; i >= 0; i--)
        {
            if (users[i]?.Renderer?.Renderer == null)
                users.RemoveAt(i);
        }
        var groups = atlaser.combineGroups;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i]?.Root == null)
                groups.RemoveAt(i);
        }
    }

    private void OnSpawned()
    {
        var task = _spawning;
        _spawning = null;
        Selecting = false;
        VehicleRendererAtlaser.active = _atlaser;
        _atlaser = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            Plugin.Logger.LogError($"Spawn menu: spawn failed: {task.Exception?.ToString() ?? "canceled"}");
            return;
        }
        var spawns = task.Result;
        if (_target == null || _target.WasCollected || spawns == null)
            return;
        _target.spawns = spawns;
        foreach (var spawn in spawns)
        {
            var vehicle = spawn?.Vehicle;
            if (vehicle == null)
                continue;
            _target.register?.Register(vehicle);
            var behaviour = vehicle.TryCast<Sprocket.Vehicles.IVehicleGateway>()?.Behaviour;
            var body = behaviour?.Rigidbody;
            if (body != null)
                body.isKinematic = false;
        }
    }
}
