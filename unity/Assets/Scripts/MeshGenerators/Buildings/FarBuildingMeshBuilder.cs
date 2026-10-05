using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 遠景(Far)の建物。遠景モデル1つを、置く単位の外形の箱へXYZとも引き伸ばして置く(積み上げない)。
/// 置く単位は棟(BuildingSummary.sub_buildings)で、棟を持たない建物は建物全体を1つの単位とする。
/// - 建物全体: `far` を、正面の辺(頂点0→1。既成建物と同じ向き)に沿った底面の外接矩形 × 階数×階高へ。
/// - 棟: `mashup-{model_prefix}-far` を、占有セル(サブ建物マスターの width×height セル) × 棟の階数×階高へ。
///   遠景モデルの無い棟は単位立方体を同じ箱へ伸ばす。
/// モデルは閾値フォルダを外観バリアントで走査して解決する。XZはモデルの外接箱を外形へ合わせ、
/// 高さはモデルの原点(接地面)から上を合わせるので、原点より下の地中・水中の造形も同じ倍率で伸びる。
/// 遠景の建物は共有マテリアル1つで描くので、モデルの材質は使わない。
/// </summary>
public static class FarBuildingMeshBuilder
{
    private static Mesh unitBox;

    /// <returns>棟を持たず遠景モデルも無い建物は null(呼び出し側が底面を押し出した箱にする)。</returns>
    public static BuildingMeshResult Generate(BuildingSummary buildingData)
    {
        string prefix = BuildingMeshUtils.GetGlbPrefix1(buildingData);
        int variantNumber = BuildingMeshUtils.ComputeVariantNumber(buildingData.visual_variant);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        if (buildingData.sub_buildings != null && buildingData.sub_buildings.Count > 0)
        {
            // 建物GOは中心の地形標高に置かれるので、棟は自分の位置の地形標高との差に接地させる(通常の組み立てと同じ)。
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
    /// モデル(無ければ単位立方体)を、回転前のローカルで XZ が rect・高さ height の箱へ伸ばし、回転と平行移動を掛けて積む。
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

    /// <summary>底面中心を原点とする幅・奥行き・高さ 1 の箱(底面は張らない)。</summary>
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
