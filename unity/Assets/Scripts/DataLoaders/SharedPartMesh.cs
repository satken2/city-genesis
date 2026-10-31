using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Combined mesh and DepthOffset materials of a part node that buildings attach as a separate moving child (crane top, rotor).
/// The geometry depends only on the shared prefab, so all buildings share it; building destruction or parked-cache eviction
/// must not release it (no RenderCacheIdentity). Retain across sessions.
/// </summary>
public static class SharedPartMesh
{
    private static readonly Dictionary<GameObject, (Mesh mesh, Material[] materials)> cache =
        new Dictionary<GameObject, (Mesh mesh, Material[] materials)>();

    /// <summary>Mesh of the node and its children in the node's local space. False if the node has no mesh.</summary>
    public static bool TryGet(GameObject node, out Mesh mesh, out Material[] materials)
    {
        mesh = null;
        materials = null;
        if (node == null) return false;
        if (!cache.TryGetValue(node, out var shared) || shared.mesh == null)
        {
            Mesh combined = BuildingMeshUtils.CombineModuleMeshes(node, out Material[] combinedMaterials);
            if (combined == null) return false;
            var result = BuildingMeshBuilder.EnsureMaterials(
                BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(combined, combinedMaterials)));
            shared = (result.Mesh, BuildingManager.GetSharedDepthOffsetMaterials(result.Materials));
            cache[node] = shared;
        }
        mesh = shared.mesh;
        materials = shared.materials;
        return true;
    }
}
