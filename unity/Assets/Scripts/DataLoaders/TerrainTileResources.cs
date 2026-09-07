using System.Collections.Generic;
using UnityEngine;

/// <summary>タイル専用の実行時アセットを所有し、共有のマテリアル・テクスチャには触れない。</summary>
public sealed class TerrainTileResources : MonoBehaviour
{
    private readonly HashSet<Object> owned = new HashSet<Object>();
    public T Own<T>(T asset) where T : Object { owned.Add(asset); return asset; }
    public void Clear()
    {
        foreach (var asset in owned) if (asset != null) Destroy(asset);
        owned.Clear();
    }
    private void OnDestroy() => Clear();
}
