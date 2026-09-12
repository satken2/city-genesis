using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 描画LODの計測基盤。各マネージャーが「描画LOD別のオブジェクト数」と「メッシュ生成にかかった時間」を
/// 報告し、<see cref="DrawLodStatsOverlay"/> が画面左上に表示する(F8で切替)。
/// 性能改善の合否は「頂点を何%減らしたか」ではなく、この画面のフレーム時間・生成時間・頂点数で判定する。
/// 計測値は静的に保持するので、シーンにコンポーネントを置かなくても集計だけは常に動く。
/// </summary>
public static class DrawLodStats
{
    public enum Category { Building = 0, Path = 1 }

    private static readonly int[,] objectCounts = new int[2, 4];
    private static readonly double[] generationMsWindow = new double[2];
    private static readonly int[] generationCountWindow = new int[2];
    private static double lastGenerationMs0, lastGenerationMs1;
    private static int lastGenerationCount0, lastGenerationCount1;
    private static float windowStart;

    /// <summary>オブジェクトの描画LODが変わったとき(生成・破棄を含む)に呼ぶ。from/to は null 可(生成/破棄)。</summary>
    public static void OnDrawLodChanged(Category category, DrawLod? from, DrawLod? to)
    {
        int c = (int)category;
        if (from.HasValue) objectCounts[c, (int)from.Value] = Mathf.Max(0, objectCounts[c, (int)from.Value] - 1);
        if (to.HasValue) objectCounts[c, (int)to.Value]++;
    }

    /// <summary>メッシュ生成1件の所要時間(ms)を報告する。1秒窓で集計する。</summary>
    public static void AddGenerationMs(Category category, double ms)
    {
        RollWindow();
        generationMsWindow[(int)category] += ms;
        generationCountWindow[(int)category]++;
        RenderWorkScheduler.CountEvent(category == Category.Building ? "building.generated" : "path.generated");
    }

    public static int GetObjectCount(Category category, DrawLod lod) => objectCounts[(int)category, (int)lod];

    /// <summary>直近1秒窓(確定分)の生成時間合計と件数。</summary>
    public static void GetLastWindow(Category category, out double ms, out int count)
    {
        RollWindow();
        if (category == Category.Building) { ms = lastGenerationMs0; count = lastGenerationCount0; }
        else { ms = lastGenerationMs1; count = lastGenerationCount1; }
    }

    /// <summary>カテゴリのオブジェクト数集計をゼロに戻す(各マネージャーの全クリア時に呼ぶ)。</summary>
    public static void ResetCounts(Category category)
    {
        int c = (int)category;
        for (int i = 0; i < 4; i++) objectCounts[c, i] = 0;
    }

    private static void RollWindow()
    {
        float now = Time.realtimeSinceStartup;
        if (now - windowStart < 1f) return;
        lastGenerationMs0 = generationMsWindow[0];
        lastGenerationMs1 = generationMsWindow[1];
        lastGenerationCount0 = generationCountWindow[0];
        lastGenerationCount1 = generationCountWindow[1];
        generationMsWindow[0] = generationMsWindow[1] = 0;
        generationCountWindow[0] = generationCountWindow[1] = 0;
        windowStart = now;
    }
}

/// <summary>
/// 描画LOD統計のデバッグ表示。シーンへの配置は不要で、起動時に自前で常駐オブジェクトを作る。
/// F8 で表示切替。表示中は2秒ごとにアクティブな MeshFilter を走査して頂点・三角形の総数を集計する
/// (走査は数万件でも数ms程度だが常時は行わない)。
/// </summary>
public class DrawLodStatsOverlay : MonoBehaviour
{
    private const Key TOGGLE_KEY = Key.F8;
    private const float SAMPLE_INTERVAL_SEC = 2f;

    private bool visible;
    private float nextSampleTime;
    private long sampledVertices, sampledTriangles;
    private int sampledRenderers;
    private float smoothedFrameMs;
    private readonly float[] frameSamples = new float[600];
    private int frameSampleCount, frameSampleIndex;
    private float p95, p99;
    private readonly StringBuilder sb = new StringBuilder(1024);
    private GUIStyle style;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<DrawLodStatsOverlay>() != null) return;
        var go = new GameObject("DrawLodStatsOverlay");
        go.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(go);
        go.AddComponent<DrawLodStatsOverlay>();
    }

    private void Update()
    {
        // フレーム時間は指数平滑で表示する(瞬間値は読めない)。
        float frameMs = Time.unscaledDeltaTime * 1000f;
        frameSamples[frameSampleIndex++ % frameSamples.Length] = frameMs;
        frameSampleCount = Mathf.Min(frameSampleCount+1, frameSamples.Length);
        smoothedFrameMs = smoothedFrameMs <= 0f ? frameMs : Mathf.Lerp(smoothedFrameMs, frameMs, 0.1f);

        if (InputUtil.KeyWasPressedThisFrame(TOGGLE_KEY))
        {
            visible = !visible;
            nextSampleTime = 0f;
        }
        if (!visible) return;
        if (Time.realtimeSinceStartup >= nextSampleTime)
        {
            var sorted = new float[frameSampleCount];
            System.Array.Copy(frameSamples, sorted, frameSampleCount);
            System.Array.Sort(sorted);
            p95 = sorted[Mathf.Min(sorted.Length-1, Mathf.FloorToInt(sorted.Length*.95f))];
            p99 = sorted[Mathf.Min(sorted.Length-1, Mathf.FloorToInt(sorted.Length*.99f))];
            SampleSceneMeshes();
            nextSampleTime = Time.realtimeSinceStartup + SAMPLE_INTERVAL_SEC;
        }
    }

    private void SampleSceneMeshes()
    {
        sampledVertices = 0;
        sampledTriangles = 0;
        sampledRenderers = 0;
        // 非アクティブ(プール退避中)は描画されないので除外する。
        var filters = FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var mf in filters)
        {
            Mesh m = mf.sharedMesh;
            if (m == null) continue;
            var r = mf.GetComponent<Renderer>();
            if (r != null && (!r.enabled || r.forceRenderingOff)) continue;
            sampledRenderers++;
            sampledVertices += m.vertexCount;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                sampledTriangles += (long)m.GetIndexCount(s) / 3;
            }
        }
    }

    private void OnGUI()
    {
        if (!visible) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                richText = false,
            };
            style.normal.textColor = Color.white;
        }

        sb.Clear();
        sb.AppendLine($"[DrawLOD stats] F8 で閉じる   frame {smoothedFrameMs:F1} ms ({(smoothedFrameMs > 0 ? 1000f / smoothedFrameMs : 0):F0} fps)");
        sb.AppendLine($"frame p95 {p95:F1} / p99 {p99:F1} ms | 共通生成 {RenderWorkScheduler.LastFrameMs:F2} / {PerformanceConfig.FrameBudgetMs:F2} ms | 最大単工程 {RenderWorkScheduler.MaximumStepMs:F2} ms");
        sb.AppendLine($"退避メッシュ推定 {RenderObjectCache.TotalBytes/(1024*1024)} / {RenderObjectCache.GlobalLimit/(1024*1024)} MiB | 待機 {RenderWorkScheduler.WaitingCount}");
        if (PropManager.Instance != null) sb.AppendLine($"プロップ {PropManager.Instance.ResidentCandidateCount:N0}候補 / {PropManager.Instance.SubmittedInstanceCount:N0}描画 / {PropManager.Instance.SubmittedShadowInstances:N0}影");
        AppendCategory("建物", DrawLodStats.Category.Building);
        AppendCategory("パス", DrawLodStats.Category.Path);
        sb.AppendLine($"描画メッシュ: renderers {sampledRenderers:N0}  verts {sampledVertices:N0}  tris {sampledTriangles:N0}  (2秒ごと更新)");
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 p = cam.transform.position;
            sb.AppendLine($"camera: ({p.x:F0}, {p.y:F0}, {p.z:F0})  pitch {cam.transform.eulerAngles.x:F0}°  fov {cam.fieldOfView:F0}°");
        }
        GUI.Box(new Rect(8, 8, 900, 205), sb.ToString(), style);
    }

    private void AppendCategory(string label, DrawLodStats.Category category)
    {
        DrawLodStats.GetLastWindow(category, out double ms, out int count);
        sb.AppendLine(
            $"{label}: close {DrawLodStats.GetObjectCount(category, DrawLod.Close):N0}  " +
            $"normal {DrawLodStats.GetObjectCount(category, DrawLod.Normal):N0}  " +
            $"far {DrawLodStats.GetObjectCount(category, DrawLod.Far):N0}  " +
            $"| 生成 {count} 件 / {ms:F1} ms (直近1秒)");
    }
}
