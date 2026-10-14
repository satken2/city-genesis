using System;
using System.Collections.Generic;
using UnityEngine;

public enum LodTier
{
    UltraFar = 0,
    Far = 1,
    Near = 2,
    Dynamic = 3
}

public struct LodCellSets
{
    public List<Vector2Int> farCells;
    public List<Vector2Int> nearCells;
    public List<Vector2Int> dynamicCells;
    /// <summary>常駐に入らなかった近傍セル(近い順)。向きを変えたり少し動いたりすると常駐へ入るので、地形を先読みする。</summary>
    public List<Vector2Int> prefetchCells;
}

/// <summary>取得する空間範囲だけを決める。地形の標高源・物体の描画LODを変更しない。</summary>
public static class LodConfig
{
    public const int MAX_RESIDENT_CELLS = 81;
    public const float MAX_FOCUS_RADIUS = 2500f;
    public const float PRELOAD_MARGIN = 100f;
    // 既に常駐しているセルを残す側の余裕。回転やズームの微小な変化で縁のセルが出入りを繰り返さないため。
    public const float RESIDENT_HYSTERESIS_MARGIN = 250f;
    /// <summary>先読みするセルの数。地形の LRU(TerrainManager)に常駐から外れたセルと合わせて収まる数にする。</summary>
    public const int PREFETCH_CELLS = 24;

    /// <summary>
    /// 常駐セル(最大 MAX_RESIDENT_CELLS)を近い順に選ぶ。
    /// previousResident は現在の常駐集合で、境界付近の入れ替わりを抑えるヒステリシスに使う。
    /// 視錐台の外や常駐の上限からあふれた近傍セル(注視半径より1セル外まで)を、同じ順位付けで先読みセルとして返す。
    /// LodQA が省略可能引数を既定値で補ってリフレクション呼び出しするので、オーバーロードを足さないこと。
    /// </summary>
    public static LodCellSets CalculateCellSets(
        Vector2Int centerCell, Vector3 viewCenterPosition, Vector3 cameraPosition,
        Vector3 cameraForward, TerrainManager terrainManager, float cellSize,
        int mapWidth, int mapHeight, ICollection<Vector2Int> previousResident = null)
    {
        var resident = new List<Vector2Int>();
        var prefetch = new List<Vector2Int>();
        if (cellSize <= 0 || mapWidth <= 0 || mapHeight <= 0)
            return new LodCellSets { farCells = resident, nearCells = new List<Vector2Int>(), dynamicCells = new List<Vector2Int>(), prefetchCells = prefetch };
        var camera = Camera.main;
        float halfView = camera != null && camera.orthographic ? camera.orthographicSize :
            Mathf.Abs(cameraPosition.y-viewCenterPosition.y) * DrawLodEvaluator.GetTanHalfFov(camera);
        float radius = Mathf.Clamp(halfView * (camera != null ? Mathf.Max(1f, camera.aspect) : 1f) + PRELOAD_MARGIN, 1100f, MAX_FOCUS_RADIUS);
        Plane[] planes = camera != null ? GeometryUtility.CalculateFrustumPlanes(camera) : null;
        int halfX = Mathf.RoundToInt(mapWidth/(2f*cellSize)), halfZ = Mathf.RoundToInt(mapHeight/(2f*cellSize));
        Rect extent = terrainManager != null ? terrainManager.BackgroundBounds : new Rect(-mapWidth*.5f, -mapHeight*.5f, mapWidth, mapHeight);
        int minX = Mathf.Min(-halfX, Mathf.FloorToInt(extent.xMin/cellSize));
        int minZ = Mathf.Min(-halfZ, Mathf.FloorToInt(extent.yMin/cellSize));
        int maxX = Mathf.Max(halfX, Mathf.CeilToInt(extent.xMax/cellSize));
        int maxZ = Mathf.Max(halfZ, Mathf.CeilToInt(extent.yMax/cellSize));
        var candidates = new HashSet<Vector2Int>();
        void Include(Vector3 center, float range)
        {
            int cx = Mathf.FloorToInt(center.x/cellSize), cz = Mathf.FloorToInt(center.z/cellSize);
            int count = Mathf.CeilToInt(range/cellSize)+1;
            for (int z = Mathf.Max(minZ, cz-count); z < Mathf.Min(maxZ, cz+count+1); z++)
                for (int x = Mathf.Max(minX, cx-count); x < Mathf.Min(maxX, cx+count+1); x++)
                    candidates.Add(new Vector2Int(x,z));
        }
        Include(viewCenterPosition, radius + cellSize);
        Include(cameraPosition, 500f);
        if (previousResident != null)
            foreach (var cell in previousResident)
                if (cell.x >= minX && cell.x < maxX && cell.y >= minZ && cell.y < maxZ) candidates.Add(cell);
        var scored = new List<(Vector2Int cell, float score)>();
        var spare = new List<(Vector2Int cell, float score)>();
        foreach (var cell in candidates)
        {
            bool wasResident = previousResident != null && previousResident.Contains(cell);
            float keep = wasResident ? RESIDENT_HYSTERESIS_MARGIN : 0f;
            float low = -100f, high = 100f;
            if (terrainManager != null) terrainManager.TryGetCellHeightRange(cell, out low, out high);
            var bounds = new Bounds(new Vector3((cell.x+.5f)*cellSize, (low+high)*.5f, (cell.y+.5f)*cellSize),
                new Vector3(cellSize, Mathf.Max(1f,high-low),cellSize));
            float nearDistance = bounds.SqrDistance(cameraPosition);
            float nearLimit = 500f + keep;
            bool alwaysNear = nearDistance <= nearLimit*nearLimit;
            Bounds expanded = bounds; expanded.Expand(PRELOAD_MARGIN*2f + keep*2f);
            var focus = new Vector3(viewCenterPosition.x, bounds.center.y, viewCenterPosition.z);
            float focusDistance = bounds.SqrDistance(focus);
            float score = Mathf.Min(nearDistance, focusDistance + cellSize*cellSize);
            bool inView = planes == null || alwaysNear || GeometryUtility.TestPlanesAABB(planes, expanded);
            bool inRange = alwaysNear || focusDistance <= (radius+keep)*(radius+keep);
            if (!inView || !inRange)
            {
                if (focusDistance <= (radius+cellSize)*(radius+cellSize)) spare.Add((cell, score));
                continue;
            }
            // 再取得境界に余裕を持たせ、微小な移動で常駐集合が入れ替わらないようにする。
            if (wasResident || (terrainManager != null && terrainManager.HasSurfaceTile(cell))) score *= .8f;
            scored.Add((cell, score));
        }
        Comparison<(Vector2Int cell, float score)> byScore = (a,b) => {
            int order = a.score.CompareTo(b.score);
            if (order != 0) return order;
            order = a.cell.x.CompareTo(b.cell.x);
            return order != 0 ? order : a.cell.y.CompareTo(b.cell.y);
        };
        scored.Sort(byScore);
        for (int i = 0; i < scored.Count; i++)
        {
            if (i < MAX_RESIDENT_CELLS) resident.Add(scored[i].cell);
            else spare.Add(scored[i]);
        }
        spare.Sort(byScore);
        for (int i = 0; i < Mathf.Min(PREFETCH_CELLS, spare.Count); i++) prefetch.Add(spare[i].cell);
        // 既存マネージャーの集合名は互換入口。3種類の地形データティアを表さない。
        return new LodCellSets { farCells = resident, nearCells = new List<Vector2Int>(resident), dynamicCells = new List<Vector2Int>(resident), prefetchCells = prefetch };
    }
}
