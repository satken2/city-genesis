using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>Public transport snapshot independent of color-band visibility. Discard previous-city responses by generation.</summary>
public sealed class TransitDataStore : MonoBehaviour
{
    public static TransitDataStore Instance { get; private set; }
    public readonly List<TransitLineSummary> Lines = new();
    public int Revision { get; private set; }
    public int Session { get; private set; }
    private int generation;
    private bool inFlight;
    private bool stale = true;   // Lines change only with epochs, edits, and line settings, so refetch on Refresh rather than on a timer.
    private float retryAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() => Ensure();
    public static TransitDataStore Ensure()
    {
        if (Instance == null) new GameObject("TransitDataStore").AddComponent<TransitDataStore>();
        return Instance;
    }
    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }
    private void Update()
    {
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) return;
        if (stale && !inFlight && Time.unscaledTime >= retryAt) Fetch();
    }
    public void Clear()
    {
        generation++; Session++; Revision++;
        StopAllCoroutines(); inFlight = false; stale = true; retryAt = 0;
        Lines.Clear();
    }
    /// <summary>Refetch after the current request. Call whenever city data is refetched or the UI changes line settings.</summary>
    public void Refresh() => stale = true;
    private void Fetch()
    {
        stale = false; inFlight = true;
        int requestGeneration = generation;
        StartCoroutine(ApiClient.GetBulk<TransitLinesResponse>("/transit/lines", response =>
        {
            if (requestGeneration != generation) return;
            Lines.Clear();
            if (response?.lines != null) Lines.AddRange(response.lines);
            Revision++; inFlight = false;
        }, _ =>
        {
            if (requestGeneration != generation) return;
            inFlight = false; stale = true; retryAt = Time.unscaledTime + 5;
        }));
    }
}
