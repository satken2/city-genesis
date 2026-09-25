using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

[Serializable]
public sealed class ForestOverviewResponse
{
    public List<PropScatterItem> scatters;
    public List<PropObstacleItem> obstacles;
    public JObject coastline;
}

/// <summary>木の座標を保持せず、範囲の被覆率だけを全域で共有する。</summary>
public sealed class ForestOverview : IDisposable
{
    public sealed class Raster
    {
        public Rect bounds;
        public int width, height;
        public Color32[] pixels;
        public ulong hash; // 寸法・範囲・画素の FNV-1a。同じ内容の再取得で全域テクスチャを作り直さないために使う
    }
    private Texture2D coverage, detail;
    private int detailHash = int.MinValue;
    private ulong publishedHash;
    public bool Ready => coverage != null;

    public static Raster Build(ForestOverviewResponse response, CancellationToken cancel)
    {
        var regions = new List<(PropGeometry.Region region, int type, float density, float fadeWidth, bool allowWater)>();
        Rect bounds = new Rect();
        foreach (var scatter in response.scatters ?? new List<PropScatterItem>())
        {
            if (scatter.prop_type != 1 && scatter.prop_type != 2) continue;
            foreach (var region in PropGeometry.Parse(scatter.region))
            {
                bounds = regions.Count == 0 ? region.bounds : Union(bounds, region.bounds);
                regions.Add((region, scatter.prop_type, scatter.density, scatter.edge_fade_width, scatter.allow_water));
            }
        }
        bounds = Rect.MinMaxRect(bounds.xMin - 8, bounds.yMin - 8, bounds.xMax + 8, bounds.yMax + 8);
        float step = Mathf.Max(4, Mathf.Max(bounds.width, bounds.height) / 2048);
        var raster = new Raster { bounds = bounds, width = Mathf.Max(1, Mathf.CeilToInt(bounds.width / step)), height = Mathf.Max(1, Mathf.CeilToInt(bounds.height / step)) };
        raster.pixels = new Color32[raster.width * raster.height];
        bool[] land = null;
        if (response.coastline != null)
        {
            land = new bool[raster.pixels.Length];
            var coordinates = response.coastline["coordinates"] as JArray;
            if ((string)response.coastline["type"] == "Polygon") coordinates = new JArray { coordinates };
            foreach (var region in PropGeometry.Parse(coordinates)) Fill(raster, region, cancel, index => land[index] = true);
        }
        foreach (var item in regions)
            Fill(raster, item.region, cancel, index => {
                if (!item.allowWater && land != null && !land[index]) return;
                var position = new Vector2(bounds.xMin + (index % raster.width + .5f) * bounds.width / raster.width,
                    bounds.yMin + (index / raster.width + .5f) * bounds.height / raster.height);
                float density = item.density * PropGeometry.EdgeWeight(position, item.region, item.fadeWidth);
                // 樹冠の投影面積から被覆率を推定し、境界の疎林も地形表現へ反映する。
                byte amount = (byte)Mathf.RoundToInt(255 * (1 - Mathf.Exp(-Mathf.Max(0, density) * (item.type == 1 ? 16 : 36) / 10000)));
                var p = raster.pixels[index];
                if (item.type == 1) p.r = (byte)(255 - (255 - p.r) * (255 - amount) / 255);
                else p.g = (byte)(255 - (255 - p.g) * (255 - amount) / 255);
                raster.pixels[index] = p;
            });
        foreach (var obstacle in response.obstacles ?? new List<PropObstacleItem>())
        {
            if (obstacle.polygon == null || obstacle.polygon.Count < 3) continue;
            var region = new PropGeometry.Region();
            region.rings.Add(obstacle.polygon.ConvertAll(p => new Vector2(p[0], p[1])).ToArray());
            region.bounds = PropGeometry.Bounds(region.rings[0]);
            foreach (var hole in obstacle.holes ?? new List<List<List<float>>>())
                if (hole.Count >= 3) region.rings.Add(hole.ConvertAll(p => new Vector2(p[0], p[1])).ToArray());
            Fill(raster, region, cancel, index => raster.pixels[index] = default);
        }
        cancel.ThrowIfCancellationRequested();
        raster.hash = Hash(raster);
        return raster;
    }

    private static ulong Hash(Raster raster)
    {
        const ulong prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        void Mix(uint value) { unchecked { hash = (hash ^ value) * prime; } }
        Mix((uint)raster.width); Mix((uint)raster.height);
        Mix((uint)BitConverter.SingleToInt32Bits(raster.bounds.xMin)); Mix((uint)BitConverter.SingleToInt32Bits(raster.bounds.yMin));
        Mix((uint)BitConverter.SingleToInt32Bits(raster.bounds.width)); Mix((uint)BitConverter.SingleToInt32Bits(raster.bounds.height));
        foreach (var p in raster.pixels) Mix(p.r | (uint)p.g << 8 | (uint)p.b << 16 | (uint)p.a << 24);
        return hash;
    }

    private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin,b.xMin), Mathf.Min(a.yMin,b.yMin), Mathf.Max(a.xMax,b.xMax), Mathf.Max(a.yMax,b.yMax));

    // 走査線の交点を対にし、穴を含むポリゴンを木の本数と無関係な計算量で塗る。
    private static void Fill(Raster r, PropGeometry.Region region, CancellationToken cancel, Action<int> write)
    {
        var intersections = new List<float>();
        float dx = r.bounds.width / r.width, dz = r.bounds.height / r.height;
        int first = Mathf.Max(0, Mathf.CeilToInt((region.bounds.yMin - r.bounds.yMin) / dz - .5f));
        int last = Mathf.Min(r.height - 1, Mathf.FloorToInt((region.bounds.yMax - r.bounds.yMin) / dz - .5f));
        for (int y = first; y <= last; y++)
        {
            cancel.ThrowIfCancellationRequested();
            float z = r.bounds.yMin + (y + .5f) * dz;
            intersections.Clear();
            foreach (var ring in region.rings)
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    var a = ring[j]; var b = ring[i];
                    if ((a.y > z) != (b.y > z)) intersections.Add(a.x + (b.x - a.x) * (z - a.y) / (b.y - a.y));
                }
            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                int left = Mathf.Max(0, Mathf.CeilToInt((intersections[i] - r.bounds.xMin) / dx - .5f));
                int right = Mathf.Min(r.width - 1, Mathf.FloorToInt((intersections[i + 1] - r.bounds.xMin) / dx - .5f));
                for (int x = left; x <= right; x++) write(y * r.width + x);
            }
        }
    }

    private static Vector4 Mapping(Rect r) => new Vector4(r.xMin, r.yMin, 1 / r.width, 1 / r.height);
    private static Texture2D Texture(int width, int height) => new Texture2D(width, height, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Forest coverage" };
    public void Publish(Raster raster)
    {
        if (coverage != null && raster.hash == publishedHash) return;
        var next = Texture(raster.width, raster.height);
        next.SetPixels32(raster.pixels); next.Apply(false, true);
        var previous = coverage; coverage = next;
        Shader.SetGlobalTexture("_ForestCoverage", coverage);
        Shader.SetGlobalVector("_ForestCoverageRect", Mapping(raster.bounds));
        Shader.SetGlobalFloat("_ForestOverviewEnabled", 1);
        if (previous != null) UnityEngine.Object.Destroy(previous);
        publishedHash = raster.hash;
    }

    public void UpdateDetail(List<Rect> rects, float chunkSize)
    {
        int hash = rects.Count;
        unchecked { foreach (var rect in rects) hash += rect.GetHashCode() * 397; }
        if (hash == detailHash) return;
        detailHash = hash;
        Rect bounds = rects.Count == 0 ? new Rect(0, 0, chunkSize, chunkSize) : rects[0];
        foreach (var rect in rects) bounds = Union(bounds, rect);
        bounds = Rect.MinMaxRect(bounds.xMin - chunkSize, bounds.yMin - chunkSize, bounds.xMax + chunkSize, bounds.yMax + chunkSize);
        int width = Mathf.Clamp(Mathf.RoundToInt(bounds.width / chunkSize), 1, 512), height = Mathf.Clamp(Mathf.RoundToInt(bounds.height / chunkSize), 1, 512);
        var pixels = new Color32[width * height];
        foreach (var rect in rects)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((rect.center.x - bounds.xMin) / bounds.width * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((rect.center.y - bounds.yMin) / bounds.height * height), 0, height - 1);
            pixels[y * width + x] = new Color32(255,255,255,255);
        }
        // 境界までの距離を先に焼き、木の各頂点ではテクスチャを一度だけ参照する。
        var ready = (Color32[])pixels.Clone();
        int radius = Mathf.CeilToInt(100 / chunkSize) + 1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (ready[y * width + x].r == 0) continue;
            float distance = 100;
            for (int dz = -radius; dz <= radius; dz++) for (int dx = -radius; dx <= radius; dx++)
            {
                int xx = x + dx, yy = y + dz;
                if (xx >= 0 && xx < width && yy >= 0 && yy < height && ready[yy * width + xx].r != 0) continue;
                distance = Mathf.Min(distance, Mathf.Max(0, Mathf.Sqrt(dx * dx + dz * dz) - .5f) * chunkSize);
            }
            byte weight = (byte)Mathf.RoundToInt(255 * distance / 100);
            pixels[y * width + x] = new Color32(weight,weight,weight,255);
        }
        // 寸法が同じなら同じテクスチャへ書き直す。書き直せるよう CPU 側の画素を残す(最大512²で1MB)。
        if (detail == null || detail.width != width || detail.height != height)
        {
            if (detail != null) UnityEngine.Object.Destroy(detail);
            detail = Texture(width, height);
        }
        detail.SetPixels32(pixels); detail.Apply(false, false);
        Shader.SetGlobalTexture("_ForestDetail", detail);
        Shader.SetGlobalVector("_ForestDetailRect", Mapping(bounds));
    }

    public void SetLeafColor(int type, PropRenderModel model)
    {
        if (model.far.Count == 0) return;
        var material = model.far[0].material;
        var color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor").linear : Color.white;
        var map = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
        if (map != null)
        {
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(16,16,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            var sample = new Texture2D(16,16,TextureFormat.RGBAFloat,false,true);
            try
            {
                Graphics.Blit(map,target); RenderTexture.active = target;
                sample.ReadPixels(new Rect(0,0,16,16),0,0); sample.Apply();
                Color mean = Color.clear; foreach (var pixel in sample.GetPixels()) mean += pixel / 256f;
                color *= mean;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(sample); }
        }
        Shader.SetGlobalVector(type == 1 ? "_ForestPineColor" : "_ForestLeafColor", (Vector4)color);
    }

    public void Dispose()
    {
        Shader.SetGlobalFloat("_ForestOverviewEnabled", 0);
        Shader.SetGlobalTexture("_ForestCoverage", Texture2D.blackTexture);
        Shader.SetGlobalTexture("_ForestDetail", Texture2D.blackTexture);
        if (coverage != null) UnityEngine.Object.Destroy(coverage);
        if (detail != null) UnityEngine.Object.Destroy(detail);
        coverage = detail = null; detailHash = int.MinValue;
    }
}
