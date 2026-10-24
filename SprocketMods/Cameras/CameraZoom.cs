using System;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Vehicles.AttachedBehaviours;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.VehicleControl;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMods.Cameras;

/// <summary>War Thunder 風の右クリック操作の設定（[Camera] セクション）。</summary>
public class CameraConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyboardShortcut> Key;
    public readonly ConfigEntry<bool> ScopeZoomToggle;
    public readonly ConfigEntry<bool> CupolaView;
    public readonly ConfigEntry<float> TransitionTime;
    public readonly ConfigEntry<float> CupolaHeightOffset;

    public CameraConfig(ConfigFile cfg)
    {
        const string s = "Camera";
        Enabled = cfg.Bind(s, "Enabled", true, "War Thunder 風の右クリック操作（照準器のズーム切替・キューポラ視点）");
        Key = cfg.Bind(s, "Key", new KeyboardShortcut(KeyCode.Mouse1), "切り替えキー（既定: 右クリック）");
        ScopeZoomToggle = cfg.Bind(s, "ScopeZoomToggle", true, "照準器を覗いているとき、キーで最大倍率と最低倍率を切り替える");
        CupolaView = cfg.Bind(s, "CupolaView", true, "三人称視点のとき、キーで車長キューポラ視点と三人称視点を切り替える");
        TransitionTime = cfg.Bind(s, "TransitionTime", 0.35f,
            new ConfigDescription("切り替えにかける時間（秒）。イージング付き", new AcceptableValueRange<float>(0.05f, 0.5f)));
        CupolaHeightOffset = cfg.Bind(s, "CupolaHeightOffset", 0.15f,
            new ConfigDescription("キューポラ視点の高さの補正（m、車体の上方向）", new AcceptableValueRange<float>(-1f, 2f)));
    }
}

/// <summary>
/// War Thunder 風の右クリック操作。
///  - 照準器を覗いているとき: 最大倍率 ⇔ 最低倍率
///  - 三人称視点のとき: 車長キューポラ視点 ⇔ 三人称視点
/// どちらもイージング付きで TransitionTime 秒（0.5 秒以内）かけて滑らかに切り替える。
///
/// 照準器のズームは ScopeCameraController の焦点距離（targetFocalLength / appliedFocalLength、大きいほど拡大）を補間する。
/// キューポラ視点は三人称カメラ（OrbitalMovementController）の仕組みをそのまま使う。三人称カメラの注視点は
///   遠景追従対象.TransformPoint(遠景オフセット) と 近景追従対象.TransformPoint(近景オフセット) を距離で補間した点
/// で、カメラはそこから distance だけ後ろに置かれる。そこで両方のオフセットを「キューポラ位置を各追従対象のローカル座標に
/// 直した値」へ、distance を 0 へ補間する。視点操作・照準（画面中央の照準線）・カメラの揺れはゲームの処理がそのまま働く。
/// 値はゲームのカメラ処理（LateUpdate）より前の Update で書き込む。
/// </summary>
internal sealed unsafe class CameraZoom
{
    // OrbitalMovementController（IL2CPP メタデータで確認したオフセット）
    private const int OrbitalMinDistance = 0x70;
    private const int OrbitalTracking = 0x8E;
    private const int OrbitalFarTarget = 0xA0;
    private const int OrbitalCloseTarget = 0xA8;
    private const int OrbitalFarOffset = 0xB0;
    private const int OrbitalCloseOffset = 0xBC;
    private const int OrbitalDistance = 0x10C;
    private const int OrbitalTargetDistance = 0x110;

    // ScopeCameraController
    private const int ScopeScroll = 0x4C;
    private const int ScopeTargetFocal = 0x7C;
    private const int ScopeAppliedFocal = 0x80;
    private const int ScopeMinZoom = 0x88;
    private const int ScopeMaxZoom = 0x8C;

    private const float CupolaDistance = 0.02f;

    private VehicleControlPlayerState _state;
    private float _nextStateSearch;

    // 照準器のズーム
    private bool _zooming;
    private float _zoomTime;
    private float _zoomFrom;
    private float _zoomTo;

    // キューポラ視点
    private IntPtr _orbital;
    private IntPtr _cupolaVehicle;
    private bool _cupolaWanted;
    private float _cupolaBlend; // 0 = 三人称, 1 = キューポラ（時間の割合。イージング前）
    private Vector3 _savedFarOffset;
    private Vector3 _savedCloseOffset;
    private float _savedDistance;
    private float _savedMinDistance;

    public static float Ease(float t)
    {
        t = Mathf.Clamp01(t);
        return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f; // easeInOutCubic
    }

    public void Update()
    {
        var cfg = Plugin.Camera;
        var state = GetState();
        if (!cfg.Enabled.Value || state == null || !state.StateActive)
        {
            ResetAll();
            return;
        }

        var vc = state.vehicleController;
        var body = vc?.ControlledVehicle?.Rigidbody;
        if (body == null)
        {
            ResetAll();
            return;
        }

        var scope = state.scopeController;
        var scoped = scope != null && (scope.Scoped || scope.ScopeRequested);
        var orbital = state.cameraMovementController;
        var thirdPerson = orbital != null && state.activeMovementController != null
                          && state.activeMovementController.Pointer == orbital.Pointer;

        // 修飾キーは見ない（照準器を Shift 長押しで覗いていても反応させる）
        if (BepInEx.UnityInput.Current.GetKeyDown(cfg.Key.Value.MainKey) && InputAllowed(vc))
        {
            if (scoped && cfg.ScopeZoomToggle.Value)
                StartScopeZoom(state.scopeMovementController);
            else if (thirdPerson && cfg.CupolaView.Value)
                _cupolaWanted = !_cupolaWanted;
        }

        UpdateScopeZoom(state.scopeMovementController, scoped);
        UpdateCupola(orbital, body);
    }

    /// <summary>メニュー表示中（カーソルが出ている）・ポーズ中・味方への命令中は反応しない（右クリックは命令の取り消しと共用）。</summary>
    private static bool InputAllowed(Sprocket.Gameplay.VehicleControl.VehicleController vc)
    {
        if (Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked)
            return false;
        return vc.currentOrderConstruction == null;
    }

    private VehicleControlPlayerState GetState()
    {
        if (_state != null && _state.WasCollected)
            _state = null;
        if (_state == null && Time.unscaledTime >= _nextStateSearch)
        {
            _nextStateSearch = Time.unscaledTime + 1f;
            _state = Object.FindAnyObjectByType(Il2CppType.Of<VehicleControlPlayerState>())?.TryCast<VehicleControlPlayerState>();
        }
        return _state;
    }

    private void ResetAll()
    {
        _zooming = false;
        RestoreCupola();
    }

    // ---------------------------------------------------------------- 照準器のズーム

    private void StartScopeZoom(ScopeCameraController scope)
    {
        if (scope == null)
            return;
        var p = (byte*)scope.Pointer;
        var min = *(float*)(p + ScopeMinZoom);
        var max = *(float*)(p + ScopeMaxZoom);
        var current = *(float*)(p + ScopeAppliedFocal);
        if (!(max > min))
            return;
        _zoomFrom = current;
        // 今が中間より拡大寄りなら最低倍率へ、そうでなければ最大倍率へ（拡大中にもう一度押すと戻る）
        var target = _zooming ? _zoomTo : *(float*)(p + ScopeTargetFocal);
        _zoomTo = target > (min + max) * 0.5f ? min : max;
        _zoomTime = 0f;
        _zooming = true;
    }

    private void UpdateScopeZoom(ScopeCameraController scope, bool scoped)
    {
        if (!_zooming)
            return;
        if (scope == null || !scoped)
        {
            _zooming = false;
            return;
        }
        var p = (byte*)scope.Pointer;
        if (Mathf.Abs(*(float*)(p + ScopeScroll)) > 0.0001f)
        {
            _zooming = false; // ホイール操作を優先
            return;
        }
        _zoomTime += Time.unscaledDeltaTime;
        var t = _zoomTime / Plugin.Camera.TransitionTime.Value;
        var value = Mathf.LerpUnclamped(_zoomFrom, _zoomTo, Ease(t));
        *(float*)(p + ScopeTargetFocal) = value;
        *(float*)(p + ScopeAppliedFocal) = value;
        if (t >= 1f)
            _zooming = false;
    }

    // ---------------------------------------------------------------- キューポラ視点

    private void UpdateCupola(OrbitalMovementController orbital, Rigidbody body)
    {
        if (orbital == null)
        {
            RestoreCupola();
            return;
        }
        // 乗り換え・三人称カメラの作り直しがあったら元に戻してから
        if (_cupolaBlend > 0f && (orbital.Pointer != _orbital || body.Pointer != _cupolaVehicle))
        {
            RestoreCupola();
            _cupolaWanted = false;
        }

        var target = _cupolaWanted ? 1f : 0f;
        if (_cupolaBlend <= 0f && target <= 0f)
            return;

        var p = (byte*)orbital.Pointer;
        if (p[OrbitalTracking] == 0)
        {
            RestoreCupola();
            _cupolaWanted = false;
            return;
        }

        if (_cupolaBlend <= 0f)
        {
            // 切り替え開始時に元の値を覚える
            _orbital = orbital.Pointer;
            _cupolaVehicle = body.Pointer;
            _savedFarOffset = *(Vector3*)(p + OrbitalFarOffset);
            _savedCloseOffset = *(Vector3*)(p + OrbitalCloseOffset);
            _savedDistance = *(float*)(p + OrbitalTargetDistance);
            _savedMinDistance = *(float*)(p + OrbitalMinDistance);
        }

        _cupolaBlend = Mathf.MoveTowards(_cupolaBlend, target, Time.unscaledDeltaTime / Plugin.Camera.TransitionTime.Value);
        if (_cupolaBlend <= 0f)
        {
            RestoreCupola();
            return;
        }

        var e = Ease(_cupolaBlend);
        var point = GetCupolaPoint(body);
        WriteOffset(p, OrbitalFarTarget, OrbitalFarOffset, _savedFarOffset, point, e);
        WriteOffset(p, OrbitalCloseTarget, OrbitalCloseOffset, _savedCloseOffset, point, e);

        var distance = Mathf.Lerp(_savedDistance, CupolaDistance, e);
        *(float*)(p + OrbitalMinDistance) = Mathf.Min(_savedMinDistance, CupolaDistance);
        *(float*)(p + OrbitalTargetDistance) = distance;
        *(float*)(p + OrbitalDistance) = distance;
    }

    private static void WriteOffset(byte* orbital, int targetField, int offsetField, Vector3 original, Vector3 worldPoint, float blend)
    {
        var transformPtr = *(IntPtr*)(orbital + targetField);
        if (transformPtr == IntPtr.Zero)
            return;
        var local = new Transform(transformPtr).InverseTransformPoint(worldPoint);
        *(Vector3*)(orbital + offsetField) = Vector3.LerpUnclamped(original, local, blend);
    }

    private void RestoreCupola()
    {
        if (_cupolaBlend > 0f && _orbital != IntPtr.Zero)
        {
            var orbital = GetState()?.cameraMovementController;
            if (orbital != null && orbital.Pointer == _orbital)
            {
                var p = (byte*)_orbital;
                *(Vector3*)(p + OrbitalFarOffset) = _savedFarOffset;
                *(Vector3*)(p + OrbitalCloseOffset) = _savedCloseOffset;
                *(float*)(p + OrbitalTargetDistance) = _savedDistance;
                *(float*)(p + OrbitalDistance) = _savedDistance;
                *(float*)(p + OrbitalMinDistance) = _savedMinDistance;
            }
        }
        _cupolaBlend = 0f;
        _orbital = IntPtr.Zero;
    }

    /// <summary>
    /// 車長キューポラ（CommanderCupola）のカメラ位置。複数あれば一番高いもの。
    /// 無ければ一番高い視察口、それも無ければ重心の上 2 m。
    /// </summary>
    private static Vector3 GetCupolaPoint(Rigidbody body)
    {
        var up = body.transform.up;
        var offset = up * Plugin.Camera.CupolaHeightOffset.Value;
        var root = body.gameObject;

        var best = Vector3.zero;
        var found = false;
        var cupolas = root.GetComponentsInChildren<CommanderCupola>();
        if (cupolas != null)
        {
            foreach (var cupola in cupolas)
            {
                if (cupola == null)
                    continue;
                var local = cupola.cameraTrackLocalPosition;
                if (local == Vector3.zero)
                    local = cupola.objectiveLensLocalPosition;
                var world = cupola.transform.TransformPoint(local);
                if (!found || Vector3.Dot(world - best, up) > 0f)
                {
                    best = world;
                    found = true;
                }
            }
        }
        if (!found)
        {
            var viewports = root.GetComponentsInChildren<Viewport>();
            if (viewports != null)
            {
                foreach (var viewport in viewports)
                {
                    if (viewport == null)
                        continue;
                    var world = viewport.transform.TransformPoint(viewport.ObjectiveLensLocalPosition);
                    if (!found || Vector3.Dot(world - best, up) > 0f)
                    {
                        best = world;
                        found = true;
                    }
                }
            }
        }
        if (!found)
            best = body.worldCenterOfMass + up * 2f;
        return best + offset;
    }
}
