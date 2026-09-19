using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>範囲配置の木をGPU上で生成する。CPUには範囲と小区画だけを保持する。</summary>
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
        public Rect rect;
        public Bounds bounds;
        public PropRenderModel model;
        public GraphicsBuffer points, vertices, rings;
        public int regionRings, ringCount;
        public bool dirty = true, ready, hidden;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        public void Dispose() { points?.Dispose(); vertices?.Dispose(); rings?.Dispose(); points = vertices = rings = null; }
    }

    // 各リングは頂点開始・頂点数・ポリゴン番号・穴フラグを保持する。
    public Patch Create(PropScatterItem data, List<PropGeometry.Region> regions,
        List<PropGeometry.Region> obstacles, Rect rect, PropRenderModel model, int budget)
    {
        if (!Supported || budget <= 0 || data.density <= 0 || float.IsNaN(data.density) || float.IsInfinity(data.density)) return null;
        var p = new Patch { sourceId = data.prop_scatter_id, type = data.prop_type, seed = data.seed,
            rect = rect, model = model, spacing = Mathf.Max(.5f, Mathf.Sqrt(10000f / data.density)),
            clearance = Mathf.Max(model.bounds.extents.x, model.bounds.extents.z) * .15f };
        p.startX = Mathf.FloorToInt(rect.xMin / p.spacing); p.startZ = Mathf.FloorToInt(rect.yMin / p.spacing);
        p.gridWidth = Mathf.CeilToInt(rect.xMax / p.spacing) - p.startX;
        p.gridCount = p.gridWidth * (Mathf.CeilToInt(rect.yMax / p.spacing) - p.startZ);
        p.count = Mathf.Min(p.gridCount, Mathf.Max(1, budget));
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
        compute.SetVector("_Rect", new Vector4(p.rect.xMin, p.rect.yMin, p.rect.xMax, p.rect.yMax));
        compute.SetVector("_Offset", offset);
        compute.SetVector("_TileOrigin", terrain.transform.position);
        compute.SetVector("_TileSize", data.size);
        compute.SetInt("_HeightResolution", data.heightmapResolution);
        compute.SetTexture(kernel, "_Heightmap", data.heightmapTexture);
        compute.Dispatch(kernel, (p.count + 63) / 64, 1, 1);
        // 地形の全高を含む保守的な境界。一本ずつ高さを読み戻さずにカリングできる。
        float extent = p.model.Radius * 1.2f;
        p.bounds = new Bounds(new Vector3(p.rect.center.x + offset.x,
            terrain.transform.position.y + data.size.y * .5f, p.rect.center.y + offset.z),
            new Vector3(p.rect.width + extent*2, data.size.y + extent*2, p.rect.height + extent*2));
        p.dirty = false; p.ready = true;
        return true;
    }

    Material MaterialFor(Material source)
    {
        if (materials.TryGetValue(source, out var result)) return result;
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
        return result;
    }

    public int Draw(Patch p, Camera camera, Plane[] planes, ref int shadowBudget)
    {
        if (!p.ready || p.hidden || !GeometryUtility.TestPlanesAABB(planes, p.bounds)) return 0;
        float nearest = Mathf.Sqrt(p.bounds.SqrDistance(camera.transform.position));
        if (nearest > DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE + p.model.Radius) return 0;
        var properties = p.properties;
        properties.SetBuffer("_ForestPoints", p.points);
        properties.SetVector("_ForestCamera", camera.transform.position);
        properties.SetFloat("_DetailDistance", DetailDistance);
        properties.SetFloat("_TransitionWidth", TransitionWidth);
        properties.SetFloat("_MaxDistance", DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE);
        int draws = 0;
        // 近景と遠景が同じ固定乱数で切り替わるため、二重描画や境界の帯を作らない。
        if (nearest < DetailDistance + TransitionWidth * .5f)
            draws += DrawParts(p, p.model.normal, camera, 0, ref shadowBudget);
        draws += DrawParts(p, p.model.far, camera, 1, ref shadowBudget);
        return draws;
    }

    int DrawParts(Patch p, List<PropRenderModel.Part> parts, Camera camera, int lod, ref int shadowBudget)
    {
        int draws = 0;
        foreach (var part in parts)
        {
            bool shadows = lod == 0 && shadowBudget >= p.count;
            if (shadows) shadowBudget -= p.count;
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
