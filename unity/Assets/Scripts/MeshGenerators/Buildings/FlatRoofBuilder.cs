// Assets/Scripts/MeshGenerators/Buildings/FlatRoofBuilder.cs
using UnityEngine;

/// <summary>
/// 平らな屋根（フラットルーフ）のメッシュを生成するクラス。
/// 底面ポリゴンと同じ形状の水平ポリゴンを建物最上部に配置する。
/// </summary>
public static class FlatRoofBuilder
{
    /// <summary>
    /// 平らな屋上パネルを生成する。
    /// マテリアルが取得できない場合でも、最終的なメッシュ結合段階でデフォルトマテリアルが割り当てられるよう、
    /// メッシュ自体は破棄せずに結果を返す。
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
