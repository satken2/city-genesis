using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generate site decorations as work separate from the structure and collider.
/// Building GameObjects have no scheduler lifetime component (RenderWorkLifetime), so run work on a shared host
/// and stop it in this component's OnDisable. Work stops while hidden (parked) and resumes unfinished jobs when shown again.
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

    /// <summary>Persistent host running site decoration jobs for all buildings. Recreated if destroyed.</summary>
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
        // When the host is destroyed its work has stopped too, so discard the handle and restart.
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
                // Share decoration geometry with the normal model, independent of structure close-up switching.
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
                    GetComponent<VisualModeObject>()?.RefreshMaterials();
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

/// <summary>Shared execution host for BuildingExteriorRenderer. Added through AddComponent at runtime; never placed in a scene.</summary>
internal sealed class ExteriorWorkHost : MonoBehaviour
{
}
