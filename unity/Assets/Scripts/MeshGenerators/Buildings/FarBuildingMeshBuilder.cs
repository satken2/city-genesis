using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Distant (Far) buildings. Stretch one distant model along XYZ to its placement unit's bounding box (no stacking).
/// Placement units are wings (BuildingSummary.sub_buildings); buildings without wings use the entire building as one unit.
/// - Whole building: fit `far` to the footprint bounding rectangle aligned to the front edge (vertex 0→1, same as prebuilt) × floors×floor height.
/// - Wing: fit `mashup-{model_prefix}-far` to occupied cells (sub-building master width×height cells) × wing floors×floor height.
///   Wings without distant models stretch a unit cube to the same box.
/// Resolve models by scanning exterior model-number folders. Fit model XZ bounds to the outline;
/// fit height above the model origin (contact plane), so underground/underwater geometry below origin scales identically.
/// Distant buildings use one shared material, ignoring model materials.
/// </summary>
public static class FarBuildingMeshBuilder
{
    private static Mesh unitBox;

    /// <returns>Null for buildings without wings or distant models (caller uses an extruded footprint box).</returns>
    public static BuildingMeshResult Generate(BuildingSummary buildingData)
    {
        string prefix = BuildingMeshUtils.GetGlbPrefix1(buildingData);
        int variantNumber = BuildingMeshUtils.ResolveModelId(prefix, buildingData.visual_variant);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        if (buildingData.sub_buildings != null && buildingData.sub_buildings.Count > 0)
        {
            // Building GO is placed at center terrain elevation; rest wings on their own terrain-height difference (same as normal assembly).
            Vector2 center = buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2
                ? new Vector2(buildingData.centerPointArray[0], buildingData.centerPointArray[1])
                : Vector2.zero;
            float centerY = Utils.GetGroundHeight(new Vector3(center.x, 1000f, center.y));
            foreach (SubBuildingPlacement placement in buildingData.sub_buildings)
            {
                if (placement?.position == null || placement.position.Length < 2 || string.IsNullOrEmpty(placement.model_prefix))
                {
                    continue;
                }
                var master = MasterDataStore.Instance?.GetSubBuildingMaster(placement.subbuilding_id);
                float halfWidth = (master?.width ?? 1f) * Constants.MASHUP_CELL_SIZE * 0.5f;
                float halfDepth = (master?.height ?? 1f) * Constants.MASHUP_CELL_SIZE * 0.5f;
                float storyHeight = placement.height_per_floor is float h && h > 0f ? h : floorHeight;
                var offset = new Vector2(placement.position[0], placement.position[1]);
                float baseY = Utils.GetGroundHeight(new Vector3(center.x + offset.x, 1000f, center.y + offset.y)) - centerY;
                GameObject model = BuildingMeshUtils.GetPartBySuffixScan(prefix, $"mashup-{placement.model_prefix}-far", variantNumber);
                AddStretched(model, new Rect(-halfWidth, -halfDepth, halfWidth * 2f, halfDepth * 2f),
                    Mathf.Max(1, placement.floors) * storyHeight, Quaternion.Euler(0f, -placement.rotation + 180f, 0f),
                    new Vector3(offset.x, baseY, offset.y), vertices, triangles);
            }
        }
        else
        {
            GameObject model = BuildingMeshUtils.GetPartBySuffixScan(prefix, "far", variantNumber);
            if (model == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
            {
                return null;
            }
            Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
            Quaternion rotation = BuildingRoadAccess.PrebuiltRotation(footprint);
            Quaternion inverse = Quaternion.Inverse(rotation);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector2 point in footprint)
            {
                Vector3 local = inverse * new Vector3(point.x, 0f, point.y);
                min = Vector2.Min(min, new Vector2(local.x, local.z));
                max = Vector2.Max(max, new Vector2(local.x, local.z));
            }
            AddStretched(model, Rect.MinMaxRect(min.x, min.y, max.x, max.y),
                Mathf.Max(1, buildingData.floors ?? 1) * floorHeight, rotation, Vector3.zero, vertices, triangles);
        }

        if (triangles.Count == 0)
        {
            return null;
        }
        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return new BuildingMeshResult(mesh, null);
    }

    /// <summary>
    /// Stretch the model (or unit cube) in unrotated local space to rect in XZ and height vertically, then rotate/translate into place.
    /// </summary>
    private static void AddStretched(GameObject model, Rect rect, float height, Quaternion rotation, Vector3 offset,
        List<Vector3> vertices, List<int> triangles)
    {
        Mesh source = model != null ? BuildingMeshUtils.CombineModuleMeshes(model, out _) : null;
        Mesh mesh = source != null ? source : UnitBox;
        Bounds bounds = mesh.bounds;
        float scaleX = rect.width / Mathf.Max(bounds.size.x, 0.01f);
        float scaleZ = rect.height / Mathf.Max(bounds.size.z, 0.01f);
        float scaleY = height / Mathf.Max(bounds.max.y, 0.01f);
        int first = vertices.Count;
        foreach (Vector3 v in mesh.vertices)
        {
            var local = new Vector3(rect.xMin + (v.x - bounds.min.x) * scaleX, v.y * scaleY, rect.yMin + (v.z - bounds.min.z) * scaleZ);
            vertices.Add(rotation * local + offset);
        }
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            foreach (int index in mesh.GetTriangles(s))
            {
                triangles.Add(first + index);
            }
        }
        if (source != null)
        {
            Object.Destroy(source);
        }
    }

    /// <summary>Unit-width/depth/height box with origin at bottom center (no bottom face).</summary>
    private static Mesh UnitBox
    {
        get
        {
            if (unitBox != null)
            {
                return unitBox;
            }
            var faces = new[]
            {
                new[] { new Vector3(-.5f, 1f, -.5f), new Vector3(-.5f, 1f, .5f), new Vector3(.5f, 1f, .5f), new Vector3(.5f, 1f, -.5f) },
                new[] { new Vector3(-.5f, 0f, -.5f), new Vector3(-.5f, 1f, -.5f), new Vector3(.5f, 1f, -.5f), new Vector3(.5f, 0f, -.5f) },
                new[] { new Vector3(.5f, 0f, .5f), new Vector3(.5f, 1f, .5f), new Vector3(-.5f, 1f, .5f), new Vector3(-.5f, 0f, .5f) },
                new[] { new Vector3(-.5f, 0f, .5f), new Vector3(-.5f, 1f, .5f), new Vector3(-.5f, 1f, -.5f), new Vector3(-.5f, 0f, -.5f) },
                new[] { new Vector3(.5f, 0f, -.5f), new Vector3(.5f, 1f, -.5f), new Vector3(.5f, 1f, .5f), new Vector3(.5f, 0f, .5f) },
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (Vector3[] face in faces)
            {
                int first = vertices.Count;
                vertices.AddRange(face);
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            unitBox = new Mesh { name = "FarUnitBox" };
            unitBox.SetVertices(vertices);
            unitBox.SetTriangles(triangles, 0);
            unitBox.RecalculateBounds();
            return unitBox;
        }
    }
}
