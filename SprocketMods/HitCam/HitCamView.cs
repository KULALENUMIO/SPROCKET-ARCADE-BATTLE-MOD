using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Sprocket.Vehicles;
using SprocketMods.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SprocketMods.HitCam;

/// <summary>
/// War Thunder 風のヒットカメラの表示（画面右上）。
///
///  - 命中した瞬間の車両の部品（HitCamTracker が写し取ったメッシュと位置）を、画面に映らない遠くの高空に「模型」として並べ、
///    専用のレイヤーだけを写すカメラで RenderTexture に描いて右上のパネルに出す。
///  - カメラは砲弾のすぐ後ろから追いかけ、命中点を過ぎたところで止まって近くから見せる。
///  - 最初は元の色。砲弾が貫通した瞬間に、外板・履帯はゲームの X 線表示の材質（灰色の半透明）、
///    中の部品はゲームの戦闘用オーバーレイの材質（体力で色が変わる）に替える。健全 = 薄い青、機能停止 = 黒。
///  - この弾で傷ついた部品は、それを傷つけた破片（HitCamTracker が命中の直後に決める）が届いた瞬間から赤く塗る。
///    完全に壊れた部品だけ、赤と黄色の縁取り（少し大きくした同じメッシュを裏面だけ描く）を付ける。
///    その後の火災・撃破後の体力の減少・ほかの弾による損害は色に混ぜない。
///  - 弾薬庫の誘爆・エンジン（燃料）の火災は、その位置から赤い輪が何重にも広がる。
///  - 砲弾と破片の経路（Fragment の木）は、模型を写したカメラで画面の位置に直して UI の線で描き、親の破片が分かれた位置から順に伸ばす。
///    破片は尾を引く光（近いほど太い）。部品を傷つけた破片とその親は明るく残し、ほかはすぐ薄くする。部品に届いた所で火花を出す。
///  - 撃破したときは車両の周りを回りながら、撃破した弾をくり返し見せる。
/// </summary>
internal sealed class HitCamView
{
    private static readonly Vector3 StageOrigin = new(0f, 30000f, 0f);
    private const float FieldOfView = 34f;
    private const float FadeTime = 0.18f;
    private const float ShellStart = 0.25f;
    private const float AnimationEnd = 3.6f;
    private const float ApproachLength = 3f; // カメラ（着弾点から 2〜4 m）より手前から飛んでくるように
    private const float ShellVisualSpeed = 5f;

    // 色（HDR の線形値）
    private static readonly Color HealthyColour = new(0.42f, 0.66f, 0.82f, 1f);
    private static readonly Color WornColour = new(0.30f, 0.46f, 0.58f, 1f);
    private static readonly Color DeadColour = new(0.012f, 0.012f, 0.014f, 1f);
    private static readonly Color HitColour = new(1f, 0.08f, 0.04f, 1f);
    private static readonly Color HitLowColour = new(0.55f, 0.03f, 0.02f, 1f);
    private static readonly Color OutlineYellow = new(1f, 0.78f, 0.12f, 1f);
    private static readonly Color OutlineRed = new(1f, 0.16f, 0.05f, 1f);

    private static HitCamView _instance;

    private enum SegmentKind
    {
        Shell,
        Spall,
    }

    private struct Segment
    {
        public Vector3 A;
        public Vector3 B;
        public float T0;
        public float T1;
        public SegmentKind Kind;
        public bool Stopped;
        public bool Important; // 部品を傷つけた破片（とその親）
    }

    /// <summary>破片が傷つけた部品に届いた所の火花。</summary>
    private struct Spark
    {
        public Vector3 Position;
        public float Time;
    }

    /// <summary>表示中の部品 1 つの状態。</summary>
    private sealed class ModuleView
    {
        public CapturedModule Module;
        public readonly List<(float Time, float Value)> Changes = new(); // 表示中の命中で傷ついた時刻（破片が届く時刻）と、その後の体力
        public bool Burst; // 誘爆の輪を出した

        /// <summary>時刻 t の状態。hit = この表示の命中で傷ついた後か。</summary>
        public void State(float t, out bool hit, out float value)
        {
            hit = false;
            value = Module.Before;
            foreach (var change in Changes)
            {
                if (change.Time > t)
                    break;
                hit = true;
                value = change.Value;
            }
        }
    }

    private struct Burst
    {
        public Vector3 Position;
        public float Start; // アニメーションの時刻（t）
        public bool Fire;
    }

    /// <summary>1 つのカメラで見せている命中 1 発（同じ車両への次の命中は、カメラを切り替えずにここへ足す）。</summary>
    private sealed class HitView
    {
        public HitCapture Capture;
        public Vector3 Entry; // 表示している模型（最初の命中）の撮影フレーム
        public Vector3 Direction;
        public Vector3 Normal;
        public HitCamOutcome Outcome;
        public float Start; // 砲弾が飛び始める時刻（t）
        public float EntryTime; // 着弾の時刻（t）
        public float Spread;
        public Vector3 View;
        public float Distance;
    }

    // 表示中
    private HitCapture _capture; // 模型にしている命中（最初の 1 発）
    private bool _kill; // 撃破した（題と「撃破」の表示）
    private bool _orbit; // 撃破の表示として始めた（車両の周りを回る）。命中の表示中に撃破したときは回らない
    private IntPtr _lastBody; // 最後に表示した車両と、その表示を終えた時刻
    private float _lastEnded = float.NegativeInfinity;
    private float _started;
    private float _endAt; // 表示を終える時刻（unscaledTime）。命中が続く間は延びる
    private float _lastHitAt;
    private readonly List<HitView> _hits = new();
    private readonly List<Segment> _segments = new();
    private readonly List<ModuleView> _modules = new();
    private readonly List<Burst> _bursts = new();
    private readonly List<Spark> _sparks = new();
    private float _entryTime; // 最初の着弾の時刻
    private float _killAt; // 「撃破」を出し始める時刻（unscaledTime）
    private float _loopLength;
    private bool _xrayApplied;
    private float _nextSanitize;
    private bool _fireSeen;

    // 模型
    private GameObject _stage;
    private Camera _camera;
    private RenderTexture _texture;
    private int _layer = -1;
    private Exposure _exposure;
    private readonly List<MeshRenderer> _partRenderers = new();
    private readonly List<CapturedPart> _shownParts = new();
    private readonly List<MeshRenderer> _outlineRenderers = new();
    private readonly List<(int Part, ModuleView Module, bool Outer)> _outlines = new();
    private Material _xray;
    private Material _xrayFitting; // 外側の装備品用（X 線表示より濃く明るい）
    private Material _component;
    private Material[] _healthMaterials; // 体力の段階ごと（薄い青 → 暗い青 → 黒）
    private Material[] _hitMaterials; // この弾で傷ついた部品（赤 → 黒）
    private int[] _partKeys = Array.Empty<int>();
    private Material _outlineYellow;
    private Material _outlineRed;
    private bool _materialsSearched;
    private MaterialPropertyBlock _block;
    private MaterialPropertyBlock _emptyBlock;
    private int _overlayValueId;
    private int _highColourId;
    private int _lowColourId;
    private int _deadColourId;

    // UI
    private GameObject _root;
    private CanvasGroup _group;
    private Image _panel;
    private Image _accent;
    private RawImage _image;
    private RectTransform _overlay;
    private TextMeshProUGUI _title;
    private TextMeshProUGUI _name;
    private TextMeshProUGUI _banner;
    private readonly List<Image> _lines = new();
    private readonly List<Image> _dots = new();
    private int _lineCount;
    private int _dotCount;
    private static Sprite _glowSprite;
    private static Sprite _ringSprite;
    private bool _subscribed;
    private float _nextErrorLog;

    public HitCamView() => _instance = this;

    private static HitCamConfig Cfg => Plugin.HitCam;

    public static bool IsShowing(HitCapture capture) => _instance != null && _instance._capture == capture;

    // ------------------------------------------------------------------ 開始

    private void Subscribe()
    {
        var tracker = HitCamTracker.Instance;
        if (_subscribed || tracker == null)
            return;
        tracker.Hit += OnHit;
        tracker.Killed += OnKilled;
        _subscribed = true;
    }

    private bool Active => _capture != null && Time.unscaledTime < _endAt;

    private void OnHit(HitCapture capture)
    {
        if (capture.Outcome != HitCamOutcome.Penetration && !Cfg.ShowNonPenetrating.Value)
        {
            capture.ViewNote = "skipped (non-penetration off)";
            return;
        }
        if (Active)
        {
            // 表示中の車両への命中は、同じカメラに足す（切り替えない）
            if (capture.Body == _capture.Body)
            {
                if (!_orbit)
                    AddHit(capture);
                capture.ViewNote = _orbit ? "skipped (kill orbit of the same vehicle)" : "added";
                return;
            }
            // 別の車両: 撃破の表示中や、命中が続いている間は切り替えない
            if (_orbit || Time.unscaledTime - _lastHitAt < 1.5f)
            {
                capture.ViewNote = _orbit ? "skipped (kill orbit of another vehicle)" : "skipped (showing another vehicle)";
                return;
            }
        }
        Show(capture, false);
        capture.ViewNote = _capture == capture ? "shown" : "failed to show";
    }

    private void OnKilled(HitCapture capture)
    {
        // 命中の表示中にその車両を撃破したら、別の撃破の表示は出さず、今の表示に「撃破」を出して少し延ばすだけ
        if (Active && capture.Body == _capture.Body)
        {
            if (!_kill)
            {
                _kill = true;
                if (!Contains(capture))
                    AddHit(capture);
                _killAt = Time.unscaledTime;
                _endAt = Mathf.Max(_endAt, Time.unscaledTime + 2.5f);
                Compose();
            }
            return;
        }
        // 命中の表示を終えた直後の撃破（乗員の判定や火災で少し遅れて撃破になった）も出さない
        if (capture.Body == _lastBody && Time.unscaledTime - _lastEnded < 4f)
            return;
        Show(capture, true);
    }

    private bool Contains(HitCapture capture)
    {
        foreach (var hit in _hits)
        {
            if (hit.Capture == capture)
                return true;
        }
        return false;
    }

    /// <summary>開発用: 最後の記録を表示する。</summary>
    public static void Replay(bool kill, HitCamOutcome? outcome = null, bool damaged = false)
    {
        var capture = damaged ? HitCamTracker.Instance?.LatestDamaged() : HitCamTracker.Instance?.Latest(outcome);
        if (_instance == null || capture == null)
            return;
        _instance._capture = null; // 表示中でも最初から
        _instance.Show(capture, kill);
    }

    private void Show(HitCapture capture, bool kill)
    {
        if (capture == null || capture.Released || capture.Parts.Count == 0)
            return;
        var previous = _capture;
        _capture = capture;
        _kill = kill;
        _orbit = kill;
        if (previous != capture)
            ReleaseIfDropped(previous);
        _started = Time.unscaledTime;
        _endAt = _started;
        _hits.Clear();
        _segments.Clear();
        _bursts.Clear();
        _sparks.Clear();
        _loopLength = 0f;
        BuildModules(capture);
        if (!BuildStage(capture))
        {
            _capture = null;
            return;
        }
        AddHit(capture);
        _entryTime = _hits[0].EntryTime;
        if (kill)
        {
            _endAt = Mathf.Max(_endAt, Time.unscaledTime + Cfg.KillDuration.Value);
            _killAt = _started + _entryTime;
        }
        Compose();
    }

    /// <summary>命中 1 発を今の表示に足す。砲弾は今から飛び始める。</summary>
    private void AddHit(HitCapture capture)
    {
        // その命中の撮影フレームから、模型（最初の命中）の撮影フレームへ（車両はその間に動いている）
        var rotation = _capture.RootRotation * Quaternion.Inverse(capture.RootRotation);
        var age = Time.unscaledTime - _started;
        var start = _hits.Count == 0 ? ShellStart : age;
        var hit = new HitView
        {
            Capture = capture,
            Entry = rotation * capture.Entry,
            Direction = (rotation * capture.Direction).normalized,
            Normal = rotation * capture.Normal,
            Outcome = capture.Outcome,
            Start = start,
            EntryTime = start + ApproachLength / ShellVisualSpeed,
        };
        BuildSegments(capture, hit, rotation);
        ChooseView(hit);
        _hits.Add(hit);
        _lastHitAt = Time.unscaledTime;
        _endAt = Mathf.Max(_endAt, Time.unscaledTime + Mathf.Max(Cfg.HitDuration.Value, AnimationEnd - ShellStart + 0.6f));
        if (_capture != capture && _hits.Count > 1)
            Compose();
    }

    private static void ReleaseIfDropped(HitCapture capture)
    {
        if (capture != null && HitCamTracker.Instance?.IsTracked(capture) != true)
            capture.Release();
    }

    // ------------------------------------------------------------------ 経路

    private void BuildSegments(HitCapture capture, HitView hit, Quaternion rotation)
    {
        var first = _segments.Count;
        var fragments = capture.Fragments;
        var n = fragments.Count;
        var t0 = new float[n];
        var speed = new float[n];
        var starts = new Vector3[n];
        var shellSpeed = n > 0 && fragments[0].Speed > 1f ? fragments[0].Speed : 800f;
        var entryTime = hit.EntryTime;
        var entry = hit.Entry;
        var dir = hit.Direction;
        if (hit.Outcome != HitCamOutcome.Penetration)
        {
            BuildSurfaceHit(hit);
            ApplyResult(capture, hit, null);
            return;
        }

        var segmentOf = new int[n];
        for (var i = 0; i < n; i++)
        {
            var f = fragments[i];
            var start = rotation * f.Start;
            var end = rotation * f.End;
            var parent = f.Parent;
            if (parent < 0 || parent >= i)
            {
                // 砲弾: 命中点の手前 ApproachLength m から飛んでくるように見せる
                var toEntry = Vector3.Dot(entry - start, dir);
                if (f.IsPenetrator && toEntry > ApproachLength)
                    start = entry - dir * ApproachLength;
                var lead = f.IsPenetrator ? Mathf.Max(0f, Vector3.Dot(entry - start, dir)) : 0f;
                t0[i] = entryTime - lead / ShellVisualSpeed;
            }
            else
            {
                var p = fragments[parent];
                var along = Mathf.Max(0f, Vector3.Dot(start - starts[parent], (rotation * (p.End - p.Start)).normalized));
                t0[i] = t0[parent] + along / Mathf.Max(0.5f, speed[parent]);
            }
            starts[i] = start;
            segmentOf[i] = -1;
            // 中に入ってからは砲弾も破片もゆっくり（見えるように）
            speed[i] = f.IsPenetrator ? ShellVisualSpeed * 0.55f : ShellVisualSpeed * 0.5f * Mathf.Clamp(f.Speed / shellSpeed, 0.4f, 1.1f);
            var length = (end - start).magnitude;
            if (length < 0.01f)
                continue;
            var approach = f.IsPenetrator && (parent < 0 || parent >= i);
            segmentOf[i] = _segments.Count;
            _segments.Add(new Segment
            {
                A = start,
                B = end,
                T0 = t0[i],
                T1 = t0[i] + length / (approach ? ShellVisualSpeed : speed[i]),
                Kind = f.IsPenetrator ? SegmentKind.Shell : SegmentKind.Spall,
                Stopped = f.Stopped,
            });
        }

        // 長すぎる経路は縮めて、着弾から（AnimationEnd - 最初の着弾）秒で終わるようにする
        var span = AnimationEnd - (ShellStart + ApproachLength / ShellVisualSpeed);
        var last = entryTime;
        for (var i = first; i < _segments.Count; i++)
            last = Mathf.Max(last, _segments[i].T1);
        if (last > entryTime + span)
        {
            var k = span / (last - entryTime);
            for (var i = first; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                seg.T0 = seg.T0 <= entryTime ? seg.T0 : entryTime + (seg.T0 - entryTime) * k;
                seg.T1 = seg.T1 <= entryTime ? seg.T1 : entryTime + (seg.T1 - entryTime) * k;
                _segments[i] = seg;
            }
            last = entryTime + span;
        }
        _loopLength = Mathf.Max(_loopLength, last + 1.6f);

        // 破片の広がり（カメラの距離に使う。過貫通して遠くへ抜けた砲弾は数えない）
        hit.Spread = 0.6f;
        for (var i = first; i < _segments.Count; i++)
        {
            if (_segments[i].Kind == SegmentKind.Spall)
                hit.Spread = Mathf.Max(hit.Spread, Mathf.Min(2.5f, (_segments[i].B - entry).magnitude));
        }
        ApplyResult(capture, hit, segmentOf);
    }

    /// <summary>
    /// 命中の結果（HitCamTracker.Settle が命中の直後に決めた、傷ついた部品と傷つけた破片）を表示の時刻に直す。
    /// 部品の色はその破片が届く時刻に変わり、その破片と親は明るく描き、届いた所に火花を出す。経路が無ければ着弾の時刻。
    /// </summary>
    private void ApplyResult(HitCapture capture, HitView hit, int[] segmentOf)
    {
        var fragments = capture.Fragments;
        foreach (var module in capture.Modules)
        {
            if (!module.Damaged)
                continue;
            var view = FindModule(module);
            if (view == null)
                continue;
            var time = hit.EntryTime;
            var index = segmentOf != null && module.Cause >= 0 && module.Cause < segmentOf.Length ? segmentOf[module.Cause] : -1;
            if (index >= 0)
            {
                var seg = _segments[index];
                time = Mathf.Lerp(seg.T0, seg.T1, module.CauseAlong);
                _sparks.Add(new Spark { Position = Vector3.Lerp(seg.A, seg.B, module.CauseAlong), Time = time });
                for (var f = module.Cause; f >= 0;)
                {
                    var si = segmentOf[f];
                    if (si >= 0)
                    {
                        var marked = _segments[si];
                        marked.Important = true;
                        _segments[si] = marked;
                    }
                    var parent = fragments[f].Parent;
                    f = parent >= 0 && parent < f ? parent : -1;
                }
            }
            view.Changes.Add((time, module.Result));
            view.Changes.Sort((a, b) => a.Time.CompareTo(b.Time));
            // 弾薬庫の誘爆: この命中で弾薬庫が壊れた
            if (!view.Burst && module.IsAmmo && module.Before > 0f && module.Result <= 0f && view.Module.HasBounds)
            {
                view.Burst = true;
                _bursts.Add(new Burst { Position = view.Module.Bounds.center, Start = time, Fire = false });
            }
        }
    }

    /// <summary>表示している模型（最初の命中）の部品のうち、同じ体力の部品。</summary>
    private ModuleView FindModule(CapturedModule module)
    {
        foreach (var view in _modules)
        {
            if (view.Module == module || view.Module.Pool == module.Pool)
                return view;
        }
        return null;
    }

    /// <summary>
    /// 貫通しなかった命中: 砲弾は装甲の表面（命中点）で止める。跳弾は表面で跳ね返る向きへ飛ばす。
    /// 貫通計算の破片の木には装甲の中を進んだ分も入っているため、そのまま描くと装甲を抜けたように見える。
    /// </summary>
    private void BuildSurfaceHit(HitView hit)
    {
        var entry = hit.Entry;
        var dir = hit.Direction;
        _segments.Add(new Segment
        {
            A = entry - dir * ApproachLength,
            B = entry,
            T0 = hit.Start,
            T1 = hit.EntryTime,
            Kind = SegmentKind.Shell,
            Stopped = hit.Outcome == HitCamOutcome.NonPenetration,
        });
        var end = hit.EntryTime;
        if (hit.Outcome == HitCamOutcome.Ricochet)
        {
            var bounce = Vector3.Reflect(dir, hit.Normal).normalized;
            // 法線の向きが逆でも、車両の外へ飛ぶようにする
            if (Vector3.Dot(bounce, dir) > 0.999f || Vector3.Dot(bounce, dir) < -0.95f)
                bounce = (Vector3.ProjectOnPlane(dir, Vector3.up) + Vector3.up).normalized;
            _segments.Add(new Segment
            {
                A = entry,
                B = entry + bounce * 3f,
                T0 = hit.EntryTime,
                T1 = hit.EntryTime + 0.5f,
                Kind = SegmentKind.Shell,
            });
            end += 0.5f;
        }
        _loopLength = Mathf.Max(_loopLength, end + 1.6f);
        hit.Spread = 0.6f;
    }

    /// <summary>表示の始めの部品の状態（模型にした命中の前の体力）。</summary>
    private void BuildModules(HitCapture capture)
    {
        _modules.Clear();
        _fireSeen = capture.WasOnFire;
        foreach (var module in capture.Modules)
            _modules.Add(new ModuleView { Module = module });
    }

    /// <summary>火災: 表示している命中のどれかで燃え始めたら（HitCamTracker が確定から 1.5 秒見ている）、エンジン（無ければ燃料タンク）から輪を出す。</summary>
    private void UpdateFire(float t)
    {
        if (_fireSeen)
            return;
        foreach (var hit in _hits)
        {
            if (!hit.Capture.FireStarted || hit.Outcome != HitCamOutcome.Penetration && !_kill)
                continue;
            _fireSeen = true;
            var position = _capture.Bounds.center;
            var found = false;
            foreach (var view in _modules)
            {
                if (view.Module.IsEngine && view.Module.HasBounds)
                {
                    position = view.Module.Bounds.center;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                foreach (var view in _modules)
                {
                    if (view.Module.IsFuel && view.Module.HasBounds)
                    {
                        position = view.Module.Bounds.center;
                        break;
                    }
                }
            }
            _bursts.Add(new Burst { Position = position, Start = Mathf.Max(hit.EntryTime + 0.4f, t + 0.3f), Fire = true });
            return;
        }
    }

    // ------------------------------------------------------------------ 模型

    private bool BuildStage(HitCapture capture)
    {
        if (!EnsureStage())
            return false;
        FindMaterials();
        _shownParts.Clear();
        _outlines.Clear();
        var used = 0;
        foreach (var part in capture.Parts)
        {
            if (part.Mesh == null || part.Mesh.WasCollected)
                continue;
            var renderer = GetRenderer(_partRenderers, used++, false);
            var t = renderer.transform;
            t.localPosition = part.Matrix.GetColumn(3);
            t.localRotation = part.Matrix.rotation;
            t.localScale = part.Matrix.lossyScale;
            renderer.GetComponent<MeshFilter>().sharedMesh = part.Mesh;
            for (var m = 0; m < part.Materials.Length; m++)
            {
                if (part.Materials[m] == null || part.Materials[m].WasCollected)
                    part.Materials[m] = _xray;
            }
            renderer.sharedMaterials = part.Materials;
            renderer.SetPropertyBlock(_emptyBlock);
            renderer.gameObject.SetActive(true);
            _shownParts.Add(part);
        }
        for (var i = used; i < _partRenderers.Count; i++)
        {
            _partRenderers[i].gameObject.SetActive(false);
            _partRenderers[i].GetComponent<MeshFilter>().sharedMesh = null;
        }

        // 縁取り: 部品のメッシュを少し大きくして裏面だけ描く（外側に黄色、内側に赤）
        var outlines = 0;
        if (_outlineYellow != null)
        {
            for (var i = 0; i < _shownParts.Count; i++)
            {
                var part = _shownParts[i];
                if (part.Module < 0 || part.Module >= _modules.Count || !IsModulePart(part) && !IsTrackPart(part))
                    continue;
                var module = _modules[part.Module];
                PlaceOutline(outlines++, part, 0.045f, _outlineYellow);
                _outlines.Add((i, module, true));
                PlaceOutline(outlines++, part, 0.02f, _outlineRed);
                _outlines.Add((i, module, false));
            }
        }
        for (var i = outlines; i < _outlineRenderers.Count; i++)
        {
            _outlineRenderers[i].gameObject.SetActive(false);
            _outlineRenderers[i].GetComponent<MeshFilter>().sharedMesh = null;
        }
        _xrayApplied = false;
        return used > 0;
    }

    private void PlaceOutline(int index, CapturedPart part, float thickness, Material material)
    {
        var renderer = GetRenderer(_outlineRenderers, index, true);
        var bounds = part.Mesh.bounds;
        var scale = part.Matrix.lossyScale;
        var size = Mathf.Max(0.05f, Mathf.Max(bounds.extents.x * Mathf.Abs(scale.x), bounds.extents.y * Mathf.Abs(scale.y), bounds.extents.z * Mathf.Abs(scale.z)));
        var k = 1f + thickness / size;
        // 部品の中心を基準に k 倍
        var m = part.Matrix * Matrix4x4.Translate(bounds.center) * Matrix4x4.Scale(Vector3.one * k) * Matrix4x4.Translate(-bounds.center);
        var t = renderer.transform;
        t.localPosition = m.GetColumn(3);
        t.localRotation = m.rotation;
        t.localScale = m.lossyScale;
        renderer.GetComponent<MeshFilter>().sharedMesh = part.Mesh;
        var count = Math.Max(1, part.Materials.Length);
        var materials = new Material[count];
        for (var i = 0; i < count; i++)
            materials[i] = material;
        renderer.sharedMaterials = materials;
        renderer.gameObject.SetActive(false);
    }

    private MeshRenderer GetRenderer(List<MeshRenderer> pool, int index, bool outline)
    {
        while (pool.Count <= index)
        {
            var go = new GameObject(outline ? "Outline" : "Part");
            go.layer = _layer;
            go.transform.SetParent(_stage.transform, false);
            go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = outline ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveShadows = !outline;
            pool.Add(renderer);
        }
        return pool[index];
    }

    /// <summary>外板・履帯（透かす部品）か。</summary>
    private static bool IsShell(CapturedPart part) =>
        part.PartType == VehiclePartType.None
        || (part.PartType & (HitCamTracker.XRayParts | VehiclePartType.TrackBelt | VehiclePartType.Wheel | VehiclePartType.Suspension)) != 0;

    /// <summary>中の部品（体力の色で塗る）か。</summary>
    private static bool IsModulePart(CapturedPart part) => !IsShell(part);

    /// <summary>履帯（体力のある部品）のリンク。傷つくまでは外側の装備品の色、傷ついたら体力の色・縁取り。</summary>
    private static bool IsTrackPart(CapturedPart part) => part.Module >= 0 && (part.PartType & VehiclePartType.TrackBelt) != 0;

    /// <summary>砲弾が貫通した瞬間から、外板を透かし、中の部品を体力の色にする。</summary>
    private void ApplyXRay(bool on)
    {
        if (_xrayApplied == on)
            return;
        _xrayApplied = on;
        if (_partKeys.Length < _shownParts.Count)
            _partKeys = new int[_shownParts.Count];
        for (var i = 0; i < _partKeys.Length; i++)
            _partKeys[i] = on ? 0 : -1;
        for (var i = 0; i < _shownParts.Count && i < _partRenderers.Count; i++)
        {
            var part = _shownParts[i];
            var renderer = _partRenderers[i];
            var replacement = IsShell(part) ? (part.Fitting && _xrayFitting != null ? _xrayFitting : _xray) : _healthMaterials?[0];
            if (!on || replacement == null)
            {
                renderer.sharedMaterials = part.Materials;
                renderer.SetPropertyBlock(_emptyBlock);
                continue;
            }
            var materials = new Material[Math.Max(1, part.Materials.Length)];
            for (var m = 0; m < materials.Length; m++)
                materials[m] = replacement;
            renderer.sharedMaterials = materials;
        }
        if (!on)
        {
            foreach (var outline in _outlineRenderers)
                outline.gameObject.SetActive(false);
        }
    }

    /// <summary>中の部品の色（体力）と、この弾で傷ついた部品の赤い塗りと縁取り。</summary>
    private void ColourModules(float t, float age)
    {
        if (!_xrayApplied || _healthMaterials == null)
            return;
        for (var i = 0; i < _shownParts.Count && i < _partRenderers.Count && i < _partKeys.Length; i++)
        {
            var part = _shownParts[i];
            var track = IsTrackPart(part);
            if (!IsModulePart(part) && !track)
                continue;
            float value;
            bool highlighted;
            if (part.Module >= 0 && part.Module < _modules.Count)
            {
                _modules[part.Module].State(t, out highlighted, out value);
            }
            else
            {
                highlighted = false;
                value = 1f;
            }
            // 履帯は傷つくまで外側の装備品の材質のまま
            if (track && !highlighted)
            {
                if (_partKeys[i] == -2)
                    continue;
                _partKeys[i] = -2;
                var fitting = _xrayFitting ?? _xray;
                var plain = new Material[Math.Max(1, part.Materials.Length)];
                for (var m = 0; m < plain.Length; m++)
                    plain[m] = fitting;
                _partRenderers[i].sharedMaterials = plain;
                continue;
            }
            // 材質が変わったときだけ差し替える
            var step = Step(value);
            var key = (highlighted ? 100 : 0) + step;
            if (_partKeys[i] == key)
                continue;
            _partKeys[i] = key;
            var material = highlighted ? _hitMaterials[step] : _healthMaterials[step];
            var materials = new Material[Math.Max(1, part.Materials.Length)];
            for (var m = 0; m < materials.Length; m++)
                materials[m] = material;
            _partRenderers[i].sharedMaterials = materials;
            _partRenderers[i].SetPropertyBlock(_emptyBlock);
        }

        // 縁取りは傷ついた瞬間から、ゆっくり明滅させる
        var pulse = 0.75f + 0.25f * Mathf.Sin(age * 7f);
        for (var index = 0; index < _outlines.Count && index < _outlineRenderers.Count; index++)
        {
            var (_, module, outer) = _outlines[index];
            var renderer = _outlineRenderers[index];
            // 縁取りは、この弾で完全に壊れた（体力 0）部品だけ
            module.State(t, out var hit, out var value);
            var show = hit && value <= 0f;
            if (renderer.gameObject.activeSelf != show)
                renderer.gameObject.SetActive(show);
            if (!show)
                continue;
            var colour = outer ? OutlineYellow : OutlineRed;
            colour *= outer ? pulse : 1f;
            _block.Clear();
            _block.SetColor(_litColourId, colour);
            _block.SetColor(_unlitColourId, colour);
            renderer.SetPropertyBlock(_block);
        }
    }

    private int _litColourId;
    private int _unlitColourId;

    /// <summary>消えた材質（撃破で作り直された車両など）は X 線表示の材質に替える（そのままだと紫で描かれる）。</summary>
    private void SanitizeMaterials()
    {
        for (var i = 0; i < _shownParts.Count && i < _partRenderers.Count; i++)
        {
            var materials = _partRenderers[i].sharedMaterials;
            var broken = false;
            for (var m = 0; m < materials.Length; m++)
            {
                if (materials[m] == null || materials[m].WasCollected)
                {
                    broken = true;
                    break;
                }
            }
            if (!broken)
                continue;
            var part = _shownParts[i];
            for (var m = 0; m < part.Materials.Length; m++)
            {
                if (part.Materials[m] == null || part.Materials[m].WasCollected)
                    part.Materials[m] = _xray;
            }
            var fixedMaterials = new Material[Math.Max(1, materials.Length)];
            for (var m = 0; m < fixedMaterials.Length; m++)
                fixedMaterials[m] = m < materials.Length && materials[m] != null && !materials[m].WasCollected ? materials[m] : _xray;
            _partRenderers[i].sharedMaterials = fixedMaterials;
        }
    }

    private void FindMaterials()
    {
        if (_materialsSearched && _xray != null && !_xray.WasCollected)
            return;
        _materialsSearched = true;
        _xray = null;
        _component = null;
        _block ??= new MaterialPropertyBlock();
        _emptyBlock ??= new MaterialPropertyBlock();
        _overlayValueId = Shader.PropertyToID("_OverlayValue");
        _highColourId = Shader.PropertyToID("_High_Health_Colour");
        _lowColourId = Shader.PropertyToID("_Low_Health_Colour");
        _deadColourId = Shader.PropertyToID("_Destroyed_Health_Colour");
        _litColourId = Shader.PropertyToID("_LitColour");
        _unlitColourId = Shader.PropertyToID("_UnlitColour");
        try
        {
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprocket.Vehicles.UI.VehicleBattleOverlayController>());
            if (found != null)
            {
                foreach (var obj in found)
                {
                    var controller = obj?.TryCast<Sprocket.Vehicles.UI.VehicleBattleOverlayController>();
                    if (controller?.xRayMaterial == null)
                        continue;
                    _xray = controller.xRayMaterial;
                    _fittingTint = new Vector2(-1f, -1f);
                    _xrayFitting = MakeFitting(_xray);
                    if (controller.componentMaterial != null)
                    {
                        // 中の部品: 健全 = 薄い青、機能停止 = 黒、この弾で傷ついた = 赤。体力の段階ごとに材質を分けておく
                        _component = new Material(controller.componentMaterial) { name = "SprocketHitCamComponent", hideFlags = HideFlags.HideAndDontSave };
                        _healthMaterials = new Material[HealthSteps.Length];
                        _hitMaterials = new Material[HealthSteps.Length];
                        for (var i = 0; i < HealthSteps.Length; i++)
                        {
                            _healthMaterials[i] = MakeModuleMaterial(controller.componentMaterial, HealthSteps[i], false);
                            _hitMaterials[i] = MakeModuleMaterial(controller.componentMaterial, HealthSteps[i], true);
                        }
                    }
                    _outlineYellow = MakeOutline(_xray, OutlineYellow, "SprocketHitCamOutlineYellow");
                    _outlineRed = MakeOutline(_xray, OutlineRed, "SprocketHitCamOutlineRed");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Hit cam: material search failed: {e.Message}");
        }
        Plugin.Logger.LogInfo($"Hit cam: x-ray material {(_xray != null ? $"{_xray.name} ({_xray.shader?.name})" : "not found")}, " +
                              $"component material {(_component != null ? "ok" : "not found")}");
    }

    private static readonly float[] HealthSteps = { 1f, 0.75f, 0.5f, 0.25f, 0f };

    private static int Step(float fraction) => fraction >= 0.95f ? 0 : fraction >= 0.65f ? 1 : fraction >= 0.4f ? 2 : fraction > 0.001f ? 3 : 4;

    /// <summary>中の部品の材質（ゲームの戦闘用オーバーレイの材質の複製）。体力の値と色を材質そのものに書く。</summary>
    private static Material MakeModuleMaterial(Material source, float value, bool hit)
    {
        var m = new Material(source) { name = $"SprocketHitCamModule{(hit ? "Hit" : "")}{value:0.00}", hideFlags = HideFlags.HideAndDontSave };
        m.SetFloat("_OverlayValue", value);
        // 体力の値の補間に頼らず、段階ごとの色を両方に入れる
        Color colour;
        if (value <= 0f)
            colour = DeadColour;
        else if (hit)
            colour = Color.Lerp(HitLowColour, HitColour, value);
        else
            colour = Color.Lerp(WornColour, HealthyColour, value);
        m.SetColor("_High_Health_Colour", colour);
        m.SetColor("_Low_Health_Colour", colour);
        m.SetColor("_Destroyed_Health_Colour", value <= 0f ? DeadColour : colour);
        return m;
    }

    private static readonly Color FittingColour = new(0.62f, 0.66f, 0.70f, 1f);

    /// <summary>外側の装備品の材質: X 線表示の材質を元に、明るく濃くする（透かしても履帯・フェンダー・小物の形が分かる）。</summary>
    private static Material MakeFitting(Material source)
    {
        var m = new Material(source) { name = "SprocketHitCamFitting", hideFlags = HideFlags.HideAndDontSave };
        TintFitting(m, source);
        return m;
    }

    private static Vector2 _fittingTint = new(-1f, -1f);

    /// <summary>設定（濃さ・明るさ）を外側の装備品の材質に入れる。設定が変わったときだけ。</summary>
    private static void TintFitting(Material m, Material source)
    {
        var tint = new Vector2(Cfg.FittingOpacity.Value, Cfg.FittingBrightness.Value);
        if (m == null || source == null || m.WasCollected || source.WasCollected || tint == _fittingTint)
            return;
        _fittingTint = tint;
        foreach (var name in new[] { "_LitColour", "_UnlitColour" })
        {
            if (!source.HasProperty(name))
                continue;
            var c = source.GetColor(name);
            var stronger = Color.Lerp(c, FittingColour, tint.y);
            stronger.a = Mathf.Clamp01(Mathf.Max(c.a, tint.x));
            m.SetColor(name, stronger);
        }
    }

    /// <summary>縁取りの材質: X 線表示の材質を元に、色を付けて表の面を描かない（裏面だけ = 部品の周りの縁になる）。</summary>
    private static Material MakeOutline(Material source, Color colour, string name)
    {
        var m = new Material(source) { name = name, hideFlags = HideFlags.HideAndDontSave };
        m.SetColor("_LitColour", colour);
        m.SetColor("_UnlitColour", colour);
        m.SetColor("_EmissionColor", colour * 2f);
        // CullMode.Front = 1
        m.SetFloat("_CullMode", 1f);
        m.SetFloat("_CullModeForward", 1f);
        m.SetFloat("_TransparentCullMode", 1f);
        m.SetFloat("_DoubleSidedEnable", 0f);
        m.renderQueue = source.renderQueue + 10;
        return m;
    }

    /// <summary>開発用: 材質のシェーダーとプロパティ。</summary>
    public static IEnumerable<string> DescribeMaterials()
    {
        var lines = new List<string>();
        var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprocket.Vehicles.UI.VehicleBattleOverlayController>());
        lines.Add($"battle overlay controllers: {found?.Length ?? 0}");
        if (found == null)
            return lines;
        foreach (var obj in found)
        {
            var c = obj?.TryCast<Sprocket.Vehicles.UI.VehicleBattleOverlayController>();
            if (c == null)
                continue;
            Describe(lines, "xray", c.xRayMaterial);
            Describe(lines, "component", c.componentMaterial);
        }
        return lines;
    }

    private static void Describe(List<string> lines, string label, Material m)
    {
        if (m == null)
        {
            lines.Add($"{label}: null");
            return;
        }
        var shader = m.shader;
        lines.Add($"{label}: {m.name} shader={shader?.name} queue={m.renderQueue} keywords={string.Join(" ", m.shaderKeywords)}");
        if (shader == null)
            return;
        var props = new List<string>();
        for (var i = 0; i < shader.GetPropertyCount(); i++)
            props.Add($"{shader.GetPropertyName(i)}:{shader.GetPropertyType(i)}");
        lines.Add("  props " + string.Join(" ", props));
    }

    private bool EnsureStage()
    {
        if (_layer < 0)
        {
            for (var i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    _layer = i;
                    break;
                }
            }
            if (_layer < 0)
                _layer = 31;
            Plugin.Logger.LogInfo($"Hit cam: layer {_layer}");
        }
        if (_stage != null && _stage.WasCollected)
        {
            _stage = null;
            _camera = null;
            _partRenderers.Clear();
            _outlineRenderers.Clear();
        }
        if (_stage == null)
        {
            _stage = new GameObject("SprocketHitCamStage");
            Object.DontDestroyOnLoad(_stage);
            _stage.hideFlags = HideFlags.HideAndDontSave;
            _stage.layer = _layer;
            _stage.transform.position = StageOrigin;

            var cameraObject = new GameObject("SprocketHitCamCamera");
            cameraObject.layer = _layer;
            cameraObject.transform.SetParent(_stage.transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.cullingMask = 1 << _layer;
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = 200f;
            _camera.depth = -50f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.08f, 0.09f, 1f);
            _camera.allowHDR = true;
            try
            {
                var hd = cameraObject.GetComponent<HDAdditionalCameraData>() ?? cameraObject.AddComponent<HDAdditionalCameraData>();
                hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                hd.backgroundColorHDR = new Color(0.07f, 0.08f, 0.09f, 1f);
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
                // 透かしの材質で NaN の画素が出ると、後処理で画面全体が黒くなる（1 フレームだけ真っ黒になる）。NaN を止める
                hd.stopNaNs = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Hit cam: HDRP camera setup failed: {e.Message}");
            }
            SetupVolume(cameraObject);
            _stage.SetActive(false);
        }
        if (_exposure != null && !_exposure.WasCollected)
            _exposure.fixedExposure.value = Cfg.Exposure.Value;
        return _camera != null;
    }

    /// <summary>
    /// このカメラだけの露出（固定）。場面の自動露出のままだと、暗い背景に合わせて明るくなり、透かす前の車両が白く飛ぶ。
    /// 場面の Volume は使わず（volumeLayerMask を模型のレイヤーだけにする）、模型のレイヤーに置いた Volume で露出を固定する。
    /// </summary>
    private void SetupVolume(GameObject cameraObject)
    {
        try
        {
            var go = new GameObject("SprocketHitCamVolume");
            go.layer = _layer;
            go.transform.SetParent(_stage.transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1000f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _exposure = ScriptableObject.CreateInstance<Exposure>();
            _exposure.active = true;
            _exposure.mode.overrideState = true;
            _exposure.mode.value = ExposureMode.Fixed;
            _exposure.fixedExposure.overrideState = true;
            _exposure.fixedExposure.value = Cfg.Exposure.Value;
            profile.components.Add(_exposure);
            // 動くカメラで透かした外板がぶれないように、動きのぼかしを切る
            var blur = ScriptableObject.CreateInstance<MotionBlur>();
            blur.active = true;
            blur.intensity.overrideState = true;
            blur.intensity.value = 0f;
            profile.components.Add(blur);
            volume.sharedProfile = profile;
            var hd = cameraObject.GetComponent<HDAdditionalCameraData>();
            if (hd != null)
                hd.volumeLayerMask = 1 << _layer;
        }
        catch (Exception e)
        {
            _exposure = null;
            Plugin.Logger.LogWarning($"Hit cam: exposure volume failed: {e.Message}");
        }
    }

    private void EnsureTexture(int width, int height)
    {
        if (_texture != null && !_texture.WasCollected && _texture.width == width && _texture.height == height)
            return;
        if (_texture != null && !_texture.WasCollected)
        {
            _camera.targetTexture = null;
            _texture.Release();
            Object.Destroy(_texture);
        }
        _texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "SprocketHitCam" };
        _texture.Create();
        _camera.targetTexture = _texture;
        _image.texture = _texture;
    }

    /// <summary>ほかのカメラに模型が写らないようにする（模型は遠くにあるが念のため）。</summary>
    private void HideFromOtherCameras()
    {
        var bit = 1 << _layer;
        foreach (var camera in Camera.allCameras)
        {
            if (camera != null && camera != _camera && (camera.cullingMask & bit) != 0)
                camera.cullingMask &= ~bit;
        }
    }

    // ------------------------------------------------------------------ 毎フレーム

    public void LateUpdate()
    {
        Subscribe();
        try
        {
            Draw();
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextErrorLog)
            {
                _nextErrorLog = Time.unscaledTime + 5f;
                Plugin.Logger.LogError($"Hit cam view failed: {e}");
            }
            Hide();
        }
    }

    private void Draw()
    {
        if (!Cfg.Enabled.Value || _capture == null)
        {
            Hide();
            return;
        }
        var age = Time.unscaledTime - _started;
        if (Time.unscaledTime > _endAt || _capture.Released)
        {
            _lastBody = _capture.Body;
            _lastEnded = Time.unscaledTime;
            ReleaseIfDropped(_capture);
            _capture = null;
            Hide();
            return;
        }
        if (!EnsureUi() || !EnsureStage())
            return;

        var scale = HudStyle.Scale;
        var width = Mathf.Round(Cfg.Size.Value * scale);
        var height = Mathf.Round(width * 0.6f);
        var pad = Mathf.Round(4f * scale);
        var header = Mathf.Round(26f * scale);
        var margin = 24f * scale;
        var panelW = width + pad * 2f;
        var panelH = height + header + pad * 2f;
        var x = Screen.width - margin - panelW;
        var y = Screen.height - margin - panelH;

        _root.SetActive(true);
        _stage.SetActive(true);
        HideFromOtherCameras();
        EnsureTexture((int)width, (int)height);

        HudStyle.StylePanel(_panel, scale);
        HudStyle.Place(_panel, x, y, panelW, panelH);
        HudStyle.Place(_image, x + pad, y + pad + header, width, height);
        _overlay.anchoredPosition = new Vector2(x + pad, y + pad + header);
        _overlay.sizeDelta = new Vector2(width, height);
        var accentW = Mathf.Max(2f, 3f * scale);
        _accent.color = HudStyle.Hex(AccentHex());
        HudStyle.Place(_accent, x + pad + 4f * scale, y + pad + header * 0.2f, accentW, header * 0.6f);
        _title.fontSize = 16f * scale;
        _name.fontSize = 15f * scale;
        HudStyle.Place(_title, x + pad + 12f * scale, y + pad, width * 0.5f, header);
        HudStyle.Place(_name, x + pad + width * 0.4f, y + pad, width * 0.6f - 8f * scale, header);

        var alpha = Mathf.Clamp01(age / FadeTime) * Mathf.Clamp01((_endAt - Time.unscaledTime) / 0.35f);
        _group.alpha = alpha;

        // アニメーションの時間（撃破はくり返す）
        var t = _orbit && age > _loopLength ? (age - _loopLength) % _loopLength + 0.2f : age;
        // 貫通した弾が着いた瞬間から透かす（撃破は 2 周目以降も透かしたまま）
        var xray = _orbit && age > _loopLength;
        foreach (var hit in _hits)
            xray |= (hit.Outcome == HitCamOutcome.Penetration || _kill) && t >= hit.EntryTime;
        ApplyXRay(xray);

        if (Time.unscaledTime >= _nextSanitize)
        {
            _nextSanitize = Time.unscaledTime + 0.25f;
            SanitizeMaterials();
        }
        UpdateFire(t);
        TintFitting(_xrayFitting, _xray);
        ColourModules(t, age);
        PlaceCamera(t, age, width / height);

        _lineCount = 0;
        _dotCount = 0;
        DrawImpact(t, width, height, scale);
        DrawPaths(t, width, height, scale);
        DrawBursts(t, width, height);
        for (var i = _lineCount; i < _lines.Count; i++)
            HudStyle.SetActive(_lines[i], false);
        for (var i = _dotCount; i < _dots.Count; i++)
            HudStyle.SetActive(_dots[i], false);

        _banner.gameObject.SetActive(_kill);
        if (_kill)
        {
            var k = Mathf.Clamp01((Time.unscaledTime - _killAt) / 0.3f);
            _banner.fontSize = 30f * scale;
            _banner.alpha = k;
            _banner.transform.localScale = Vector3.one * Mathf.Lerp(1.25f, 1f, k);
            HudStyle.Place(_banner, x + pad, y + pad + header + height * 0.72f, width, height * 0.25f);
        }
    }

    // ------------------------------------------------------------------ カメラ

    private void PlaceCamera(float t, float age, float aspect)
    {
        var bounds = _capture.Bounds;
        if (_orbit && age > _loopLength)
        {
            // 撃破: 2 周目からは車両の周りを回る
            var tan = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            var length = Mathf.Max(1.5f, Mathf.Max(bounds.size.x, bounds.size.z));
            var fit = Mathf.Max(length / (2f * tan * aspect * 0.75f), Mathf.Max(1f, bounds.size.y) / (2f * tan * 0.85f));
            var progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((age - _loopLength) / Mathf.Max(1f, _endAt - _started - _loopLength)));
            var flat = Vector3.ProjectOnPlane(_capture.Direction, Vector3.up);
            var start = flat.sqrMagnitude > 1e-4f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f;
            var orbit = Quaternion.Euler(24f, start + 40f + 220f * progress, 0f) * Vector3.forward;
            SetCamera(bounds.center, orbit, fit * 1.05f);
            return;
        }

        // 着弾点に焦点を固定する（過貫通した砲弾は追わない）。向きは命中ごとに決めたまま動かさず、着弾までに少し寄るだけ。
        // 次の命中が来たら、その着弾点へ 0.5 秒かけて滑らかに移る
        var index = 0;
        for (var i = 1; i < _hits.Count; i++)
        {
            if (_hits[i].Start <= t)
                index = i;
        }
        var current = _hits[index];
        Pose(current, t, out var target, out var view, out var distance);
        if (index > 0)
        {
            var blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - current.Start) / 0.5f));
            if (blend < 1f)
            {
                Pose(_hits[index - 1], t, out var target0, out var view0, out var distance0);
                target = Vector3.Lerp(target0, target, blend);
                view = Vector3.Slerp(view0, view, blend).normalized;
                distance = Mathf.Lerp(distance0, distance, blend);
            }
        }
        SetCamera(target, view, distance);
    }

    private static void Pose(HitView hit, float t, out Vector3 target, out Vector3 view, out float distance)
    {
        var approach = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - hit.Start) / Mathf.Max(0.1f, hit.EntryTime - hit.Start)));
        var after = Mathf.Clamp01((t - hit.EntryTime) / 3f);
        target = hit.Entry;
        view = hit.View;
        distance = hit.Distance * Mathf.Lerp(1.3f, 1f, approach) * Mathf.Lerp(1f, 0.95f, after);
    }

    /// <summary>
    /// 着弾点を見る向きを選ぶ。砲弾が飛んできた線の上は（その車両の）部品に遮られていないので、そこから少しずつずらした候補を順に試し、
    /// カメラから着弾点までの線がほかの部品の箱（メッシュの境界を撮影フレームに直したもの）を通らない最初の向きにする。
    /// 着弾点を含む部品（当たった装甲板など）は数えない。どれも遮られるなら砲弾の線そのもの。
    /// </summary>
    private void ChooseView(HitView hit)
    {
        var entry = hit.Entry;
        var along = hit.Direction;
        var bounds = _capture.Bounds;
        hit.Distance = hit.Outcome == HitCamOutcome.Penetration
            ? Mathf.Clamp(hit.Spread * 1.3f + 1.0f, 2.2f, 4.5f)
            : 3.0f;

        // 遮るかもしれない部品の箱
        _occluders.Clear();
        foreach (var part in _shownParts)
        {
            var box = WorldBox(part);
            var near = box;
            near.Expand(0.1f);
            if (near.Contains(entry) || box.size.sqrMagnitude < 0.0025f)
                continue;
            _occluders.Add(box);
        }

        var side = Vector3.Cross(Vector3.up, along);
        var sign = Vector3.Dot(entry - bounds.center, side) >= 0f ? 1f : -1f;
        var heading = Vector3.ProjectOnPlane(along, Vector3.up);
        if (heading.sqrMagnitude < 1e-4f)
            heading = Vector3.ProjectOnPlane(bounds.center - entry, Vector3.up);
        if (heading.sqrMagnitude < 1e-4f)
            heading = Vector3.forward;
        var yaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
        // 砲弾の上下の角度（下向きが正）
        var shellPitch = Mathf.Asin(Mathf.Clamp(-along.y, -1f, 1f)) * Mathf.Rad2Deg;

        // (横へずらす角度, 見下ろす角度)
        var candidates = new (float Yaw, float Pitch)[]
        {
            (25f, 20f), (15f, 14f), (35f, 28f), (0f, Mathf.Clamp(shellPitch, 5f, 35f)), (-20f, 20f),
            (10f, 40f), (45f, 18f), (-35f, 30f), (0f, 55f),
        };
        hit.View = along; // どれも遮られるなら、砲弾が通ってきた線
        foreach (var (dy, pitch) in candidates)
        {
            var view = Quaternion.Euler(pitch, yaw - sign * dy, 0f) * Vector3.forward;
            if (Clear(entry, view, hit.Distance * 1.3f))
            {
                hit.View = view;
                break;
            }
        }
    }

    /// <summary>カメラ（着弾点から view の逆向きに distance）から着弾点の手前まで、どの部品の箱も通らないか。</summary>
    private bool Clear(Vector3 entry, Vector3 view, float distance)
    {
        var origin = entry - view * distance;
        var length = distance - 0.15f;
        foreach (var box in _occluders)
        {
            if (box.Contains(origin))
                return false;
            if (box.IntersectRay(new Ray(origin, view), out var hit) && hit < length)
                return false;
        }
        return true;
    }

    private static Bounds WorldBox(CapturedPart part) => HitCamTracker.WorldBox(part);

    private readonly List<Bounds> _occluders = new();

    private void SetCamera(Vector3 target, Vector3 view, float distance)
    {
        _camera.transform.localPosition = target - view * distance;
        _camera.transform.localRotation = Quaternion.LookRotation(view, Vector3.up);
    }

    // ------------------------------------------------------------------ 経路の線・効果

    private void DrawPaths(float t, float width, float height, float scale)
    {
        var shellColour = HudStyle.Hex("FFE48A");
        var spallCore = HudStyle.Hex("FFE6A0");
        var spallTrail = HudStyle.Hex("FFB25C");
        var eye = _camera.transform.localPosition;
        foreach (var s in _segments)
        {
            if (t < s.T0)
                continue;
            var k = s.T1 > s.T0 ? Mathf.Clamp01((t - s.T0) / (s.T1 - s.T0)) : 1f;
            var head = Vector3.Lerp(s.A, s.B, k);
            if (s.Kind == SegmentKind.Shell)
            {
                if (!Project(s.A, width, height, out var a) || !Project(head, width, height, out var b))
                    continue;
                // 着いた後は早めに薄くする（破片が見えるように）
                var fade = k >= 1f ? Mathf.Clamp01(1f - (t - s.T1) / 1.2f) * 0.6f + 0.25f : 1f;
                // 砲弾: 太く光る線と、大きく光る先端
                AddLine(a, b, 12f * scale, new Color(1f, 0.62f, 0.2f, fade * 0.35f));
                var core = shellColour;
                core.a = fade;
                AddLine(a, b, 3.5f * scale, core);
                if (k < 1f)
                {
                    AddDot(b, 34f * scale, new Color(1f, 0.6f, 0.15f, 0.75f), _glowSprite);
                    AddDot(b, 12f * scale, Color.white, _glowSprite);
                }
                continue;
            }

            // 破片: 尾を引く光。カメラに近いほど太い。傷つけた破片は明るく残し、ほかは着いたらすぐ薄くする
            var near = Mathf.Clamp(2.5f / Mathf.Max(0.3f, (head - eye).magnitude), 0.6f, 1.8f) * scale;
            var landed = t - s.T1;
            var pathAlpha = s.Important
                ? (k < 1f ? 0.65f : Mathf.Lerp(0.95f, 0.55f, Mathf.Clamp01(landed / 2f)))
                : (k < 1f ? 0.4f : 0.4f * Mathf.Clamp01(1f - landed / 1.2f));
            if (!Project(head, width, height, out var h))
                continue;
            if (pathAlpha > 0.01f && Project(s.A, width, height, out var from))
            {
                var path = spallTrail;
                path.a = pathAlpha;
                AddLine(from, h, (s.Important ? 2f : 1.2f) * near, path);
            }
            if (k < 1f)
            {
                var length = (s.B - s.A).magnitude;
                var tailK = length > 1e-3f ? Mathf.Max(0f, k - 0.9f / length) : 0f;
                if (Project(Vector3.Lerp(s.A, s.B, tailK), width, height, out var tail))
                {
                    AddLine(tail, h, (s.Important ? 9f : 6f) * near, new Color(1f, 0.5f, 0.12f, s.Important ? 0.45f : 0.3f));
                    AddLine(tail, h, (s.Important ? 3.5f : 2.5f) * near, spallCore);
                }
                AddDot(h, (s.Important ? 20f : 13f) * near, new Color(1f, 0.85f, 0.5f, 0.95f), _glowSprite);
            }
            else if (s.Stopped && pathAlpha > 0.05f)
                AddDot(h, 5f * near, new Color(1f, 0.35f, 0.15f, pathAlpha), _glowSprite);
        }

        // 破片が傷つけた部品に届いた所の火花
        foreach (var spark in _sparks)
        {
            var dt = t - spark.Time;
            if (dt < 0f || dt > 0.6f || !Project(spark.Position, width, height, out var p))
                continue;
            var k = dt / 0.6f;
            AddDot(p, Mathf.Lerp(10f, 40f, Mathf.Sqrt(k)) * scale, new Color(1f, 0.25f, 0.08f, 0.9f * (1f - k)), _glowSprite);
            AddDot(p, Mathf.Lerp(8f, 14f, k) * scale, new Color(1f, 1f, 0.85f, 1f - k), _glowSprite);
        }
    }

    /// <summary>命中点の閃光（貫通 = 赤、それ以外 = 黄色い火花）。</summary>
    private void DrawImpact(float t, float width, float height, float scale)
    {
        foreach (var hit in _hits)
        {
            var dt = t - hit.EntryTime;
            if (dt < 0f || dt > 0.9f || !Project(hit.Entry, width, height, out var p))
                continue;
            var k = dt / 0.9f;
            var penetrated = hit.Outcome == HitCamOutcome.Penetration;
            var colour = penetrated ? new Color(1f, 0.12f, 0.05f, 0.85f * (1f - k)) : new Color(1f, 0.85f, 0.4f, 0.85f * (1f - k));
            AddDot(p, Mathf.Lerp(20f, 90f, Mathf.Sqrt(k)) * scale, colour, _glowSprite);
            AddDot(p, Mathf.Lerp(14f, 30f, k) * scale, new Color(1f, 1f, 0.9f, 1f - k), _glowSprite);
        }
    }

    /// <summary>誘爆・火災: 赤い輪が何重にも広がる。</summary>
    private void DrawBursts(float t, float width, float height)
    {
        foreach (var burst in _bursts)
        {
            var dt = t - burst.Start;
            if (dt < 0f || !Project(burst.Position, width, height, out var centre))
                continue;
            // 1 m が画面で何ピクセルか
            var right = _camera.transform.right;
            if (!Project(burst.Position + right, width, height, out var edge))
                continue;
            // 輪の大きさは 1 m の見かけの 2 倍（小さいと見えにくい）
            var metre = Mathf.Clamp((edge - centre).magnitude * 2f, 8f, height * 0.44f);
            var colour = burst.Fire ? new Color(1f, 0.32f, 0.06f, 1f) : new Color(1f, 0.1f, 0.06f, 1f);
            // 中心の光
            var glow = colour;
            glow.a = 0.45f + 0.15f * Mathf.Sin(dt * 6f);
            AddDot(centre, metre * 0.8f, glow, _glowSprite);
            // 0.4 秒ごとに新しい輪が生まれ、1.4 秒かけて広がりながら消える
            const float period = 0.4f;
            const float life = 1.4f;
            for (var n = 0; n < 4; n++)
            {
                var age = dt - n * period;
                if (age < 0f)
                    continue;
                age %= period * 4f;
                if (age > life)
                    continue;
                var k = age / life;
                var ring = colour;
                ring.a = (1f - k) * 0.7f;
                AddDot(centre, metre * Mathf.Lerp(0.2f, 1.5f, Mathf.Sqrt(k)), ring, _ringSprite);
            }
        }
    }

    private bool Project(Vector3 point, float width, float height, out Vector2 screen)
    {
        var v = _camera.WorldToViewportPoint(StageOrigin + point);
        screen = new Vector2(v.x * width, v.y * height);
        return v.z > 0.05f;
    }

    private void AddLine(Vector2 a, Vector2 b, float thickness, Color colour)
    {
        var d = b - a;
        var length = d.magnitude;
        if (length < 0.5f)
            return;
        if (_lineCount >= _lines.Count)
        {
            var image = HudStyle.CreateImage(_overlay, "Line", false);
            image.rectTransform.pivot = new Vector2(0f, 0.5f);
            _lines.Add(image);
        }
        var line = _lines[_lineCount++];
        HudStyle.SetActive(line, true);
        line.color = colour;
        var rt = line.rectTransform;
        rt.anchoredPosition = a;
        rt.sizeDelta = new Vector2(length, Mathf.Max(1f, thickness));
        rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    private void AddDot(Vector2 p, float size, Color colour, Sprite sprite)
    {
        if (_dotCount >= _dots.Count)
        {
            var image = HudStyle.CreateImage(_overlay, "Dot", false);
            image.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _dots.Add(image);
        }
        var dot = _dots[_dotCount++];
        HudStyle.SetActive(dot, true);
        if (dot.sprite != sprite)
            dot.sprite = sprite;
        dot.color = colour;
        var rt = dot.rectTransform;
        rt.anchoredPosition = p;
        rt.sizeDelta = new Vector2(size, size);
    }

    // ------------------------------------------------------------------ 光と輪の絵

    private static unsafe Sprite MakeRadial(bool ring)
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                float a;
                if (ring)
                {
                    // 外側が濃く、内側へぼける輪
                    var band = Mathf.Clamp01(1f - Mathf.Abs(r - 0.86f) / 0.11f);
                    var inner = Mathf.Clamp01(1f - Mathf.Abs(r - 0.7f) / 0.3f) * 0.25f;
                    a = r > 1f ? 0f : Mathf.Max(band, inner);
                }
                else
                {
                    a = Mathf.Clamp01(1f - r);
                    a *= a;
                }
                var i = (y * size + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Mathf.RoundToInt(a * 255f);
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = pixels)
            texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // ------------------------------------------------------------------ 文字

    private string AccentHex() => _kill ? HudStyle.Red
        : _capture?.Outcome switch
        {
            HitCamOutcome.Penetration => HudStyle.Orange,
            HitCamOutcome.Ricochet => "7FC8FF",
            _ => HudStyle.Grey,
        };

    private void Compose()
    {
        if (!EnsureUi())
            return;
        var title = _kill ? "KNOCKED OUT"
            : _capture.Outcome switch
            {
                HitCamOutcome.Penetration => "PENETRATION",
                HitCamOutcome.Ricochet => "RICOCHET",
                _ => "NON-PENETRATION",
            };
        var ammo = string.IsNullOrEmpty(_capture.Ammo) ? "" : "<size=80%><color=#D8D8D8>  " + T(_capture.Ammo) + "</color></size>";
        _title.text = "<b><color=#" + AccentHex() + ">" + T(title) + "</color></b>" + ammo;
        _name.text = "<color=#FF6A5A><noparse>" + (_capture.VictimName ?? "?") + "</noparse></color>";
        _banner.text = "<b><color=#FF5A47>" + T("KNOCKED OUT") + "</color></b>";
    }

    private static string T(string english) => Plugin.Localization?.Translate(english) ?? english;

    // ------------------------------------------------------------------ 作成・破棄

    private void Hide()
    {
        if (_root != null && !_root.WasCollected && _root.activeSelf)
            _root.SetActive(false);
        if (_stage != null && !_stage.WasCollected && _stage.activeSelf)
            _stage.SetActive(false);
    }

    private bool EnsureUi()
    {
        if (_root != null && _root.WasCollected)
        {
            _root = null;
            _lines.Clear();
            _dots.Clear();
        }
        if (_glowSprite == null || _glowSprite.WasCollected)
            _glowSprite = MakeRadial(false);
        if (_ringSprite == null || _ringSprite.WasCollected)
            _ringSprite = MakeRadial(true);
        if (_root != null)
            return true;
        if (HudStyle.Font == null)
            return false;
        _root = HudStyle.CreateCanvas("SprocketHitCam", 30003, out _group);
        _panel = HudStyle.CreateImage(_root.transform, "Panel", true);
        _image = new GameObject("View").AddComponent<RawImage>();
        _image.transform.SetParent(_root.transform, false);
        _image.raycastTarget = false;
        var irt = _image.rectTransform;
        irt.anchorMin = irt.anchorMax = Vector2.zero;
        irt.pivot = Vector2.zero;

        var overlay = new GameObject("Paths");
        overlay.transform.SetParent(_root.transform, false);
        _overlay = overlay.AddComponent<RectTransform>();
        _overlay.anchorMin = _overlay.anchorMax = Vector2.zero;
        _overlay.pivot = Vector2.zero;
        overlay.AddComponent<RectMask2D>();

        _accent = HudStyle.CreateImage(_root.transform, "Accent", true);
        _title = HudStyle.CreateText(_root.transform, "Title", TextAlignmentOptions.MidlineLeft);
        _name = HudStyle.CreateText(_root.transform, "Name", TextAlignmentOptions.MidlineRight);
        _banner = HudStyle.CreateText(_root.transform, "Banner", TextAlignmentOptions.Center);
        _banner.outlineWidth = 0.25f;
        _root.SetActive(false);
        return true;
    }
}
