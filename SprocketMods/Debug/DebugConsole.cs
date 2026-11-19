using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SprocketMods.Debug;

internal sealed class DebugConfig
{
    public readonly ConfigEntry<bool> Commands;

    public DebugConfig(ConfigFile cfg)
    {
        Commands = cfg.Bind("Debug", "Commands", false,
            "開発用: BepInEx/sprocketmods_commands.txt に書いたコマンドを実行し、結果を BepInEx/sprocketmods_debug.txt に書く（観戦・倍速・試合の自動実行。AI の調整用）");
    }
}

/// <summary>
/// 開発用のコマンド実行（正式な機能ではない。AI の調整を自動で回すためのもの）。
/// BepInEx/sprocketmods_commands.txt を 0.25 秒ごとに見て、あれば 1 行ずつ実行してファイルを消し、
/// 結果を BepInEx/sprocketmods_debug.txt に追記する。コマンドは help を参照。
///  - 観戦（spectate on）: プレイヤーの車両を地中へ移して止め、戦いに加わらないようにする（AI からは見えない）
///  - 倍速（timescale 4）: Time.timeScale。物理の刻みはそのままなので、速くするほど重くなる
///  - 試合の自動実行（series N）: カスタムバトルで、片方の AI が全滅（または時間切れ）したら結果を書いてやり直す
/// </summary>
internal sealed class DebugConsole
{
    private static DebugConfig Cfg => Plugin.Debug;

    private readonly string _commandPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "sprocketmods_commands.txt");
    private readonly string _outputPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "sprocketmods_debug.txt");
    private float _nextPoll;
    private float _timeScale = 1f;

    // 観戦
    private bool _spectate;
    private Rigidbody _hiddenBody;
    private Vector3 _hiddenFrom;
    private VehicleController _controller;

    // 試合
    private int _seriesLeft;
    private int _battleNumber;
    private float _battleStart = float.NaN;
    private float _timeLimit = 600f;
    private bool _battleEnded;
    private float _restartAt = float.NaN;
    private readonly Dictionary<string, int> _initial = new();

    public void Update()
    {
        if (!Cfg.Commands.Value)
            return;
        // 倍速（一時停止中は 0 なのでそのまま）
        if (!Mathf.Approximately(_timeScale, 1f) && Time.timeScale > 0f && !Mathf.Approximately(Time.timeScale, _timeScale)
            && !Design.SandboxSpawner.Selecting)
            Time.timeScale = _timeScale;
        if (_spectate)
            KeepHidden();
        UpdateBattle();
        if (Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + 0.25f;
        if (!File.Exists(_commandPath))
            return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(_commandPath);
            File.Delete(_commandPath);
        }
        catch (IOException)
        {
            return; // 書き込み中
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            Write($"> {line}");
            try
            {
                Execute(line);
            }
            catch (Exception e)
            {
                Write($"error: {e.Message}");
                Plugin.Logger.LogError($"Debug command '{line}' failed: {e}");
            }
        }
    }

    private void Write(string text)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {text}";
        try
        {
            File.AppendAllText(_outputPath, stamped + Environment.NewLine);
        }
        catch (IOException)
        {
            // 読み取り中など
        }
        Plugin.Logger.LogInfo($"Debug: {text}");
    }

    // ------------------------------------------------------------------ コマンド

    private void Execute(string line)
    {
        var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? line.Substring(line.IndexOf(parts[1], parts[0].Length, StringComparison.Ordinal)) : string.Empty;
        switch (command)
        {
            case "help":
                Write("commands: scene | ui | click <label> | timescale <x> | spectate on|off | status | " +
                      "config <section> <key> <value> | restart | series <n> | limit <seconds> | stop | screenshot <name> | " +
                      "hitcam probe|replay [pen|nonpen|ricochet]|kill|all on|off | quit");
                break;
            case "scene":
                Write($"scene={SceneManager.GetActiveScene().name} mode={GameModeName()} timescale={Time.timeScale:0.##} spectate={_spectate}");
                break;
            case "ui":
                foreach (var label in Clickables().Select(c => c.Label).Distinct())
                    Write($"  [{label}]");
                break;
            case "click":
                Click(rest);
                break;
            case "timescale":
                _timeScale = Mathf.Clamp(float.Parse(parts[1], CultureInfo.InvariantCulture), 0.1f, 10f);
                Time.timeScale = _timeScale;
                Write($"timescale {_timeScale:0.##}");
                break;
            case "spectate":
                SetSpectate(parts.Length < 2 || parts[1].ToLowerInvariant() != "off");
                break;
            case "status":
                WriteStatus("status");
                break;
            case "config":
                SetConfig(parts);
                break;
            case "restart":
                Restart();
                break;
            case "series":
                _seriesLeft = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1;
                Write($"series: {_seriesLeft} battle(s), time limit {_timeLimit:0} s");
                StartBattleRecord();
                break;
            case "limit":
                _timeLimit = float.Parse(parts[1], CultureInfo.InvariantCulture);
                Write($"time limit {_timeLimit:0} s (game time)");
                break;
            case "stop":
                _seriesLeft = 0;
                _restartAt = float.NaN;
                Write("series stopped");
                break;
            case "screenshot":
                var name = parts.Length > 1 ? parts[1] : DateTime.Now.ToString("HHmmss");
                var path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"screenshot_{name}.png");
                ScreenCapture.CaptureScreenshot(path);
                Write($"screenshot {path}");
                break;
            case "hitcam":
                HitCamCommand(parts);
                break;
            case "ammo":
                AmmoCommand(parts);
                break;
            case "tracks":
                foreach (var trackLine in Tracks.TrackDamage.Describe())
                    Write(trackLine);
                break;
            case "quit":
                Application.Quit();
                break;
            default:
                Write($"unknown command '{command}' (help)");
                break;
        }
    }

    private void HitCamCommand(string[] parts)
    {
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "probe";
        switch (sub)
        {
            case "probe":
                foreach (var line in HitCam.HitCamView.DescribeMaterials())
                    Write(line);
                foreach (var line in HitCam.HitCamTracker.Instance?.Describe(HitCam.HitCamTracker.Instance.Latest()) ?? Array.Empty<string>())
                    Write(line);
                break;
            case "replay":
            case "kill":
                HitCam.HitCamOutcome? outcome = (parts.Length > 2 ? parts[2].ToLowerInvariant() : "") switch
                {
                    "pen" => HitCam.HitCamOutcome.Penetration,
                    "nonpen" => HitCam.HitCamOutcome.NonPenetration,
                    "ricochet" => HitCam.HitCamOutcome.Ricochet,
                    _ => null,
                };
                HitCam.HitCamView.Replay(sub == "kill", outcome, parts.Length > 2 && parts[2].ToLowerInvariant() == "damaged");
                Write($"hitcam {sub} {outcome?.ToString() ?? "latest"}");
                break;
            case "renderers":
                foreach (var line in HitCam.HitCamTracker.Instance?.DumpRenderers() ?? Array.Empty<string>())
                    Write(line);
                break;
            case "proxy":
                HitCam.HitCamTracker.ProxyMode = parts.Length < 3 || parts[2].ToLowerInvariant() != "off";
                HitCam.HitCamTracker.ProxyTeam = () => Ai.AiDirector.Instance?.ProxyTeam() ?? new List<IntPtr>();
                Write($"hitcam proxy {HitCam.HitCamTracker.ProxyMode}");
                break;
            case "audit":
                HitCam.HitCamTracker.AuditLog = parts.Length < 3 || parts[2].ToLowerInvariant() != "off";
                Write($"hitcam audit {HitCam.HitCamTracker.AuditLog}");
                break;
            case "all":
                Plugin.HitCam.AllShots.Value = parts.Length < 3 || parts[2].ToLowerInvariant() != "off";
                Write($"hitcam all shots {Plugin.HitCam.AllShots.Value}");
                break;
            default:
                Write("usage: hitcam probe|replay|kill|all on|off");
                break;
        }
    }

    private static string GameModeName()
    {
        var mode = Object.FindObjectOfType<Sprocket.GameControl.GameController>()?.gamemode;
        return mode == null ? "-" : mode.GetIl2CppType().Name;
    }

    private void SetConfig(string[] parts)
    {
        if (parts.Length < 4)
        {
            Write("usage: config <section> <key> <value>");
            return;
        }
        var definition = new ConfigDefinition(parts[1], parts[2]);
        if (!Plugin.ConfigFile.TryGetEntry<object>(definition, out _) && !Plugin.ConfigFile.ContainsKey(definition))
        {
            Write($"no config [{parts[1]}] {parts[2]}");
            return;
        }
        var entry = Plugin.ConfigFile[definition];
        entry.SetSerializedValue(string.Join(" ", parts.Skip(3)));
        Write($"[{parts[1]}] {parts[2]} = {entry.GetSerializedValue()}");
    }

    // ------------------------------------------------------------------ 画面のボタン

    private readonly struct Clickable
    {
        public readonly string Label;
        public readonly Action Invoke;

        public Clickable(string label, Action invoke)
        {
            Label = label;
            Invoke = invoke;
        }
    }

    /// <summary>今押せるボタン（ゲームの Tab と uGUI の Button）。表示中の文字（翻訳後）で探す。</summary>
    private static List<Clickable> Clickables()
    {
        var list = new List<Clickable>();
        foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<Sprocket.UI.Tab>(), FindObjectsSortMode.None))
        {
            var tab = obj?.TryCast<Sprocket.UI.Tab>();
            if (tab == null || !tab.isActiveAndEnabled || !tab.IsInteractable())
                continue;
            var label = tab.Label;
            if (string.IsNullOrWhiteSpace(label))
                label = tab.gameObject.name;
            list.Add(new Clickable(label.Trim(), () => tab.Click()));
        }
        foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<UnityEngine.UI.Button>(), FindObjectsSortMode.None))
        {
            var button = obj?.TryCast<UnityEngine.UI.Button>();
            if (button == null || !button.isActiveAndEnabled || !button.IsInteractable())
                continue;
            var text = button.GetComponentInChildren<TMPro.TMP_Text>();
            var label = text != null && !string.IsNullOrWhiteSpace(text.text) ? text.text : button.gameObject.name;
            list.Add(new Clickable(label.Trim(), () => button.onClick.Invoke()));
        }
        return list;
    }

    private void Click(string label)
    {
        var all = Clickables();
        var match = all.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
        if (match.Invoke == null)
            match = all.FirstOrDefault(c => c.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
        if (match.Invoke == null)
        {
            Write($"no button '{label}' (ui to list)");
            return;
        }
        match.Invoke();
        Write($"clicked [{match.Label}]");
    }

    private void AmmoCommand(string[] parts)
    {
        var system = Ammo.AmmoSystem.Instance;
        var arg = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        switch (parts.Length >= 2 ? parts[1].ToLowerInvariant() : "status")
        {
            case "select":
                if (system != null && int.TryParse(arg, out var n) && n >= 1 && n <= 3)
                    system.DebugSelect((Ammo.AmmoType)(n - 1));
                break;
            case "blasttest":
                foreach (var line in system?.BlastTest() ?? Array.Empty<string>())
                    Write(line);
                break;
            case "all":
                Ammo.AmmoSystem.Everyone = arg switch
                {
                    "ap" => Ammo.AmmoType.AP,
                    "aphe" => Ammo.AmmoType.APHE,
                    "he" => Ammo.AmmoType.HE,
                    _ => null,
                };
                break;
        }
        Write(system?.Describe() ?? "no ammo system");
    }

    // ------------------------------------------------------------------ 観戦

    private void SetSpectate(bool on)
    {
        _spectate = on;
        if (!on)
            ShowPlayer();
        Write($"spectate {(on ? "on" : "off")}");
    }

    private Rigidbody GetPlayerBody()
    {
        if (_controller != null && _controller.WasCollected)
            _controller = null;
        _controller ??= Object.FindAnyObjectByType(Il2CppType.Of<VehicleController>())?.TryCast<VehicleController>();
        return _controller?.ControlledVehicle?.Rigidbody;
    }

    /// <summary>プレイヤーの車両を地中に移して止める（地形で視線が切れるので AI から見えない）。</summary>
    private void KeepHidden()
    {
        var body = GetPlayerBody();
        if (body == null || body.WasCollected)
            return;
        if (_hiddenBody != null && !_hiddenBody.WasCollected && _hiddenBody.Pointer == body.Pointer)
            return;
        _hiddenBody = body;
        _hiddenFrom = body.position;
        body.isKinematic = true;
        body.position = _hiddenFrom + Vector3.down * 80f;
        body.transform.position = body.position;
        Write($"spectate: hid {body.name}");
    }

    private void ShowPlayer()
    {
        if (_hiddenBody == null || _hiddenBody.WasCollected)
        {
            _hiddenBody = null;
            return;
        }
        _hiddenBody.transform.position = _hiddenFrom + Vector3.up * 1f;
        _hiddenBody.position = _hiddenBody.transform.position;
        _hiddenBody.isKinematic = false;
        _hiddenBody = null;
    }

    /// <summary>観戦で隠しているプレイヤーの車両か（AI の隊の前線の計算などから外す）。</summary>
    public static bool IsHidden(Rigidbody body) =>
        Instance != null && Instance._spectate && body != null && Instance._hiddenBody != null && !Instance._hiddenBody.WasCollected
        && Instance._hiddenBody.Pointer == body.Pointer;

    public static DebugConsole Instance { get; private set; }

    public DebugConsole() => Instance = this;

    // ------------------------------------------------------------------ 試合

    private static Sprocket.DeathmatchGameMode Deathmatch() =>
        Object.FindObjectOfType<Sprocket.GameControl.GameController>()?.gamemode?.TryCast<Sprocket.DeathmatchGameMode>();

    private void Restart()
    {
        var mode = Deathmatch();
        if (mode == null)
        {
            Write("restart: not in a custom battle");
            return;
        }
        _hiddenBody = null; // 車両が作り直される
        mode.RetryAsyncVoid();
        Write("restart");
        StartBattleRecord();
    }

    private void StartBattleRecord()
    {
        _battleStart = float.NaN;
        _battleEnded = false;
        _initial.Clear();
        Ai.AiDirector.Instance?.ResetTacticCounts();
    }

    private void UpdateBattle()
    {
        if (_seriesLeft <= 0 && float.IsNaN(_restartAt))
            return;
        if (!float.IsNaN(_restartAt))
        {
            if (Time.unscaledTime >= _restartAt)
            {
                _restartAt = float.NaN;
                Restart();
            }
            return;
        }
        var mode = Deathmatch();
        if (mode == null || mode.gameState != Sprocket.DeathmatchGameMode.ModeState.Running || _battleEnded)
            return;
        var alive = Ai.AiDirector.Instance?.CountAlive();
        if (alive == null || alive.Count == 0)
            return;
        if (float.IsNaN(_battleStart))
        {
            // 全車両がそろってから数え始める
            if (alive.Count < 2)
                return;
            _battleStart = Time.time;
            _battleNumber++;
            foreach (var pair in alive)
                _initial[pair.Key] = pair.Value;
            Write($"battle #{_battleNumber} start: {Describe(alive)}");
            return;
        }
        var elapsed = Time.time - _battleStart;
        var wiped = _initial.Keys.Where(team => !alive.TryGetValue(team, out var n) || n == 0).ToList();
        var timeout = elapsed >= _timeLimit;
        if (wiped.Count == 0 && !timeout)
            return;
        _battleEnded = true;
        var winner = timeout ? "draw (time limit)" : _initial.Keys.Except(wiped).FirstOrDefault() ?? "none";
        Write($"battle #{_battleNumber} end: winner {winner}, {elapsed:0} s, alive {Describe(alive)} (start {Describe(_initial)})");
        WriteStatus("final");
        _seriesLeft--;
        if (_seriesLeft > 0)
            _restartAt = Time.unscaledTime + 3f;
        else
            Write("series finished");
    }

    private static string Describe(Dictionary<string, int> counts) =>
        string.Join(", ", counts.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));

    private void WriteStatus(string title)
    {
        var director = Ai.AiDirector.Instance;
        if (director == null)
        {
            Write($"{title}: no AI");
            return;
        }
        var elapsed = float.IsNaN(_battleStart) ? 0f : Time.time - _battleStart;
        Write($"{title}: battle #{_battleNumber} {elapsed:0} s, alive {Describe(director.CountAlive())}");
        foreach (var line in director.DescribeBattleStats())
            Write($"  stats {line}");
        foreach (var line in director.DescribeDeaths())
            Write($"  {line}");
        foreach (var line in director.DescribeTacticCounts())
            Write($"  {line}");
        foreach (var line in director.DescribeUnits())
            Write($"  {line}");
    }
}
