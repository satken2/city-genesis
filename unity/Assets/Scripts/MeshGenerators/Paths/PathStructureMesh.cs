using System.Collections.Generic;
using UnityEngine;

/// <summary>Dynamic path-structure mesh. Use shared assets and finalize rendering units within vertex budgets.</summary>
public sealed class PathStructureMesh
{
    readonly GameObject parent;
    readonly string name;
    readonly Material[] materials;
    readonly List<Vector3> vertices = new();
    readonly List<Vector2> uv = new();
    readonly List<int>[] triangles;

    public PathStructureMesh(GameObject parent, string name, Material[] materials)
    {
        this.parent = parent; this.name = name;
        this.materials = new Material[materials.Length];
        triangles = new List<int>[materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            this.materials[i] = RoadPath.GetOrCreateRoadMaterial(materials[i]);
            triangles[i] = new List<int>();
        }
    }

    public void Append(Mesh source, Vector3[] worldVertices, bool reverseWinding = false)
    {
        if (vertices.Count + worldVertices.Length > RoadPath.SPLINE_MESH_VERTEX_BUDGET) Finish();
        int start = vertices.Count;
        var sourceUv = source.uv;
        for (int i = 0; i < worldVertices.Length; i++)
        {
            vertices.Add(parent.transform.InverseTransformPoint(worldVertices[i]));
            uv.Add(sourceUv.Length == worldVertices.Length ? sourceUv[i] : Vector2.zero);
        }
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            var indices = source.GetTriangles(sub);
            for (int i = 0; i < indices.Length; i += 3)
            {
                triangles[sub].Add(start + indices[i]);
                triangles[sub].Add(start + indices[i + (reverseWinding ? 2 : 1)]);
                triangles[sub].Add(start + indices[i + (reverseWinding ? 1 : 2)]);
            }
        }
    }

    /// <summary>面ごとに頂点を分け、隣り合う面の法線を混ぜずに直方体を描く。</summary>
    public void Box(Vector3 center, Vector3 size, Quaternion rotation, int material = 0)
    {
        if (vertices.Count + 24 > RoadPath.SPLINE_MESH_VERTEX_BUDGET) Finish();
        var axes = new[] { rotation * Vector3.right * (size.x * .5f), rotation * Vector3.up * (size.y * .5f), rotation * Vector3.forward * (size.z * .5f) };
        for (int axis = 0; axis < 3; axis++) for (int side = -1; side <= 1; side += 2)
        {
            Vector3 normal = axes[axis] * side;
            Vector3 u = axes[(axis + 1) % 3], v = axes[(axis + 2) % 3] * side;
            int start = vertices.Count;
            vertices.Add(parent.transform.InverseTransformPoint(center + normal - u - v));
            vertices.Add(parent.transform.InverseTransformPoint(center + normal + u - v));
            vertices.Add(parent.transform.InverseTransformPoint(center + normal + u + v));
            vertices.Add(parent.transform.InverseTransformPoint(center + normal - u + v));
            uv.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
            triangles[material].AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }
    }

    public void Bar(Vector3 a, Vector3 b, float radius, int material = 0, int sides = 6)
    {
        if ((b-a).sqrMagnitude < .000001f) return;
        if (vertices.Count + sides*2 > RoadPath.SPLINE_MESH_VERTEX_BUDGET) Finish();
        int start = vertices.Count;
        Vector3 direction = (b-a).normalized;
        Vector3 x = Vector3.Cross(direction, Mathf.Abs(direction.y) > .9f ? Vector3.right : Vector3.up).normalized;
        Vector3 y = Vector3.Cross(direction, x);
        for (int end = 0; end < 2; end++) for (int i = 0; i < sides; i++)
        {
            float angle = i * Mathf.PI * 2 / sides;
            vertices.Add(parent.transform.InverseTransformPoint((end == 0 ? a : b) + radius*(x*Mathf.Cos(angle)+y*Mathf.Sin(angle))));
            uv.Add(new Vector2((float)i/sides, end * Vector3.Distance(a,b)));
        }
        var t = triangles[material];
        for (int i = 0; i < sides; i++)
        {
            int j = (i+1)%sides;
            t.AddRange(new[] { start+i,start+j,start+sides+j,start+i,start+sides+j,start+sides+i });
        }
        for (int i = 1; i < sides-1; i++)
            t.AddRange(new[] { start,start+i+1,start+i,start+sides,start+sides+i,start+sides+i+1 });
    }

    public void Finish()
    {
        if (vertices.Count == 0) return;
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices); mesh.SetUVs(0,uv); mesh.subMeshCount = materials.Length;
        for (int i = 0; i < triangles.Length; i++) { mesh.SetTriangles(triangles[i],i); triangles[i].Clear(); }
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var go = new GameObject(name); go.transform.SetParent(parent.transform,false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = materials;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        vertices.Clear(); uv.Clear();
    }
}
