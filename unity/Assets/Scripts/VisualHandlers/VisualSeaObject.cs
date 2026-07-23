using UnityEngine;

/// <summary>
/// 海面（Sea）用のビジュアルモード適用。
/// 地形の透過（TRANSPARENT）または非表示（MODE_HIDDEN）のときにRendererを非表示にする。
/// </summary>
[RequireComponent(typeof(Renderer))]
public class VisualSeaObject : MonoBehaviour, IVisualModeHandler
{
    private Renderer seaRenderer;

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

        // 透過（TRANSPARENT）または非表示（MODE_HIDDEN）のときに非表示にする
        bool visible = (mode != VisualMode.MODE_HIDDEN && mode != VisualModeTerrain.TRANSPARENT);
        seaRenderer.enabled = visible;
    }
}
