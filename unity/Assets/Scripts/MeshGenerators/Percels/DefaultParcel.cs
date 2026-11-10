using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Example parcel generator extruding polygons into boxes.
/// </summary>
public class DefaultParcel : IParcelMeshGenerator
{
    /// <summary>
    /// Top face (n vertices) and sides for thickness&gt;0 (four vertices per edge) determine final vertex/index counts;
    /// write directly to arrays, avoiding intermediate vertex Lists on parcel generation/terrain reconformity.
    /// </summary>
    public Mesh Generate(Vector2[] points, Vector2 centroid, float thickness, float[] topVertexLocalY)
    {
        Mesh mesh = new Mesh();
        int n = points.Length;
        bool hasSides = thickness > 0f;

        // The top face is the click surface, so a concave parcel must not cover the road or the neighbor it wraps around.
        List<int> top = BuildingMeshBuilder.TriangulateOrFan(points);

        int vertexCount = n + (hasSides ? 4 * n : 0);
        int indexCount = top.Count + (hasSides ? 6 * n : 0);
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        int[] triangles = new int[indexCount];

        // ----- Top Face -----
        // Top vertices occupy indices 0..n-1; side generation also uses them as upper coordinates.
        for (int i = 0; i < n; i++)
        {
            // Apply topVertexLocalY to per-vertex local Y.
            // This makes parcel colliders follow sloped terrain correctly.
            float localY = (topVertexLocalY != null && i < topVertexLocalY.Length) ? topVertexLocalY[i] : 0f;
            vertices[i] = new Vector3(points[i].x - centroid.x, localY, points[i].y - centroid.y);
            normals[i] = Vector3.up;
        }

        // Backend parcel polygons are normalized counterclockwise (CCW).
        // To make top meshes face upward (visible from +Y),
        // emit each counterclockwise triangle in reverse order.
        // Otherwise Unity MeshCollider's one-sided collisions let downward raycasts pass through.
        int t = 0;
        for (int i = 0; i < top.Count; i += 3)
        {
            triangles[t++] = top[i];
            triangles[t++] = top[i + 2];
            triangles[t++] = top[i + 1];
        }

        // ----- Sides -----
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

                // Compute outward normals.
                Vector3 edge = vTopNext - vTopCurrent;
                Vector3 faceNormal = Vector3.Cross(edge, Vector3.up).normalized;

                int sideStart = v;

                // Register vertices top→bottom.
                vertices[v++] = vTopCurrent;
                vertices[v++] = vBottomCurrent;
                vertices[v++] = vBottomNext;
                vertices[v++] = vTopNext;

                normals[sideStart] = faceNormal;
                normals[sideStart + 1] = faceNormal;
                normals[sideStart + 2] = faceNormal;
                normals[sideStart + 3] = faceNormal;

                // Triangle (1).
                triangles[t++] = sideStart;
                triangles[t++] = sideStart + 1;
                triangles[t++] = sideStart + 2;

                // Triangle (2).
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
