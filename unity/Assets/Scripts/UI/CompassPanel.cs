using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a 3D compass in the on-screen CompassPanel.
/// Attach to a Canvas UI panel (RectTransform).
///
/// Mechanism:
///   Since the UI is 2D, render the 3D compass with a dedicated camera and render texture,
///   then show it as a RawImage on the panel. Place the compass rig far above the world
///   at RigWorldPosition so gameplay objects cannot enter the dedicated camera's view.
///   If compassLayerName exists, also isolate it through the culling mask.
///
///   Only the red north-pointing pyramid (needle) and "N" glyph are visible. Rotate the needle opposite
///   to the main camera's yaw so north always indicates world +Z. "N" follows the needle tip,
///   but always faces the dedicated camera as a billboard so it is readable from every bearing.
///   Match the main camera's downward angle: viewed from directly above in top-down mode,
///   or at a shallow angle from low viewpoints, matching the 3D world's appearance.
///
/// Rendering cost:
///   Enable the camera and redraw only on frames where yaw or pitch changes;
///   reuse the render texture while stationary instead of rendering every frame.
///
/// Assign an artist-made 3D model to compassModelPrefab to replace the appearance.
/// Otherwise use CompassNeedleMesh's procedural mesh.
/// </summary>
[RequireComponent(typeof(RectTransform))]
// FloatingOrigin moves each Transform individually when shifting the whole world,
// disrupting the rig's relative child positions. Rebuild them within the same frame
// by running LateUpdate after FloatingOrigin's default execution order of 0.
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

    /// <summary>World position of the compass rig, at an altitude completely outside the gameplay rendering bounds.</summary>
    private static readonly Vector3 RigWorldPosition = new Vector3(0f, 100000f, 0f);
    /// <summary>Distance from the dedicated camera to the rig; orthographic projection makes apparent size independent of this.</summary>
    private const float CameraDistance = 5f;
    /// <summary>Orthographic half-view size, fitting the needle tip and "N" glyph with padding.</summary>
    private const float CameraOrthoSize = 1.25f;
    /// <summary>Position of "N" in needle-local coordinates, slightly beyond and above the tip.</summary>
    private static readonly Vector3 LabelLocalPosition = new Vector3(0f, 0.30f, CompassNeedleMesh.TipZ + 0.12f);

    private GameObject rig;
    private Transform needleTransform;
    private Transform labelTransform;
    private Camera compassCamera;
    private RenderTexture renderTexture;
    private Material compassMaterial;
    private Mesh needleMesh;
    private Mesh labelMesh;

    // Compass isolation layer. 0 (Default) means no dedicated layer; isolation relies only on its high-altitude placement.
    private int compassLayer;
    // Whether the compass layer has been removed from the main camera's culling mask; deferred because the camera may be unresolved at startup.
    private bool mainCameraMaskApplied;

    // Last applied camera angles. Do not redraw frames without changes.
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
        // Always redraw when shown again because the render texture contents may have been lost.
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
    /// Prepare the target RawImage and render texture.
    /// Use AspectRatioFitter to keep a 1:1 aspect ratio inside the panel,
    /// preventing distortion even if the panel is not square.
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

        // Pass clicks through to the UI and 3D world behind; the compass only displays information.
        targetImage.raycastTarget = false;
        targetImage.texture = renderTexture;
    }

    /// <summary>
    /// Build the compass rig high above the world: needle, "N" glyph, and dedicated camera.
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
            // Artist-made models must follow the convention: local +Z is north, origin is the rotation pivot.
            // Treat the whole model as the needle, assuming it includes the "N" glyph.
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
            // Parent "N" to the needle so it follows the tip, resetting only its orientation to face the camera.
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
        // Use a fully transparent background so the panel background shows through unchanged.
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
    /// Update in LateUpdate to follow after camera movement has completed.
    /// </summary>
    private void LateUpdate()
    {
        if (rig == null || compassCamera == null) return;

        // Follow the main gameplay camera. In scenes with CameraController, use its camera as authoritative;
        // fall back to Camera.main in standalone scenes such as mesh tests.
        Transform source = CameraController.Instance != null
            ? CameraController.Instance.transform
            : (Camera.main != null ? Camera.main.transform : null);
        if (source == null) return;

        // If a dedicated layer exists, exclude it from the main camera as additional isolation beyond altitude.
        // Apply this once here because the main camera may not be resolved immediately at startup.
        if (!mainCameraMaskApplied && compassLayer > 0)
        {
            Camera sourceCamera = source.GetComponent<Camera>();
            if (sourceCamera != null)
            {
                sourceCamera.cullingMask &= ~(1 << compassLayer);
                mainCameraMaskApplied = true;
            }
        }

        // Restore the rig to its designated position if a FloatingOrigin world shift displaced it.
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

        // Redraw if the render texture was lost, such as when resuming the application.
        if (renderTexture != null && !renderTexture.IsCreated())
        {
            renderTexture.Create();
            needsRender = true;
        }

        // Enable the camera to draw one image only on changed frames.
        // Disable it again after rendering, on the next frame, eliminating camera execution cost while stationary.
        compassCamera.enabled = needsRender;
        needsRender = false;
    }

    /// <summary>
    /// Restore the rig to its designated world position and rebuild local part positions.
    /// Leave camera and "N" orientations to the subsequent ApplyView call.
    /// </summary>
    private void RestoreRigTransforms()
    {
        rig.transform.position = RigWorldPosition;
        rig.transform.rotation = Quaternion.identity;
        if (needleTransform != null) needleTransform.localPosition = Vector3.zero;
        if (labelTransform != null) labelTransform.localPosition = LabelLocalPosition;
    }

    /// <summary>
    /// Rotate the needle to match bearing and orbit the dedicated camera to match the downward angle.
    /// The camera always views from the rig's -Z side, so screen up is world +Z (north).
    /// At its rotated position, align "N" with the camera so it remains readable head-on
    /// (the glyph reads correctly without mirroring when its local +Z points away from the camera).
    /// </summary>
    private void ApplyView(float yaw, float viewPitch)
    {
        if (needleTransform != null)
        {
            needleTransform.localRotation = Quaternion.Euler(0f, -yaw, 0f);
        }

        float rad = viewPitch * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(0f, Mathf.Sin(rad), -Mathf.Cos(rad)) * CameraDistance;
        compassCamera.transform.localPosition = offset;
        compassCamera.transform.localRotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);

        if (labelTransform != null)
        {
            labelTransform.rotation = compassCamera.transform.rotation;
        }
    }
}
