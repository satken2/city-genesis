using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

public partial class CameraController
{
    [Header("ミニチュアモード設定")]
    [SerializeField] private Button dioramaModeButton;
    [Min(0f)] public float dioramaBlurStrength = 1.5f;             // Blur per unit of (1 - focus / depth), as a fraction of the maximum radius.
    [Range(0f, 0.05f)] public float dioramaMaxBlurRadius = 0.015f; // Maximum blur radius as a fraction of the screen height.

    private bool isDioramaMode;
    private DioramaBlurPass dioramaPass;
    private float dioramaFocusDistance; // Kept while the screen center sees no ground, such as when looking above the horizon.

    public void ToggleDioramaMode()
    {
        isDioramaMode = !isDioramaMode;
        if (isDioramaMode)
        {
            dioramaPass ??= new DioramaBlurPass();
            dioramaFocusDistance = 0f;
            RenderPipelineManager.beginCameraRendering += EnqueueDioramaPass;
        }
        else
        {
            RenderPipelineManager.beginCameraRendering -= EnqueueDioramaPass;
        }

        // Leaving a Button selected also treats Space/arrow keys as Submit/Navigation.
        EventSystem.current?.SetSelectedGameObject(null);
    }

    // Inject only into this camera so the compass and thumbnail cameras stay sharp.
    private void EnqueueDioramaPass(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (renderingCamera != cam) return;
        if (TryGetRotationPivotPoint(out Vector3 focusPoint, false))
        {
            dioramaFocusDistance = Vector3.Dot(focusPoint - transform.position, transform.forward);
        }
        if (dioramaFocusDistance <= 0f) return;

        dioramaPass.Setup(dioramaFocusDistance, dioramaBlurStrength, dioramaMaxBlurRadius);
        renderingCamera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(dioramaPass);
    }

    private void DisposeDioramaMode()
    {
        RenderPipelineManager.beginCameraRendering -= EnqueueDioramaPass;
        dioramaPass?.Dispose();
        dioramaPass = null;
    }
}
