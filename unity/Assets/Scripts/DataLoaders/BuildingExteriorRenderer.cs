using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>敷地装飾を躯体・コライダーとは別の仕事として生成する。</summary>
public sealed class BuildingExteriorRenderer : MonoBehaviour
{
    public delegate void Build(List<CombineInstance> meshes, List<Material> materials, List<Mesh> temporary);
    internal static List<Build> Capture;
    private List<Build> jobs;
    private int completed;
    private Coroutine worker;

    public void Configure(List<Build> pending)
    {
        jobs = pending;
        OnEnable();
    }
    private void OnEnable()
    {
        if (jobs != null && completed < jobs.Count && worker == null && isActiveAndEnabled)
            worker = RenderWorkScheduler.Start(this, Generate(), "BuildingExterior");
    }
    private void OnDisable()
    {
        if (worker != null) RenderWorkScheduler.Stop(this, worker);
        worker = null;
    }
    private IEnumerator Generate()
    {
        while (completed < jobs.Count)
        {
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
    }
}
