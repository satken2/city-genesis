using System.Collections.Generic;
using UnityEngine;

/// <summary>既存路面の描画に標示座標を結び付ける。独立した白線メッシュは生成しない。</summary>
[DisallowMultipleComponent]
public sealed class RoadMarkingSurface : MonoBehaviour
{
    public sealed class Tile
    {
        public RoadMarkingSurface Surface;
        public Vector2Int Cell;
        public int TableIndex;
        public RoadMarkingAtlas.Page Page;
        public Rect Rect => new(Cell.x * RoadMarkingPlan.TileSize, Cell.y * RoadMarkingPlan.TileSize, RoadMarkingPlan.TileSize, RoadMarkingPlan.TileSize);
        public string Key => Surface.Plan.Signature + ":" + Cell.x + ":" + Cell.y;
    }

    public RoadMarkingPlan Plan { get; private set; }
    public readonly List<Tile> Tiles = new();
    public Renderer Renderer { get; private set; }
    public RoadMarkingContext Context { get; private set; }
    public bool Far { get; private set; }
    public bool Initialized { get; private set; }
    private Texture2D table;
    private Color[] values;
    private Vector4 grid;
    private MaterialPropertyBlock properties;
    private static readonly Dictionary<Material, Material> materials = new();
    private static Shader shader;

    public void Configure(RoadMarkingPlan plan, RoadMarkingContext context = null, bool far = false)
    {
        if (Context == null && Plan != plan) Plan?.ReleaseMesh();
        ReleaseTable();
        Plan = plan; Context = context; Far = far;
        Renderer = GetComponent<Renderer>();
        var sources = Renderer.sharedMaterials;
        for (int i = 0; i < sources.Length; i++) sources[i] = SurfaceMaterial(sources[i]);
        Renderer.sharedMaterials = sources;
        Initialized = true;
        if (isActiveAndEnabled && !Far) RoadMarkingAtlas.Register(this);
        ApplyProperties();
    }

    private static Material SurfaceMaterial(Material source)
    {
        if (source == null || source.shader.name == "Custom/RoadSurface") return source;
        if (materials.TryGetValue(source, out var material) && material != null) return material;
        shader ??= Resources.Load<Shader>("Shaders/RoadSurface");
        if (shader == null) return source;
        material = new Material(source) { shader = shader, name = source.name + " (road surface)" };
        materials[source] = material;
        return material;
    }

    public void EnsureTable()
    {
        if (table != null || Far || Plan == null || Plan.Empty) return;
        var mesh = GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) return;
        var uv = new List<Vector4>(); mesh.GetUVs(3, uv);
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = minX, maxY = maxX;
        foreach (var p in uv)
        {
            if (p.z < .5f) continue;
            minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
        }
        Rect plan = Plan.Bounds;
        minX = Mathf.Max(minX, plan.xMin); minY = Mathf.Max(minY, plan.yMin);
        maxX = Mathf.Min(maxX, plan.xMax); maxY = Mathf.Min(maxY, plan.yMax);
        if (minX > maxX || minY > maxY) return;
        int x0 = Mathf.FloorToInt(minX / RoadMarkingPlan.TileSize), y0 = Mathf.FloorToInt(minY / RoadMarkingPlan.TileSize);
        int width = Mathf.FloorToInt(maxX / RoadMarkingPlan.TileSize) - x0 + 1;
        int height = Mathf.FloorToInt(maxY / RoadMarkingPlan.TileSize) - y0 + 1;
        // 長大な例外形状がページ表だけで容量を消費しないように制限する。
        if ((long)width * height > 4096 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize) return;
        long bytes = (long)width * height * 16;
        if (!RoadMarkingAtlas.ReserveTable(bytes)) return;
        table = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
        { name = "Road marking page table", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        values = new Color[width * height]; grid = new Vector4(x0, y0, width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var tile = new Tile { Surface = this, Cell = new Vector2Int(x0 + x, y0 + y), TableIndex = y * width + x };
            if (Plan.IntersectsTile(tile.Rect)) Tiles.Add(tile);
        }
        table.SetPixels(values); table.Apply(false, false);
        ApplyProperties();
    }

    public void SetPage(Tile tile, RoadMarkingAtlas.Page page)
    {
        tile.Page?.Users.Remove(tile);
        tile.Page = page; page?.Users.Add(tile);
        if (table == null) return;
        values[tile.TableIndex] = page == null ? Color.clear : new Color(page.Slot + 1, page.Tier, 0, 0);
        // ページ表は数画素程度。標示画像本体をCPUへ読み戻さない。
        table.SetPixel(tile.TableIndex % table.width, tile.TableIndex / table.width, values[tile.TableIndex]);
        table.Apply(false, false);
    }

    public Vector3 World(Vector2 coordinate) => Context != null ? Context.World(coordinate) : transform.TransformPoint(new Vector3(coordinate.x, 0, coordinate.y));

    public void ApplyProperties()
    {
        if (Renderer == null || !Initialized) return;
        properties ??= new MaterialPropertyBlock();
        Renderer.GetPropertyBlock(properties);
        properties.SetFloat("_RoadPaintEnabled", Far ? 0 : 1);
        properties.SetFloat("_RoadBandCount", Context != null && !Far ? Context.BandCount : 0);
        if (Context != null)
        {
            properties.SetVectorArray("_RoadBands", Context.Bands);
            properties.SetVectorArray("_RoadBandSpans", Context.BandSpans);
            properties.SetVector("_RoadDistances", new Vector4(Context.Length, Context.Phase, 0, 0));
            properties.SetVector("_RoadApproaches", Context.Approaches);
        }
        properties.SetTexture("_RoadPageTable", table != null ? table : Texture2D.blackTexture);
        properties.SetVector("_RoadPageGrid", table != null ? grid : Vector4.zero);
        Renderer.SetPropertyBlock(properties);
    }

    public static void Restore(Renderer renderer) => renderer.GetComponent<RoadMarkingSurface>()?.ApplyProperties();

    private void OnEnable() { if (Initialized && !Far) RoadMarkingAtlas.Register(this); }
    private void OnDisable() { RoadMarkingAtlas.Unregister(this); ReleaseTable(); }
    private void OnDestroy() { ReleaseTable(); if (Context == null) Plan?.ReleaseMesh(); }

    public void ReleaseTable()
    {
        foreach (var tile in Tiles) { tile.Page?.Users.Remove(tile); tile.Page = null; }
        Tiles.Clear();
        if (table != null)
        {
            RoadMarkingAtlas.ReleaseTable((long)table.width * table.height * 16);
            Destroy(table); table = null;
        }
        values = null;
        if (Initialized) ApplyProperties();
    }

    public static void ClearMaterials()
    {
        foreach (var material in materials.Values) if (material != null) Destroy(material);
        materials.Clear();
    }
}
