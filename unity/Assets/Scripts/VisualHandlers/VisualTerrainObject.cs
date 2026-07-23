using UnityEngine;

/// <summary>
/// 地形用のビジュアルモード適用。Terrain の enabled を切り替える。
/// 超遠景(UltraFar)は Terrain ではなく陸地メッシュ(MeshRenderer)なので、
/// Terrain が無い場合は MeshRenderer の enabled で代替する。
/// </summary>
public class VisualTerrainObject : MonoBehaviour, IVisualModeHandler
{
    private Terrain terrainComp;
    private MeshRenderer meshRenderer;

    private void Awake()
    {
        terrainComp = GetComponent<Terrain>();
        if (terrainComp == null)
        {
            meshRenderer = GetComponent<MeshRenderer>();
        }
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
        bool visible;
        switch (mode)
        {
            case VisualMode.MODE_HIDDEN:
            case VisualModeTerrain.TRANSPARENT:
                visible = false;
                break;
            default:
                visible = true;
                break;
        }

        if (terrainComp != null) terrainComp.enabled = visible;
        else if (meshRenderer != null) meshRenderer.enabled = visible;
    }
}


