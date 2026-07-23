using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Collections.Generic;
using GeneratedModels;

public class BuildingPreviewWindow : EditorWindow
{
    /// <summary>
    /// バックエンド API のベース URL。
    /// ApiClient.BASE_URL を参照することで、動的ポート対応と一貫性を保つ。
    /// </summary>
    private static string API_BASE => ApiClient.BASE_URL;

    private int buildingIdInput = 0;
    private Vector2 scrollPosition;
    private GameObject previewRoot;

    // 取得した建物リスト
    private List<BuildingSummary> fetchedBuildings = new List<BuildingSummary>();
    private int selectedIndex = -1;
    private string statusMessage = "";
    private bool isFetching = false;
    private UnityWebRequest activeRequest;

    // プレビュー対象の BuildingSummary
    private BuildingSummary previewTarget;

    // パラメータ上書き
    private bool overrideFloors = false;
    private int overrideFloorsValue = 3;
    private bool overrideBuildingShape = false;
    private int overrideBuildingShapeValue = 2;
    private float floorHeight = 0.3f;

    [MenuItem("Tools/Building Preview")]
    static void Init()
    {
        var window = GetWindow<BuildingPreviewWindow>("Building Preview");
        window.minSize = new Vector2(300, 400);
        window.Show();
    }

    void OnGUI()
    {
        Debug.Log($"[BMB] OnGUI");
        EditorGUILayout.Space(8);

        // === API から取得 ===
        EditorGUILayout.LabelField("Fetch Buildings", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        buildingIdInput = EditorGUILayout.IntField("Building ID", buildingIdInput);
        if (GUILayout.Button("Fetch", GUILayout.Width(60)))
        {
            FetchBuildingById(buildingIdInput);
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Fetch All in View (bbox 0,0,100,100)"))
        {
            FetchBuildingsByBbox(0, 0, 100, 100);
        }

        EditorGUILayout.Space(4);
        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, isFetching ? MessageType.Info : MessageType.None);
        }

        EditorGUILayout.Space(8);

        // === 建物リスト ===
        if (fetchedBuildings.Count > 0)
        {
            EditorGUILayout.LabelField($"Buildings ({fetchedBuildings.Count})", EditorStyles.boldLabel);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(150));
            for (int i = 0; i < fetchedBuildings.Count; i++)
            {
                var b = fetchedBuildings[i];
                string label = $"ID:{b.agent_id}  variant:{b.variant}  floors:{b.floors}  bd_type:{b.bd_type}";
                if (GUILayout.Toggle(selectedIndex == i, label, EditorStyles.radioButton))
                {
                    selectedIndex = i;
                    previewTarget = b;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        EditorGUILayout.Space(8);

        // === パラメータ上書き ===
        EditorGUILayout.LabelField("Parameters", EditorStyles.boldLabel);

        overrideFloors = EditorGUILayout.Toggle("Override Floors", overrideFloors);
        if (overrideFloors)
        {
            overrideFloorsValue = EditorGUILayout.IntSlider("Floors", overrideFloorsValue, 1, 30);
        }

        overrideBuildingShape = EditorGUILayout.Toggle("Override Shape", overrideBuildingShape);
        if (overrideBuildingShape)
        {
            overrideBuildingShapeValue = EditorGUILayout.IntPopup("Shape",
                overrideBuildingShapeValue,
                new[] { "House (1)", "Office (2)", "Commercial (3)", "Full (4)" },
                new[] { 1, 2, 3, 4 });
        }

        floorHeight = EditorGUILayout.FloatField("Floor Height", floorHeight);

        EditorGUILayout.Space(8);

        // === プレビュー操作 ===
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(previewTarget == null);
        if (GUILayout.Button("Generate Preview", GUILayout.Height(30)))
        {
            GeneratePreview();
        }
        EditorGUI.EndDisabledGroup();

        if (GUILayout.Button("Clear", GUILayout.Height(30), GUILayout.Width(60)))
        {
            ClearPreview();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        // === プレビュー対象の情報 ===
        if (previewTarget != null)
        {
            EditorGUILayout.LabelField("Selected Building Info", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Agent ID", previewTarget.agent_id.ToString());
            EditorGUILayout.LabelField("Variant", previewTarget.variant.ToString());
            EditorGUILayout.LabelField("BD Type", previewTarget.bd_type.ToString());
            EditorGUILayout.LabelField("Floors", previewTarget.floors.ToString());

            if (previewTarget.polygonArray != null)
            {
                EditorGUILayout.LabelField("Polygon Verts", previewTarget.polygonArray.Length.ToString());
            }

            EditorGUILayout.LabelField("visual_variant", previewTarget.visual_variant.ToString("F3"));
            EditorGUILayout.LabelField("visual_variant2", previewTarget.visual_variant2.ToString("F3"));

            float[] cp = previewTarget.centerPointArray;
            if (cp != null && cp.Length >= 2)
            {
                EditorGUILayout.LabelField("Center", $"({cp[0]:F2}, {cp[1]:F2})");
            }
        }
    }

    void Update()
    {
        if (isFetching && activeRequest != null && activeRequest.isDone)
        {
            HandleFetchResult();
        }
    }

    void OnDestroy()
    {
        ClearPreview();
        if (activeRequest != null)
        {
            activeRequest.Abort();
            activeRequest.Dispose();
        }
    }

    private void FetchBuildingById(int id)
    {
        if (isFetching) return;

        string url = $"{API_BASE}/buildings/{id}";
        StartFetch(url, single: true);
    }

    private void FetchBuildingsByBbox(float x1, float z1, float x2, float z2)
    {
        if (isFetching) return;

        string url = $"{API_BASE}/buildings/?bbox={x1},{z1},{x2},{z2}";
        StartFetch(url, single: false);
    }

    private void StartFetch(string url, bool single)
    {
        isFetching = true;
        statusMessage = $"Fetching: {url}";

        activeRequest = UnityWebRequest.Get(url);
        activeRequest.timeout = 10;
        activeRequest.SendWebRequest();

        // single フラグをトラッキング
        _isSingleFetch = single;
    }

    private bool _isSingleFetch = false;

    private void HandleFetchResult()
    {
        isFetching = false;

        if (activeRequest.result != UnityWebRequest.Result.Success)
        {
            statusMessage = $"Error: {activeRequest.error}";
            activeRequest.Dispose();
            activeRequest = null;
            return;
        }

        string json = activeRequest.downloadHandler.text;
        activeRequest.Dispose();
        activeRequest = null;

        try
        {
            if (_isSingleFetch)
            {
                var building = JsonConvert.DeserializeObject<BuildingSummary>(json);
                if (building != null)
                {
                    fetchedBuildings.Clear();
                    fetchedBuildings.Add(building);
                    selectedIndex = 0;
                    previewTarget = building;
                    statusMessage = $"Fetched building ID:{building.agent_id}";
                }
                else
                {
                    statusMessage = "Failed to parse building data.";
                }
            }
            else
            {
                var response = JsonConvert.DeserializeObject<BuildingsListResponse>(json);
                if (response?.buildings != null)
                {
                    fetchedBuildings = response.buildings;
                    selectedIndex = -1;
                    previewTarget = null;
                    statusMessage = $"Fetched {fetchedBuildings.Count} buildings.";
                }
                else
                {
                    statusMessage = "No buildings found.";
                }
            }
        }
        catch (System.Exception ex)
        {
            statusMessage = $"Parse error: {ex.Message}";
        }

        Repaint();
    }

    private void GeneratePreview()
    {
        if (previewTarget == null) return;

        ClearPreview();

        BuildingMeshUtils.InitializeCache();
        Debug.Log($"[BMB] GeneratePreview: previewTarget={previewTarget.agent_id}");


        // パラメータ上書き用に BuildingSummary をコピー
        BuildingSummary data = CreateCopy(previewTarget);
        if (overrideFloors) data.floors = overrideFloorsValue;

        // floorHeight をローカル定数として上書き
        // （直接 BuildingMeshUtils を使わず、マスターデータの初期化を試みる）
        TryInitializeMasterData();

        // メッシュ生成
        BuildingMeshResult result;

        if (overrideBuildingShape)
        {
            Debug.Log($"[BMB] GeneratePreview: overrideBuildingShape={overrideBuildingShape}");
            // building_shape を上書きしたい場合、直接ビルダーを呼ぶ
            Vector2[] footprint = BuildingMeshUtils.GetFootprint(data);
            if (footprint == null || footprint.Length < 3)
            {
                statusMessage = "Invalid polygon data.";
                return;
            }

            int floors = Mathf.Max(1, data.floors ?? 1);
            string prefix = BuildingMeshUtils.ResolveModelNaming(data).Prefix;
            float fh = GetFloorHeight(data);

            result = ModularBuildingMeshBuilder.GenerateWithBuildingShape(
                footprint, floors, prefix, fh,
                data.visual_variant, data.visual_variant2,
                overrideBuildingShapeValue);
        }
        else
        {
            Debug.Log($"[BMB] GeneratePreview: overrideBuildingShape=null");
            result = new BuildingMeshBuilder().Generate(data);
        }

        if (result?.Mesh == null)
        {
            Debug.Log($"[BMB] GeneratePreview: result?.Mesh=null");
            statusMessage = "Mesh generation returned null.";
            return;
        }

        // プレビューオブジェクトを作成
        previewRoot = new GameObject($"Preview_Building_{data.agent_id}");
        previewRoot.transform.position = Vector3.zero;

        var mf = previewRoot.AddComponent<MeshFilter>();
        mf.mesh = result.Mesh;

        var mr = previewRoot.AddComponent<MeshRenderer>();
        if (result.Materials != null && result.Materials.Length > 0)
        {
            mr.sharedMaterials = result.Materials;
        }

        // コライダー
        if (result.ColliderMesh != null)
        {
            var mc = previewRoot.AddComponent<MeshCollider>();
            mc.sharedMesh = result.ColliderMesh;
        }

        // Scene ビューでフォーカス
        Selection.activeGameObject = previewRoot;
        SceneView.FrameLastActiveSceneView();

        // 情報表示
        string shapeName = overrideBuildingShape
            ? ShapeNameFromValue(overrideBuildingShapeValue)
            : "auto";
        statusMessage = $"Preview generated: {result.Mesh.vertexCount} vertices, {result.Mesh.subMeshCount} submeshes (shape={shapeName})";
    }

    private float GetFloorHeight(BuildingSummary data)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var master = MasterDataStore.Instance.GetAgentMaster(data.variant);
            if (master?.height_per_floor != null)
            {
                return master.height_per_floor.Value;
            }
        }
        return floorHeight;
    }

    private void TryInitializeMasterData()
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            return;
        }

        // MasterDataStore がエディタモードで初期化されていない場合の警告
        Debug.LogWarning("BuildingPreview: MasterDataStore is not initialized. Using default floor height.");
    }

    private BuildingSummary CreateCopy(BuildingSummary source)
    {
        // 簡易コピー: JSON シリアライズ経由
        string json = JsonConvert.SerializeObject(source);
        return JsonConvert.DeserializeObject<BuildingSummary>(json);
    }

    private void ClearPreview()
    {
        if (previewRoot != null)
        {
            DestroyImmediate(previewRoot);
            previewRoot = null;
        }
    }

    private static string ShapeNameFromValue(int value)
    {
        if (value == Constants.SHAPE_HOUSE) return "House";
        if (value == Constants.SHAPE_OFFICE) return "Office";
        if (value == Constants.SHAPE_COMMERCIAL) return "Commercial";
        if (value == Constants.SHAPE_FULL) return "Full";
        return value.ToString();
    }
}

// BuildingsListResponse は BuildingManager.cs で定義済み
// エディタスクリプトからアクセス可能
