// Assets/Scripts/MeshGenerators/Buildings/FlatRoofBuilder.cs
using UnityEngine;

/// <summary>
/// Flat-roof mesh generator.
/// Place a horizontal polygon matching the footprint at the building top.
/// </summary>
public static class FlatRoofBuilder
{
    /// <summary>
    /// Generate a flat roof panel.
    /// Even when no material is available, return the mesh without destroying it,
    /// allowing the final combining stage to assign the default material.
    /// </summary>
    public static BuildingMeshResult GenerateRoof(Vector2[] footprint, float height, string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        Mesh roofMesh = BuildingMeshBuilder.GenerateHorizontalPolygonMesh(footprint, height);
        if (roofMesh == null)
        {
            return null;
        }

        Material roofMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, resolvedSuffix, fallbackMaterials);
        return new BuildingMeshResult(roofMesh, roofMaterial != null ? new[] { roofMaterial } : null);
    }
}
