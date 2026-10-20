using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// RiverSurfaceManager:
/// Persistent manager rendering river water in 3D, the river counterpart of WaterSurfaceManager (lakes).
///
/// Fetch rivers (area polygons and water elevation profiles) from backend <c>GET /api/v1/terrain/rivers</c>,
/// build <see cref="RiverStore"/>, and create grid-surface water meshes covering each river area.
/// Vertex Y equals local profile elevation (nearest profile-line point) plus a tiny lift,
/// descending downstream unlike horizontal lake surfaces. Avoid triangulating polygon perimeter vertices directly:
/// long, winding rivers create long downstream triangles whose linear interpolation deviates from actual water gradients
/// (same structure as riverGeometry.ts buildRiverSurfaceMesh in terrain editor 3D previews).
///
/// Include cells with any corner inside and their one-cell neighbors, so water extends at most two cells past banks;
/// the extension is buried beneath higher bank terrain and invisible (<see cref="RiverRecord.SurfaceCellIncluded"/>).
///
/// Clone sea (SeaObject) material and enable flow mode (_FlowEnabled).
/// Store downstream XZ unit vectors in UV2; Custom/WaterLit moves ripples and foam in that direction.
///
/// Rivers are map-specific static data fixed during init_game, but like coastlines/lakes,
/// fetch and regenerate on initial load, forced reload, and every session:reload through <see cref="TerrainManager.FetchCoastline"/>.
/// </summary>
public class RiverSurfaceManager : MonoBehaviour
{
    public static RiverSurfaceManager Instance { get; private set; }

    /// <summary>Water surface lift (m) to avoid Z-fighting with riverbeds.</summary>
    private const float WATER_LIFT = 0.05f;
    /// <summary>Downstream ripple/foam speed (m/s), applied to the cloned sea material.</summary>
    [SerializeField] private float flowSpeed = 1.5f;
    /// <summary>Fallback water color (translucent blue) if sea material cannot be borrowed.</summary>
    [SerializeField] private Color fallbackWaterColor = new Color(0.20f, 0.45f, 0.65f, 0.75f);

    // --- Riverbed/bank surface appearance (global mask read by Assets/Shaders/TerrainRiverBank.hlsl) ---
    [Header("River Bank Appearance")]
    [Tooltip("カーブ内岸の砂地の色")]
    [SerializeField] private Color sandColor = new Color(0.74f, 0.68f, 0.54f, 1f);
    [Tooltip("砂地のスムースネス")]
    [SerializeField] private float sandSmoothness = 0.12f;
    [Tooltip("濡れた川床の乗算色(rgb)とスムースネス(a)")]
    [SerializeField] private Color bedTint = new Color(0.62f, 0.62f, 0.60f, 0.45f);
    [Tooltip("砂地を置く岸帯の幅(m)。川の範囲の外側へこの距離まで、距離で減衰しながら砂にする")]
    [SerializeField] private float sandBandMeters = 16f;
    [Tooltip("砂地が最大濃度になる曲率(1/m)。半径 1/値 のカーブで内岸が完全な砂地になる")]
    [SerializeField] private float sandCurvatureRef = 0.005f;
    /// <summary>Maximum mask texture dimension (texels). Coarsen texels above this limit.</summary>
    private const int MASK_MAX_DIM = 4096;
    /// <summary>Riverbed wetness value (0..255) for one texel outside river areas, representing wet banks.</summary>
    private const byte WET_EDGE_VALUE = 96;

    private static readonly int RiverMaskId = Shader.PropertyToID("_RiverMask");
    private static readonly int RiverMaskParamsId = Shader.PropertyToID("_RiverMaskParams");
    private static readonly int RiverMaskSizeId = Shader.PropertyToID("_RiverMaskSize");
    private static readonly int RiverSandColorId = Shader.PropertyToID("_RiverSandColor");
    private static readonly int RiverBedTintId = Shader.PropertyToID("_RiverBedTint");
    private static readonly int RiverSandSmoothnessId = Shader.PropertyToID("_RiverSandSmoothness");
    private Texture2D riverMaskTex;

    private readonly List<GameObject> riverObjects = new List<GameObject>();
    private readonly List<Mesh> riverMeshes = new List<Mesh>();
    private Material riverMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("RiverSurfaceManager");
            go.AddComponent<RiverSurfaceManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        ClearRiverObjects();
        ClearRiverMask();
        if (riverMaterial != null) Destroy(riverMaterial);
        riverMaterial = null;
    }

    /// <summary>
    /// Fetch rivers and regenerate RiverStore/water meshes idempotently.
    /// Called by FetchCoastline at session start/full reload.
    /// </summary>
    public IEnumerator LoadAndBuild()
    {
        ClearRiverObjects();
        ClearRiverMask();
        RiverStore.Clear();

        RiversResponse response = null;
        string error = null;
        yield return ApiClient.Get<RiversResponse>(
            "/terrain/rivers",
            res => response = res,
            err => error = err
        );
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"RiverSurfaceManager: 川の取得に失敗: {error}");
            yield break;
        }
        if (response?.rivers == null) yield break;

        RiverStore.SetFromJson(response.rivers);
        if (!RiverStore.HasRivers) yield break;

        Material mat = ResolveRiverMaterial();
        foreach (var river in RiverStore.Rivers)
        {
            BuildOneRiver(river, mat);
        }
        BuildRiverMask();
        Debug.Log($"RiverSurfaceManager: 川の水面を{riverObjects.Count}本生成しました。");
    }

    /// <summary>Destroy all water objects during session shutdown or similar cleanup.</summary>
    public void Clear()
    {
        ClearRiverObjects();
        ClearRiverMask();
        RiverStore.Clear();
    }

    /// <summary>
    /// Generate global riverbed/bank masks (RG8: R=riverbed, G=sand) and publish terrain shader globals.
    ///
    /// Texels lie on the same grid lines as RiverStore (GRID_STEP), allowing CornerMask copies
    /// with integer offsets without repeated polygon containment. If the river bounding rectangle
    /// exceeds MASK_MAX_DIM, coarsen texels by integer grid multiples (subsample grid points).
    /// Expand external bank distances in texels from interior texels using four-neighbor breadth-first search;
    /// distance 1 is wet bank (weak R), and sandy-band G equals inner-bank factor (RiverRecord.InnerBankFactorAt) times distance attenuation.
    /// </summary>
    private void BuildRiverMask()
    {
        ClearRiverMask();
        if (!RiverStore.HasRivers) return;

        float step = RiverStore.GRID_STEP;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var r in RiverStore.Rivers)
        {
            minX = Mathf.Min(minX, r.GridOriginX); minZ = Mathf.Min(minZ, r.GridOriginZ);
            maxX = Mathf.Max(maxX, r.GridOriginX + (r.GridW - 1) * step);
            maxZ = Mathf.Max(maxZ, r.GridOriginZ + (r.GridH - 1) * step);
        }
        int bandCells = Mathf.Max(1, Mathf.CeilToInt(sandBandMeters / step));
        // Use integer multiples of grid spacing for texels; coarsen until within limits
        int factor = 1;
        while (Mathf.Max(maxX - minX, maxZ - minZ) / (step * factor) + 2 * bandCells + 2 > MASK_MAX_DIM) factor *= 2;
        float texel = step * factor;
        int bandTexels = Mathf.Max(1, Mathf.CeilToInt(sandBandMeters / texel));
        float ox = minX - bandTexels * texel - texel;
        float oz = minZ - bandTexels * texel - texel;
        int w = Mathf.CeilToInt((maxX - ox) / texel) + bandTexels + 2;
        int h = Mathf.CeilToInt((maxZ - oz) / texel) + bandTexels + 2;
        if (w <= 0 || h <= 0) return;

        // dist: -1=unreached, 0=inside river, n=four-neighbor distance from area. source: nearest river propagated from interior texels
        var dist = new short[w * h];
        var source = new short[w * h];
        for (int k = 0; k < dist.Length; k++) { dist[k] = -1; source[k] = -1; }
        var queue = new Queue<int>();
        for (int ri = 0; ri < RiverStore.Rivers.Count; ri++)
        {
            var r = RiverStore.Rivers[ri];
            // River grid point (i, j) → mask texel (I, J). Shared grid lines allow copying only points divisible by factor
            int offI = Mathf.RoundToInt((r.GridOriginX - ox) / step);
            int offJ = Mathf.RoundToInt((r.GridOriginZ - oz) / step);
            for (int j = 0; j < r.GridH; j++)
            {
                int gj = j + offJ;
                if (gj % factor != 0) continue;
                int J = gj / factor;
                if (J < 0 || J >= h) continue;
                for (int i = 0; i < r.GridW; i++)
                {
                    if (r.CornerMask[j * r.GridW + i] == 0) continue;
                    int gi = i + offI;
                    if (gi % factor != 0) continue;
                    int I = gi / factor;
                    if (I < 0 || I >= w) continue;
                    int idx = J * w + I;
                    if (dist[idx] == 0) continue;
                    dist[idx] = 0;
                    source[idx] = (short)ri;
                    queue.Enqueue(idx);
                }
            }
        }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int d = dist[idx];
            if (d >= bandTexels) continue;
            int I = idx % w, J = idx / w;
            Span<int> neighbors = stackalloc int[4] { idx - 1, idx + 1, idx - w, idx + w };
            for (int n = 0; n < 4; n++)
            {
                if (n == 0 && I == 0) continue;
                if (n == 1 && I == w - 1) continue;
                if (n == 2 && J == 0) continue;
                if (n == 3 && J == h - 1) continue;
                int nidx = neighbors[n];
                if (dist[nidx] >= 0) continue;
                dist[nidx] = (short)(d + 1);
                source[nidx] = source[idx];
                queue.Enqueue(nidx);
            }
        }

        var data = new byte[w * h * 2];
        for (int idx = 0; idx < dist.Length; idx++)
        {
            int d = dist[idx];
            if (d < 0) continue;
            byte bed = 0, sand = 0;
            if (d == 0)
            {
                bed = 255;
            }
            else
            {
                if (d == 1) bed = WET_EDGE_VALUE;
                var river = RiverStore.Rivers[source[idx]];
                float x = ox + (idx % w) * texel;
                float z = oz + (idx / w) * texel;
                float falloff = 1f - (float)d / (bandTexels + 1);
                sand = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(falloff * river.InnerBankFactorAt(x, z, sandCurvatureRef)));
            }
            data[idx * 2] = bed;
            data[idx * 2 + 1] = sand;
        }

        riverMaskTex = new Texture2D(w, h, TextureFormat.RG16, false, true)
        {
            name = "RiverMask",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        riverMaskTex.SetPixelData(data, 0);
        riverMaskTex.Apply(false, true);

        Shader.SetGlobalTexture(RiverMaskId, riverMaskTex);
        Shader.SetGlobalVector(RiverMaskParamsId, new Vector4(ox, oz, 1f / texel, 1f));
        Shader.SetGlobalVector(RiverMaskSizeId, new Vector4(w, h, 0f, 0f));
        Shader.SetGlobalColor(RiverSandColorId, sandColor);
        Shader.SetGlobalColor(RiverBedTintId, bedTint);
        Shader.SetGlobalFloat(RiverSandSmoothnessId, sandSmoothness);
    }

    /// <summary>Disable and destroy river masks (shaders stop reading when _RiverMaskParams.w=0).</summary>
    private void ClearRiverMask()
    {
        Shader.SetGlobalVector(RiverMaskParamsId, Vector4.zero);
        if (riverMaskTex != null) Destroy(riverMaskTex);
        riverMaskTex = null;
    }

    private void ClearRiverObjects()
    {
        foreach (var go in riverObjects) if (go != null) Destroy(go);
        riverObjects.Clear();
        foreach (var m in riverMeshes) if (m != null) Destroy(m);
        riverMeshes.Clear();
    }

    /// <summary>
    /// Clone sea (SeaObject) material and enable flow. Without sea material,
    /// create a Custom/WaterLit fallback (DepthOffsetLit if unavailable).
    /// Cloning avoids adding flow to seas/lakes, which share sea material.
    /// </summary>
    private Material ResolveRiverMaterial()
    {
        if (riverMaterial != null) return riverMaterial;
        Material source = null;
        GameObject sea = null;
        try { sea = GameObject.FindWithTag("SeaObject"); }
        catch { sea = null; }
        if (sea != null)
        {
            Renderer seaRenderer = sea.GetComponentInChildren<Renderer>();
            if (seaRenderer != null) source = seaRenderer.sharedMaterial;
        }
        if (source != null)
        {
            riverMaterial = new Material(source) { name = "RiverSurface" };
        }
        else
        {
            Shader sh = Shader.Find("Custom/WaterLit") ?? Shader.Find("Custom/DepthOffsetLit");
            riverMaterial = new Material(sh) { name = "RiverSurfaceFallback" };
            if (riverMaterial.HasProperty("_BaseColor")) riverMaterial.SetColor("_BaseColor", fallbackWaterColor);
        }
        if (riverMaterial.HasProperty("_FlowEnabled")) riverMaterial.SetFloat("_FlowEnabled", 1f);
        if (riverMaterial.HasProperty("_FlowSpeed")) riverMaterial.SetFloat("_FlowSpeed", flowSpeed);
        return riverMaterial;
    }

    /// <summary>Create one river's grid surface mesh and GameObject.</summary>
    private void BuildOneRiver(RiverRecord river, Material mat)
    {
        int w = river.GridW, h = river.GridH;
        float step = river.GridStep;
        var vertexIndex = new int[w * h];
        for (int k = 0; k < vertexIndex.Length; k++) vertexIndex[k] = -1;
        var vertices = new List<Vector3>();
        var uv2 = new List<Vector2>();
        var triangles = new List<int>();

        int GetVertex(int i, int j)
        {
            int gi = j * w + i;
            int vi = vertexIndex[gi];
            if (vi >= 0) return vi;
            float x = river.GridOriginX + i * step;
            float z = river.GridOriginZ + j * step;
            vi = vertices.Count;
            vertexIndex[gi] = vi;
            vertices.Add(new Vector3(x, river.WaterLevelAt(x, z) + WATER_LIFT, z));
            uv2.Add(river.FlowDirAt(x, z));
            return vi;
        }

        for (int j = 0; j < h - 1; j++)
        {
            for (int i = 0; i < w - 1; i++)
            {
                if (!river.SurfaceCellIncluded(i, j)) continue;
                int a = GetVertex(i, j), b = GetVertex(i + 1, j), c = GetVertex(i, j + 1), d = GetVertex(i + 1, j + 1);
                // Winding for +Y normals (Unity is left-handed; clockwise faces forward)
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);
            }
        }
        if (triangles.Count < 3) return;

        Mesh mesh = new Mesh { name = $"RiverSurfaceMesh_{river.Id}" };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetUVs(1, uv2);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        riverMeshes.Add(mesh);

        GameObject go = new GameObject($"RiverSurface_{river.Id}");
        go.transform.SetParent(transform);
        go.transform.position = Vector3.zero;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        // Like sea surfaces, allow hiding water with terrain transparency/hidden modes.
        go.tag = "SeaObject";
        go.AddComponent<VisualSeaObject>();
        riverObjects.Add(go);
    }

    [System.Serializable]
    private class RiversResponse
    {
        // Elements are {id, kind, polygon (GeoJSON-like), profile[[x,y,z],...]}. Receive JToken because the structure is nested.
        public JToken rivers;
    }
}
