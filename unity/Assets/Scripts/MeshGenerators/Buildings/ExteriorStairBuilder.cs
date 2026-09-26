using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 辺ごとの外階段 {base|mid|top}-stair{辺index+1} を元の占有輪郭内に確保し、その辺の終点を原点として各階へ配置する。
/// 壁・角と違い位置番号のフォールバックはしない。mid-stair{n} が無い辺には外階段を置かない。
/// </summary>
public static class ExteriorStairBuilder
{
    static readonly string[] Levels = { "base", "mid", "top" };

    /// <summary>1辺ぶんの外階段。</summary>
    public sealed class Stair
    {
        public int Edge;
        public Bounds Bounds;
        public Vector3 Origin;
        public Quaternion Rotation;
        public float OpeningStart, OpeningEnd; // 後退後の本体輪郭の同じ辺上で、辺の始点から測った開口の範囲
        public readonly Mesh[] Meshes = new Mesh[3];
        public readonly Material[][] Materials = new Material[3][];

        public void Release()
        {
            foreach (Mesh mesh in Meshes) if (mesh != null) Object.Destroy(mesh);
        }
    }

    public sealed class Layout
    {
        public Vector2[] Body;
        public float MidScale;
        public readonly List<Stair> Stairs = new List<Stair>();

        /// <summary>Append せずに捨てるときに、読み込んだパーツのメッシュを解放する。</summary>
        public void Release()
        {
            foreach (var stair in Stairs) stair.Release();
        }

        public bool TryGetOpening(int edge, out float start, out float end)
        {
            foreach (var stair in Stairs)
            {
                if (stair.Edge != edge) continue;
                start = stair.OpeningStart; end = stair.OpeningEnd;
                return true;
            }
            start = end = 0;
            return false;
        }

        /// <summary>開口が辺の端まで達する頂点の角パーツは、階段パーツと重なるので置かない。</summary>
        public bool OmitsCorner(int vertex)
        {
            int n = Body.Length;
            foreach (var stair in Stairs)
            {
                int end = (stair.Edge + 1) % n;
                if (stair.Edge == vertex && stair.OpeningStart <= .01f) return true;
                if (end == vertex && stair.OpeningEnd >= Vector2.Distance(Body[stair.Edge], Body[end]) - .01f) return true;
            }
            return false;
        }

        public void Append(int floors, float height, List<CombineInstance> instances,
            List<Material> materials, List<Mesh> temporary)
        {
            foreach (var stair in Stairs)
            {
                for (int floor = 0; floor < floors; floor++)
                {
                    int level = floor == 0 ? 0 : floor == floors - 1 ? 2 : 1;
                    Mesh mesh = stair.Meshes[level];
                    var matrix = Matrix4x4.TRS(stair.Origin + Vector3.up * (floor * height), stair.Rotation,
                        new Vector3(1, level == 1 ? MidScale : 1, 1));
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = matrix });
                        materials.Add(stair.Materials[level][sub]);
                    }
                }
                temporary.AddRange(stair.Meshes);
            }
        }
    }

    public static Layout Create(Vector2[] footprint, string prefix, int suffix, float floorHeight)
    {
        if (footprint == null || footprint.Length < 3 || suffix < 0) return null;
        var candidates = new List<Stair>();
        for (int edge = 0; edge < footprint.Length; edge++)
        {
            var stair = Load(prefix, edge, suffix);
            if (stair != null) candidates.Add(stair);
        }
        if (candidates.Count == 0) return null;
        // 手すり・床厚を含む階段のbbox高さではなく、同じ建物の中層壁の著作階高で倍率を決める。
        var wall = BuildingMeshUtils.GetPart(prefix, "mid", "w", 1, suffix, true);
        Mesh reference = BuildingMeshUtils.CombineModuleMeshes(wall, out _);
        float authoredHeight = reference != null ? reference.bounds.size.y : 0;
        if (reference != null) Object.Destroy(reference);
        if (authoredHeight <= .01f)
        {
            foreach (var stair in candidates) stair.Release();
            return null;
        }
        var layout = new Layout { MidScale = floorHeight / authoredHeight };
        // 頂点0の辺から順に採否を決め、先に採用した外階段と両立しない辺の外階段は省略する。
        // 成立した試行は採用済み全件の配置を書き直すので、開口は常に最終の本体輪郭で測られている。
        foreach (var stair in candidates)
        {
            layout.Stairs.Add(stair);
            if (TryLayout(footprint, layout.Stairs, out var body)) { layout.Body = body; continue; }
            layout.Stairs.RemoveAt(layout.Stairs.Count - 1);
            stair.Release();
        }
        return layout.Stairs.Count > 0 ? layout : null;
    }

    static Stair Load(string prefix, int edge, int suffix)
    {
        int position = edge + 1;
        if (!BuildingMeshUtils.HasPart(prefix, "mid", "stair", position, suffix)) return null;
        var stair = new Stair { Edge = edge };
        for (int level = 0; level < 3; level++)
        {
            var part = BuildingMeshUtils.GetPart(prefix, Levels[level], "stair", position, suffix, true)
                ?? BuildingMeshUtils.GetPart(prefix, "mid", "stair", position, suffix, true);
            stair.Meshes[level] = BuildingMeshUtils.CombineModuleMeshes(part, out stair.Materials[level]);
            if (stair.Meshes[level] == null) { stair.Release(); return null; }
            if (level == 0) stair.Bounds = stair.Meshes[level].bounds;
            else stair.Bounds.Encapsulate(stair.Meshes[level].bounds);
        }
        return stair;
    }

    /// <summary>
    /// 外階段のある辺を奥行きだけ内側へ平行移動し、隣の辺（後退していればその後退線）との交点で本体輪郭を作る。
    /// 辺の反転・自己交差・元の輪郭からのはみ出し・階段どうしや本体との重なりが起きる配置は成立させない。
    /// 成立したときだけ各外階段の原点・向き・開口を書き込む。
    /// </summary>
    public static bool TryLayout(Vector2[] polygon, IReadOnlyList<Stair> stairs, out Vector2[] body)
    {
        body = null;
        int n = polygon?.Length ?? 0;
        if (n < 3 || stairs == null || PolygonUtils.SignedArea(polygon) <= 0) return false;
        var depths = new float[n];
        foreach (var stair in stairs)
        {
            if (stair.Edge < 0 || stair.Edge >= n || depths[stair.Edge] > 0) return false;
            Bounds bounds = stair.Bounds;
            if (Vector2.Distance(polygon[stair.Edge], polygon[(stair.Edge + 1) % n]) < .1f || bounds.size.x < .1f
                || -bounds.min.z < .1f || bounds.min.x < -.01f || bounds.max.z > .01f) return false;
            depths[stair.Edge] = -bounds.min.z;
        }
        var result = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n;
            if (depths[prev] <= 0 && depths[i] <= 0) { result[i] = polygon[i]; continue; }
            if (!Intersect(polygon[prev] + Inward(polygon, prev) * depths[prev], polygon[i] - polygon[prev],
                polygon[i] + Inward(polygon, i) * depths[i], polygon[(i + 1) % n] - polygon[i], out result[i])) return false;
        }
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n, next = (i + 1) % n;
            if (depths[prev] <= 0 && depths[i] <= 0 && depths[next] <= 0) continue;
            Vector2 along = (polygon[next] - polygon[i]).normalized;
            if (Vector2.Dot(result[next] - result[i], along) < (depths[i] > 0 ? .1f : .01f)) return false;
        }
        if (!SimpleInside(result, polygon) || PolygonUtils.SignedArea(result) < .1f) return false;

        var origins = new Vector3[stairs.Count];
        var rotations = new Quaternion[stairs.Count];
        var openings = new Vector2[stairs.Count];
        var footprints = new Vector2[stairs.Count][];
        for (int k = 0; k < stairs.Count; k++)
        {
            int start = stairs[k].Edge, end = (start + 1) % n;
            Vector2 along = (polygon[end] - polygon[start]).normalized;
            Vector2 inward = Inward(polygon, start);
            origins[k] = new Vector3(polygon[end].x, 0, polygon[end].y);
            rotations[k] = Quaternion.LookRotation(new Vector3(-inward.x, 0, -inward.y));
            Bounds bounds = stairs[k].Bounds;
            var corners = new Vector2[4];
            int index = 0;
            foreach (var local in new[] {new Vector2(bounds.min.x,bounds.min.z),new Vector2(bounds.max.x,bounds.min.z),
                new Vector2(bounds.max.x,bounds.max.z),new Vector2(bounds.min.x,bounds.max.z)})
            {
                Vector3 p = origins[k] + rotations[k] * new Vector3(local.x, 0, local.y);
                corners[index++] = new Vector2(p.x, p.z);
            }
            if (!SimpleInside(corners, polygon) || Overlaps(corners, result)) return false;
            for (int j = 0; j < k; j++) if (Overlaps(corners, footprints[j])) return false;
            footprints[k] = corners;
            float from = float.PositiveInfinity, to = float.NegativeInfinity;
            foreach (var point in corners)
            {
                float distance = Vector2.Dot(point - result[start], along);
                from = Mathf.Min(from, distance);
                to = Mathf.Max(to, distance);
            }
            openings[k] = new Vector2(from, to);
        }
        for (int k = 0; k < stairs.Count; k++)
        {
            stairs[k].Origin = origins[k];
            stairs[k].Rotation = rotations[k];
            stairs[k].OpeningStart = openings[k].x;
            stairs[k].OpeningEnd = openings[k].y;
        }
        body = result;
        return true;
    }

    /// <summary>反時計回りの輪郭で、辺 edge の内側を向く単位法線。</summary>
    static Vector2 Inward(Vector2[] polygon, int edge)
    {
        Vector2 along = (polygon[(edge + 1) % polygon.Length] - polygon[edge]).normalized;
        return new Vector2(-along.y, along.x);
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static bool Intersect(Vector2 a, Vector2 da, Vector2 b, Vector2 db, out Vector2 p)
    {
        float det = Cross(da, db); p = a;
        if (Mathf.Abs(det) < .0001f) return false;
        p = a + da * (Cross(b - a, db) / det);
        return true;
    }
    static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float ab1 = Cross(b-a,c-a), ab2 = Cross(b-a,d-a);
        float cd1 = Cross(d-c,a-c), cd2 = Cross(d-c,b-c);
        float abTolerance = (b-a).magnitude * .005f;
        float cdTolerance = (d-c).magnitude * .005f;
        return ((ab1 > abTolerance && ab2 < -abTolerance) || (ab2 > abTolerance && ab1 < -abTolerance))
            && ((cd1 > cdTolerance && cd2 < -cdTolerance) || (cd2 > cdTolerance && cd1 < -cdTolerance));
    }
    static bool SimpleInside(Vector2[] ring, Vector2[] boundary)
    {
        for (int i=0;i<ring.Length;i++)
        {
            Vector2 a=ring[i], b=ring[(i+1)%ring.Length];
            if (!PolygonUtils.Contains(boundary,a,.005f) || !PolygonUtils.Contains(boundary,(a+b)*.5f,.005f)) return false;
            for (int j=0;j<boundary.Length;j++)
                if (Crosses(a,b,boundary[j],boundary[(j+1)%boundary.Length])) return false;
            for (int j=i+1;j<ring.Length;j++)
                if (Crosses(a,b,ring[j],ring[(j+1)%ring.Length])) return false;
        }
        return true;
    }

    /// <summary>内部どうしが重なるか。辺を共有して接するだけの配置（本体と外階段の帯）は重なりとしない。</summary>
    static bool Overlaps(Vector2[] a, Vector2[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (Crosses(a[i], a[(i+1)%a.Length], b[j], b[(j+1)%b.Length])) return true;
        return HasPointInside(a, b) || HasPointInside(b, a);
    }
    static bool HasPointInside(Vector2[] ring, Vector2[] boundary)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < ring.Length; i++)
        {
            Vector2 a = ring[i], b = ring[(i+1)%ring.Length];
            centroid += a;
            if (StrictlyInside(boundary, a) || StrictlyInside(boundary, (a+b)*.5f)) return true;
        }
        return StrictlyInside(boundary, centroid / ring.Length);
    }
    static bool StrictlyInside(Vector2[] polygon, Vector2 point)
        => PolygonUtils.Contains(polygon, point) && PolygonUtils.DistanceToBoundary(polygon, point) > .005f;
}
