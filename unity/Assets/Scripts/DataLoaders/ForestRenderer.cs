using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Generate area-placed props on the GPU. Retain only areas and patches on the CPU.</summary>
public sealed class ForestRenderer : IDisposable
{
    public static float DetailDistance => 100f * GraphicsConfig.FarDistanceScale; // Distance where trees switch from normal to far models.
    public const float TransitionWidth = 20f;
    public const int CandidateBudget = 2000000;
    readonly ComputeShader compute;
    readonly Shader shader, foliageShader;
    readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
    readonly Dictionary<(Vector2Int cell, int source, int type), Group> groups = new Dictionary<(Vector2Int, int, int), Group>();
    readonly int kernel;
    public bool Supported => compute != null && shader != null && shader.isSupported;

    struct GpuPoint { public Vector4 positionScale, rotationVisible; } // Same layout as ForestPoint in ForestPlacement.compute.
    static GpuPoint[] zeros = new GpuPoint[0];
    static GpuPoint[] Zeros(int count)
    {
        if (zeros.Length < count) zeros = new GpuPoint[Mathf.NextPowerOfTwo(count)];
        return zeros;
    }

    public ForestRenderer()
    {
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) return;
        compute = Resources.Load<ComputeShader>("Shaders/ForestPlacement");
        shader = Resources.Load<Shader>("Shaders/Forest");
        foliageShader = Resources.Load<Shader>("Shaders/Foliage");
        if (Supported) kernel = compute.FindKernel("Generate");
    }

    /// <summary>
    /// One patch's generation inputs and its range in the group's points buffer. Placement, exclusion and picking stay per patch;
    /// only the points are stored and drawn per group.
    /// </summary>
    public sealed class Patch : IDisposable
    {
        public int sourceId, type, seed, count, gridWidth, gridCount, startX, startZ;
        public float spacing, clearance;
        public float edgeFadeWidth;
        public Rect rect;
        public Bounds bounds;
        public PropRenderModel model;
        public GraphicsBuffer vertices, rings;
        public int regionRings, ringCount;
        public bool dirty = true, ready, hidden;
        public Vector2Int key;
        public Group group;
        public int offset;
        public void Hide() { hidden = true; group?.Clear(this); }
        public void Dispose() { vertices?.Dispose(); rings?.Dispose(); vertices = rings = null; group?.Detach(this); group = null; }
    }

    /// <summary>
    /// Points of every patch of one scatter in one terrain cell, drawn with one call per model part. Drawing per patch would make
    /// draw-call submission dominate the frame for scatters covering many patches.
    /// A patch key keeps its range while its count fits, so regenerating one patch rewrites only that range.
    /// Any other membership change lays the buffer out again and regenerates every member.
    /// </summary>
    public sealed class Group : IDisposable
    {
        public readonly Vector2Int cell;
        public readonly PropRenderModel model;
        public GraphicsBuffer points;
        public int instanceCount;
        public Bounds bounds;
        public bool hasBounds;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        readonly Dictionary<Vector2Int, Patch> members = new Dictionary<Vector2Int, Patch>();
        readonly Dictionary<Vector2Int, (int offset, int count)> ranges = new Dictionary<Vector2Int, (int, int)>();
        readonly Action<Group> released;
        bool layoutDirty;

        public Group(Vector2Int cell, PropRenderModel model, Action<Group> released)
        {
            this.cell = cell; this.model = model; this.released = released;
        }

        public void Attach(Patch p)
        {
            members[p.key] = p; p.group = this;
            if (!layoutDirty && ranges.TryGetValue(p.key, out var range) && p.count <= range.count) p.offset = range.offset;
            else layoutDirty = true;
        }

        public void Detach(Patch p)
        {
            if (!members.TryGetValue(p.key, out var current) || current != p) return;
            members.Remove(p.key);
            Clear(p);
            if (members.Count == 0) released(this);
        }

        /// <summary>Zero the patch's range: zero points are invisible in the forest shader.</summary>
        public void Clear(Patch p)
        {
            if (points == null || !ranges.TryGetValue(p.key, out var range)) return;
            points.SetData(Zeros(range.count), 0, range.offset, range.count);
        }

        /// <summary>Called before a member generates. A new buffer starts zeroed, so members are hidden until regenerated.</summary>
        public void EnsureLayout()
        {
            if (!layoutDirty) return;
            layoutDirty = false;
            ranges.Clear();
            int total = 0;
            foreach (var pair in members)
            {
                var p = pair.Value;
                ranges[pair.Key] = (total, p.count);
                p.offset = total; p.dirty = true; p.ready = false;
                total += p.count;
            }
            points?.Dispose();
            points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total, 32);
            points.SetData(Zeros(total), 0, 0, total);
            instanceCount = total;
        }

        public void Dispose() { points?.Dispose(); points = null; }
    }

    // リングには頂点開始・頂点数・ポリゴン番号・穴/水域のビットを保持する。
    public Patch Create(PropScatterItem data, List<PropGeometry.Region> regions,
        List<PropGeometry.Region> obstacles, Rect rect, PropRenderModel model, int budget, Vector2Int cell, Vector2Int key)
    {
        if (!Supported || !Valid(data.density, budget)) return null;
        var p = new Patch { sourceId = data.prop_scatter_id, type = data.prop_type, seed = data.seed,
            edgeFadeWidth = data.edge_fade_width, rect = rect, model = model, key = key,
            clearance = Mathf.Max(model.bounds.extents.x, model.bounds.extents.z) * .15f };
        Grid(rect, data.density, out p.spacing, out p.startX, out p.startZ, out p.gridWidth, out p.gridCount);
        p.count = Count(p.gridCount, budget);
        if (p.count <= 0) return null;
        var verts = new List<Vector2>(); var descriptors = new List<Vector4>();
        AddPolygons(regions, rect, 0, verts, descriptors);
        p.regionRings = descriptors.Count;
        AddPolygons(obstacles, rect, Mathf.Max(8f, PropGeometry.WaterFadeWidth), verts, descriptors);
        p.ringCount = descriptors.Count;
        if (p.regionRings == 0) return null;
        try
        {
            p.vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, verts.Count, 8);
            p.rings = new GraphicsBuffer(GraphicsBuffer.Target.Structured, descriptors.Count, 16);
            p.vertices.SetData(verts); p.rings.SetData(descriptors);
        }
        catch { p.Dispose(); throw; }
        var groupKey = (cell, p.sourceId, p.type);
        if (!groups.TryGetValue(groupKey, out var group))
            groups.Add(groupKey, group = new Group(cell, model, g => { groups.Remove(groupKey); g.Dispose(); }));
        group.Attach(p);
        return p;
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
                rings.Add(new Vector4(vertices.Count, polygon.rings[i].Length, group, (i == 0 ? 0 : 1) + (polygon.water ? 2 : 0)));
                vertices.AddRange(polygon.rings[i]);
            }
            group++;
        }
    }

    public bool Generate(Patch p, Terrain terrain, Vector3 offset)
    {
        if (terrain == null || terrain.terrainData == null) return false;
        var data = terrain.terrainData;
        var pos = terrain.transform.position;
        return DispatchHeightmap(p, data.heightmapTexture, pos, data.size,
            new Vector2Int(data.heightmapResolution, data.heightmapResolution),
            2f * data.size.y, pos.y, pos.y, pos.y + data.size.y, offset);
    }

    public bool Generate(Patch p, Texture heightmap, Vector2 origin, Vector2 size, Vector2Int resolution,
        float minHeight, float maxHeight, Vector3 offset)
    {
        if (heightmap == null || size.x <= 0f || size.y <= 0f || resolution.x < 2 || resolution.y < 2) return false;
        float span = Mathf.Max(1f, maxHeight - minHeight);
        return DispatchHeightmap(p, heightmap,
            new Vector3(origin.x + offset.x, 0f, origin.y + offset.z),
            new Vector3(size.x, span, size.y),
            resolution, 1f, offset.y, minHeight + offset.y, maxHeight + offset.y, offset);
    }

    private bool DispatchHeightmap(Patch p, Texture heightmap, Vector3 tileOrigin, Vector3 tileSize, Vector2Int resolution,
        float heightScale, float heightBias, float minHeight, float maxHeight, Vector3 offset)
    {
        // A hidden patch keeps its range zeroed until the reloaded snapshot replaces it.
        if (p.hidden) { p.dirty = false; return false; }
        if (heightmap == null) return false;
        var group = p.group;
        group.EnsureLayout();
        compute.SetBuffer(kernel, "_Points", group.points);
        compute.SetInt("_PointOffset", p.offset);
        compute.SetBuffer(kernel, "_Vertices", p.vertices);
        compute.SetBuffer(kernel, "_Rings", p.rings);
        compute.SetInts("_Grid", p.startX, p.startZ, p.gridWidth, p.gridCount);
        compute.SetInt("_Count", p.count); compute.SetInt("_Seed", p.seed);
        compute.SetInt("_RegionRings", p.regionRings); compute.SetInt("_RingCount", p.ringCount);
        compute.SetFloat("_Spacing", p.spacing); compute.SetFloat("_Clearance", p.clearance);
        compute.SetFloat("_EdgeFadeWidth", p.edgeFadeWidth);
        compute.SetFloat("_WaterFadeWidth", PropGeometry.WaterFadeWidth);
        compute.SetVector("_Rect", new Vector4(p.rect.xMin, p.rect.yMin, p.rect.xMax, p.rect.yMax));
        compute.SetVector("_Offset", offset);
        compute.SetVector("_TileOrigin", tileOrigin);
        compute.SetVector("_TileSize", tileSize);
        compute.SetInts("_HeightResolution", resolution.x, resolution.y);
        compute.SetVector("_HeightScaleBias", new Vector4(heightScale, heightBias, 0f, 0f));
        compute.SetTexture(kernel, "_Heightmap", heightmap);
        compute.Dispatch(kernel, (p.count + 63) / 64, 1, 1);
        // Conservative bounds include the full terrain height range, enabling culling without reading back each tree height.
        float extent = p.model.Radius * 1.2f;
        float heightSpan = Mathf.Max(1f, maxHeight - minHeight);
        float centerY = (minHeight + maxHeight) * .5f;
        p.bounds = new Bounds(new Vector3(p.rect.center.x + offset.x, centerY, p.rect.center.y + offset.z),
            new Vector3(p.rect.width + extent*2, heightSpan + extent*2, p.rect.height + extent*2));
        float cellSize = TerrainManager.CellSize;
        group.bounds = new Bounds(new Vector3((group.cell.x + .5f) * cellSize + offset.x, centerY, (group.cell.y + .5f) * cellSize + offset.z),
            new Vector3(cellSize + extent*2, heightSpan + extent*2, cellSize + extent*2));
        group.hasBounds = true;
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
        result.SetFloat("_Foliage", source.shader == foliageShader ? 1f : 0f);
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

    /// <summary>Submit every group. castShadows enables shadows within the shadow distance; shadowInstances accumulates instances submitted with shadows.</summary>
    public int Draw(Camera camera, Plane[] planes, bool castShadows, ref int shadowInstances)
    {
        int draws = 0;
        foreach (var group in groups.Values) draws += Draw(group, camera, planes, castShadows, ref shadowInstances);
        return draws;
    }

    int Draw(Group g, Camera camera, Plane[] planes, bool castShadows, ref int shadowInstances)
    {
        if (!g.hasBounds || g.points == null) return 0;
        // Off-view groups that cast shadows are submitted too; Unity culls them from the view by worldBounds.
        bool shadows = castShadows && PropRenderPolicy.WithinShadowDistance(g.bounds, camera.transform.position);
        if (!shadows && !GeometryUtility.TestPlanesAABB(planes, g.bounds)) return 0;
        float nearest = Mathf.Sqrt(g.bounds.SqrDistance(camera.transform.position));
        if (nearest > DrawLodConfig.PropHiddenEnterDistance + g.model.Radius) return 0;
        var properties = g.properties;
        properties.SetBuffer("_ForestPoints", g.points);
        properties.SetVector("_ForestCamera", camera.transform.position);
        properties.SetFloat("_DetailDistance", DetailDistance);
        properties.SetFloat("_TransitionWidth", TransitionWidth);
        properties.SetFloat("_MaxDistance", DrawLodConfig.PropHiddenEnterDistance);
        int draws = 0;
        // Near and Far use the same fixed random values when switching, avoiding duplicate rendering or boundary bands.
        if (nearest < DetailDistance + TransitionWidth * .5f)
            draws += DrawParts(g, g.model.normal, camera, 0, shadows, ref shadowInstances);
        draws += DrawParts(g, g.model.far, camera, 1, shadows, ref shadowInstances);
        return draws;
    }

    int DrawParts(Group g, List<PropRenderModel.Part> parts, Camera camera, int lod, bool shadows, ref int shadowInstances)
    {
        int draws = 0;
        foreach (var part in parts)
        {
            if (shadows) shadowInstances += g.instanceCount;
            g.properties.SetInt("_ForestLod", lod);
            g.properties.SetMatrix("_PartLocal", part.local);
            g.properties.SetMatrix("_PartNormal", part.local.inverse.transpose);
            var rp = new RenderParams(MaterialFor(part.material)) { camera = camera, worldBounds = g.bounds,
                matProps = g.properties, receiveShadows = true,
                shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off };
            Graphics.RenderMeshPrimitives(rp, part.mesh, part.submesh, g.instanceCount);
            draws++;
        }
        return draws;
    }

    public void Dispose()
    {
        foreach (var mat in materials.Values)
            if (Application.isPlaying) UnityEngine.Object.Destroy(mat); else UnityEngine.Object.DestroyImmediate(mat);
        materials.Clear();
        foreach (var group in groups.Values) group.Dispose();
        groups.Clear();
    }
}
