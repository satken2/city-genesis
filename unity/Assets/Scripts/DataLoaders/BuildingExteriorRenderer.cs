using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敷地装飾を躯体・コライダーとは別の仕事として生成する。
/// 建物GOにスケジューラの寿命コンポーネント(RenderWorkLifetime)を付けないため、実行は共通ホストで行い、
/// 停止は自分の OnDisable で行う。非表示(退避)中は止まり、再表示で未完了のジョブから再開する。
/// </summary>
public sealed class BuildingExteriorRenderer : MonoBehaviour
{
    public delegate void Build(List<CombineInstance> meshes, List<Material> materials, List<Mesh> temporary);
    internal static List<Build> Capture;
    private List<Build> jobs;
    private int completed;
    private Coroutine worker;
    private ExteriorWorkHost workerHost;

    private static ExteriorWorkHost host;

    /// <summary>全建物の敷地装飾ジョブを実行する常駐ホスト。破棄されていれば作り直す。</summary>
    private static ExteriorWorkHost Host
    {
        get
        {
            if (host == null)
            {
                var go = new GameObject("BuildingExteriorWork");
                if (Application.isPlaying) DontDestroyOnLoad(go);
                host = go.AddComponent<ExteriorWorkHost>();
            }
            return host;
        }
    }

    public void Configure(List<Build> pending)
    {
        jobs = pending;
        OnEnable();
    }
    private void OnEnable()
    {
        // ホストが破棄されるとその仕事も止まっているので、ハンドルを捨てて開始し直す。
        if (worker != null && workerHost == null) worker = null;
        if (jobs != null && completed < jobs.Count && worker == null && isActiveAndEnabled)
        {
            workerHost = Host;
            worker = RenderWorkScheduler.Start(workerHost, Generate(), "BuildingExterior");
        }
    }
    private void OnDisable()
    {
        if (worker != null) RenderWorkScheduler.Stop(workerHost, worker);
        worker = null;
        workerHost = null;
    }
    private IEnumerator Generate()
    {
        while (completed < jobs.Count)
        {
            if (this == null) yield break;
            var parts = new List<CombineInstance>();
            var materials = new List<Material>();
            var temporary = new List<Mesh>();
            Mesh mesh = null;
            DrawLod previous = BuildingMeshUtils.CurrentDrawLod;
            try
            {
                // 装飾の形状は通常モデルで共有し、躯体の接写切り替えには依存させない。
                BuildingMeshUtils.CurrentDrawLod = DrawLod.Normal;
                jobs[completed](parts, materials, temporary);
                if (parts.Count > 0)
                {
                    mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    mesh.CombineMeshes(parts.ToArray(), false, true);
                    mesh.RecalculateBounds();
                    var result = BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(mesh, materials.ToArray()));
                    var go = new GameObject("BuildingExterior");
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = result.Materials;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    go.AddComponent<RenderCacheIdentity>().mesh = mesh;
                    mesh = null;
                    GetComponent<VisualModeObject>()?.InitializeMaterials();
                }
                completed++;
            }
            finally
            {
                BuildingMeshUtils.CurrentDrawLod = previous;
                foreach (var item in temporary) if (item != null) Destroy(item);
                if (mesh != null) Destroy(mesh);
            }
            yield return null;
        }
        jobs = null;
        worker = null;
        workerHost = null;
    }
}

/// <summary>BuildingExteriorRenderer の共通実行ホスト。実行時に AddComponent するだけで、シーンには置かない。</summary>
internal sealed class ExteriorWorkHost : MonoBehaviour
{
}
