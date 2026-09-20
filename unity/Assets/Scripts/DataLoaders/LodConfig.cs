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
}

/// <summary>取得する空間範囲だけを決める。地形の標高源・物体の描画LODを変更しない。</summary>
public static class LodConfig
{
    public const int MAX_RESIDENT_CELLS = 81;
    public const float MAX_FOCUS_RADIUS = 2500f;
    public const float PRELOAD_MARGIN = 100f;

    public static LodCellSets CalculateCellSets(
        Vector2Int centerCell, Vector3 viewCenterPosition, Vector3 cameraPosition,
        Vector3 cameraForward, TerrainManager terrainManager, float cellSize,
        int mapWidth, int mapHeight)
    {
        var resident = new List<Vector2Int>();
        if (cellSize <= 0 || mapWidth <= 0 || mapHeight <= 0)
            return new LodCellSets { farCells = resident, nearCells = new List<Vector2Int>(), dynamicCells = new List<Vector2Int>() };
        var camera = Camera.main;
        float halfView = camera != null && camera.orthographic ? camera.orthographicSize :
            Mathf.Abs(cameraPosition.y-viewCenterPosition.y) * DrawLodEvaluator.GetTanHalfFov(camera);
        float radius = Mathf.Clamp(halfView * (camera != null ? Mathf.Max(1f, camera.aspect) : 1f) + PRELOAD_MARGIN, 1100f, MAX_FOCUS_RADIUS);
        Plane[] planes = camera != null ? GeometryUtility.CalculateFrustumPlanes(camera) : null;
        int halfX = Mathf.RoundToInt(mapWidth/(2f*cellSize)), halfZ = Mathf.RoundToInt(mapHeight/(2f*cellSize));
        var candidates = new HashSet<Vector2Int>();
        void Include(Vector3 center, float range)
        {
            int cx = Mathf.FloorToInt(center.x/cellSize), cz = Mathf.FloorToInt(center.z/cellSize);
            int count = Mathf.CeilToInt(range/cellSize)+1;
            for (int z = Mathf.Max(-halfZ, cz-count); z < Mathf.Min(halfZ, cz+count+1); z++)
                for (int x = Mathf.Max(-halfX, cx-count); x < Mathf.Min(halfX, cx+count+1); x++)
                    candidates.Add(new Vector2Int(x,z));
        }
        Include(viewCenterPosition, radius);
        Include(cameraPosition, 500f);
        var scored = new List<(Vector2Int cell, float score)>();
        foreach (var cell in candidates)
        {
            float low = -100f, high = 100f;
            if (terrainManager != null) terrainManager.TryGetCellHeightRange(cell, out low, out high);
            var bounds = new Bounds(new Vector3((cell.x+.5f)*cellSize, (low+high)*.5f, (cell.y+.5f)*cellSize),
                new Vector3(cellSize, Mathf.Max(1f,high-low),cellSize));
            float nearDistance = bounds.SqrDistance(cameraPosition);
            Bounds expanded = bounds; expanded.Expand(PRELOAD_MARGIN*2f);
            if (planes != null && nearDistance > 500f*500f && !GeometryUtility.TestPlanesAABB(planes, expanded)) continue;
            var focus = new Vector3(viewCenterPosition.x, bounds.center.y, viewCenterPosition.z);
            float focusDistance = bounds.SqrDistance(focus);
            if (focusDistance > radius*radius && nearDistance > 500f*500f) continue;
            float score = Mathf.Min(nearDistance, focusDistance + cellSize*cellSize);
            // 再取得境界に余裕を持たせ、微小な移動で常駐集合が入れ替わらないようにする。
            if (terrainManager != null && terrainManager.HasSurfaceTile(cell)) score *= .8f;
            scored.Add((cell, score));
        }
        scored.Sort((a,b) => {
            int order = a.score.CompareTo(b.score);
            if (order != 0) return order;
            order = a.cell.x.CompareTo(b.cell.x);
            return order != 0 ? order : a.cell.y.CompareTo(b.cell.y);
        });
        for (int i = 0; i < Mathf.Min(MAX_RESIDENT_CELLS, scored.Count); i++) resident.Add(scored[i].cell);
        // 既存マネージャーの集合名は互換入口。3種類の地形データティアを表さない。
        return new LodCellSets { farCells = resident, nearCells = new List<Vector2Int>(resident), dynamicCells = new List<Vector2Int>(resident) };
    }
}
