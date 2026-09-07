using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>共有モデルから描画部品を取り出す。生成したMeshとMaterialだけを所有する。</summary>
public sealed class PropRenderModel : IDisposable
{
    public sealed class Part
    {
        public Mesh mesh;
        public int submesh;
        public Material material;
        public Matrix4x4 local;
    }
    public readonly List<Part> normal = new List<Part>();
    public readonly List<Part> close = new List<Part>();
    public readonly List<Part> far = new List<Part>();
    public Bounds bounds;
    public float Radius => bounds.extents.magnitude;
    public float Clearance(float scale) => Mathf.Min(8f, Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.15f * scale);
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

    public PropRenderModel(int type)
    {
        var manager = ModelManager.Instance;
        if (manager == null) return;
        var prefab = manager.GetModel($"Models/Props/{type}");
        if (prefab == null) return;
        Extract(prefab, normal);
        if (normal.Count == 0) return;
        bool first = true;
        foreach (var part in normal)
        {
            var b = TransformBounds(part.mesh.bounds, part.local);
            if (first) { bounds = b; first = false; } else bounds.Encapsulate(b);
        }
        var closePrefab = manager.GetModel($"Models/Props/{type}_close", true);
        if (closePrefab != null) Extract(closePrefab, close);
        if (close.Count == 0) close.AddRange(normal);
        var farPrefab = manager.GetModel($"Models/Props/{type}_far", true);
        if (farPrefab != null) Extract(farPrefab, far);
        if (far.Count == 0)
        {
            // 既に軽いモデルはその形を維持する。複雑なモデルだけ頂点を空間集約し、
            // 種類の輪郭を別形状へ置き換えずに遠景の頂点数を抑える。
            foreach (var part in normal)
            {
                var mesh = Simplify(part.mesh, part.submesh);
                if (mesh == null) { far.Add(part); continue; }
                owned.Add(mesh);
                far.Add(new Part { mesh = mesh, material = part.material, local = part.local });
            }
        }
    }

    private void Extract(GameObject prefab, List<Part> parts)
    {
        var materials = new Dictionary<Material, Material>();
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || filter.sharedMesh == null) continue;
            var source = renderer.sharedMaterials;
            for (int i = 0; i < filter.sharedMesh.subMeshCount && i < source.Length; i++)
            {
                if (source[i] == null) continue;
                if (!materials.TryGetValue(source[i], out var mat))
                {
                    mat = new Material(source[i]) { enableInstancing = true };
                    materials.Add(source[i], mat); owned.Add(mat);
                }
                parts.Add(new Part { mesh = filter.sharedMesh, submesh = i, material = mat,
                    local = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix });
            }
        }
    }

    public static Bounds TransformBounds(Bounds b, Matrix4x4 matrix)
    {
        var result = new Bounds(matrix.MultiplyPoint3x4(b.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
            result.Encapsulate(matrix.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
        return result;
    }

    private static Mesh Simplify(Mesh source, int submesh)
    {
        if (!source.isReadable || source.GetTopology(submesh) != MeshTopology.Triangles) return null;
        var indices = source.GetTriangles(submesh);
        if (indices.Length <= 384) return null;
        var positions = source.vertices;
        var sourceUv = source.uv;
        var bounds = source.bounds;
        var size = bounds.size;
        var cells = new Dictionary<Vector3Int, int>();
        var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var counts = new List<int>();
        var mapping = new Dictionary<int, int>();
        foreach (int index in indices)
        {
            if (mapping.ContainsKey(index)) continue;
            Vector3 p = positions[index] - bounds.min;
            var key = new Vector3Int(
                Mathf.Clamp((int)(p.x / Mathf.Max(size.x, 1e-6f) * 4), 0, 3),
                Mathf.Clamp((int)(p.y / Mathf.Max(size.y, 1e-6f) * 8), 0, 7),
                Mathf.Clamp((int)(p.z / Mathf.Max(size.z, 1e-6f) * 4), 0, 3));
            if (!cells.TryGetValue(key, out int cluster))
            {
                cluster = vertices.Count; cells.Add(key, cluster);
                vertices.Add(Vector3.zero); uvs.Add(Vector2.zero); counts.Add(0);
            }
            mapping.Add(index, cluster);
            vertices[cluster] += positions[index]; counts[cluster]++;
            if (sourceUv.Length == positions.Length) uvs[cluster] += sourceUv[index];
        }
        for (int i = 0; i < vertices.Count; i++) { vertices[i] /= counts[i]; uvs[i] /= counts[i]; }
        var triangles = new List<int>(); var seen = new HashSet<(int,int,int)>();
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = mapping[indices[i]], b = mapping[indices[i+1]], c = mapping[indices[i+2]];
            if (a == b || b == c || a == c) continue;
            if (Vector3.Cross(vertices[b]-vertices[a], vertices[c]-vertices[a]).sqrMagnitude < 1e-12f) continue;
            var canonical = a < b && a < c ? (a,b,c) : b < c ? (b,c,a) : (c,a,b);
            if (!seen.Add(canonical)) continue;
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }
        if (triangles.Count == 0 || triangles.Count >= indices.Length) return null;
        var mesh = new Mesh { name = "PropFarClustered" };
        mesh.SetVertices(vertices); mesh.SetUVs(0,uvs); mesh.SetTriangles(triangles,0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }

    public void Dispose()
    {
        foreach (var obj in owned) if (obj != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
        owned.Clear();
    }
}
