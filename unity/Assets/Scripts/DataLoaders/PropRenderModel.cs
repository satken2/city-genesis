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
    public readonly List<Part> distant = new List<Part>();
    /// <summary>固定名の読み込みで `{名前}_close` / `{名前}_far` の専用モデルを読んだか。false の close は無印、far は自動集約。</summary>
    public bool DedicatedClose { get; private set; }
    public bool DedicatedFar { get; private set; }
    public Bounds bounds;
    public float Radius => bounds.extents.magnitude;
    public float Clearance(float scale) => Mathf.Min(8f, Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.15f * scale);
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

    public PropRenderModel(int type) : this(type.ToString(), type == 1 || type == 2) { }

    /// <summary>配置原点を解決済みのモデルを、GameObjectを個体ごとに持たない静止描画へ渡す。</summary>
    public PropRenderModel(GameObject root)
    {
        var lod = root.GetComponent<LODGroup>();
        if (lod == null) Extract(root, normal);
        else
        {
            foreach (var level in lod.GetLODs())
            {
                var target = level.screenRelativeTransitionHeight >= .25f ? close
                    : level.screenRelativeTransitionHeight > 0 ? normal : far;
                Extract(root, target, new HashSet<Renderer>(level.renderers));
            }
            if (normal.Count == 0) normal.AddRange(far);
        }
        if (close.Count == 0) close.AddRange(normal);
        if (far.Count == 0) far.AddRange(normal);
        CalculateBounds();
    }

    /// <summary>固定名のモデル(Props/ambient_* など)も同じ抽出で読む。distantTree は森林の最遠景樹冠を作るか。</summary>
    public PropRenderModel(string name, bool distantTree = false)
    {
        var manager = ModelManager.Instance;
        if (manager == null) return;
        var prefab = manager.GetModel($"Models/Props/{name}");
        if (prefab == null) return;
        Extract(prefab, normal);
        if (normal.Count == 0) return;
        CalculateBounds();
        var closePrefab = manager.GetModel($"Models/Props/{name}_close", true);
        if (closePrefab != null) Extract(closePrefab, close);
        DedicatedClose = close.Count > 0;
        if (close.Count == 0) close.AddRange(normal);
        var farPrefab = manager.GetModel($"Models/Props/{name}_far", true);
        if (farPrefab != null) Extract(farPrefab, far);
        DedicatedFar = far.Count > 0;
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
        if (distantTree) CreateDistantTree();
    }

    private void CreateDistantTree()
    {
        if (far.Count == 0) return;
        var canopy = Utils.TransformBounds(far[0].mesh.bounds, far[0].local);
        foreach (var part in far) canopy.Encapsulate(Utils.TransformBounds(part.mesh.bounds, part.local));
        var a = canopy.min; var b = canopy.max;
        var mesh = new Mesh { name = "PropDistantCrown" };
        mesh.vertices = new[] {new Vector3(a.x,a.y,a.z), new Vector3(b.x,a.y,a.z),
            new Vector3(b.x,a.y,b.z), new Vector3(a.x,a.y,b.z),
            new Vector3(canopy.center.x,b.y,canopy.center.z)};
        mesh.triangles = new[] {0,1,2, 0,2,3, 0,4,1, 1,4,2, 2,4,3, 3,4,0};
        mesh.uv = new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up,new Vector2(.5f,.5f)};
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        owned.Add(mesh);
        distant.Add(new Part {mesh=mesh, material=far[0].material, local=Matrix4x4.identity});
    }

    private void CalculateBounds()
    {
        bool first = true;
        foreach (var part in normal)
        {
            var b = Utils.TransformBounds(part.mesh.bounds, part.local);
            if (first) { bounds = b; first = false; } else bounds.Encapsulate(b);
        }
    }

    private void Extract(GameObject prefab, List<Part> parts, HashSet<Renderer> selected = null)
    {
        var materials = new Dictionary<Material, Material>();
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || filter.sharedMesh == null) continue;
            if (selected != null && !selected.Contains(renderer)) continue;
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
