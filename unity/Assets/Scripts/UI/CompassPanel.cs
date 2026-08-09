using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 画面上のCompassPanelに立体の方位磁針を表示するコンポーネント。
/// CanvasのUIパネル(RectTransform)に貼り付けて使う。
///
/// 仕組み:
///   UIは2Dなので、3Dの方位磁針は「専用カメラ+レンダーテクスチャ」で描いた絵を
///   RawImageとしてパネルに貼る。方位磁針の実体(リグ)はワールドの遥か上空
///   (RigWorldPosition)に置き、専用カメラの被写界に本編のオブジェクトが写り込まないようにする。
///   専用レイヤー(compassLayerName)が定義されていればカリングマスクでも二重に隔離する。
///
///   表示物は北を指す赤い四角錐(針)と「N」の文字だけ。針を本編カメラのヨーの
///   逆方向に回すことで、常に北がワールド+Z方向を指す。「N」は針の先端に付いて回るが、
///   どの方位でも読めるよう常に専用カメラの方を向く(ビルボード)。
///   俯瞰の角度は本編カメラの俯角に追従させ、真上モードでは真上から、
///   低い視点では浅い角度から見た絵になる(3D空間の見え方と一致させるため)。
///
/// 描画コスト:
///   カメラのヨー・ピッチが変わったフレームだけカメラを有効化して1枚描き直し、
///   静止中はレンダーテクスチャの内容を使い回す(カメラを毎フレーム回さない)。
///
/// 見た目を差し替えたい場合は compassModelPrefab にアート製の3Dモデルを割り当てる。
/// 未割り当てなら CompassNeedleMesh の手続き生成メッシュを使う。
/// </summary>
[RequireComponent(typeof(RectTransform))]
// FloatingOriginはワールド全体をずらす際にシーン中の全Transformを個別に動かすため、
// リグとその子の相対位置が崩れる。それを同じフレームのうちに組み直せるよう、
// FloatingOrigin(既定の実行順0)より後にLateUpdateが回るようにする。
[DefaultExecutionOrder(200)]
public class CompassPanel : MonoBehaviour
{
    [Header("表示先")]
    [Tooltip("方位磁針を描画するRawImage。未設定なら自分の子として自動生成する")]
    [SerializeField] private RawImage targetImage;
    [Tooltip("レンダーテクスチャの一辺(px)。パネル実寸の2倍程度にするとエッジが滑らかになる")]
    [SerializeField] private int renderTextureSize = 256;

    [Header("見た目")]
    [Tooltip("差し替え用の方位磁針モデル。未設定ならメッシュを手続き生成する")]
    [SerializeField] private GameObject compassModelPrefab;
    [Tooltip("方位磁針を隔離する専用レイヤー名。存在しない場合はDefaultで描く")]
    [SerializeField] private string compassLayerName = "Compass";

    [Header("視点")]
    [Tooltip("見下ろし角の下限(度)。水平に近い視点でも潰れて読めなくならないようにする")]
    [SerializeField] private float minViewPitch = 22f;
    [Tooltip("見下ろし角の上限(度)。真上に近づくとカメラの上方向が不定になるため90度未満にする")]
    [SerializeField] private float maxViewPitch = 78f;

    /// <summary>方位磁針のリグを置くワールド座標。本編の描画範囲から完全に外れる高度に置く</summary>
    private static readonly Vector3 RigWorldPosition = new Vector3(0f, 100000f, 0f);
    /// <summary>専用カメラとリグの距離。正射投影なので見た目の大きさには影響しない</summary>
    private const float CameraDistance = 5f;
    /// <summary>正射投影の半画角。針の先端+「N」の文字が収まる範囲に余白を足した値</summary>
    private const float CameraOrthoSize = 1.25f;
    /// <summary>「N」の文字を置く位置(針のローカル座標)。先端の少し先・少し上に浮かせる</summary>
    private static readonly Vector3 LabelLocalPosition = new Vector3(0f, 0.30f, CompassNeedleMesh.TipZ + 0.12f);

    private GameObject rig;
    private Transform needleTransform;
    private Transform labelTransform;
    private Camera compassCamera;
    private RenderTexture renderTexture;
    private Material compassMaterial;
    private Mesh needleMesh;
    private Mesh labelMesh;

    // 方位磁針を隔離するレイヤー。0(Default)なら専用レイヤー未定義で、上空配置だけで隔離する
    private int compassLayer;
    // 本編カメラのカリングマスクから方位磁針レイヤーを外したか(カメラは起動直後だと未確定のため遅延実行)
    private bool mainCameraMaskApplied;

    // 直近に適用したカメラ角。変化がないフレームは描き直さない
    private bool hasAppliedView;
    private float lastYaw;
    private float lastViewPitch;
    private bool needsRender = true;

    private void Awake()
    {
        BuildRenderTarget();
        BuildRig();
    }

    private void OnEnable()
    {
        // 再表示時はレンダーテクスチャの内容が失われている可能性があるため必ず描き直す
        needsRender = true;
    }

    private void OnDisable()
    {
        if (compassCamera != null) compassCamera.enabled = false;
    }

    private void OnDestroy()
    {
        if (rig != null) Destroy(rig);
        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
        }
        if (compassMaterial != null) Destroy(compassMaterial);
        if (needleMesh != null) Destroy(needleMesh);
        if (labelMesh != null) Destroy(labelMesh);
    }

    /// <summary>
    /// 表示先のRawImageとレンダーテクスチャを用意する。
    /// パネルが正方形でなくても方位磁針が歪まないよう、AspectRatioFitterで
    /// 1:1を保ったままパネル内に収める。
    /// </summary>
    private void BuildRenderTarget()
    {
        renderTexture = new RenderTexture(renderTextureSize, renderTextureSize, 16, RenderTextureFormat.ARGB32)
        {
            name = "CompassRT",
            antiAliasing = 4,
            filterMode = FilterMode.Bilinear,
        };
        renderTexture.Create();

        if (targetImage == null)
        {
            GameObject imageGO = new GameObject("CompassImage", typeof(RectTransform));
            imageGO.layer = gameObject.layer;
            imageGO.transform.SetParent(transform, false);
            targetImage = imageGO.AddComponent<RawImage>();

            RectTransform rt = targetImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            AspectRatioFitter fitter = imageGO.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 1f;
        }

        // クリックは背後のUI・3D空間へ通す(方位磁針は情報表示だけの部品)
        targetImage.raycastTarget = false;
        targetImage.texture = renderTexture;
    }

    /// <summary>
    /// 上空に方位磁針のリグ(針・「N」の文字・専用カメラ)を組み立てる。
    /// </summary>
    private void BuildRig()
    {
        compassLayer = LayerMask.NameToLayer(compassLayerName);
        if (compassLayer < 0) compassLayer = 0;
        int layer = compassLayer;

        rig = new GameObject("CompassRig");
        rig.transform.position = RigWorldPosition;

        compassMaterial = new Material(Shader.Find("Custom/CompassUnlit")) { name = "CompassUnlit" };

        if (compassModelPrefab != null)
        {
            // アート製モデルを使う場合、ローカル+Zを北・原点を回転中心とする規約に従わせる。
            // モデル全体を針として扱い、「N」の文字はモデル側に含める前提とする。
            GameObject model = Instantiate(compassModelPrefab, rig.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            needleTransform = model.transform;
        }
        else
        {
            needleMesh = CompassNeedleMesh.BuildNeedle();
            labelMesh = CompassNeedleMesh.BuildNorthLabel();
            needleTransform = CreatePart("CompassNeedle", needleMesh, rig.transform);
            // 「N」は針の子にして先端と一緒に回り、向きだけ毎回カメラへ向け直す
            labelTransform = CreatePart("CompassNorthLabel", labelMesh, needleTransform);
            labelTransform.localPosition = LabelLocalPosition;
        }

        SetLayerRecursively(rig, layer);

        GameObject camGO = new GameObject("CompassCamera");
        camGO.transform.SetParent(rig.transform, false);
        compassCamera = camGO.AddComponent<Camera>();
        compassCamera.orthographic = true;
        compassCamera.orthographicSize = CameraOrthoSize;
        compassCamera.nearClipPlane = 0.01f;
        compassCamera.farClipPlane = CameraDistance * 2f;
        compassCamera.clearFlags = CameraClearFlags.SolidColor;
        // 背景を完全な透明にして、パネル側の背景がそのまま透ける状態にする
        compassCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        compassCamera.cullingMask = 1 << layer;
        compassCamera.useOcclusionCulling = false;
        compassCamera.allowHDR = false;
        compassCamera.targetTexture = renderTexture;
        compassCamera.enabled = false;
    }

    private Transform CreatePart(string name, Mesh mesh, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = compassMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        return go.transform;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    /// <summary>
    /// カメラの移動処理が終わったあとに追従させたいのでLateUpdateで更新する。
    /// </summary>
    private void LateUpdate()
    {
        if (rig == null || compassCamera == null) return;

        // 追従元は本編カメラ。CameraControllerが居るシーンではそれを正とし、
        // メッシュテスト等の単体シーンではCamera.mainへフォールバックする。
        Transform source = CameraController.Instance != null
            ? CameraController.Instance.transform
            : (Camera.main != null ? Camera.main.transform : null);
        if (source == null) return;

        // 専用レイヤーがある場合は本編カメラの描画対象から外す(上空配置に加えた二重の隔離)。
        // 起動直後は本編カメラが未確定のことがあるため、ここで一度だけ適用する。
        if (!mainCameraMaskApplied && compassLayer > 0)
        {
            Camera sourceCamera = source.GetComponent<Camera>();
            if (sourceCamera != null)
            {
                sourceCamera.cullingMask &= ~(1 << compassLayer);
                mainCameraMaskApplied = true;
            }
        }

        // FloatingOriginのワールドシフトに巻き込まれてリグがずれていたら定位置へ組み直す
        if (rig.transform.position != RigWorldPosition)
        {
            RestoreRigTransforms();
            hasAppliedView = false;
        }

        float yaw = source.eulerAngles.y;
        float pitch = source.eulerAngles.x;
        if (pitch > 180f) pitch -= 360f;
        float viewPitch = Mathf.Clamp(pitch, minViewPitch, maxViewPitch);

        if (!hasAppliedView
            || Mathf.Abs(Mathf.DeltaAngle(yaw, lastYaw)) > 0.01f
            || Mathf.Abs(viewPitch - lastViewPitch) > 0.01f)
        {
            ApplyView(yaw, viewPitch);
            hasAppliedView = true;
            lastYaw = yaw;
            lastViewPitch = viewPitch;
            needsRender = true;
        }

        // アプリの復帰などでレンダーテクスチャが破棄されていたら描き直す
        if (renderTexture != null && !renderTexture.IsCreated())
        {
            renderTexture.Create();
            needsRender = true;
        }

        // 変化があったフレームだけカメラを有効にして1枚描く。
        // 描画後(次フレーム)に無効へ戻すことで、静止中はカメラの実行コストをゼロにする。
        compassCamera.enabled = needsRender;
        needsRender = false;
    }

    /// <summary>
    /// リグを規定のワールド座標へ戻し、子(ケース・カード・カメラ)の位置も組み直す。
    /// カメラのローカル位置は直後のApplyViewが設定するため、ここでは触らない。
    /// </summary>
    private void RestoreRigTransforms()
    {
        rig.transform.position = RigWorldPosition;
        rig.transform.rotation = Quaternion.identity;
        foreach (Transform child in rig.transform)
        {
            if (compassCamera != null && child == compassCamera.transform) continue;
            child.localPosition = Vector3.zero;
        }
    }

    /// <summary>
    /// カードを方位に合わせて回し、専用カメラを俯角に合わせて周回させる。
    /// カメラは常にリグの-Z側から見るため、画面の上がワールド+Z(北)になる。
    /// </summary>
    private void ApplyView(float yaw, float viewPitch)
    {
        if (cardTransform != null)
        {
            cardTransform.localRotation = Quaternion.Euler(0f, -yaw, 0f);
        }

        float rad = viewPitch * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(0f, Mathf.Sin(rad), -Mathf.Cos(rad)) * CameraDistance;
        compassCamera.transform.localPosition = offset;
        compassCamera.transform.localRotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);
    }
}
