using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generate ground panels and site decorations as work separate from the structure and collider. Scatter parts go to <see cref="BuildingScatterRenderer"/>.
/// One shared worker serves every building. It runs at low priority (only with budget left after normal render work, so it never delays
/// building or road generation) and each step picks the pending building nearest to the camera.
/// Building GameObjects have no scheduler lifetime component (RenderWorkLifetime), so the worker runs on a shared host;
/// a hidden (parked) building leaves the queue in OnDisable and rejoins with its unfinished jobs when shown again.
/// </summary>
public sealed class BuildingExteriorRenderer : MonoBehaviour
{
    public delegate void Build(List<CombineInstance> meshes, List<Material> materials, List<Mesh> temporary);
    /// <summary>Destination for deferred jobs while a shown building is generated. Null builds everything immediately (plan previews and thumbnails).</summary>
    internal static List<Build> Capture;
    /// <summary>Whether site decorations other than the ground panel are deferred as well (set per model type by BuildingMeshBuilder). Ground panels are always deferred while Capture is set.</summary>
    internal static bool DeferSiteDecorations;
    /// <summary>Whether the building being generated has deferred jobs. Generators fall back to a white box only when there is nothing to draw now or later.</summary>
    internal static bool HasCapturedWork => Capture != null && Capture.Count > 0;

    private static readonly StageMarker jobMarker = new StageMarker("Building.Exterior");
    private static readonly HashSet<BuildingExteriorRenderer> pending = new HashSet<BuildingExteriorRenderer>();
    private static ExteriorWorkHost host;
    private static Coroutine worker;
    private List<Build> jobs;
    private int completed;

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

    public void Configure(List<Build> pendingJobs)
    {
        jobs = pendingJobs;
        OnEnable();
    }

    private void OnEnable()
    {
        if (jobs == null || completed >= jobs.Count || !isActiveAndEnabled) return;
        pending.Add(this);
        // The worker stops together with a destroyed host, so a missing host means it must be restarted.
        if (worker == null || host == null)
            worker = RenderWorkScheduler.Start(Host, Work(), "BuildingExterior", () => pending.Count > 0, RenderWorkPriority.Low);
    }

    private void OnDisable() => pending.Remove(this);

    private static IEnumerator Work()
    {
        try
        {
            while (pending.Count > 0)
            {
                Nearest().BuildNext();
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            worker = null;
        }
    }

    private static BuildingExteriorRenderer Nearest()
    {
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        BuildingExteriorRenderer best = null;
        float bestDistance = float.MaxValue;
        foreach (var candidate in pending)
        {
            float distance = (candidate.transform.position - camPos).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Run one job. A failing job is logged and skipped so the shared worker keeps serving other buildings.</summary>
    private void BuildNext()
    {
        using var scope = jobMarker.Auto();
        Build job = jobs[completed++];
        var parts = new List<CombineInstance>();
        var materials = new List<Material>();
        var temporary = new List<Mesh>();
        var scatter = new List<BuildingScatterRenderer.Instance>();
        Mesh mesh = null;
        DrawLod previous = BuildingMeshUtils.CurrentDrawLod;
        var previousSink = BuildingScatterRenderer.Sink;
        try
        {
            // Share decoration geometry with the normal model, independent of structure close-up switching.
            BuildingMeshUtils.CurrentDrawLod = DrawLod.Normal;
            BuildingScatterRenderer.Sink = scatter;
            job(parts, materials, temporary);
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
            BuildingScatterMember.Add(gameObject, scatter);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            BuildingMeshUtils.CurrentDrawLod = previous;
            BuildingScatterRenderer.Sink = previousSink;
            foreach (var item in temporary) if (item != null) Destroy(item);
            if (mesh != null) Destroy(mesh);
        }
        if (completed >= jobs.Count)
        {
            jobs = null;
            pending.Remove(this);
        }
    }
}

/// <summary>Shared execution host for BuildingExteriorRenderer. Added through AddComponent at runtime; never placed in a scene.</summary>
internal sealed class ExteriorWorkHost : MonoBehaviour
{
}
