using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 埋立地の岸壁メッシュ(天端と垂直の面)を純データで作る。頂点はワールド座標で、GameObject は原点に置く。
///
/// 帯・壁・天端の高さはバックエンドの派生値(<see cref="ReclaimedLandStore"/>)をそのまま使い、ここでは判定しない。
/// 地面の高さは関数で受け取るので、シーンの状態(地形タイル・ストア)が無い検証ハーネスからも呼べる。
/// Mesh の組み立てはメインスレッドで呼び出し側(<see cref="ReclaimedLandManager"/>)が行う。
/// </summary>
public static class ReclaimedLandMeshBuilder
{
    public const float WALL_SAMPLE_SPACING = 2f;   // 面の下端を外側の地面に合わせる列の最大間隔(m)
    public const float WALL_FOOTING_DEPTH = 1f;    // 下端を外側の地面より下げ、列の間の起伏で隙間を見せない量(m)
    public const float WALL_MIN_HEIGHT = 0.05f;    // 外側の地面が天端より高い所でも潰し切らずに残す面の高さ(m)
    public const float UV_TILE_SIZE = 10f;         // ワールド座標で1枚に貼る長さ(m)。建物のコンクリート基盤と同じ

    /// <summary>ビルド結果。三角形は天端と壁で分けて返す(どちらも Vertices への添字)。</summary>
    public sealed class MeshData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<int> TopTriangles = new List<int>();
        public readonly List<int> WallTriangles = new List<int>();
    }

    /// <summary>
    /// 天端(帯の多角形を高さ top の水平面にしたもの。法線は上)と、壁の各区間に沿った垂直の面を作る。
    /// 面の上端は top、下端は区間の外側 <see cref="Constants.QUAY_PROBE_DISTANCE"/> の地面から
    /// <see cref="WALL_FOOTING_DEPTH"/> 下(top − <see cref="WALL_MIN_HEIGHT"/> で頭打ち)。法線は区間の外向き。
    /// </summary>
    /// <param name="band">帯の多角形の列。各多角形は [外周, 穴...] のリング(閉じ点なし)。</param>
    /// <param name="walls">面を張る区間。Normal は外向きの単位ベクトル。</param>
    /// <param name="top">天端の絶対標高。</param>
    /// <param name="groundY">ワールド(x, z) → 地面の絶対標高。</param>
    public static MeshData Build(Vector2[][][] band, ReclaimedLandSegment[] walls, float top, Func<float, float, float> groundY)
    {
        var data = new MeshData();
        if (band != null)
        {
            foreach (Vector2[][] polygon in band) AppendTop(data, polygon, top);
        }
        if (walls != null && groundY != null)
        {
            foreach (ReclaimedLandSegment wall in walls) AppendWall(data, wall, top, groundY);
        }
        return data;
    }

    private static void AppendTop(MeshData data, Vector2[][] polygon, float top)
    {
        if (polygon == null || polygon.Length == 0 || polygon[0] == null || polygon[0].Length < 3) return;
        var holes = new List<Vector2[]>();
        for (int i = 1; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 3) holes.Add(polygon[i]);
        }

        List<Vector2> points;
        List<int> triangles;
        try
        {
            // 穴(全周が岸壁の島など)を扱える分割は OpenSpace の面と共用する。
            var surface = new OpenSpaceLayout.Surface { Polygon = polygon[0], Holes = holes.Count > 0 ? holes.ToArray() : null };
            OpenSpaceBuildingMeshBuilder.Triangulate(surface, out points, out triangles);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"ReclaimedLandMeshBuilder: 天端の三角形分割に失敗しました: {e.Message}");
            return;
        }
        if (triangles.Count < 3) return;

        int baseIndex = data.Vertices.Count;
        foreach (Vector2 p in points)
        {
            data.Vertices.Add(new Vector3(p.x, top, p.y));
            data.Normals.Add(Vector3.up);
            data.Uvs.Add(p / UV_TILE_SIZE);
        }
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            Vector2 pa = points[a], pb = points[b], pc = points[c];
            float cross = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
            // XZ で反時計回りの三角形は法線が下を向くので、上から見える向きに巻き直す。
            if (cross > 0f)
            {
                data.TopTriangles.Add(baseIndex + a);
                data.TopTriangles.Add(baseIndex + c);
                data.TopTriangles.Add(baseIndex + b);
            }
            else
            {
                data.TopTriangles.Add(baseIndex + a);
                data.TopTriangles.Add(baseIndex + b);
                data.TopTriangles.Add(baseIndex + c);
            }
        }
    }

    private static void AppendWall(MeshData data, ReclaimedLandSegment wall, float top, Func<float, float, float> groundY)
    {
        Vector2 d = wall.B - wall.A;
        float length = d.magnitude;
        if (length < 1e-4f) return;
        Vector2 outward = wall.Normal.sqrMagnitude > 1e-8f ? wall.Normal.normalized : new Vector2(d.y, -d.x) / length;
        var normal = new Vector3(outward.x, 0f, outward.y);

        int columns = Mathf.Max(1, Mathf.CeilToInt(length / WALL_SAMPLE_SPACING));
        int baseIndex = data.Vertices.Count;
        for (int c = 0; c <= columns; c++)
        {
            float t = c / (float)columns;
            Vector2 p = wall.A + d * t;
            Vector2 probe = p + outward * Constants.QUAY_PROBE_DISTANCE;
            float bottom = Mathf.Min(groundY(probe.x, probe.y) - WALL_FOOTING_DEPTH, top - WALL_MIN_HEIGHT);
            float u = length * t / UV_TILE_SIZE;
            data.Vertices.Add(new Vector3(p.x, top, p.y));
            data.Normals.Add(normal);
            data.Uvs.Add(new Vector2(u, top / UV_TILE_SIZE));
            data.Vertices.Add(new Vector3(p.x, bottom, p.y));
            data.Normals.Add(normal);
            data.Uvs.Add(new Vector2(u, bottom / UV_TILE_SIZE));
        }

        // (上端i, 上端i+1, 下端i+1) の表は XZ で (dz, −dx) を向くので、外向きと逆なら巻き直す。
        bool flip = d.y * outward.x - d.x * outward.y < 0f;
        for (int c = 0; c < columns; c++)
        {
            int t0 = baseIndex + c * 2, b0 = t0 + 1, t1 = t0 + 2, b1 = t0 + 3;
            if (flip)
            {
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b1); data.WallTriangles.Add(t1);
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b0); data.WallTriangles.Add(b1);
            }
            else
            {
                data.WallTriangles.Add(t0); data.WallTriangles.Add(t1); data.WallTriangles.Add(b1);
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b1); data.WallTriangles.Add(b0);
            }
        }
    }
}
