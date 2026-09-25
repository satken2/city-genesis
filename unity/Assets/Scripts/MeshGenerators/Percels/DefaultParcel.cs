using UnityEngine;

/// <summary>
/// ポリゴンを押し出して箱状にするパーセルメッシュ生成クラス例
/// </summary>
public class DefaultParcel : IParcelMeshGenerator
{
    /// <summary>
    /// 上面ファン(n頂点)と、thickness&gt;0 のときの側面(辺ごとに4頂点)で頂点数・インデックス数が確定するので、
    /// 最終配列へ直接書き込む(区画の生成・地形再追従のたびに走るため中間 List を作らない)。
    /// </summary>
    public Mesh Generate(Vector2[] points, Vector2 centroid, float thickness, float[] topVertexLocalY)
    {
        Mesh mesh = new Mesh();
        int n = points.Length;
        bool hasSides = thickness > 0f;

        int vertexCount = n + (hasSides ? 4 * n : 0);
        int indexCount = 3 * Mathf.Max(0, n - 2) + (hasSides ? 6 * n : 0);
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        int[] triangles = new int[indexCount];

        // ----- 上面 (Top Face) -----
        // 上面の頂点は添字 0..n-1 に置き、側面の生成でも上端の座標として参照する。
        for (int i = 0; i < n; i++)
        {
            // 頂点ごとのローカルY座標に topVertexLocalY を適用する。
            // これにより、地形が傾斜していてもパーセルのコライダーが正しく地形に追従する。
            float localY = (topVertexLocalY != null && i < topVertexLocalY.Length) ? topVertexLocalY[i] : 0f;
            vertices[i] = new Vector3(points[i].x - centroid.x, localY, points[i].y - centroid.y);
            normals[i] = Vector3.up;
        }

        // バックエンドから送られてくる土地区画のポリゴンは反時計回り（CCW）に正規化されている。
        // 上面メッシュ（+Y方向から見える面）が上を向く（表になる）ようにするため、
        // 三角形インデックスの張り順を反時計回り（0 -> i + 1 -> i）のファンにする。
        // これを行わないと、UnityのMeshColliderは片面衝突のため、上からのレイキャストが通り抜けてしまう。
        int t = 0;
        for (int i = 1; i < n - 1; i++)
        {
            triangles[t++] = 0;
            triangles[t++] = i + 1;
            triangles[t++] = i;
        }

        // ----- 側面 (Sides) -----
        if (hasSides)
        {
            int v = n;
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector3 vTopCurrent = vertices[i];
                Vector3 vTopNext    = vertices[next];
                Vector3 vBottomCurrent = new Vector3(vTopCurrent.x, -thickness, vTopCurrent.z);
                Vector3 vBottomNext    = new Vector3(vTopNext.x,    -thickness, vTopNext.z);

                // 外向きの法線を計算
                Vector3 edge = vTopNext - vTopCurrent;
                Vector3 faceNormal = Vector3.Cross(edge, Vector3.up).normalized;

                int sideStart = v;

                // 頂点登録(上→下の順)
                vertices[v++] = vTopCurrent;
                vertices[v++] = vBottomCurrent;
                vertices[v++] = vBottomNext;
                vertices[v++] = vTopNext;

                normals[sideStart] = faceNormal;
                normals[sideStart + 1] = faceNormal;
                normals[sideStart + 2] = faceNormal;
                normals[sideStart + 3] = faceNormal;

                // 三角形(1)
                triangles[t++] = sideStart;
                triangles[t++] = sideStart + 1;
                triangles[t++] = sideStart + 2;

                // 三角形(2)
                triangles[t++] = sideStart;
                triangles[t++] = sideStart + 2;
                triangles[t++] = sideStart + 3;
            }
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.triangles = triangles;

        mesh.RecalculateBounds();
        return mesh;
    }
}
