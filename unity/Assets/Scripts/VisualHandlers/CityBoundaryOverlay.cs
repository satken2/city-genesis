using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 都市の範囲(詳細地形の矩形)の境界に、地表と海面に沿った半透明の太い破線を近距離で描く。
/// さらに近づくと、境界から立ち上がる水色の格子壁を表示する。
/// 都市外の背景地形は地続きに見えても建設できない(テンプレートモードを除く)ため、プレイヤーに範囲を示す。
///
/// 線の太さは画面上のピクセルで一定にする。破線の周期はチャンクごとにカメラ距離から2の累乗メートルへ丸め、
/// 境界に沿った累積距離が周期の倍数になる位置から描く。周期が2倍刻みでしか変わらないので、距離の違うチャンクの
/// 継ぎ目やズーム中も破線はワールドに固定されたまま入れ子になり、カメラの移動で画面上を流れない。
/// </summary>
public sealed class CityBoundaryOverlay : MonoBehaviour
{
    private const float SAMPLE_SPACING = 10f;      // 境界の高さを読む間隔(m)。詳細地形の格子間隔と同じ
    private const int CHUNK_SAMPLES = 32;          // 視錐台カリングと破線周期を決める単位のサンプル数
    private const float LIFT = 0.3f;               // 地表・海面から浮かせる高さ(m)
    private const float LINE_PIXELS = 7f;          // 線の太さ(px)
    private const float DASH_PERIOD_PIXELS = 40f;  // 破線1周期(線+隙間)の画面上の目安(px)
    private const float DASH_RATIO = 0.6f;         // 1周期のうち線を描く割合
    private const float MIN_DASH_PERIOD = 2f;      // 接写時の破線周期の下限(m)
    private const float MIN_SEGMENT_PIXELS = 6f;   // 画面上でこれより細かい起伏には追従させない(px)
    private const float CULL_MARGIN = 20f;         // チャンクのAABBに足す余白(m)
    private const float FULL_VISIBILITY_DISTANCE = 600f;
    private const float HIDDEN_DISTANCE = 1000f;
    private const float WALL_HEIGHT = 1000f;
    private const float WALL_GRID_SPACING = 20f;
    private const float WALL_LINE_PIXELS = 1.5f;
    private const float WALL_FULL_VISIBILITY_DISTANCE = 100f;
    private const float WALL_HIDDEN_DISTANCE = 300f;
    private const float CAMERA_POS_THRESHOLD = 0.5f;
    private const float CAMERA_ROT_THRESHOLD = 0.5f;
    private static readonly Color LINE_COLOR = new Color(1f, 1f, 1f, 0.55f);
    private static readonly Color WALL_COLOR = new Color(0.1f, 0.85f, 1f, 0.7f);

    private struct Chunk
    {
        public int start;
        public int end;
        public Bounds bounds;
        public Bounds wallBounds;
    }

    private TerrainManager terrain;
    private OverlayMeshRenderer overlay;
    private OverlayMeshRenderer wallOverlay;
    private readonly List<Vector3> wallTriangles = new List<Vector3>();
    // 境界を1周する折れ線。末尾は先頭と同じ点で閉じる。arc は XZ 平面での累積距離。
    private readonly List<Vector3> points = new List<Vector3>();
    private readonly List<float> arc = new List<float>();
    private readonly List<Vector2Int> sampleCells = new List<Vector2Int>();
    private readonly List<Chunk> chunks = new List<Chunk>();
    // 矩形の4隅のサンプル番号(閉じ点を含む5つ)。間引いて描くときも隅は必ず通す。
    private readonly int[] cornerIndices = new int[5];
    private readonly HashSet<Vector2Int> tiledEdgeCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> changedCells = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> droppedCells = new List<Vector2Int>();
    private readonly Plane[] frustumPlanes = new Plane[6];
    private float halfWidth, halfHeight;
    // 境界を作った直後の高さは全景が未受信だと仮の値なので、最初のロード完了で全点を読み直す。
    private bool fullRefreshPending;
    private bool meshDirty;
    private bool meshCleared = true;
    private Vector3 lastCamPos;
    private Quaternion lastCamRot;

    private void Start()
    {
        terrain = GetComponent<TerrainManager>();
        overlay = OverlayMeshRenderer.Create("CityBoundaryOverlay", alwaysOnTop: false, parent: transform);
        // 線の各位置からカメラまでの距離でフェードさせ、チャンクの継ぎ目や微小移動でも濃さを滑らかに保つ。
        overlay.Material.EnableKeyword("OVERLAY_DISTANCE_FADE");
        overlay.Material.SetVector("_DistanceFadeRange", new Vector4(FULL_VISIBILITY_DISTANCE, HIDDEN_DISTANCE, 0f, 0f));
        wallOverlay = OverlayMeshRenderer.Create("CityBoundaryWall", alwaysOnTop: false, parent: transform);
        wallOverlay.Material.EnableKeyword("OVERLAY_DISTANCE_FADE");
        wallOverlay.Material.EnableKeyword("OVERLAY_WORLD_GRID");
        wallOverlay.Material.SetVector("_DistanceFadeRange", new Vector4(WALL_FULL_VISIBILITY_DISTANCE, WALL_HIDDEN_DISTANCE, 0f, 0f));
        wallOverlay.Material.SetVector("_WorldGrid", new Vector4(WALL_GRID_SPACING, WALL_LINE_PIXELS, 0f, 0f));
        if (terrain != null) terrain.OnAllTerrainLodLoaded += OnTerrainLoaded;
    }

    private void OnDestroy()
    {
        if (terrain != null) terrain.OnAllTerrainLodLoaded -= OnTerrainLoaded;
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        bool visible = overlay != null && wallOverlay != null && terrain != null && terrain.HasTerrainSettings && cam != null
            && GameStateManager.Instance != null && GameStateManager.Instance.IsWorldVisible;
        if (!visible)
        {
            // セッション切り替え中は地形設定が消えるので、次の都市で境界を作り直す。
            if (terrain == null || !terrain.HasTerrainSettings) points.Clear();
            if (!meshCleared)
            {
                if (overlay != null) overlay.ClearMesh();
                if (wallOverlay != null) wallOverlay.ClearMesh();
            }
            meshCleared = true;
            return;
        }

        float w = terrain.GetMapWidth() * 0.5f, h = terrain.GetMapHeight() * 0.5f;
        if (points.Count == 0 || w != halfWidth || h != halfHeight) BuildBoundary(w, h);

        bool moved = Vector3.Distance(cam.transform.position, lastCamPos) > CAMERA_POS_THRESHOLD
            || Quaternion.Angle(cam.transform.rotation, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!moved && !meshDirty && !meshCleared) return;
        lastCamPos = cam.transform.position;
        lastCamRot = cam.transform.rotation;
        meshDirty = false;
        meshCleared = false;
        RebuildMesh(cam);
    }

    private void BuildBoundary(float w, float h)
    {
        halfWidth = w;
        halfHeight = h;
        points.Clear();
        arc.Clear();
        sampleCells.Clear();
        var corners = new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) };
        float length = 0f;
        for (int e = 0; e < 4; e++)
        {
            Vector2 a = corners[e], b = corners[(e + 1) % 4];
            float edge = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(edge / SAMPLE_SPACING));
            cornerIndices[e] = points.Count;
            for (int j = 0; j < n; j++) AddSample(Vector2.Lerp(a, b, (float)j / n), length + edge * j / n);
            length += edge;
        }
        cornerIndices[4] = points.Count;
        AddSample(corners[0], length);

        chunks.Clear();
        for (int start = 0; start < points.Count - 1; start += CHUNK_SAMPLES)
            chunks.Add(new Chunk { start = start, end = Mathf.Min(start + CHUNK_SAMPLES, points.Count - 1) });
        RefreshChunkBounds();

        tiledEdgeCells.Clear();
        foreach (Vector2Int cell in sampleCells)
            if (terrain.HasSurfaceTile(cell)) tiledEdgeCells.Add(cell);
        fullRefreshPending = true;
        meshDirty = true;
    }

    private void AddSample(Vector2 xz, float distance)
    {
        points.Add(new Vector3(xz.x, LineHeight(xz), xz.y));
        arc.Add(distance);
        // 境界上の点は外側のセルにも接するので、都市内へわずかに寄せた位置のセルを持ち主とする。
        sampleCells.Add(TerrainManager.WorldToCell(
            Mathf.Clamp(xz.x, -halfWidth + 0.01f, halfWidth - 0.01f),
            Mathf.Clamp(xz.y, -halfHeight + 0.01f, halfHeight - 0.01f)));
    }

    /// <summary>線を置く高さ。海の上では海面(ワールドY=0)に置く。</summary>
    private static float LineHeight(Vector2 xz) => Mathf.Max(Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y)), 0f) + LIFT;

    /// <summary>
    /// 地形タイルの到着・破棄で、境界に接するセルの高さが変わったサンプルだけを読み直す。
    /// タイルが外れたセルは全景の高さへ戻るので、到着したセルと同様に読み直す。
    /// </summary>
    private void OnTerrainLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        if (points.Count == 0) return;
        changedCells.Clear();
        if (updatedCells != null)
            foreach (Vector2Int cell in updatedCells) changedCells.Add(cell);
        droppedCells.Clear();
        foreach (Vector2Int cell in tiledEdgeCells)
            if (!terrain.HasSurfaceTile(cell)) droppedCells.Add(cell);
        foreach (Vector2Int cell in droppedCells)
        {
            tiledEdgeCells.Remove(cell);
            changedCells.Add(cell);
        }
        if (changedCells.Count == 0 && !fullRefreshPending) return;

        bool refreshed = false;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2Int cell = sampleCells[i];
            if (!fullRefreshPending && !TouchesChangedCell(cell)) continue;
            Vector3 p = points[i];
            p.y = LineHeight(new Vector2(p.x, p.z));
            points[i] = p;
            if (terrain.HasSurfaceTile(cell)) tiledEdgeCells.Add(cell);
            refreshed = true;
        }
        fullRefreshPending = false;
        if (!refreshed) return;
        RefreshChunkBounds();
        meshDirty = true;
    }

    /// <summary>セル境界上のサンプルは隣のセルのタイルからも高さを引くので、周囲1セルの変化も拾う。</summary>
    private bool TouchesChangedCell(Vector2Int cell)
    {
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (changedCells.Contains(new Vector2Int(cell.x + dx, cell.y + dz))) return true;
        return false;
    }

    private void RefreshChunkBounds()
    {
        for (int c = 0; c < chunks.Count; c++)
        {
            Chunk chunk = chunks[c];
            Vector3 min = points[chunk.start], max = min;
            for (int i = chunk.start; i <= chunk.end; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            chunk.bounds = new Bounds();
            chunk.bounds.SetMinMax(min, max);
            chunk.bounds.Expand(CULL_MARGIN * 2f);
            // 壁の上部だけが見える視点でも、地表の破線とは独立にカリングする。
            chunk.wallBounds = new Bounds();
            chunk.wallBounds.SetMinMax(min, max + Vector3.up * WALL_HEIGHT);
            chunk.wallBounds.Expand(CULL_MARGIN * 2f);
            chunks[c] = chunk;
        }
    }

    private void RebuildMesh(Camera cam)
    {
        overlay.BeginFrame();
        GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);
        Vector3 camPos = cam.transform.position;
        Plane nearPlane = new Plane(cam.transform.forward, camPos + cam.transform.forward * (cam.nearClipPlane + 0.001f));
        float worldPerPixelAtUnit = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / (Screen.height * 0.5f);

        foreach (Chunk chunk in chunks)
        {
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, chunk.bounds)) continue;
            float distance = Mathf.Max(Vector3.Distance(camPos, chunk.bounds.ClosestPoint(camPos)), cam.nearClipPlane);
            if (distance >= HIDDEN_DISTANCE) continue;
            float worldPerPixel = cam.orthographic ? cam.orthographicSize * 2f / Screen.height : distance * worldPerPixelAtUnit;
            float period = Mathf.Pow(2f, Mathf.Round(Mathf.Log(Mathf.Max(DASH_PERIOD_PIXELS * worldPerPixel, MIN_DASH_PERIOD), 2f)));
            int stride = Mathf.Max(1, Mathf.FloorToInt(MIN_SEGMENT_PIXELS * worldPerPixel / SAMPLE_SPACING));
            float s0 = arc[chunk.start], s1 = arc[chunk.end];
            for (float k = Mathf.Floor(s0 / period) * period; k < s1; k += period)
            {
                float a = Mathf.Max(k, s0), b = Mathf.Min(k + period * DASH_RATIO, s1);
                if (b > a) AddDash(a, b, stride, nearPlane);
            }
        }
        overlay.EndFrame(cam);

        wallOverlay.BeginFrame();
        wallTriangles.Clear();
        foreach (Chunk chunk in chunks)
        {
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, chunk.wallBounds)) continue;
            if ((camPos - chunk.wallBounds.ClosestPoint(camPos)).sqrMagnitude >= WALL_HIDDEN_DISTANCE * WALL_HIDDEN_DISTANCE) continue;
            AddWallChunk(chunk);
        }
        wallOverlay.AddPreTriangulated(wallTriangles, WALL_COLOR);
        wallOverlay.EndFrame(cam);
    }

    private void AddWallChunk(Chunk chunk)
    {
        for (int i = chunk.start; i < chunk.end; i++)
        {
            Vector3 a = points[i], b = points[i + 1];
            Vector3 topA = a + Vector3.up * WALL_HEIGHT, topB = b + Vector3.up * WALL_HEIGHT;
            wallTriangles.Add(a); wallTriangles.Add(topA); wallTriangles.Add(topB);
            wallTriangles.Add(a); wallTriangles.Add(topB); wallTriangles.Add(b);
        }
    }

    /// <summary>累積距離 a〜b の区間を、stride サンプルおき(隅は必ず通す)に折れ線で描く。</summary>
    private void AddDash(float a, float b, int stride, Plane nearPlane)
    {
        int i = IndexAt(a);
        Vector3 previous = PointAt(i, a);
        int next = NextBreak(i, stride);
        while (next < points.Count && arc[next] < b)
        {
            AddSegment(previous, points[next], nearPlane);
            previous = points[next];
            next = NextBreak(next, stride);
        }
        AddSegment(previous, PointAt(IndexAt(b), b), nearPlane);
    }

    private int NextBreak(int index, int stride)
    {
        int next = (index / stride + 1) * stride;
        foreach (int corner in cornerIndices)
            if (corner > index && corner < next) return corner;
        return next;
    }

    /// <summary>arc[i] ≤ s を満たす最大の i(最後の区間の始点を上限とする)。</summary>
    private int IndexAt(float s)
    {
        int index = arc.BinarySearch(s);
        if (index < 0) index = ~index - 1;
        return Mathf.Clamp(index, 0, points.Count - 2);
    }

    private Vector3 PointAt(int index, float s)
    {
        float span = arc[index + 1] - arc[index];
        return Vector3.Lerp(points[index], points[index + 1], span > 0f ? (s - arc[index]) / span : 0f);
    }

    /// <summary>カメラの背後へ回り込む線分はニアクリップ面で切り詰める(ビルボードの向きが反転して画面を横切るため)。</summary>
    private void AddSegment(Vector3 a, Vector3 b, Plane nearPlane)
    {
        float da = nearPlane.GetDistanceToPoint(a), db = nearPlane.GetDistanceToPoint(b);
        if (da < 0f && db < 0f) return;
        if (da < 0f || db < 0f)
        {
            Vector3 p = a + (b - a) * (da / (da - db));
            if (da < 0f) a = p; else b = p;
        }
        overlay.AddSolidLine(a, b, LINE_COLOR, LINE_PIXELS);
    }
}
