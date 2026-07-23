using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// RoadRenderer: 道路エディタの3Dプレビューメッシュ生成のみを担当する。
/// 2Dオーバーレイ描画は RoadEditorOverlayRenderer に移動済み。
/// </summary>
public class RoadRenderer : MonoBehaviour
{
    private MeshFilter previewMeshFilter;
    private MeshRenderer previewMeshRenderer;
    private Mesh previewMesh;
    private Material previewMaterial;
    private GameObject currentPreviewObject;

    // 直近に生成したプレビューの形状・パラメータ。
    // 同一形状で再度呼ばれた場合に破棄→再生成を行わないための重複判定に使う。
    // 特にグリッド吸着中はカーソルがセル内で動いても吸着先（点列）が不変なため、
    // ここで弾かないと毎フレーム破棄→再生成され、SplineMesh の遅延変形でプレビューが
    // 「生成された瞬間は未変形＝不可視」のまま消え続ける。
    private List<Vector3> lastPreviewPoints;
    private int lastPathType;
    private int lastPathSubtype;
    private bool lastCurved;
    private bool lastIsValid;
    private int lastPlatformSide;
    private bool hasPreviewState = false;

    /// <summary>
    /// バリデーションエラー時（立体交差など）に使用する赤色のプレビューマテリアル。
    /// null の場合は通常の previewMaterial を使用する。
    /// </summary>
    public Material errorPreviewMaterial;

    /// <summary>
    /// プレビュー用メッシュコンポーネントの作成
    /// </summary>
    public void CreatePreviewMeshComponents(Material material)
    {
        this.previewMaterial = material;

        GameObject previewObj = GameObject.Find("RoadPreview");
        if (previewObj == null)
        {
            previewObj = new GameObject("RoadPreview");
        }

        previewMeshFilter = previewObj.GetComponent<MeshFilter>();
        if (previewMeshFilter == null) previewMeshFilter = previewObj.AddComponent<MeshFilter>();

        previewMeshRenderer = previewObj.GetComponent<MeshRenderer>();
        if (previewMeshRenderer == null) previewMeshRenderer = previewObj.AddComponent<MeshRenderer>();

        // 既存のプレビューメッシュを確実にクリアする。
        // 同じ GameObject を再利用する際に前回のメッシュが残らないようにする。
        ResetPreview();
    }

    /// <summary>
    /// 3Dプレビューメッシュを更新する。細分化済みの点群を受け取る。
    /// isValid=false の場合は errorPreviewMaterial（未設定なら previewMaterial）を
    /// 使用して赤色のエラープレビューを表示する。
    /// platformSide は駅パスのホーム設置側（Constants.PLATFORM_SIDE_*）。
    /// 駅バリアントのプレビューで、プラットフォームを設置側どおりに張り出させるために使う
    /// （TrackPath.ResolvePlatformExtents が仮 PathSummary の platform_side を読む）。
    /// </summary>
    public void UpdateRoadPreview(List<Vector3> subdividePoints, int pathType, int pathSubtype, bool curved = true, bool isValid = true, int platformSide = Constants.PLATFORM_SIDE_OUTSIDE)
    {
        if (previewMeshFilter == null || previewMeshRenderer == null) return;

        if (subdividePoints == null || subdividePoints.Count < 2)
        {
            ResetPreview();
            return;
        }

        // 形状・パラメータが前回と同一なら再生成しない（破棄→再生成のスラッシングを防ぐ）。
        // これにより生成済みオブジェクトが維持され、SplineMesh が次フレームで変形を完了して
        // 安定的に表示される。グリッド吸着中はセル内移動で点列が変わらないため特に効く。
        if (hasPreviewState && currentPreviewObject != null &&
            lastPathType == pathType && lastPathSubtype == pathSubtype &&
            lastCurved == curved && lastIsValid == isValid &&
            lastPlatformSide == platformSide &&
            PointsApproximatelyEqual(lastPreviewPoints, subdividePoints))
        {
            return;
        }

        // 前回のプレビューオブジェクトを削除
        if (currentPreviewObject != null)
        {
            Destroy(currentPreviewObject);
            currentPreviewObject = null;
        }

        // 仮のPathSummaryを作成
        Vector3 startPos = subdividePoints[0];
        Vector3 endPos = subdividePoints[subdividePoints.Count - 1];
        float startGroundY = Utils.GetGroundHeight(startPos);
        float endGroundY = Utils.GetGroundHeight(endPos);
        float startRelY = startPos.y - startGroundY;
        float endRelY = endPos.y - endGroundY;

        List<object> waypointsList = new List<object>();
        if (!curved)
        {
            for (int i = 1; i < subdividePoints.Count - 1; i++)
            {
                Vector3 wpPos = subdividePoints[i];
                float wpGroundY = Utils.GetGroundHeight(wpPos);
                float wpRelY = wpPos.y - wpGroundY;
                waypointsList.Add(new List<object> { (object)wpPos.x, (object)wpRelY, (object)wpPos.z });
            }
        }

        PathSummary tempPathData = new PathSummary
        {
            path_type = pathType,
            path_variant = pathSubtype,
            waypoints = waypointsList,
            curved = curved,
            platform_side = platformSide,
            start_point = new List<object> { (object)startPos.x, (object)startRelY, (object)startPos.z },
            end_point = new List<object> { (object)endPos.x, (object)endRelY, (object)endPos.z }
        };

        PathMeshResult meshResult = PathMeshFactory.GeneratePathMesh(tempPathData, subdividePoints);

        // バリデーション結果に応じたマテリアルを選択
        Material activeMaterial = previewMaterial;
        if (!isValid)
        {
            activeMaterial = errorPreviewMaterial != null ? errorPreviewMaterial : previewMaterial;
        }

        if (meshResult != null)
        {
            if (meshResult.GeneratedObject != null)
            {
                currentPreviewObject = meshResult.GeneratedObject;

                if (previewMeshFilter != null)
                {
                    currentPreviewObject.transform.SetParent(previewMeshFilter.transform, false);
                }

                if (activeMaterial != null)
                {
                    Renderer[] renderers = currentPreviewObject.GetComponentsInChildren<Renderer>();
                    foreach (var rend in renderers)
                    {
                        rend.sharedMaterial = activeMaterial;
                    }
                }

                previewMeshFilter.mesh = null;
            }
            else if (meshResult.Mesh != null)
            {
                previewMesh = meshResult.Mesh;
                previewMeshFilter.mesh = previewMesh;

                if (activeMaterial != null)
                {
                    int subMeshCount = previewMesh.subMeshCount;
                    Material[] mats = new Material[subMeshCount];
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        mats[i] = activeMaterial;
                    }
                    previewMeshRenderer.sharedMaterials = mats;
                }
            }
        }

        // 今回生成した形状・パラメータを記録し、次回同一なら再生成を省く。
        if (lastPreviewPoints == null) lastPreviewPoints = new List<Vector3>(subdividePoints.Count);
        lastPreviewPoints.Clear();
        lastPreviewPoints.AddRange(subdividePoints);
        lastPathType = pathType;
        lastPathSubtype = pathSubtype;
        lastCurved = curved;
        lastIsValid = isValid;
        lastPlatformSide = platformSide;
        hasPreviewState = true;
    }

    /// <summary>
    /// 2つの点列がほぼ同一かどうかを判定する（プレビュー再生成の重複判定用）。
    /// </summary>
    private static bool PointsApproximatelyEqual(List<Vector3> a, List<Vector3> b)
    {
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if ((a[i] - b[i]).sqrMagnitude > 1e-6f) return false;
        }
        return true;
    }

    /// <summary>
    /// プレビューをクリア
    /// </summary>
    public void ResetPreview()
    {
        if (currentPreviewObject != null)
        {
            Destroy(currentPreviewObject);
            currentPreviewObject = null;
        }

        if (previewMesh != null)
        {
            previewMesh.Clear();
            previewMesh = null;
        }
        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }

        // 重複判定状態をリセットし、次回は確実に再生成されるようにする。
        hasPreviewState = false;
        lastPreviewPoints?.Clear();
    }
}
