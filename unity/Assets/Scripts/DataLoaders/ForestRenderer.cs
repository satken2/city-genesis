using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Generate area-placed trees on the GPU. Retain only areas and patches on the CPU.</summary>
public sealed class ForestRenderer : IDisposable
{
    public const float DetailDistance = 100f;
    public const float TransitionWidth = 20f;
    public const int CandidateBudget = 2000000;
    readonly ComputeShader compute;
    readonly Shader shader;
    readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
    readonly int kernel;
    public bool Supported => compute != null && shader != null && shader.isSupported;

    public ForestRenderer()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) return;
        compute = Resources.Load<ComputeShader>("Shaders/ForestPlacement");
        shader = Resources.Load<Shader>("Shaders/Forest");
        if (Supported) kernel = compute.FindKernel("Generate");
    }

    public sealed class Patch : IDisposable
    {
        public int sourceId, type, seed, count, gridWidth, gridCount, startX, startZ;
        public float spacing, clearance;
        public float edgeFadeWidth;
        public Rect rect;
        public Bounds bounds;
        public PropRenderModel model;
        public GraphicsBuffer points, vertices, rings;
        public int regionRings, ringCount;
        public bool dirty = true, ready, hidden;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        public void Dispose() { points?.Dispose(); vertices?.Dispose(); rings?.Dispose(); points = vertices = rings = null; }
    }

    // Each ring stores vertex start, vertex count, polygon index, and hole flag.
    public Patch Create(PropScatterItem data, List<PropGeometry.Region> regions,
        List<PropGeometry.Region> obstacles, Rect rect, PropRenderModel model, int budget)
    {
        if (!Supported || !Valid(data.density, budget)) return null;
        var p = new Patch { sourceId = data.prop_scatter_id, type = data.prop_type, seed = data.seed,
            edgeFadeWidth = data.edge_fade_width, rect = rect, model = model,
            clearance = Mathf.Max(model.bounds.extents.x, model.bounds.extents.z) * .15f };
        Grid(rect, data.density, out p.spacing, out p.startX, out p.startZ, out p.gridWidth, out p.gridCount);
        p.count = Count(p.gridCount, budget);
        if (p.count <= 0) return null;
        var verts = new List<Vector2>(); var descriptors = new List<Vector4>();
        AddPolygons(regions, rect, 0, verts, descriptors);
        p.regionRings = descriptors.Count;
        AddPolygons(obstacles, rect, 8f, verts, descriptors);
        p.ringCount = descriptors.Count;
        if (p.regionRings == 0) return null;
        try
        {
            p.points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, p.count, 32);
            p.vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, verts.Count, 8);
            p.rings = new GraphicsBuffer(GraphicsBuffer.Target.Structured, descriptors.Count, 16);
            p.vertices.SetData(verts); p.rings.SetData(descriptors);
            return p;
        }
        catch { p.Dispose(); throw; }
    }

    /// <summary>
    /// Patch grid candidate count calculated with the same formula as Create. PropManager uses it in patch signatures,
    /// avoiding GPU buffer recreation when candidate budgets change but counts remain equal.
    /// </summary>
    public static int PatchCount(Rect rect, float density, int budget)
    {
        if (!Valid(density, budget)) return 0;
        Grid(rect, density, out _, out _, out _, out _, out int gridCount);
        return Count(gridCount, budget);
    }

    static bool Valid(float density, int budget) => budget > 0 && density > 0 && !float.IsNaN(density) && !float.IsInfinity(density);
    static int Count(int gridCount, int budget) => Mathf.Min(gridCount, Mathf.Max(1, budget));

    static void Grid(Rect rect, float density, out float spacing, out int startX, out int startZ, out int gridWidth, out int gridCount)
    {
        spacing = Mathf.Max(.5f, Mathf.Sqrt(10000f / density));
        startX = Mathf.FloorToInt(rect.xMin / spacing); startZ = Mathf.FloorToInt(rect.yMin / spacing);
        gridWidth = Mathf.CeilToInt(rect.xMax / spacing) - startX;
        gridCount = gridWidth * (Mathf.CeilToInt(rect.yMax / spacing) - startZ);
    }

    static void AddPolygons(List<PropGeometry.Region> polygons, Rect rect, float margin,
        List<Vector2> vertices, List<Vector4> rings)
    {
        var expanded = Rect.MinMaxRect(rect.xMin-margin, rect.yMin-margin, rect.xMax+margin, rect.yMax+margin);
        int group = rings.Count;
        foreach (var polygon in polygons)
        {
            if (!polygon.bounds.Overlaps(expanded)) continue;
            for (int i = 0; i < polygon.rings.Count; i++)
            {
                rings.Add(new Vector4(vertices.Count, polygon.rings[i].Length, group, i == 0 ? 0 : 1));
                vertices.AddRange(polygon.rings[i]);
            }
            group++;
        }
    }

    public bool Generate(Patch p, Terrain terrain, Vector3 offset)
    {
        if (terrain == null || terrain.terrainData == null) return false;
        var data = terrain.terrainData;
        compute.SetBuffer(kernel, "_Points", p.points);
        compute.SetBuffer(kernel, "_Vertices", p.vertices);
        compute.SetBuffer(kernel, "_Rings", p.rings);
        compute.SetInts("_Grid", p.startX, p.startZ, p.gridWidth, p.gridCount);
        compute.SetInt("_Count", p.count); compute.SetInt("_Seed", p.seed);
        compute.SetInt("_RegionRings", p.regionRings); compute.SetInt("_RingCount", p.ringCount);
        compute.SetFloat("_Spacing", p.spacing); compute.SetFloat("_Clearance", p.clearance);
        compute.SetFloat("_EdgeFadeWidth", p.edgeFadeWidth);
        compute.SetVector("_Rect", new Vector4(p.rect.xMin, p.rect.yMin, p.rect.xMax, p.rect.yMax));
        compute.SetVector("_Offset", offset);
        compute.SetVector("_TileOrigin", terrain.transform.position);
        compute.SetVector("_TileSize", data.size);
        compute.SetInt("_HeightResolution", data.heightmapResolution);
        compute.SetTexture(kernel, "_Heightmap", data.heightmapTexture);
        compute.Dispatch(kernel, (p.count + 63) / 64, 1, 1);
        // Conservative bounds include the full terrain height range, enabling culling without reading back each tree height.
        float extent = p.model.Radius * 1.2f;
        p.bounds = new Bounds(new Vector3(p.rect.center.x + offset.x,
            terrain.transform.position.y + data.size.y * .5f, p.rect.center.y + offset.z),
            new Vector3(p.rect.width + extent*2, data.size.y + extent*2, p.rect.height + extent*2));
        p.dirty = false; p.ready = true;
        return true;
    }

    Material MaterialFor(Material source)
    {
        if (materials.TryGetValue(source, out var result)) return ApplyUnderground(result);
        result = new Material(shader) { enableInstancing = true, name = source.name + " (Forest)" };
        var color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.color;
        result.SetColor("_BaseColor", color);
        result.SetFloat("_Smoothness", source.HasProperty("_Smoothness") ? source.GetFloat("_Smoothness") :
            source.HasProperty("_Glossiness") ? source.GetFloat("_Glossiness") : .1f);
        result.SetFloat("_Metallic", source.HasProperty("_Metallic") ? source.GetFloat("_Metallic") : 0f);
        result.SetTexture("_BaseMap", source.mainTexture != null ? source.mainTexture : Texture2D.whiteTexture);
        result.SetVector("_BaseMap_ST", new Vector4(source.mainTextureScale.x, source.mainTextureScale.y,
            source.mainTextureOffset.x, source.mainTextureOffset.y));
        materials.Add(source, result);
        return ApplyUnderground(result);
    }

    private static Material ApplyUnderground(Material material)
    {
        bool underground = UndergroundView.IsActive;
        float opacity = underground ? UndergroundMaterials.SurfaceOpacity : 1f;
        if (Mathf.Approximately(material.GetFloat("_UndergroundAlpha"), opacity)) return material;
        material.SetFloat("_UndergroundAlpha", opacity);
        material.SetFloat("_ZWrite", underground ? 0f : 1f);
        material.renderQueue = underground ? 3010 : 2000;
        material.SetShaderPassEnabled("DepthOnly", !underground);
        material.SetShaderPassEnabled("DepthNormals", !underground);
        material.SetShaderPassEnabled("ShadowCaster", !underground);
        return material;
    }

    /// <summary>Submit one patch. castShadows enables shadows within the shadow distance; shadowInstances accumulates instances submitted with shadows.</summary>
    public int Draw(Patch p, Camera camera, Plane[] planes, bool castShadows, ref int shadowInstances)
    {
        if (!p.ready || p.hidden) return 0;
        // Off-view patches that cast shadows are submitted too; Unity culls them from the view by worldBounds.
        bool shadows = castShadows && PropRenderPolicy.WithinShadowDistance(p.bounds, camera.transform.position);
        if (!shadows && !GeometryUtility.TestPlanesAABB(planes, p.bounds)) return 0;
        float nearest = Mathf.Sqrt(p.bounds.SqrDistance(camera.transform.position));
        if (nearest > DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE + p.model.Radius) return 0;
        var properties = p.properties;
        properties.SetBuffer("_ForestPoints", p.points);
        properties.SetVector("_ForestCamera", camera.transform.position);
        properties.SetFloat("_DetailDistance", DetailDistance);
        properties.SetFloat("_TransitionWidth", TransitionWidth);
        properties.SetFloat("_MaxDistance", DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE);
        int draws = 0;
        // Near and Far use the same fixed random values when switching, avoiding duplicate rendering or boundary bands.
        if (nearest < DetailDistance + TransitionWidth * .5f)
            draws += DrawParts(p, p.model.normal, camera, 0, shadows, ref shadowInstances);
        draws += DrawParts(p, p.model.far, camera, 1, shadows, ref shadowInstances);
        return draws;
    }

    int DrawParts(Patch p, List<PropRenderModel.Part> parts, Camera camera, int lod, bool shadows, ref int shadowInstances)
    {
        int draws = 0;
        foreach (var part in parts)
        {
            if (shadows) shadowInstances += p.count;
            p.properties.SetInt("_ForestLod", lod);
            p.properties.SetMatrix("_PartLocal", part.local);
            p.properties.SetMatrix("_PartNormal", part.local.inverse.transpose);
            var rp = new RenderParams(MaterialFor(part.material)) { camera = camera, worldBounds = p.bounds,
                matProps = p.properties, receiveShadows = true,
                shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off };
            Graphics.RenderMeshPrimitives(rp, part.mesh, part.submesh, p.count);
            draws++;
        }
        return draws;
    }

    public void Dispose()
    {
        foreach (var mat in materials.Values)
            if (Application.isPlaying) UnityEngine.Object.Destroy(mat); else UnityEngine.Object.DestroyImmediate(mat);
        materials.Clear();
    }
}
