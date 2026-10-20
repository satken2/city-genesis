using UnityEngine;

/// <summary>
/// Apply terrain visual modes, replacing normal Terrain with a thin surface mesh in underground view.
/// UltraFar uses land meshes with MeshRenderer rather than Terrain,
/// so toggle MeshRenderer.enabled when Terrain is absent.
/// </summary>
public class VisualTerrainObject : MonoBehaviour, IVisualModeHandler
{
    private Terrain terrainComp;
    private MeshRenderer meshRenderer;
    private Material[] originalMaterials;
    private UndergroundTerrainSurface undergroundSurface;

    private void Awake()
    {
        terrainComp = GetComponent<Terrain>();
        if (terrainComp == null)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null) originalMaterials = meshRenderer.sharedMaterials;
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

        bool underground = mode == VisualModeTerrain.TRANSPARENT;
        if (terrainComp != null)
        {
            terrainComp.enabled = visible;
            if (underground && undergroundSurface == null) undergroundSurface = gameObject.AddComponent<UndergroundTerrainSurface>();
            undergroundSurface?.SetVisible(underground);
        }
        else if (meshRenderer != null)
        {
            meshRenderer.enabled = visible || underground;
            var materials = new Material[originalMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = underground ? UndergroundMaterials.Ghost(originalMaterials[i]) : originalMaterials[i];
            meshRenderer.sharedMaterials = materials;
        }
    }
}


