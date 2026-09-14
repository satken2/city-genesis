using System.Collections.Generic;
using UnityEngine;

/// <summary>パス構造物の動的メッシュ。共有素材を使い、頂点予算ごとに描画単位を閉じる。</summary>
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

    public void Append(Mesh source, Vector3[] worldVertices)
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
            foreach (int index in source.GetTriangles(sub)) triangles[sub].Add(start + index);
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
