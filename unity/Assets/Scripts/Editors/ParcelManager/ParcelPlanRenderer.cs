using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 土地区画プレビューや分割・道路沿い区画の描画、マーカー表示などを行うクラス。
/// 3D空間に OverlayMeshRenderer + Custom/OverlayLine シェーダーで直接メッシュを構築し、
/// ParcelManager の枠線描画と同じ方式でワールド座標のまま描画する。
/// </summary>
public class ParcelPlanRenderer : MonoBehaviour
{
    [Header("Line Settings")]
    /// <summary>プレビュー区画の枠線色。既存区画の白線と区別するため青色を使用。</summary>
    public Color previewLineColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);
    /// <summary>元区画（分割前・道路等）の枠線色。プレビュー区画と区別するため薄い灰色を使用。</summary>
    public Color originalLineColor = new Color(0.6f, 0.6f, 0.6f, 0.8f);
    public float lineThickness = 1.0f;

    [Header("Marker Settings")]
    public Material tempMarkerMaterial;
    public Color markerColor = Color.red;
    public float markerSize = 1.0f;

    /// <summary>海上でプレビュー枠線を置く基準ワールドY（メートル）。海面板(worldY=0.001)の上に出す。</summary>
    private const float SeaSurfaceY = 0.01f;

    private GameObject overlayMeshObj;
    private OverlayMeshRenderer overlayRenderer;

    // プレビュー用ポリゴンデータ（ワールド座標）
    private List<List<Vector3>> previewPolygons = new List<List<Vector3>>();
    private List<Vector3> originalPolygon = new List<Vector3>();
    private List<Vector3> pathPoints = new List<Vector3>();
    private bool hasPreview = false;

    // マーカーオブジェクト
    private GameObject markerObject;

    private void Start()
    {
        CreateMarkerObject();
        HideMarker();
    }

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;

        overlayRenderer = OverlayMeshRenderer.Create("ParcelPlanOverlayMesh", alwaysOnTop: false);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    private void Update()
    {
        if (!hasPreview || overlayRenderer == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        // 元区画や道路の枠線（灰色）
        DrawPolygon(originalPolygon, originalLineColor);
        DrawPath(pathPoints, originalLineColor);

        // プレビュー区画の枠線（青色）
        foreach (var poly in previewPolygons)
        {
            DrawPolygon(poly, previewLineColor);
        }

        overlayRenderer.EndFrame(cam);
    }

    /// <summary>
    /// 閉じたポリゴンの枠線を OverlayMeshRenderer に追加する。
    /// </summary>
    private void DrawPolygon(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;
        int count = points.Count;
        for (int i = 0; i < count; i++)
        {
            int nextI = (i + 1) % count;
            overlayRenderer.AddSolidLine(points[i], points[nextI], color, lineThickness);
        }
    }

    /// <summary>
    /// 開いたパスの線分を OverlayMeshRenderer に追加する。
    /// </summary>
    private void DrawPath(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;
        for (int i = 0; i < points.Count - 1; i++)
        {
            overlayRenderer.AddSolidLine(points[i], points[i + 1], color, lineThickness);
        }
    }

    /// <summary>
    /// ポリゴン頂点のY座標を地形の高さに合わせて少し浮かせる。
    /// 海上(地形標高が海面下)では海面板に隠れないよう海面を下限にする。
    /// 海上区画は完成後も海面基準の altitude に載るため、プレビューと結果の高さが揃う。
    /// </summary>
    private List<Vector3> AdjustPolygonHeight(List<Vector3> polygonPoints)
    {
        if (polygonPoints == null || polygonPoints.Count == 0) return new List<Vector3>();
        List<Vector3> adjusted = new List<Vector3>(polygonPoints.Count);
        foreach (var pt in polygonPoints)
        {
            float ground = Mathf.Max(Utils.GetGroundHeight(new Vector3(pt.x, 1000f, pt.z)), SeaSurfaceY);
            adjusted.Add(new Vector3(pt.x, ground + 0.1f, pt.z));
        }
        return adjusted;
    }

    /// <summary>
    /// ブロック全体ポリゴン、およびそこに内包される区画ポリゴンを描画する
    /// </summary>
    public void RenderBlockPreview(List<Vector3> blockPolygon, List<List<Vector3>> parcelPolygons)
    {
        ClearPreview();
        EnsureOverlayRenderer();

        if (blockPolygon != null && blockPolygon.Count > 0)
        {
            originalPolygon = AdjustPolygonHeight(blockPolygon);
        }

        if (parcelPolygons != null)
        {
            foreach (var poly in parcelPolygons)
            {
                if (poly != null && poly.Count > 0)
                {
                    previewPolygons.Add(AdjustPolygonHeight(poly));
                }
            }
        }

        hasPreview = (originalPolygon.Count >= 2) || (previewPolygons.Count > 0);
    }

    /// <summary>
    /// 土地区画分割プレビューを描画（分割されるブロックと分割後の区画）
    /// </summary>
    public void RenderSplitPreview(List<Vector3> originalBlockPolygon, List<List<Vector3>> splitParcelPolygons)
    {
        RenderBlockPreview(originalBlockPolygon, splitParcelPolygons);
    }

    /// <summary>
    /// 全プレビューをクリア
    /// </summary>
    public void ClearPreview()
    {
        previewPolygons.Clear();
        originalPolygon.Clear();
        pathPoints.Clear();
        hasPreview = false;
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
        HideMarker();
    }

    /// <summary>
    /// マーカーを非表示にする
    /// </summary>
    public void HideMarker()
    {
        if (markerObject != null)
        {
            markerObject.SetActive(false);
        }
    }

    private void CreateMarkerObject()
    {
        if (markerObject != null) Destroy(markerObject);

        markerObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        markerObject.name = "SelectionMarker";
        markerObject.transform.parent = transform;
        markerObject.transform.localScale = new Vector3(markerSize, markerSize, markerSize);

        Renderer renderer = markerObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            if (tempMarkerMaterial != null)
            {
                renderer.material = tempMarkerMaterial;
            }
            else
            {
                renderer.material = new Material(Shader.Find("Standard"));
                renderer.material.color = markerColor;
            }
        }

        Collider collider = markerObject.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        markerObject.SetActive(false);
    }

    private void OnDestroy()
    {
        ClearPreview();
        if (overlayMeshObj != null)
        {
            Destroy(overlayMeshObj);
        }
    }
}
