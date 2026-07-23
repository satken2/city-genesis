using UnityEngine;
using System.Collections.Generic;


/// <summary>
/// デバッグ用の視覚化機能を提供するクラス
/// - セルの境界線を表示
/// - カメラの視線中心点（Y=0平面との交点）を表示
/// </summary>
public class DebugCellVisualizer : MonoBehaviour
{
    [Header("Cell Grid Settings")]
    [SerializeField] private bool showCellGrid = true;
    [SerializeField] private Color cellGridColor = Color.white;
    [SerializeField] private float cellGridWidth = 0.1f;
    [SerializeField] private int gridRange = 5; // 中心から何セル分表示するか
    
    [Header("Camera Center Point Settings")]
    [SerializeField] private bool showCameraCenterPoint = true;
    [SerializeField] private GameObject cameraCenterMarker;
    [SerializeField] private Color cameraCenterColor = Color.red;
    [SerializeField] private float markerSize = 2f;
    
    [Header("Current Cell Highlight")]
    [SerializeField] private bool showCurrentCell = true;
    [SerializeField] private Color currentCellColor = Color.yellow;
    [SerializeField] private float currentCellWidth = 0.2f;
    
    // 動的生成されるオブジェクト管理
    private List<GameObject> gridLines = new List<GameObject>();
    private Vector2Int lastCenterCell = new Vector2Int(int.MinValue, int.MinValue);
    private Camera targetCamera;
    
    private void Awake()
    {
        // カメラを取得（Inspector未設定の場合はメインカメラ）
        targetCamera = Camera.main;
        if (targetCamera == null)
        {
            targetCamera = FindAnyObjectByType<Camera>();
        }
        
        // カメラ中心マーカーを生成
        if (cameraCenterMarker == null)
        {
            CreateCameraCenterMarker();
        }
    }
    
    private void Update()
    {
        if (targetCamera == null) return;
        
        // カメラ中心点の更新
        if (showCameraCenterPoint)
        {
            UpdateCameraCenterPoint();
        }
        
        // 現在のセルを計算
        Vector2Int currentCell = GetCurrentCameraCell();
        
        // セルが変わった場合のみグリッドを更新
        if (showCellGrid && currentCell != lastCenterCell)
        {
            UpdateCellGrid(currentCell);
            lastCenterCell = currentCell;
        }
        
        // 現在セルのハイライト
        if (showCurrentCell)
        {
            DrawCurrentCellHighlight(currentCell);
        }
    }
    
    private Vector2Int GetCurrentCameraCell()
    {
        Vector3 camPos = targetCamera.transform.position;
        float cellSize = TerrainManager.CellSize;
        
        // CameraCellCheckerと同じ計算方式
        return new Vector2Int(
            Mathf.FloorToInt(camPos.x / cellSize),
            Mathf.FloorToInt(camPos.z / cellSize)
        );
    }
    
    private void CreateCameraCenterMarker()
    {
        cameraCenterMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cameraCenterMarker.name = "CameraCenterMarker";
        cameraCenterMarker.transform.localScale = Vector3.one * markerSize;

        // 適切なシーンに移動（このスクリプトが属するシーン）
        UnityEngine.SceneManagement.Scene currentScene = this.gameObject.scene;
        if (currentScene.IsValid() && currentScene.isLoaded)
        {
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraCenterMarker, currentScene);
        }
        
        // マテリアル設定
        Renderer renderer = cameraCenterMarker.GetComponent<Renderer>();
        Material material = new Material(Shader.Find("Standard"));
        material.color = cameraCenterColor;
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Glossiness", 0.5f);
        renderer.material = material;
        
        // コライダーを削除（デバッグ用なのでクリック不要）
        Collider collider = cameraCenterMarker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }
        
        cameraCenterMarker.SetActive(showCameraCenterPoint);
    }
    
    private void UpdateCameraCenterPoint()
    {
        if (cameraCenterMarker == null) return;
        
        Vector3 cameraPos = targetCamera.transform.position;
        Vector3 cameraForward = targetCamera.transform.forward;
        
        // カメラからY=0平面への交点を計算
        if (Mathf.Abs(cameraForward.y) > 0.001f) // ほぼ水平でない場合
        {
            float t = -cameraPos.y / cameraForward.y;
            if (t > 0) // 前方向に交点がある場合
            {
                Vector3 intersectionPoint = cameraPos + cameraForward * t;
                cameraCenterMarker.transform.position = intersectionPoint;
                cameraCenterMarker.SetActive(true);
                
                // デバッグ情報を表示（CameraCellCheckerと同じ座標系を使用）
                float cellSize = TerrainManager.CellSize;
                Vector2Int cellIndex = new Vector2Int(
                    Mathf.FloorToInt(intersectionPoint.x / cellSize),
                    Mathf.FloorToInt(intersectionPoint.z / cellSize)
                );
                
                // 画面上部にデバッグ情報を表示
                Debug.DrawRay(cameraPos, cameraForward * t, Color.red, 0.1f);
            }
            else
            {
                cameraCenterMarker.SetActive(false);
            }
        }
        else
        {
            cameraCenterMarker.SetActive(false);
        }
    }
    
    private void UpdateCellGrid(Vector2Int centerCell)
    {
        // 既存のグリッド線を削除
        ClearGridLines();
        
        float cellSize = TerrainManager.CellSize;
        float gridY = 0.1f; // 地面から少し浮かせる
        
        // セルの境界線を描画（CameraCellCheckerの座標系に合わせる）
        // X方向の境界線（縦線）
        for (int x = -gridRange; x <= gridRange + 1; x++)
        {
            float worldX = (centerCell.x + x) * cellSize; // セルの境界位置
            CreateGridLine(
                new Vector3(worldX, gridY, (centerCell.y - gridRange) * cellSize),
                new Vector3(worldX, gridY, (centerCell.y + gridRange + 1) * cellSize)
            );
        }
        
        // Z方向の境界線（横線）
        for (int z = -gridRange; z <= gridRange + 1; z++)
        {
            float worldZ = (centerCell.y + z) * cellSize; // セルの境界位置
            CreateGridLine(
                new Vector3((centerCell.x - gridRange) * cellSize, gridY, worldZ),
                new Vector3((centerCell.x + gridRange + 1) * cellSize, gridY, worldZ)
            );
        }
    }
    
    private void CreateGridLine(Vector3 start, Vector3 end)
    {
        GameObject lineObj = new GameObject("GridLine");

        // 適切なシーンに移動（このスクリプトが属するシーン）
        UnityEngine.SceneManagement.Scene currentScene = this.gameObject.scene;
        if (currentScene.IsValid() && currentScene.isLoaded)
        {
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lineObj, currentScene);
        }

        LineRenderer lineRenderer = lineObj.AddComponent<LineRenderer>();
        
        // マテリアル設定
        Material lineMaterial = new Material(Shader.Find("Sprites/Default"));
        lineMaterial.color = cellGridColor;
        lineRenderer.material = lineMaterial;
        
        // 線の設定
        lineRenderer.startWidth = cellGridWidth;
        lineRenderer.endWidth = cellGridWidth;
        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, end);
        lineRenderer.useWorldSpace = true;
        
        gridLines.Add(lineObj);
    }
    
    private void DrawCurrentCellHighlight(Vector2Int cellIndex)
    {
        float cellSize = TerrainManager.CellSize;
        float highlightY = 0.15f; // グリッドより少し高く
        float pad = currentCellWidth * 0.5f;
        
        // CameraCellCheckerと同じ座標系：セル左下角が基準
        Vector3 cellMin = new Vector3(
            cellIndex.x * cellSize - pad,
            highlightY,
            cellIndex.y * cellSize - pad
        );
        
        Vector3 cellMax = new Vector3(
            (cellIndex.x + 1) * cellSize + pad,
            highlightY,
            (cellIndex.y + 1) * cellSize + pad
        );
        
        // 現在セルの枠を描画（currentCellWidth でハイライト範囲を拡げる）
        Debug.DrawLine(new Vector3(cellMin.x, highlightY, cellMin.z), new Vector3(cellMax.x, highlightY, cellMin.z), currentCellColor, 0.3f);
        Debug.DrawLine(new Vector3(cellMax.x, highlightY, cellMin.z), new Vector3(cellMax.x, highlightY, cellMax.z), currentCellColor, 0.3f);
        Debug.DrawLine(new Vector3(cellMax.x, highlightY, cellMax.z), new Vector3(cellMin.x, highlightY, cellMax.z), currentCellColor, 0.3f);
        Debug.DrawLine(new Vector3(cellMin.x, highlightY, cellMax.z), new Vector3(cellMin.x, highlightY, cellMin.z), currentCellColor, 0.3f);
    }
    
    private void ClearGridLines()
    {
        foreach (var line in gridLines)
        {
            if (line != null)
            {
                Destroy(line);
            }
        }
        gridLines.Clear();
    }
    
    private void OnGUI()
    {
        if (!showCameraCenterPoint && !showCurrentCell) return;
        
        Vector3 cameraPos = targetCamera.transform.position;
        
        GUILayout.BeginArea(new Rect(10, 10, 300, 120));
        GUILayout.BeginVertical("box");
        
        // カスタムスタイル作成
        GUIStyle titleStyle = new GUIStyle(GUI.skin.label);
        titleStyle.fontStyle = FontStyle.Bold;
        titleStyle.fontSize = 14;
        
        GUILayout.Label("Debug Cell Visualizer", titleStyle);
        GUILayout.Label($"カメラ位置: ({cameraPos.x:F1}, {cameraPos.y:F1}, {cameraPos.z:F1})");
        
        if (cameraCenterMarker != null && cameraCenterMarker.activeInHierarchy)
        {
            Vector3 centerPoint = cameraCenterMarker.transform.position;
            float cellSize = TerrainManager.CellSize;
            Vector2Int centerCell = new Vector2Int(
                Mathf.FloorToInt(centerPoint.x / cellSize),
                Mathf.FloorToInt(centerPoint.z / cellSize)
            );
            GUILayout.Label($"現在セル: ({centerCell.x}, {centerCell.y})");
            GUILayout.Label($"視線中心: ({centerPoint.x:F1}, {centerPoint.z:F1}) セル({centerCell.x}, {centerCell.y})");
        }
        
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
    
    private void OnValidate()
    {
        if (cameraCenterMarker != null)
        {
            cameraCenterMarker.SetActive(showCameraCenterPoint);
            cameraCenterMarker.transform.localScale = Vector3.one * markerSize;
            
            Renderer renderer = cameraCenterMarker.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = cameraCenterColor;
            }
        }
        
        // グリッドの再描画をトリガー
        lastCenterCell = new Vector2Int(int.MinValue, int.MinValue);
    }
    
    private void OnDestroy()
    {
        ClearGridLines();
        
        if (cameraCenterMarker != null)
        {
            Destroy(cameraCenterMarker);
        }
    }
    
    public void SetShowCellGrid(bool show)
    {
        showCellGrid = show;
        if (!show)
        {
            ClearGridLines();
        }
    }
    
}