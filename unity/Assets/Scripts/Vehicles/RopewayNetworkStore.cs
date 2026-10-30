using System;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>Ropeway snapshot extending beyond resident cells. Never publish partially successful pages as disconnections.</summary>
public sealed class RopewayNetworkStore : MonoBehaviour
{
    public static RopewayNetworkStore Instance { get; private set; }
    public IReadOnlyList<PathSummary> Paths => paths;
    public int Revision { get; private set; }
    public int Session { get; private set; } = -1;
    readonly List<PathSummary> paths = new();
    Coroutine request;
    int generation;
    bool stale = true;   // The network changes only with epochs and edits, so refetch on RequestRefresh rather than on a timer.
    float retryAt;

    public static RopewayNetworkStore Ensure()
    {
        if (Instance == null) new GameObject("RopewayNetworkStore").AddComponent<RopewayNetworkStore>();
        return Instance;
    }

    void Awake() => Instance = this;
    void OnDestroy() { Clear(); if (Instance == this) Instance = null; }
    void OnDisable() => Clear();

    void Update()
    {
        int session = TransitDataStore.Ensure().Session;
        if (Session != session) { Clear(); Session = session; }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { if (request != null || paths.Count > 0) Clear(); return; }
        if (request == null && stale && Time.unscaledTime >= retryAt)
        {
            stale = false;
            request = StartCoroutine(Fetch(generation));
        }
    }

    /// <summary>Refetch after the current request. Call whenever city data is refetched.</summary>
    public void RequestRefresh() => stale = true;

    public void Clear()
    {
        generation++;
        StopAllCoroutines(); request = null;
        paths.Clear(); Revision++; stale = true; retryAt = 0;
    }

    IEnumerator Fetch(int expectedGeneration)
    {
        var received = new List<PathSummary>();
        int cursor = 0;
        bool complete = false;
        string filter = Uri.EscapeDataString($"[path_type][eq]{Constants.PATH_CONVEYOR}");
        try
        {
            while (true)
            {
                PagedPathListResponse page = null;
                yield return ApiClient.GetBulk<PagedPathListResponse>($"/paths/?filter={filter}&limit=500&cursor={cursor}", value => page = value);
                if (expectedGeneration != generation || page?.paths == null) yield break;
                received.AddRange(page.paths);
                if (!page.next_cursor.HasValue) break;
                if (page.next_cursor.Value <= cursor) yield break;
                cursor = page.next_cursor.Value;
            }
            received.Sort((a, b) => a.path_id.CompareTo(b.path_id));
            bool changed = received.Count != paths.Count;
            for (int i = 0; !changed && i < paths.Count; i++)
            {
                var a = received[i]; var b = paths[i];
                changed = a.path_id != b.path_id || a.version != b.version || a.status != b.status
                    || a.path_variant != b.path_variant || a.start_node_id != b.start_node_id || a.end_node_id != b.end_node_id;
            }
            if (changed) { paths.Clear(); paths.AddRange(received); Revision++; }
            complete = true;
        }
        finally
        {
            if (expectedGeneration == generation)
            {
                request = null;
                if (!complete) { stale = true; retryAt = Time.unscaledTime + 5; }
            }
        }
    }
}
