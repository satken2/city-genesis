using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 信号機の配置(<see cref="TrafficSignalLayout"/>)を、交差点メッシュGO(NodeMesh_*)の子 "Signals" に1つのメッシュとして実体化する。
/// 柱・アーム・灯器のモデル(<see cref="TrafficSignalStyle"/>)を材質ごとのサブメッシュへ焼き込み、1基ごとの GameObject や
/// Collider は作らない。生成メッシュは IntersectionMeshOwner が解放する。
/// </summary>
public static class TrafficSignalBuilder
{
    public const string ChildName = "Signals";

    /// <summary>部品モデルの1メッシュ。頂点はモデルのルート基準へ変換済みで、サブメッシュごとに材質を持つ。</summary>
    private sealed class Part
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uv;
        public int[][] Triangles;
        public Material[] Materials;
    }

    private sealed class Model
    {
        public readonly List<Part> Pole = new List<Part>();
        public readonly List<Part> Arm = new List<Part>();
        public readonly List<Part> Head = new List<Part>();
        public float HeadHalfWidth;
        public float HeadHalfDepth;
    }

    private static readonly Dictionary<string, Model> Models = new Dictionary<string, Model>();
    private static readonly HashSet<string> MissingStyles = new HashSet<string>();

    /// <summary>交差点メッシュGOの信号機を配置で置き換える。配置が空なら取り除くだけ。</summary>
    public static void Apply(GameObject nodeMesh, List<TrafficSignalPlacement> placements)
    {
        for (int i = nodeMesh.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = nodeMesh.transform.GetChild(i);
            if (child.name != ChildName) continue;
            // 破棄はフレーム末なので、同じフレームで作り直した新しい子と取り違えないよう名前を外す。
            child.name = ChildName + "(replaced)";
            Object.Destroy(child.gameObject);
        }
        if (placements == null || placements.Count == 0) return;
        Model model = Resolve(TrafficSignalStyle.Current);
        if (model == null) return;

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uv = new List<Vector2>();
        var triangles = new Dictionary<Material, List<int>>();
        var materials = new List<Material>();
        Vector3 origin = nodeMesh.transform.position;
        foreach (var placement in placements)
        {
            Vector3 arm = new Vector3(placement.ArmDirection.x, 0f, placement.ArmDirection.y);
            Vector3 facing = new Vector3(placement.Facing.x, 0f, placement.Facing.y);
            Vector3 foot = placement.PoleBase - origin;
            Quaternion armRotation = Quaternion.LookRotation(arm);
            Quaternion headRotation = Quaternion.LookRotation(facing);
            // アームは灯器の吊り金具の先へ、アーム方向に見た灯器の半分の広がりだけ延ばして先端を灯器の外側に揃える。
            float across = Mathf.Abs(Vector3.Dot(arm, headRotation * Vector3.right));
            float tip = model.HeadHalfWidth * across + model.HeadHalfDepth * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across));
            float length = placement.Reach + tip;
            if (placement.OwnsPole) Append(model.Pole, Matrix4x4.TRS(foot, armRotation, Vector3.one));
            Append(model.Arm, Matrix4x4.TRS(foot + arm * (length * 0.5f), armRotation, new Vector3(1f, 1f, length)));
            Append(model.Head, Matrix4x4.TRS(foot + arm * placement.Reach, headRotation, Vector3.one));
        }

        void Append(List<Part> parts, Matrix4x4 matrix)
        {
            // アームは長手だけを伸縮するので、法線は逆転置行列で変換する。
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            foreach (var part in parts)
            {
                int start = vertices.Count;
                for (int i = 0; i < part.Vertices.Length; i++)
                {
                    vertices.Add(matrix.MultiplyPoint3x4(part.Vertices[i]));
                    normals.Add(part.Normals.Length == part.Vertices.Length ? normalMatrix.MultiplyVector(part.Normals[i]).normalized : Vector3.up);
                    uv.Add(part.Uv.Length == part.Vertices.Length ? part.Uv[i] : Vector2.zero);
                }
                for (int sub = 0; sub < part.Triangles.Length; sub++)
                {
                    Material material = part.Materials[sub];
                    if (!triangles.TryGetValue(material, out var list))
                    {
                        triangles[material] = list = new List<int>();
                        materials.Add(material);
                    }
                    foreach (int index in part.Triangles[sub]) list.Add(start + index);
                }
            }
        }

        var mesh = new Mesh { name = "TrafficSignals" };
        if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv);
        mesh.subMeshCount = materials.Count;
        for (int i = 0; i < materials.Count; i++) mesh.SetTriangles(triangles[materials[i]], i);
        mesh.RecalculateBounds();

        var go = new GameObject(ChildName);
        go.transform.SetParent(nodeMesh.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials.ToArray();
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        // 遠景では道路の付帯設備と同じく省く。交差点ごとの信号機群が画面の縦に占める割合で判定する。
        var lod = go.AddComponent<LODGroup>();
        lod.SetLODs(new[] { new LOD(DrawLodConfig.NORMAL_EXIT_RATIO, new Renderer[] { renderer }) });
        lod.RecalculateBounds();
    }

    /// <summary>様式のモデルを読み、頂点を配置前の形で保持する。部品が1つでも無ければ null(信号機を描かない)。</summary>
    private static Model Resolve(TrafficSignalStyle style)
    {
        // 共有材質が破棄されていれば(ドメインを再読み込みしない再生の繰り返しなど)読み直す。
        if (Models.TryGetValue(style.Key, out Model cached) && cached.Pole[0].Materials[0] != null) return cached;
        if (MissingStyles.Contains(style.Key) || ModelManager.Instance == null) return null;
        var model = new Model();
        bool complete = Extract(style.ModelPath("pole"), model.Pole) && Extract(style.ModelPath("arm"), model.Arm)
            && Extract(style.ModelPath("head"), model.Head);
        if (!complete)
        {
            MissingStyles.Add(style.Key);
            Debug.LogWarning($"[TrafficSignalBuilder] 信号機のモデル(様式 {style.Key})が揃わないため描画しません。");
            return null;
        }
        var bounds = new Bounds();
        bool first = true;
        foreach (var part in model.Head)
            foreach (var vertex in part.Vertices)
            {
                if (first) { bounds = new Bounds(vertex, Vector3.zero); first = false; }
                else bounds.Encapsulate(vertex);
            }
        // 灯器は原点(吊り金具)を中心に左右対称なので、原点からの広がりで測る。
        model.HeadHalfWidth = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x));
        model.HeadHalfDepth = Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
        Models[style.Key] = model;
        return model;
    }

    private static bool Extract(string path, List<Part> parts)
    {
        GameObject prefab = ModelManager.Instance.GetModel(path);
        if (prefab == null) return false;
        Matrix4x4 rootInverse = prefab.transform.worldToLocalMatrix;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (mesh == null || renderer == null) continue;
            if (!mesh.isReadable)
            {
                Debug.LogWarning($"[TrafficSignalBuilder] {path} のメッシュが Read/Write 無効のため読めません。");
                return false;
            }
            Matrix4x4 local = rootInverse * filter.transform.localToWorldMatrix;
            Matrix4x4 normalLocal = local.inverse.transpose;
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            Vector2[] uv = mesh.uv;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = local.MultiplyPoint3x4(vertices[i]);
            for (int i = 0; i < normals.Length; i++) normals[i] = normalLocal.MultiplyVector(normals[i]).normalized;
            Material[] source = renderer.sharedMaterials;
            var submeshes = new List<int[]>();
            var materials = new List<Material>();
            for (int sub = 0; sub < mesh.subMeshCount && sub < source.Length; sub++)
            {
                if (source[sub] == null) continue;
                submeshes.Add(mesh.GetTriangles(sub));
                materials.Add(RoadPath.GetOrCreateRoadMaterial(source[sub]));
            }
            if (submeshes.Count == 0) continue;
            parts.Add(new Part { Vertices = vertices, Normals = normals, Uv = uv, Triangles = submeshes.ToArray(), Materials = materials.ToArray() });
        }
        return parts.Count > 0;
    }
}
