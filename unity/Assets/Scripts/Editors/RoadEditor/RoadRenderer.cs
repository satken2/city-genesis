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
    private float[] lastLateralOffsets;
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
    /// lateralOffsets は複合パス(軌道複線)の各線の横オフセット(m。敷設方向右が正)。
    /// null/空なら受け取った点列を中心線とする単一パスとして描き、要素があれば
    /// 中心線を各オフセットで平行移動した位置に1本ずつ(=複線なら2本)プレビューを生成する。
    /// 確定時にUI側が平行な単線パス群へ展開して敷設するのと同じ見た目にするための機構で、
    /// 敷設済み線路同士の結合描画(TrackAdjacency)はプレビューには適用されない。
    /// </summary>
    public void UpdateRoadPreview(List<Vector3> subdividePoints, int pathType, int pathSubtype, bool curved = true, bool isValid = true, int platformSide = Constants.PLATFORM_SIDE_OUTSIDE, float[] lateralOffsets = null)
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
            OffsetsApproximatelyEqual(lastLateralOffsets, lateralOffsets) &&
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
        previewMeshFilter.mesh = null;

        // バリデーション結果に応じたマテリアルを選択
        Material activeMaterial = previewMaterial;
        if (!isValid)
        {
            activeMaterial = errorPreviewMaterial != null ? errorPreviewMaterial : previewMaterial;
        }

        if (lateralOffsets == null || lateralOffsets.Length == 0)
        {
            // 単一パス: 受け取った点列をそのまま中心線として1本生成する
            GenerateComponentPreview(subdividePoints, pathType, pathSubtype, curved, platformSide, activeMaterial, null);
        }
        else
        {
            // 複合パス(軌道複線): 中心線を各オフセットで平行移動した位置に1本ずつ生成し、
            // まとめてコンテナ配下にぶら下げる(破棄はコンテナごと行う)
            GameObject container = new GameObject("RoadPreviewComponents");
            container.transform.SetParent(previewMeshFilter.transform, false);
            currentPreviewObject = container;
            foreach (float lateral in lateralOffsets)
            {
                List<Vector3> offsetPoints = OffsetPolylineLateral(subdividePoints, lateral);
                GenerateComponentPreview(offsetPoints, pathType, pathSubtype, curved, platformSide, activeMaterial, container);
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
        lastLateralOffsets = lateralOffsets != null ? (float[])lateralOffsets.Clone() : null;
        hasPreviewState = true;
    }

    /// <summary>
    /// 1コンポーネント分のプレビューメッシュを生成して配置する。
    /// parent=null なら単一パスとして currentPreviewObject / previewMeshFilter に直接設定し、
    /// parent が指定されていれば複合パスの1本としてその配下にぶら下げる。
    /// PathMeshFactory へは実パスと同じ経路で仮の PathSummary を渡す。
    /// </summary>
    private void GenerateComponentPreview(List<Vector3> componentPoints, int pathType, int pathSubtype, bool curved, int platformSide, Material activeMaterial, GameObject parent)
    {
        Vector3 startPos = componentPoints[0];
        Vector3 endPos = componentPoints[componentPoints.Count - 1];
        float startGroundY = Utils.GetGroundHeight(startPos);
        float endGroundY = Utils.GetGroundHeight(endPos);
        float startRelY = startPos.y - startGroundY;
        float endRelY = endPos.y - endGroundY;

        List<object> waypointsList = new List<object>();
        if (!curved)
        {
            for (int i = 1; i < componentPoints.Count - 1; i++)
            {
                Vector3 wpPos = componentPoints[i];
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

        PathMeshResult meshResult = PathMeshFactory.GeneratePathMesh(tempPathData, componentPoints);
        if (meshResult == null) return;

        if (meshResult.GeneratedObject != null)
        {
            GameObject obj = meshResult.GeneratedObject;
            obj.transform.SetParent(parent != null ? parent.transform : previewMeshFilter.transform, false);
            if (parent == null)
            {
                currentPreviewObject = obj;
            }

            if (activeMaterial != null)
            {
                Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
                foreach (var rend in renderers)
                {
                    rend.sharedMaterial = activeMaterial;
                }
            }
        }
        else if (meshResult.Mesh != null)
        {
            if (parent == null)
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
            else
            {
                // 複合パスのフォールバック(白ストリップ)は共有の previewMeshFilter を使えないため、
                // コンポーネントごとに子オブジェクトを作ってメッシュを持たせる
                GameObject fallbackObj = new GameObject("ComponentFallbackMesh");
                fallbackObj.transform.SetParent(parent.transform, false);
                MeshFilter mf = fallbackObj.AddComponent<MeshFilter>();
                MeshRenderer mr = fallbackObj.AddComponent<MeshRenderer>();
                mf.mesh = meshResult.Mesh;
                if (activeMaterial != null)
                {
                    int subMeshCount = meshResult.Mesh.subMeshCount;
                    Material[] mats = new Material[subMeshCount];
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        mats[i] = activeMaterial;
                    }
                    mr.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// 点列を横方向(敷設方向右が正)へ lateral(m) だけ平行移動した新しい点列を返す。
    /// 各点のオフセット方向は前後セグメントの右向き法線のマイター平均で、角の外側でも
    /// 平行間隔が保たれるよう 1/cos(θ/2) でスケールする(急角度は2倍まで)。
    /// UI側の展開計算(city-genesis/tauri pathOffset.ts computeAnchorOffsetDirs)と同一ロジックで、
    /// プレビューと確定後の敷設位置を一致させる。Yは元の点の値を維持する。
    /// </summary>
    private static List<Vector3> OffsetPolylineLateral(List<Vector3> points, float lateral)
    {
        int n = points.Count;
        var result = new List<Vector3>(n);
        if (n < 2)
        {
            result.AddRange(points);
            return result;
        }

        // 各セグメントの右向き単位法線(XZ平面。ゼロ長セグメントはゼロベクトル)
        var segNormals = new Vector2[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = points[i + 1].x - points[i].x;
            float dz = points[i + 1].z - points[i].z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            segNormals[i] = len < 1e-6f ? Vector2.zero : new Vector2(dz / len, -dx / len);
        }

        for (int i = 0; i < n; i++)
        {
            Vector2 dir;
            if (i == 0)
            {
                dir = segNormals[0];
            }
            else if (i == n - 1)
            {
                dir = segNormals[n - 2];
            }
            else
            {
                Vector2 n1 = segNormals[i - 1];
                Vector2 n2 = segNormals[i];
                Vector2 m = n1 + n2;
                if (m.sqrMagnitude < 1e-12f)
                {
                    // 前後セグメントがちょうど折り返し(180°)の場合。片側の法線で代用する。
                    dir = n1;
                }
                else
                {
                    m.Normalize();
                    // マイター補正: 角の内外で平行間隔を保つためのスケール(急角度は2倍まで)
                    float cosHalf = Vector2.Dot(m, n1);
                    dir = m * (1f / Mathf.Max(0.5f, cosHalf));
                }
            }
            Vector3 p = points[i];
            result.Add(new Vector3(p.x + dir.x * lateral, p.y, p.z + dir.y * lateral));
        }
        return result;
    }

    /// <summary>
    /// 2つの横オフセット配列が同一かどうかを判定する（プレビュー再生成の重複判定用）。
    /// null と空配列はどちらも「単一パス」を意味するため同一とみなす。
    /// </summary>
    private static bool OffsetsApproximatelyEqual(float[] a, float[] b)
    {
        int lenA = a?.Length ?? 0;
        int lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
        }
        return true;
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
