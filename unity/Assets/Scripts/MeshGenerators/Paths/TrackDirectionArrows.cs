using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;

/// <summary>
/// 一方通行の線路（travel_direction が DIR_FORWARD / DIR_BACKWARD）の進行方向を示す
/// 矢印オーバーレイ。線路コンテナの子オブジェクトとして TrackPath.Generate 時に生成される。
///
/// 【表示制御】
/// 矢印は道路敷設モード（STATE_EDIT_PATH）でのみ表示する。既存線路への接続時に
/// 向きの対（複線化）を作りやすくするための編集支援表示であり、通常プレイ中は出さない。
/// 表示切替は IpcStateCoordinator がステート変更時に static な SetVisible を呼び、
/// 登録済み全インスタンスの GameObject を一括で active/inactive にする方式
/// （生成時は Awake が現在の表示フラグに従うため、敷設モード中の再生成でも即座に表示される）。
///
/// 【描画】
/// スプラインに沿って一定間隔で、進行方向を向いた平板の矢印を1枚の結合メッシュとして生成する。
/// DIR_BACKWARD は幾何の終点→始点方向の通行なので、矢印はスプラインの接線を反転して向ける。
/// 高さは RoadPartHeights.DIRECTION_ARROW_ABOVE_SPLINE（レール踏面より上）。
/// マテリアルはマテリアル変更の対象外（VisualModeObject.ShouldPreventMaterialChange）なので、
/// ホバーハイライトや計画中の青塗りでも矢印の色は維持される。
/// </summary>
public class TrackDirectionArrows : MonoBehaviour
{
    /// <summary>矢印の配置間隔(m)。</summary>
    private const float ARROW_SPACING = 25f;

    /// <summary>矢印の色。計画中(青)・ハイライト(白系)のどちらの上でも判別できる黄色。</summary>
    private static readonly Color ARROW_COLOR = new Color(1f, 0.8f, 0.1f, 1f);

    // 矢印1枚のローカル形状(m)。+Z が進行方向、原点は軸部と矢じりの境目付近。
    private const float SHAFT_HALF_WIDTH = 0.35f;
    private const float SHAFT_TAIL = 1.5f;
    private const float HEAD_SHOULDER = 0.3f;
    private const float HEAD_TIP = 1.5f;
    private const float HEAD_HALF_WIDTH = 0.9f;

    private static readonly List<TrackDirectionArrows> instances = new List<TrackDirectionArrows>();
    private static bool visible = false;

    private static Material cachedArrowMaterial;

    /// <summary>
    /// 矢印の一括表示切替。IpcStateCoordinator がステート変更時に呼ぶ
    /// （STATE_EDIT_PATH のときだけ true）。以後に生成される矢印にもこのフラグが適用される。
    /// </summary>
    public static void SetVisible(bool value)
    {
        visible = value;
        for (int i = instances.Count - 1; i >= 0; i--)
        {
            if (instances[i] == null)
            {
                instances.RemoveAt(i);
                continue;
            }
            instances[i].gameObject.SetActive(value);
        }
    }

    private void Awake()
    {
        instances.Add(this);
        gameObject.SetActive(visible);
    }

    private void OnDestroy()
    {
        instances.Remove(this);
    }

    /// <summary>
    /// 一方通行線路の進行方向矢印オブジェクトを線路コンテナの子として生成する。
    /// travelDirection が DIR_BOTH の場合は何もしない（双方向線路に矢印は不要）。
    /// </summary>
    public static void Create(GameObject container, SplineComputer spline, int travelDirection)
    {
        if (travelDirection == Constants.DIR_BOTH) return;

        float length = spline.CalculateLength();
        if (length <= 0f || spline.sampleCount == 0) return;

        // 配置点の決定。両端から間隔の半分だけ内側に寄せる（架線柱と同じ端部クリアランス）。
        // 短い線路でも向きが読めるよう、最低1枚は中央に置く。
        var samples = new List<SplineSample>();
        if (length <= ARROW_SPACING)
        {
            samples.Add(spline.Evaluate(0.5));
        }
        else
        {
            double currentPercent = spline.Travel(0.0, ARROW_SPACING * 0.5f, Spline.Direction.Forward);
            double endPercent = spline.Travel(1.0, ARROW_SPACING * 0.5f, Spline.Direction.Backward);
            // Travel が進まなくなった場合の無限ループ防止（架線柱配置と同じ安全策）
            int maxCount = Mathf.FloorToInt(length / ARROW_SPACING) + 2;
            int count = 0;
            while (currentPercent <= endPercent && count < maxCount)
            {
                samples.Add(spline.Evaluate(currentPercent));
                double next = spline.Travel(currentPercent, ARROW_SPACING, Spline.Direction.Forward);
                if (next <= currentPercent) break;
                currentPercent = next;
                count++;
            }
            if (samples.Count == 0) samples.Add(spline.Evaluate(0.5));
        }

        Mesh mesh = BuildArrowsMesh(samples, reverse: travelDirection == Constants.DIR_BACKWARD);
        if (mesh == null) return;
        mesh.name = "TrackDirectionArrows";

        GameObject go = new GameObject("DirectionArrows");
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = GetArrowMaterial();
        // TrackDirectionArrows.Awake が GO を非アクティブにし得る。一度もアクティブにならない GO では OnDestroy が呼ばれないので先に付ける。
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        go.AddComponent<TrackDirectionArrows>();
    }

    /// <summary>
    /// 全配置点の矢印を1枚に結合した水平の平板メッシュを作る（矢印ごとのオブジェクト分割はしない）。
    /// 各矢印は接線の水平成分の向き（reverse なら反転）に沿い、常にワールド上向きの面。
    /// </summary>
    private static Mesh BuildArrowsMesh(List<SplineSample> samples, bool reverse)
    {
        var vertices = new List<Vector3>(samples.Count * 7);
        var normals = new List<Vector3>(samples.Count * 7);
        var triangles = new List<int>(samples.Count * 9);

        foreach (var sample in samples)
        {
            Vector3 forward = sample.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) continue;
            forward.Normalize();
            if (reverse) forward = -forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 basePos = sample.position + Vector3.up * RoadPartHeights.DIRECTION_ARROW_ABOVE_SPLINE;

            int b = vertices.Count;
            // 軸部の四角形（0-3）+ 矢じりの三角形（4-6）
            vertices.Add(basePos - right * SHAFT_HALF_WIDTH - forward * SHAFT_TAIL);
            vertices.Add(basePos + right * SHAFT_HALF_WIDTH - forward * SHAFT_TAIL);
            vertices.Add(basePos + right * SHAFT_HALF_WIDTH + forward * HEAD_SHOULDER);
            vertices.Add(basePos - right * SHAFT_HALF_WIDTH + forward * HEAD_SHOULDER);
            vertices.Add(basePos - right * HEAD_HALF_WIDTH + forward * HEAD_SHOULDER);
            vertices.Add(basePos + forward * HEAD_TIP);
            vertices.Add(basePos + right * HEAD_HALF_WIDTH + forward * HEAD_SHOULDER);
            for (int i = 0; i < 7; i++) normals.Add(Vector3.up);

            triangles.Add(b); triangles.Add(b + 3); triangles.Add(b + 1);
            triangles.Add(b + 1); triangles.Add(b + 3); triangles.Add(b + 2);
            triangles.Add(b + 4); triangles.Add(b + 5); triangles.Add(b + 6);
        }

        if (vertices.Count == 0) return null;

        Mesh mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 矢印用マテリアル（静的キャッシュ）。数値色分け用の白ベースマテリアル
    /// （PathColorMaterial。使用中のレンダーパイプラインに合ったシェーダを持つ）を複製し、
    /// 色をマテリアル自体に焼き込む。MaterialPropertyBlock を使わないのは、
    /// VisualModeObject.RestoreOriginalMaterials が全レンダラーのブロックをクリアするため。
    /// </summary>
    private static Material GetArrowMaterial()
    {
        if (cachedArrowMaterial != null) return cachedArrowMaterial;

        Material baseMat = Resources.Load<Material>("PathColorMaterial");
        cachedArrowMaterial = baseMat != null ? new Material(baseMat) : new Material(Shader.Find("Standard"));
        cachedArrowMaterial.name = "TrackDirectionArrowMaterial";
        cachedArrowMaterial.SetColor("_BaseColor", ARROW_COLOR);
        cachedArrowMaterial.SetColor("_Color", ARROW_COLOR);
        return cachedArrowMaterial;
    }
}
