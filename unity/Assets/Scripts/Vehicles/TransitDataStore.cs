using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>色帯の表示設定から独立した公共交通スナップショット。古い都市の応答を世代で破棄する。</summary>
public sealed class TransitDataStore : MonoBehaviour
{
    public static TransitDataStore Instance { get; private set; }
    public readonly List<TransitLineSummary> Lines = new();
    public int Revision { get; private set; }
    public int Session { get; private set; }
    private int generation;
    private bool inFlight, pending;
    private float nextFetch;

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
        if (Time.unscaledTime >= nextFetch) Refresh();
    }
    public void Clear()
    {
        generation++; Session++; Revision++;
        StopAllCoroutines(); inFlight = pending = false;
        Lines.Clear(); nextFetch = 0;
    }
    public void Refresh()
    {
        if (inFlight) { pending = true; return; }
        inFlight = true; nextFetch = Time.unscaledTime + 5;
        int requestGeneration = generation;
        StartCoroutine(ApiClient.GetBulk<TransitLinesResponse>("/transit/lines", response =>
        {
            if (requestGeneration != generation) return;
            Lines.Clear();
            if (response?.lines != null) Lines.AddRange(response.lines);
            Revision++; Finish();
        }, _ => { if (requestGeneration == generation) Finish(); }));
    }
    private void Finish()
    {
        inFlight = false;
        if (pending) { pending = false; Refresh(); }
    }
}
