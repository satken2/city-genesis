using UnityEngine;

/// <summary>
/// Apply visual modes to the sea surface.
/// Hide the Renderer when terrain is transparent (TRANSPARENT) or hidden (MODE_HIDDEN).
/// </summary>
[RequireComponent(typeof(Renderer))]
public class VisualSeaObject : MonoBehaviour, IVisualModeHandler
{
    private static readonly int SeabedViewId = Shader.PropertyToID("_SeabedView");

    private Renderer seaRenderer;

    /// <summary>
    /// Seabed view (terrain flag "seabed"): make water clear enough to see the bottom.
    /// Sea, lakes, and rivers share Custom/WaterLit, so one global value switches every water surface.
    /// </summary>
    public static void SetSeabedView(bool active)
    {
        Shader.SetGlobalFloat(SeabedViewId, active ? 1f : 0f);
    }

    private void Awake()
    {
        seaRenderer = GetComponent<Renderer>();
    }

    private void Start()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(InputObjectType.TERRAIN, this);
            ApplyVisualMode(VisualModeHandler.Instance.GetMode(InputObjectType.TERRAIN));
        }
    }

    private void OnDestroy()
    {
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(InputObjectType.TERRAIN, this);
        }
    }

    public void ApplyVisualMode(int mode)
    {
        if (seaRenderer == null) return;

        // Hide in TRANSPARENT or MODE_HIDDEN modes.
        bool visible = (mode != VisualMode.MODE_HIDDEN && mode != VisualModeTerrain.TRANSPARENT);
        seaRenderer.enabled = visible;
    }
}
