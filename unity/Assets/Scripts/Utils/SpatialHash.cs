using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 簡易空間ハッシュ実装。
/// 任意のオブジェクト T をセルに登録し、半径 r 以内の候補のみ抽出できます。
/// </summary>
public class SpatialHash<T>
{
    private readonly float cellSize;
    private readonly Dictionary<Vector2Int, List<T>> buckets = new();

    public SpatialHash(float cellSize)
    {
        this.cellSize = Mathf.Max(cellSize, 0.0001f);
    }

    private Vector2Int ToCell(Vector3 pos)
    {
        return new Vector2Int(Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.z / cellSize));
    }

    public void Add(T item, Vector3 pos)
    {
        var cell = ToCell(pos);
        if (!buckets.TryGetValue(cell, out var list))
        {
            list = new List<T>();
            buckets[cell] = list;
        }
        list.Add(item);
    }

    public void Remove(T item, Vector3 pos)
    {
        var cell = ToCell(pos);
        if (buckets.TryGetValue(cell, out var list))
        {
            list.Remove(item);
            if (list.Count == 0)
            {
                buckets.Remove(cell);
            }
        }
    }

    /// <summary>
    /// 登録済みの全要素を破棄する。セッション切り替え時に登録元のキャッシュと
    /// 足並みを揃えて空にするために使う。cellSize は維持される。
    /// </summary>
    public void Clear()
    {
        buckets.Clear();
    }

    public List<T> Query(Vector3 pos, float radius)
    {
        List<T> result = new List<T>();
        int r = Mathf.CeilToInt(radius / cellSize);
        var center = ToCell(pos);

        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                var cell = new Vector2Int(center.x + dx, center.y + dz);
                if (buckets.TryGetValue(cell, out var list))
                {
                    result.AddRange(list);
                }
            }
        }
        return result;
    }

}