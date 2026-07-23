// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ポリゴンを押し出して箱状にするパーセルメッシュ生成クラス例
/// </summary>
public class DefaultParcel : IParcelMeshGenerator
{
    public Mesh Generate(Vector2[] points, Vector2 centroid, float thickness, float[] topVertexLocalY)
    {
        Mesh mesh = new Mesh();
        int n = points.Length;

        // 上面頂点リスト(ローカル座標系へ変換)
        Vector3[] topVertices = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            // 頂点ごとのローカルY座標に topVertexLocalY を適用する。
            // これにより、地形が傾斜していてもパーセルのコライダーが正しく地形に追従する。
            float localY = (topVertexLocalY != null && i < topVertexLocalY.Length) ? topVertexLocalY[i] : 0f;
            topVertices[i] = new Vector3(points[i].x - centroid.x, localY, points[i].y - centroid.y);
        }

        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<int> triangles = new List<int>();

        // ----- 上面 (Top Face) -----
        int topStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(topVertices[i]);
            normals.Add(Vector3.up);  // 上向き法線
        }
        
        // バックエンドから送られてくる土地区画のポリゴンは反時計回り（CCW）に正規化されている。
        // 上面メッシュ（+Y方向から見える面）が上を向く（表になる）ようにするため、
        // 三角形インデックスの張り順を反時計回り（0 -> i + 1 -> i）のファンに変更する。
        // これを行わないと、UnityのMeshColliderは片面衝突のため、上からのレイキャストが通り抜けてしまう。
        for (int i = 1; i < n - 1; i++)
        {
            triangles.Add(topStart);
            triangles.Add(topStart + i + 1);
            triangles.Add(topStart + i);
        }

        // ----- 側面 (Sides) -----
        if (thickness > 0f)
        {
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector3 vTopCurrent = topVertices[i];
                Vector3 vTopNext    = topVertices[next];
                Vector3 vBottomCurrent = new Vector3(vTopCurrent.x, -thickness, vTopCurrent.z);
                Vector3 vBottomNext    = new Vector3(vTopNext.x,    -thickness, vTopNext.z);

                // 外向きの法線を計算
                Vector3 edge = vTopNext - vTopCurrent;
                Vector3 faceNormal = Vector3.Cross(edge, Vector3.up).normalized;

                int sideStart = vertices.Count;

                // 頂点登録(上→下の順)
                vertices.Add(vTopCurrent);
                vertices.Add(vBottomCurrent);
                vertices.Add(vBottomNext);
                vertices.Add(vTopNext);

                // 法線登録
                normals.Add(faceNormal);
                normals.Add(faceNormal);
                normals.Add(faceNormal);
                normals.Add(faceNormal);

                // 三角形(1)
                triangles.Add(sideStart);
                triangles.Add(sideStart + 1);
                triangles.Add(sideStart + 2);

                // 三角形(2)
                triangles.Add(sideStart);
                triangles.Add(sideStart + 2);
                triangles.Add(sideStart + 3);
            }
        }

        mesh.vertices = vertices.ToArray();
        mesh.normals = normals.ToArray();
        mesh.triangles = triangles.ToArray();

        mesh.RecalculateBounds();
        return mesh;
    }
}
