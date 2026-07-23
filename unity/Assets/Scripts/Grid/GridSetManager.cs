using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 3Dビュー上での2点クリックによるグリッド角度設定を管理するシングルトンマネージャー。
/// 1点目選択前にスナップ位置に黄色マーカーを表示し、
/// 1点目選択後は1点目（黄色球体）と2点目ホバー（青色球体）を表示しながら、
/// マウスの動きと完全に同期（Z基準時計回り）してリアルタイムにグリッドを回転プレビューします。
/// </summary>
public class GridSetManager : MonoBehaviour
{
    public static GridSetManager Instance { get; private set; }

    private Vector3? firstPoint = null;
    private GameObject firstMarker = null;
    private GameObject hoverMarker = null;
    private GameObject pinkLine = null;
    private LineRenderer lineRenderer = null;
    
    private float originalAngle = 0f;
    private bool isSetting = false;

    // 角度設定モード中の吸着フラグ退避。基準線の2点はノードにだけ吸着させたいため、
    // モード開始時に吸着状態を退避して「ノード吸着のみON」へ切り替え、終了時に復元する。
    // (吸着の真実源はUnity側の TerrainInputHandler static フラグ)
    private bool hasSnapshot = false;
    private bool savedGridSnap;
    private bool savedNodeSnap;
    private bool savedPathSnap;
    private bool savedRightAngleSnap;
    private bool savedStraightLineSnap;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// グリッド角度設定モードを開始する
    /// </summary>
    public void StartGridSetting()
    {
        isSetting = true;
        firstPoint = null;

        if (GridOverlay.Instance != null)
        {
            originalAngle = GridOverlay.Instance.GridAngle;
            // グリッドを強制表示
            GridOverlay.Instance.SetGridVisible(true);
            GridOverlay.Instance.SetGridOrigin(Vector3.zero);
        }

        // 吸着状態を退避し、基準線の2点選択用に「ノード吸着のみON」へ切り替える
        if (!hasSnapshot)
        {
            savedGridSnap = TerrainInputHandler.EnableGridSnapping;
            savedNodeSnap = TerrainInputHandler.EnableNodeSnapping;
            savedPathSnap = TerrainInputHandler.EnablePathSnapping;
            savedRightAngleSnap = TerrainInputHandler.EnableRightAngleSnapping;
            savedStraightLineSnap = TerrainInputHandler.EnableStraightLineSnapping;
            hasSnapshot = true;
        }
        TerrainInputHandler.EnableGridSnapping = false;
        TerrainInputHandler.EnableNodeSnapping = true;
        TerrainInputHandler.EnablePathSnapping = false;
        TerrainInputHandler.EnableRightAngleSnapping = false;
        TerrainInputHandler.EnableStraightLineSnapping = false;

        ClearMarkers();
        Debug.Log("GridSetManager: グリッド設定モードを開始しました");
    }

    /// <summary>
    /// グリッド角度設定モードをキャンセルする（元の角度に復元）
    /// </summary>
    public void CancelGridSetting()
    {
        if (!isSetting) return;

        if (GridOverlay.Instance != null)
        {
            GridOverlay.Instance.SetGridAngle(originalAngle);
            GridOverlay.Instance.SetGridOrigin(Vector3.zero);
        }
        
        ResetState();
        Debug.Log("GridSetManager: グリッド設定モードをキャンセルしました");
    }

    /// <summary>
    /// 状態をリセットし、マーカーを破棄する。
    /// 角度確定・キャンセルのどちらの終了経路でも呼ばれるため、吸着フラグの復元もここで行う。
    /// </summary>
    private void ResetState()
    {
        isSetting = false;
        firstPoint = null;
        ClearMarkers();

        if (hasSnapshot)
        {
            TerrainInputHandler.EnableGridSnapping = savedGridSnap;
            TerrainInputHandler.EnableNodeSnapping = savedNodeSnap;
            TerrainInputHandler.EnablePathSnapping = savedPathSnap;
            TerrainInputHandler.EnableRightAngleSnapping = savedRightAngleSnap;
            TerrainInputHandler.EnableStraightLineSnapping = savedStraightLineSnap;
            hasSnapshot = false;
        }
    }

    /// <summary>
    /// 地形（またはノード吸着後）がクリックされたときの処理
    /// </summary>
    public void OnTerrainClicked(Vector3 position)
    {
        if (!isSetting) return;

        if (firstPoint == null)
        {
            // 1つ目のポイントを確定
            firstPoint = position;
            firstMarker = CreateMarker(position, Color.yellow);
            if (GridOverlay.Instance != null)
            {
                GridOverlay.Instance.SetGridOrigin(position);
            }
            CreatePinkLine(position);
            Debug.Log($"GridSetManager: 1点目を配置しました: {position}");
        }
        else
        {
            // 2つ目のポイントを確定
            Vector3 pos1 = firstPoint.Value;
            Vector3 pos2 = position;

            if (Vector3.Distance(pos1, pos2) < 0.1f)
            {
                Debug.LogWarning("GridSetManager: 1点目とほぼ同じ位置がクリックされました。無視します。");
                return;
            }

            // Z基準・時計回りの角度を算出
            float angle = CalculateAngle(pos1, pos2);

            Debug.Log($"GridSetManager: 2点選択完了。算出角度: {angle}度");

            // グリッド角度を確定
            if (GridOverlay.Instance != null)
            {
                GridOverlay.Instance.SetGridAngle(angle);
                GridOverlay.Instance.SetGridOrigin(pos1);
            }

            // IPCでUIに通知（角度と原点座標）
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitGridAngleChanged(angle, pos1.x, pos1.z);
            }

            ResetState();
        }
    }

    /// <summary>
    /// 地形ホバー時のリアルタイム角度プレビュー処理
    /// </summary>
    public void OnTerrainHover(Vector3 position)
    {
        if (!isSetting) return;

        if (firstPoint != null)
        {
            Vector3 pos1 = firstPoint.Value;
            Vector3 pos2 = position;

            if (Vector3.Distance(pos1, pos2) >= 0.1f)
            {
                // Z基準・時計回りの角度をリアルタイムに計算してグリッドを回す
                float angle = CalculateAngle(pos1, pos2);
                if (GridOverlay.Instance != null)
                {
                    GridOverlay.Instance.SetGridAngle(angle);
                }
            }

            // ピンクのラインを更新
            UpdatePinkLine(pos1, pos2);
        }

        // ホバープレビューマーカーの更新（1点目の有無で色を切り替える）
        Color markerColor = (firstPoint == null) ? Color.yellow : Color.cyan;

        if (hoverMarker == null)
        {
            hoverMarker = CreateMarker(position, markerColor);
        }
        else
        {
            hoverMarker.transform.position = position;
            UpdateMarkerColor(hoverMarker, markerColor);
            hoverMarker.SetActive(true);
        }
    }

    /// <summary>
    /// マウスが地形から離れた（ホバー終了）ときの処理
    /// </summary>
    public void OnTerrainHoverEnd()
    {
        if (hoverMarker != null)
        {
            hoverMarker.SetActive(false);
        }
    }

    /// <summary>
    /// 2点間のXZ平面上の角度をUnityのY軸回転に合わせて計算（Z軸正=0度、時計回り）
    /// </summary>
    private float CalculateAngle(Vector3 pos1, Vector3 pos2)
    {
        Vector3 diff = pos2 - pos1;
        float angle = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;
        if (angle < 0)
        {
            angle += 360f;
        }
        return angle;
    }

    /// <summary>
    /// 3Dマーカーを生成する。レイキャストの邪魔にならないようコライダーは破棄する。
    /// </summary>
    private GameObject CreateMarker(Vector3 position, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        
        // マーカー自体がレイキャストに当たらないようにコライダーを即時削除する
        Collider col = marker.GetComponent<Collider>();
        if (col != null)
        {
            Destroy(col);
        }

        marker.transform.position = position;
        marker.transform.localScale = Vector3.one * 1.5f;

        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material mat = Resources.Load<Material>("MarkerMaterial");
            if (mat != null)
            {
                renderer.material = Instantiate(mat);
            }
            else
            {
                renderer.material = new Material(Shader.Find("Standard"));
            }
            renderer.material.color = color;
            renderer.material.EnableKeyword("_EMISSION");
            renderer.material.SetColor("_EmissionColor", color * 0.5f);
        }

        return marker;
    }

    /// <summary>
    /// マーカーの色を更新する
    /// </summary>
    private void UpdateMarkerColor(GameObject marker, Color color)
    {
        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null && renderer.material != null)
        {
            renderer.material.color = color;
            renderer.material.SetColor("_EmissionColor", color * 0.5f);
        }
    }

    /// <summary>
    /// 生成されたマーカーを全て破棄する
    /// </summary>
    private void ClearMarkers()
    {
        if (firstMarker != null)
        {
            Destroy(firstMarker);
            firstMarker = null;
        }
        if (hoverMarker != null)
        {
            Destroy(hoverMarker);
            hoverMarker = null;
        }
        if (pinkLine != null)
        {
            Destroy(pinkLine);
            pinkLine = null;
            lineRenderer = null;
        }
    }

    /// <summary>
    /// 1点目と2点目の間を繋ぐピンクの LineRenderer オブジェクトを生成する
    /// </summary>
    private void CreatePinkLine(Vector3 startPos)
    {
        if (pinkLine != null)
        {
            Destroy(pinkLine);
        }

        pinkLine = new GameObject("GridSetPinkLine");
        lineRenderer = pinkLine.AddComponent<LineRenderer>();

        // 各種ビルド環境でも動作するようにフォールバック付きでシェーダーを検索
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("UI/Default");
        if (shader == null) shader = Shader.Find("Standard");

        Material lineMat = new Material(shader);
        Color pinkColor = new Color(1f, 0.08f, 0.58f, 0.8f); // 鮮やかなピンク
        lineMat.color = pinkColor;
        
        lineRenderer.material = lineMat;
        lineRenderer.startColor = pinkColor;
        lineRenderer.endColor = pinkColor;
        
        // 線の太さ（少し太めにして視認しやすくする）
        lineRenderer.startWidth = 0.5f;
        lineRenderer.endWidth = 0.5f;
        
        lineRenderer.positionCount = 2;
        lineRenderer.useWorldSpace = true;
        
        Vector3 offsetPos = startPos + Vector3.up * 0.2f;
        lineRenderer.SetPosition(0, offsetPos);
        lineRenderer.SetPosition(1, offsetPos);
    }

    /// <summary>
    /// LineRenderer の終点を更新する
    /// </summary>
    private void UpdatePinkLine(Vector3 startPos, Vector3 endPos)
    {
        if (lineRenderer != null)
        {
            // 地面の表示物との重なり（Zファイティング）を防ぐため、Y軸方向に 0.2m 浮かせて描画する
            Vector3 offsetStart = startPos + Vector3.up * 0.2f;
            Vector3 offsetEnd = endPos + Vector3.up * 0.2f;
            lineRenderer.SetPosition(0, offsetStart);
            lineRenderer.SetPosition(1, offsetEnd);
        }
    }
}
