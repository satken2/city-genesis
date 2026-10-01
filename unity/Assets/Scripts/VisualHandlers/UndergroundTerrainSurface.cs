using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Terrainの高さを薄い地表の膜として描き、通常のTerrain材質とコライダーを保持する。</summary>
public sealed class UndergroundTerrainSurface : MonoBehaviour
{
    private Terrain terrain;
    private GameObject surface;
    private Mesh mesh;
    private static Material material;
    private static int users;
    private void Awake() { terrain = GetComponent<Terrain>(); users++; }
    private void OnEnable() => TerrainCallbacks.heightmapChanged += HeightChanged;
    private void OnDisable() => TerrainCallbacks.heightmapChanged -= HeightChanged;
    private void HeightChanged(Terrain changed, RectInt region, bool synced)
    {
        if (changed != terrain) return;
        Destroy(mesh); mesh = null;
        if (surface != null && surface.activeSelf) SetVisible(true);
    }

    public void SetVisible(bool visible)
    {
        if (!visible) { if (surface != null) surface.SetActive(false); return; }
        if (terrain == null || terrain.terrainData == null) return;
        if (material == null) material = new Material(Resources.Load<Shader>("Shaders/UndergroundGround"));
        if (surface == null)
        {
            surface = new GameObject("UndergroundGroundSurface");
            surface.transform.SetParent(transform, false);
            surface.AddComponent<MeshFilter>();
            var renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        if (mesh == null)
        {
            TerrainData data = terrain.terrainData;
            int resolution = Mathf.Min(129, data.heightmapResolution);
            var vertices = new Vector3[resolution * resolution];
            var triangles = new int[(resolution-1) * (resolution-1) * 6];
            float step = 1f / (resolution-1);
            var heights = data.GetInterpolatedHeights(0, 0, resolution, resolution, step, step);
            for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
            {
                float u = x / (float)(resolution-1), v = z / (float)(resolution-1);
                vertices[z*resolution+x] = new Vector3(u*data.size.x, heights[z,x], v*data.size.z);
            }
            int t = 0;
            for (int z = 0; z < resolution-1; z++) for (int x = 0; x < resolution-1; x++)
            {
                int a = z*resolution+x;
                triangles[t++] = a; triangles[t++] = a+resolution; triangles[t++] = a+1;
                triangles[t++] = a+1; triangles[t++] = a+resolution; triangles[t++] = a+resolution+1;
            }
            mesh = new Mesh { name = "UndergroundGroundSurface" };
            mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
        }
        surface.SetActive(true);
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (--users == 0 && material != null) { Destroy(material); material = null; }
    }
}
