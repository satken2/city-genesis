using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using Newtonsoft.Json.Linq;

public class MeshTestController : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown modeDropdown;
    [SerializeField] private BuildingSelectionPanel buildingPanel;
    [SerializeField] private PathSelectionPanel pathPanel;
    [SerializeField] private Button generateButton;

    private GameObject _generatedObject;

    // 現在生成中のサンプルで並走している全ライン（複線プレビュー時の各線路）の近接情報。
    // 線路バリアントの結合描画（TrackAdjacency）に供給するため、サンプル生成の直前にセットする。
    // 単線・複線対象外のサンプル（交差点・地下）では null にリセットして結合描画を無効化する。
    private List<TrackNeighborInfo> _siblingTracks;

    private const int MODE_BUILDING = 0;
    private const int MODE_PATH = 1;

    private void Start()
    {
        if (modeDropdown != null)
        {
            modeDropdown.onValueChanged.AddListener(OnModeChanged);
        }

        if (generateButton != null)
        {
            generateButton.onClick.AddListener(OnGenerate);
        }

        OnModeChanged(modeDropdown != null ? modeDropdown.value : 0);

        if (MasterDataStore.Instance == null)
        {
            Debug.LogError("[MeshTest] MasterDataStore がシーンに存在しません。");
            PopulateDropdowns();
        }
        else if (MasterDataStore.Instance.IsInitialized)
        {
            PopulateDropdowns();
        }
        else
        {
            Debug.Log("[MeshTest] MasterDataStore.Initialize() を開始します...");
            MasterDataStore.Instance.OnInitialized += OnMasterDataInitialized;
            StartCoroutine(MasterDataStore.Instance.Initialize());
        }
    }

    private void OnMasterDataInitialized()
    {
        Debug.Log("[MeshTest] MasterDataStore 初期化完了。ドロップダウンを設定します。");
        PopulateDropdowns();
    }

    private void OnDestroy()
    {
        // テスト用に差し込んだ複線結合描画の隣接供給デリゲートを片付ける。
        if (TrackAdjacency.NeighborProvider == (System.Func<PathSummary, List<TrackNeighborInfo>>)ProvideSiblingNeighbors)
        {
            TrackAdjacency.NeighborProvider = null;
        }

        // 港湾テスト用に注入したダミー海岸線を片付ける(本編シーンへの持ち越し防止)。
        ClearDummyCoastline();

        if (MasterDataStore.Instance != null)
        {
            MasterDataStore.Instance.OnInitialized -= OnMasterDataInitialized;
        }
        if (modeDropdown != null)
        {
            modeDropdown.onValueChanged.RemoveListener(OnModeChanged);
        }
        if (generateButton != null)
        {
            generateButton.onClick.RemoveListener(OnGenerate);
        }
    }

    private void PopulateDropdowns()
    {
        buildingPanel?.PopulateFromMasterData();
        pathPanel?.PopulateFromMasterData();
        buildingPanel?.SetDefaults();
    }

    private void OnModeChanged(int mode)
    {
        if (buildingPanel != null) buildingPanel.gameObject.SetActive(mode == MODE_BUILDING);
        if (pathPanel != null) pathPanel.gameObject.SetActive(mode == MODE_PATH);
    }

    private void OnGenerate()
    {
        CleanupGenerated();

        int mode = modeDropdown != null ? modeDropdown.value : 0;

        if (mode == MODE_BUILDING)
        {
            GenerateBuilding();
        }
        else
        {
            GeneratePath();
        }
    }

    // マッシュアップ建物のテストで底面(=土地区画)に使う固定サイズ。
    // 最大の棟(4×6セル)に加えて他の棟も並べられるよう、9セル×6セル(72m×48m)を確保する。
    private const float MASHUP_PARCEL_WIDTH = Constants.MASHUP_CELL_SIZE * 9f;
    private const float MASHUP_PARCEL_DEPTH = Constants.MASHUP_CELL_SIZE * 6f;

    private void GenerateBuilding()
    {
        if (buildingPanel == null) return;

        int variant = buildingPanel.SelectedAgentVariant;
        int floors = buildingPanel.Floors;
        float vv = buildingPanel.VisualVariant;
        float vv2 = buildingPanel.VisualVariant2;
        float vv3 = buildingPanel.VisualVariant3;

        float width = buildingPanel.Width;
        float height = buildingPanel.Height;
        int shapeType = buildingPanel.ShapeType;

        // model_type はバリアントだけで決まるため、底面ポリゴンを作る前に判定できる。
        int modelType = BuildingMeshBuilder.GetModelType(new BuildingSummary { variant = variant });

        // マッシュアップ建物の底面は建物本体ではなく複数の棟を並べる土地区画なので、
        // 入力サイズのままだと最大の棟(4×6セル=32m×48m)すら収まらず配置に失敗する。
        // 主要な棟が確実に収まる広さの区画を固定で与える。
        if (modelType == Constants.MODEL_MASHUP)
        {
            width = MASHUP_PARCEL_WIDTH;
            height = MASHUP_PARCEL_DEPTH;
            Debug.Log($"[MeshTest] マッシュアップ建物のため、底面を土地区画サイズ({width}m×{height}m)に固定します。");
        }

        float[][] polygonCoords = GeneratePolygonCoords(width, height, shapeType);

        var buildingData = CreateBuildingSummary(variant, floors, polygonCoords, vv, vv2, vv3);

        // マッシュアップ建物は配置計画(sub_buildings)が無いと白い箱になるため、
        // バックエンドと同じアルゴリズムの移植(TestSubBuildingLayout)で自前生成する。
        // テストでは底面ポリゴンをそのまま土地区画とみなす。
        if (modelType == Constants.MODEL_MASHUP)
        {
            buildingData.sub_buildings = TestSubBuildingLayout.Generate(variant, ToVector2Polygon(polygonCoords), floors);
            if (buildingData.sub_buildings == null)
            {
                Debug.LogWarning($"[MeshTest] variant={variant} は subbuilding_master に棟定義が無いため、サブ建物なしで生成します。");
            }
            else
            {
                Debug.Log($"[MeshTest] マッシュアップ配置計画を生成: {buildingData.sub_buildings.Count}棟");
            }
        }

        // 港湾は海岸線が無いと岸壁の直線カットが働かないため、生成前にダミー海岸線を注入する。
        // それ以外の建物では過去のダミーを片付ける(Version が上がり岸壁側のキャッシュも無効化される)。
        if (modelType == Constants.MODEL_PORT)
        {
            SetupDummyCoastline(height);
        }
        else
        {
            ClearDummyCoastline();
        }

        BuildingMeshResult result = new BuildingMeshBuilder().Generate(buildingData);
        if (result == null || result.Mesh == null)
        {
            Debug.LogWarning("MeshTestController: 建物メッシュの生成に失敗しました。");
            return;
        }

        _generatedObject = new GameObject("TestBuilding");
        var mf = _generatedObject.AddComponent<MeshFilter>();
        mf.sharedMesh = result.Mesh;
        var mr = _generatedObject.AddComponent<MeshRenderer>();
        if (result.Materials != null)
        {
            mr.sharedMaterials = result.Materials;
        }
        if (result.ColliderMesh != null)
        {
            var mc = _generatedObject.AddComponent<MeshCollider>();
            mc.sharedMesh = result.ColliderMesh;
        }

        if (modelType == Constants.MODEL_PORT)
        {
            CreateSeaAndLandVisual(_generatedObject);
        }

        PositionCamera(result.Mesh.bounds);
    }

    /// <summary>底面ポリゴン([x, z]配列の配列)を Vector2 配列へ変換する。</summary>
    private static Vector2[] ToVector2Polygon(float[][] coords)
    {
        var polygon = new Vector2[coords.Length];
        for (int i = 0; i < coords.Length; i++)
        {
            polygon[i] = new Vector2(coords[i][0], coords[i][1]);
        }
        return polygon;
    }

    // ダミー海岸線: 底面を斜めに横切る直線 z = _coastBaseZ + COAST_SLOPE * x の +Z 側を海とする。
    // 傾きを付けるのは、岸壁の直線カットが軸に平行な場合しか動かない誤実装を検出しやすくするため。
    private const float COAST_SLOPE = 0.08f;
    // 海側・陸側パネルの色。位置関係が分かることだけが目的なので単色で塗る。
    private static readonly Color SEA_PANEL_COLOR = new Color(0.25f, 0.5f, 0.7f);
    private static readonly Color LAND_PANEL_COLOR = new Color(0.45f, 0.5f, 0.42f);
    private bool _dummyCoastlineActive;
    private float _coastBaseZ;

    /// <summary>
    /// 港湾テスト用のダミー海岸線を CoastlineStore に注入する。
    /// 陸地 = 巨大な矩形リング(上辺が海岸線の直線)で、底面(原点中心・奥行き depth)を
    /// 海岸線が横切るよう、直線の高さは奥行きの3割の位置に置く(-Z 側が陸、+Z 側が海)。
    /// 実ゲームでは TerrainManager.FetchCoastline がバックエンドから取得して格納する経路を、
    /// テストでは同じ SetFromGeoJson で置き換える。
    /// </summary>
    private void SetupDummyCoastline(float depth)
    {
        _coastBaseZ = depth * 0.5f * 0.3f;
        const float L = 3000f;
        var ring = new JArray
        {
            new JArray(-L, -L),
            new JArray(L, -L),
            new JArray(L, _coastBaseZ + COAST_SLOPE * L),
            new JArray(-L, _coastBaseZ - COAST_SLOPE * L),
        };
        CoastlineStore.SetFromGeoJson(new JArray { ring });
        _dummyCoastlineActive = true;
        Debug.Log($"[MeshTest] ダミー海岸線を注入しました (z≈{_coastBaseZ:F1}m, +Z側が海)");
    }

    /// <summary>注入したダミー海岸線を破棄し、CoastlineStore を未ロード状態へ戻す。</summary>
    private void ClearDummyCoastline()
    {
        if (_dummyCoastlineActive)
        {
            CoastlineStore.Clear();
            _dummyCoastlineActive = false;
        }
    }

    /// <summary>
    /// ダミー海岸線の位置が分かるよう、海側(+Z)に青、陸側(-Z)に緑がかった灰色の平面を敷く。
    /// 2枚は海岸線の直線で接し重ならない(Zファイティング防止)。海面・地形とも高さ0の
    /// テストシーンなので、どちらもわずかに沈めた高さに置いて岸壁スラブと干渉させない。
    /// </summary>
    private void CreateSeaAndLandVisual(GameObject container)
    {
        const float extent = 120f;  // 海岸線方向(±X)の広がり
        const float span = 150f;    // 海側・陸側それぞれの奥行き
        const float y = -0.02f;
        float zLeft = _coastBaseZ + COAST_SLOPE * (-extent);
        float zRight = _coastBaseZ + COAST_SLOPE * extent;

        CreateGroundQuad(container, "SeaPlane", SEA_PANEL_COLOR,
            new Vector3(-extent, y, zLeft), new Vector3(extent, y, zRight),
            new Vector3(extent, y, zRight + span), new Vector3(-extent, y, zLeft + span));
        CreateGroundQuad(container, "LandPlane", LAND_PANEL_COLOR,
            new Vector3(-extent, y, zLeft - span), new Vector3(extent, y, zRight - span),
            new Vector3(extent, y, zRight), new Vector3(-extent, y, zLeft));

        CreateLabel(container, "海 Sea (+Z)", new Vector3(0f, 3f, _coastBaseZ + 30f));
    }

    /// <summary>4頂点(反時計回り・上向き)の単色平面を container の子として生成する。</summary>
    private static void CreateGroundQuad(GameObject container, string name, Color color,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        var mesh = new Mesh
        {
            vertices = new[] { v0, v1, v2, v3 },
            triangles = new[] { 0, 2, 1, 0, 3, 2 },
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = CreateSolidColorMaterial(name, color);
        go.transform.SetParent(container.transform, true);
    }

    /// <summary>
    /// 単色マテリアルを生成する。本プロジェクトはURPなのでビルトインの Standard は
    /// 描画できずマゼンタになるため、URP Lit を優先して解決する
    /// (WaterSurfaceManager.ResolveWaterMaterial と同じ方針)。
    /// 色はURPの _BaseColor とビルトインの _Color の両方へ、存在するものだけ設定する。
    /// </summary>
    private static Material CreateSolidColorMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { name = $"MeshTest_{name}" };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        return material;
    }

    /// <summary>
    /// 形状タイプに応じたテスト用ポリゴン座標を生成する。
    /// 座標は [x, z] の2要素配列で、原点中心に配置される。
    /// </summary>
    private float[][] GeneratePolygonCoords(float width, float height, int shapeType)
    {
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        switch (shapeType)
        {
            case 1: // 三角形
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { 0f, halfH }
                };
            case 2: // 台形（下底が長い等脚台形）
                float topHalfW = halfW * 0.5f;
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { topHalfW, halfH },
                    new float[] { -topHalfW, halfH }
                };
            case 3: // 五角形（基準サイズに収まるようにスケーリングした正五角形）
                // 基準サイズ（width, height）に外接するようにスケーリングした正五角形の頂点を配置します。
                // 頂点の配置順は、他の形状と同様に反時計回り（CCW）に統一して裏返りを防いでいます。
                return new float[][]
                {
                    new float[] { -0.618034f * halfW, -halfH },
                    new float[] { 0.618034f * halfW, -halfH },
                    new float[] { halfW, 0.236068f * halfH },
                    new float[] { 0f, halfH },
                    new float[] { -halfW, 0.236068f * halfH }
                };
            default: // 矩形
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { halfW, halfH },
                    new float[] { -halfW, halfH }
                };
        }
    }

    // ショーケースの各サンプルを並べる格子間隔(m)。3列×2行に配置する。
    // カメラの最大ズーム距離(シーン設定で200m)内に全体が収まるよう、
    // 1列に並べず格子状にしてXの広がりを抑えている。
    private const float SAMPLE_SPACING_X = 78f;
    private const float SAMPLE_SPACING_Z = 95f;
    // 地面(Y=0)に張り付く道路のスプライン点のワールドY。
    // 本テストシーンには地形がないため、地形高さ=0とみなし、通常道路と同じ
    // ROAD_SPLINE_OFFSET だけ浮かせた高さを地表基準とする(ルートAGENTS.mdの「Y座標の考え方」)。
    private const float GROUND_Y = RoadPartHeights.ROAD_SPLINE_OFFSET;

    /// <summary>
    /// 選択中のバリアントを使って、道路のバリエーション(直線・曲線・十字交差点・
    /// 斜め融合交差点(鉄道ポイント風)・地下トンネル入口・高架)を格子状に並べて描画する。
    ///
    /// 本テストシーンには地形がないため、地形高さは一律0(=地面はY=0)とみなす。
    /// 相対Y=0の道路は地表(Y≒0.1)に、正の相対Yは高架、負の相対Yは地下として描画される。
    /// 各サンプルは一旦原点周りに生成してから、格子セルの位置へ親コンテナごと平行移動する。
    /// </summary>
    private void GeneratePath()
    {
        if (pathPanel == null) return;

        int variant = pathPanel.SelectedPathVariant;
        int type = GetPathType(variant);
        int lineCount = Mathf.Clamp(pathPanel.SelectedLineCount, 1, 4);
        float width = GetPathWidth(variant);
        Debug.Log($"[MeshTest] 道路ショーケース生成: variant={variant}, type={type}, lineCount={lineCount}, width={width}");

        // 線路バリアントの複線結合描画（TrackAdjacency）のため、隣接供給プロバイダを差し込む。
        // 実ゲームでは PathManager が設定するが、本テストシーンには PathManager が無いため
        // ここで自前のプロバイダ（_siblingTracks から供給）を差し込む。
        TrackAdjacency.NeighborProvider = ProvideSiblingNeighbors;

        var showcase = new GameObject("TestPathShowcase");
        _generatedObject = showcase;

        // (列, 行) の格子。直線・曲線・地下・高架・複線合流・斜め融合は「線路数」ぶんの
        // 平行なパスを敷く（複線プレビュー）。十字交差点だけは、点ノード方式では複線の
        // 4叉交差を破綻なく表せない（対向アームが重なる）ため単線で描く。
        var samples = new System.Action<GameObject>[]
        {
            c => GenerateStraightSample(c, variant, type, lineCount, width),
            c => GenerateCurveSample(c, variant, type, lineCount, width),
            c => GenerateCrossIntersectionSample(c, variant, type),
            c => GenerateMergeIntersectionSample(c, variant, type, lineCount),
            c => GenerateUndergroundSample(c, variant, type, lineCount, width),
            c => GenerateElevatedSample(c, variant, type, lineCount, width),
            c => GenerateConvergingSample(c, variant, type, lineCount, width),
        };

        const int columns = 3;
        int rows = (samples.Length + columns - 1) / columns;
        for (int i = 0; i < samples.Length; i++)
        {
            int col = i % columns;
            int row = i / columns;

            // 各サンプルは原点周りに生成し、セル位置へコンテナごと移動する。
            var cell = new GameObject($"Sample_{i}");
            cell.transform.SetParent(showcase.transform, false);
            // 前サンプルの複線設定が漏れて誤った結合描画をしないよう、サンプルごとにリセットする。
            _siblingTracks = null;
            try
            {
                samples[i](cell);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MeshTest] サンプル {i} の生成で例外: {ex}");
            }
            cell.transform.localPosition = new Vector3(col * SAMPLE_SPACING_X, 0f, row * SAMPLE_SPACING_Z);
        }

        // カメラのフレーミングは、レンダラーの生成が遅延しても破綻しないよう、
        // レイアウトの既知の広がり(格子の範囲)から手動でboundsを組み立てる。
        float halfSample = 45f;
        var min = new Vector3(-halfSample, -8f, -halfSample);
        var max = new Vector3((columns - 1) * SAMPLE_SPACING_X + halfSample, 10f, (rows - 1) * SAMPLE_SPACING_Z + halfSample);
        var bounds = new Bounds((min + max) * 0.5f, max - min);
        PositionCamera(bounds);
    }

    /// <summary>[x, 相対Y, z] のパス座標(List&lt;object&gt;)を作る。</summary>
    private static List<object> Pt(float x, float relY, float z)
    {
        return new List<object> { x, relY, z };
    }

    /// <summary>テスト用の PathSummary スタブを作る。</summary>
    private static PathSummary MakePath(int variant, int type, int pathId, bool curved)
    {
        return new PathSummary
        {
            path_id = pathId,
            path_type = type,
            path_variant = variant,
            width = 4f,
            curved = curved,
        };
    }

    /// <summary>
    /// 点列から道路メッシュを生成し、生成オブジェクトを container の子にする。
    /// 点列はワールド座標。生成オブジェクトは原点基準のワールド座標メッシュを持つため
    /// worldPositionStays=true で親付けする。
    /// 生成された描画オブジェクト（SplineComputer を持つコンテナ）を返す。柱の追加配置など、
    /// 生成後にスプラインへアクセスしたい呼び出し側が使う（失敗時は null）。
    /// </summary>
    private GameObject GenerateRoadPiece(GameObject container, PathSummary path, List<Vector3> points, string name)
    {
        if (points == null || points.Count < 2) return null;

        PathMeshResult result = PathMeshFactory.GeneratePathMesh(path, points);
        if (result == null)
        {
            Debug.LogWarning($"[MeshTest] {name}: メッシュ生成に失敗しました。");
            return null;
        }
        if (result.GeneratedObject != null)
        {
            result.GeneratedObject.name = name;
            result.GeneratedObject.transform.SetParent(container.transform, true);
        }
        return result.GeneratedObject;
    }

    /// <summary>選択中バリアントの全幅(m)をマスターデータから取得する（未取得時は4m）。</summary>
    private float GetPathWidth(int variant)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.width > 0f) return master.width;
        }
        return 4f;
    }

    // 平行に敷く各ラインへ割り当てる path_id の起点。ライン k の path_id は PARALLEL_ID_BASE + k。
    // 近接線路の除外判定（自分自身を隣接候補から外す）に一意なIDが必要なため定義する。
    private const int PARALLEL_ID_BASE = 100;

    /// <summary>
    /// 基準点列を lineCount 本ぶん横に複製し、中央揃えでオフセットした点列群を返す。
    /// 各点で「パス進行方向に直交する水平右ベクトル」を求めて横へずらすため、曲線・高低差
    /// （高架/地下）を持つ点列でもそのまま平行化できる。spacing は隣り合うライン中心間の距離。
    /// </summary>
    private static List<List<Vector3>> BuildParallelPointSets(List<Vector3> basePoints, int lineCount, float spacing)
    {
        var sets = new List<List<Vector3>>(Mathf.Max(1, lineCount));
        if (basePoints == null || basePoints.Count < 2 || lineCount <= 1)
        {
            sets.Add(basePoints);
            return sets;
        }

        int n = basePoints.Count;
        Vector3[] right = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = basePoints[Mathf.Min(i + 1, n - 1)] - basePoints[Mathf.Max(i - 1, 0)];
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            right[i] = Vector3.Cross(Vector3.up, fwd);
        }

        for (int k = 0; k < lineCount; k++)
        {
            // 中央揃え: 本数が偶数なら中心線をまたいで左右対称に、奇数なら中央線を含む。
            float off = (k - (lineCount - 1) * 0.5f) * spacing;
            var pts = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                pts.Add(basePoints[i] + right[i] * off);
            }
            sets.Add(pts);
        }
        return sets;
    }

    /// <summary>path_type が線路系（鉄道・モノレール）かを返す。複線の進行方向割り当ての対象判定に使う。</summary>
    private static bool IsTrackType(int type)
    {
        return type == Constants.PATH_TRACK || type == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// 複線の各ラインへ割り当てる進行方向（Constants.DIR_*）を返す。当面は左側通行固定。
    /// UI の RailVariantSelector（railBuild.defaultTravelDirections）と同じ規則:
    /// 本数を2で割り、左半分（index小）を順方向・右半分を逆方向にし、奇数なら中央1本を両方向にする。
    /// index 0 = 最も左（オフセット最小）。単線（1本）は必然的に両方向になる。
    /// </summary>
    private static int[] BuildTravelDirections(int lineCount)
    {
        int n = Mathf.Max(1, lineCount);
        var dirs = new int[n];
        float mid = (n - 1) * 0.5f;
        for (int i = 0; i < n; i++)
        {
            if (n % 2 == 1 && i == (int)mid)
            {
                dirs[i] = Constants.DIR_BOTH;
                continue;
            }
            // 左側通行: 中央より左のラインを順方向、右のラインを逆方向にする。
            dirs[i] = (i < mid) ? Constants.DIR_FORWARD : Constants.DIR_BACKWARD;
        }
        return dirs;
    }

    /// <summary>
    /// 複線の各ラインを進行方向に合わせて向き付けする。逆方向（DIR_BACKWARD）のラインだけ
    /// 点列を逆順にする（順方向＝始点→終点）。順方向・両方向（単線／奇数の中央線）は元の向きのまま。
    /// 内部データでは複線を一方通行の単線2本で表すため、逆方向側の線路を反転させて向きを対にする。
    /// 線路系（PATH_TRACK/PATH_MONORAIL）のときだけ適用し、道路では向きを変えない。
    /// </summary>
    private static void ApplyTravelDirections(List<List<Vector3>> sets, int type)
    {
        if (sets == null || sets.Count <= 1 || !IsTrackType(type)) return;
        int[] dirs = BuildTravelDirections(sets.Count);
        for (int k = 0; k < sets.Count; k++)
        {
            if (dirs[k] == Constants.DIR_BACKWARD)
            {
                sets[k]?.Reverse();
            }
        }
    }

    /// <summary>
    /// 平行ライン群を TrackAdjacency の近接候補（隣接線路情報）に変換する。
    /// path_id はラインごとに一意（PARALLEL_ID_BASE + k）で、GenerateLinePiece と揃える。
    /// </summary>
    private static List<TrackNeighborInfo> BuildSiblingInfos(List<List<Vector3>> sets, float width)
    {
        var infos = new List<TrackNeighborInfo>(sets.Count);
        for (int k = 0; k < sets.Count; k++)
        {
            infos.Add(new TrackNeighborInfo
            {
                PathId = PARALLEL_ID_BASE + k,
                Points = sets[k],
                Width = width,
            });
        }
        return infos;
    }

    /// <summary>
    /// TrackAdjacency.NeighborProvider の実体。現在のサンプルの平行ライン群のうち、
    /// 問い合わせ元パス以外を近接候補として返す（自分自身は除外する）。
    /// </summary>
    private List<TrackNeighborInfo> ProvideSiblingNeighbors(PathSummary path)
    {
        if (_siblingTracks == null || path == null) return null;
        var list = new List<TrackNeighborInfo>(_siblingTracks.Count);
        foreach (var t in _siblingTracks)
        {
            if (t.PathId != path.path_id) list.Add(t);
        }
        return list;
    }

    /// <summary>
    /// 基準点列を lineCount 本ぶん平行に敷く共通処理。
    /// 線路バリアントでは隣接ライン情報を供給して複線結合描画（バラスト充填・柱の共用）を働かせる。
    /// withPillars=true のラインには高架柱を配置する（高架サンプル用）。
    /// </summary>
    private void GenerateParallelSample(GameObject container, int variant, int type, int lineCount, float width, List<Vector3> basePoints, bool curved, bool withPillars, string baseName)
    {
        // ライン間隔はライン全幅とする（隣り合う道床/路面が接し、線路なら結合コリドーとして描画される）。
        List<List<Vector3>> sets = BuildParallelPointSets(basePoints, lineCount, width);
        // 複線の線路は左右で進行方向を対にする（逆方向側は点列を反転）。道路は向きを変えない。
        ApplyTravelDirections(sets, type);

        // 複線結合描画のため、全ラインの近接情報を先に用意しておく（生成中にプロバイダから参照される）。
        _siblingTracks = sets.Count > 1 ? BuildSiblingInfos(sets, width) : null;

        for (int k = 0; k < sets.Count; k++)
        {
            int pathId = PARALLEL_ID_BASE + k;
            GenerateLinePiece(container, variant, type, pathId, curved, sets[k], withPillars, $"{baseName}_{k}");
        }
    }

    /// <summary>
    /// 1本のラインのメッシュを生成し、必要なら高架柱を配置する。
    ///
    /// withPillars=true（高架）のラインは、点列を start_point/end_point/waypoints としても
    /// メッシュ用パスに設定する。これにより single_span（桁橋・地形非追従）バリアントでも
    /// アプローチ（取付道路）形状がアンカー間の直線スパンとして保たれる。
    ///
    /// 柱は二重生成を避けるため次のいずれか一方だけで立てる:
    ///   - マスターに柱設定（height_editable かつ pillar_spacing）があるバリアント →
    ///     RoadPath 本体（CreateRoadParts）がアンカーから自動で立てる。
    ///   - 柱設定が無いバリアント → ここで RoadPath.GeneratePillars を直接呼んで立てる
    ///     （テストの見栄えのため、マスター設定に関わらず高架柱を必ず見せる）。
    /// </summary>
    private void GenerateLinePiece(GameObject container, int variant, int type, int pathId, bool curved, List<Vector3> points, bool withPillars, string name)
    {
        PathSummary meshPath = MakePath(variant, type, pathId, curved);

        float pillarSpacing = GetPillarSpacing(variant);
        if (withPillars)
        {
            // 柱アンカーは点列そのものではなく、柱間隔ごとに間引いた疎なアンカーにする。
            // 収束サンプルのように点列が密（数m間隔）でも柱が密集しないようにするため。
            // このアンカーは (a) 柱設定を持つバリアントでの RoadPath 本体の柱生成、
            // (b) single_span バリアントのアプローチ形状（アンカー間の直線スパン）の両方に使う。
            List<Vector3> pillarAnchors = BuildSparseAnchors(points, pillarSpacing);
            ApplyAnchorsFromPoints(meshPath, pillarAnchors);
        }

        GameObject genObj = GenerateRoadPiece(container, meshPath, points, name);

        // マスターに柱設定が無いバリアントでは RoadPath 本体が柱を立てないため、明示的に立てる。
        // 設定があるバリアントは本体が(疎なアンカーから)立てるので、ここでは立てない（二重配置の防止）。
        if (withPillars && genObj != null && !VariantAutoGeneratesPillars(variant))
        {
            SplineComputer spline = genObj.GetComponent<SplineComputer>();
            if (spline != null)
            {
                string variantBase = $"Models/Paths/p{variant}";
                string typeBase = $"Models/Paths/p{type}";
                RoadPath.GeneratePillars(genObj, spline, pillarSpacing, 0f, meshPath, variantBase, typeBase);
            }
        }
    }

    /// <summary>
    /// 点列を start_point / end_point / waypoints としてパスに設定する。
    /// 相対Y = worldY − ROAD_SPLINE_OFFSET（テストシーンは地形標高0）。
    /// </summary>
    private static void ApplyAnchorsFromPoints(PathSummary path, List<Vector3> points)
    {
        if (points == null || points.Count < 2) return;

        path.start_point = Pt(points[0].x, points[0].y - RoadPartHeights.ROAD_SPLINE_OFFSET, points[0].z);
        Vector3 last = points[points.Count - 1];
        path.end_point = Pt(last.x, last.y - RoadPartHeights.ROAD_SPLINE_OFFSET, last.z);

        var waypoints = new List<object>();
        for (int i = 1; i < points.Count - 1; i++)
        {
            waypoints.Add(new List<object> { points[i].x, points[i].y - RoadPartHeights.ROAD_SPLINE_OFFSET, points[i].z });
        }
        path.waypoints = waypoints;
    }

    /// <summary>
    /// 点列を弧長 spacing ごとに間引いた疎なアンカー列（端点を必ず含む）を返す。
    /// 密にサンプリングされた曲線から、柱を立てるための適度な間隔のアンカーを得るために使う。
    /// </summary>
    private static List<Vector3> BuildSparseAnchors(List<Vector3> points, float spacing)
    {
        var anchors = new List<Vector3>();
        if (points == null || points.Count == 0) return anchors;
        if (points.Count <= 2 || spacing <= 0f)
        {
            anchors.AddRange(points);
            return anchors;
        }

        anchors.Add(points[0]);
        float acc = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            acc += Vector3.Distance(points[i - 1], points[i]);
            if (acc >= spacing)
            {
                anchors.Add(points[i]);
                acc = 0f;
            }
        }
        anchors.Add(points[points.Count - 1]);
        return anchors;
    }

    /// <summary>柱の間隔(m)。マスターの pillar_spacing を優先し、無ければ既定の12mを使う。</summary>
    private float GetPillarSpacing(int variant)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.pillar_spacing.HasValue && master.pillar_spacing.Value > 0f)
            {
                return master.pillar_spacing.Value;
            }
        }
        return 12f;
    }

    /// <summary>
    /// バリアントがマスターデータ上「柱を自動生成する」設定（height_editable かつ
    /// pillar_spacing &gt; 0）を持つかを返す。RoadPath 本体の柱生成条件と一致させる。
    /// </summary>
    private bool VariantAutoGeneratesPillars(int variant)
    {
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized) return false;
        var master = MasterDataStore.Instance.GetPathMaster(variant);
        return master != null
            && master.height_editable.HasValue && master.height_editable.Value
            && master.pillar_spacing.HasValue && master.pillar_spacing.Value > 0f;
    }

    /// <summary>直線道路(地表)。線路数ぶん平行に敷く。</summary>
    private void GenerateStraightSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        var points = new List<Vector3>
        {
            new Vector3(0f, GROUND_Y, -40f),
            new Vector3(0f, GROUND_Y,  40f),
        };
        GenerateParallelSample(container, variant, type, lineCount, width, points, curved: false, withPillars: false, "01_Straight");
        CreateLabel(container, "直線 Straight", new Vector3(0f, 3f, 45f));
    }

    /// <summary>曲線道路(地表)。三次ベジエでS字カーブをサンプリングし、線路数ぶん平行に敷く。</summary>
    private void GenerateCurveSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        Vector3 p0 = new Vector3(-22f, GROUND_Y, -40f);
        Vector3 p1 = new Vector3( 28f, GROUND_Y, -12f);
        Vector3 p2 = new Vector3(-28f, GROUND_Y,  12f);
        Vector3 p3 = new Vector3( 22f, GROUND_Y,  40f);
        var points = SampleBezierFlat(p0, p1, p2, p3, GROUND_Y, 5f);
        GenerateParallelSample(container, variant, type, lineCount, width, points, curved: true, withPillars: false, "02_Curve");
        CreateLabel(container, "曲線 Curve", new Vector3(0f, 3f, 45f));
    }

    /// <summary>
    /// 十字交差点。4本の道路が90度で交わる。
    /// 点ノード方式では複線の4叉交差を破綻なく表せない（1つの側にオフセットすると対向アームが
    /// 重なる）ため、線路数に関わらず単線で描く。複線の合流検証は「斜め融合」「複線合流」を使う。
    /// </summary>
    private void GenerateCrossIntersectionSample(GameObject container, int variant, int type)
    {
        var arms = new (Vector2 dir, float length)[]
        {
            (new Vector2( 0f,  1f), 40f),
            (new Vector2( 0f, -1f), 40f),
            (new Vector2( 1f,  0f), 40f),
            (new Vector2(-1f,  0f), 40f),
        };
        GenerateIntersectionSample(container, variant, type, arms, lineCount: 1, lateralDir: Vector2.zero);
        CreateLabel(container, "十字交差点 Cross", new Vector3(0f, 3f, 45f));
    }

    /// <summary>
    /// 斜めにシームレスに融合する交差点(鉄道のポイント/分岐器のような形)。
    /// 本線(東西)に対して浅い角度で1本が分岐する3枝のY字分岐。
    /// 線路バリアントを選ぶと、転回制約(偏向角≤45度)を満たすペアにレールコネクタが敷かれる。
    ///
    /// 線路数ぶんの平行なY字分岐を本線に直交する方向(Z)へ並べる。これにより、複線の本線・
    /// 複線の分岐がそれぞれ結合描画(複線見え化)される「複線の分岐器」を確認できる。
    /// </summary>
    private void GenerateMergeIntersectionSample(GameObject container, int variant, int type, int lineCount)
    {
        // 本線: 西(-X)と東(+X)。分岐: 東側へ +25度傾けた枝。
        float rad = 25f * Mathf.Deg2Rad;
        var arms = new (Vector2 dir, float length)[]
        {
            (new Vector2(-1f, 0f), 40f),
            (new Vector2( 1f, 0f), 40f),
            (new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), 40f),
        };
        // 本線(東西=X軸)に直交する Z 方向へ平行コピーを並べる。
        GenerateIntersectionSample(container, variant, type, arms, lineCount, new Vector2(0f, 1f));
        CreateLabel(container, "斜め融合 Points", new Vector3(0f, 3f, 45f));
    }

    /// <summary>
    /// ノード(交差点)と接続する複数本の道路を生成する共通処理。
    /// PathManager.UpdateNodeIntersectionMesh のロジックを、テスト用に自己完結する形で再現する:
    ///   1. 各枝を「ノードを端点に持つ PathSummary」として作る
    ///   2. NodeMeshFactory で交差点メッシュ・接続点(切断点)・歩道/レールコネクタを計算
    ///   3. 交差点メッシュ・歩道・レールを実体化
    ///   4. 各枝の道路を接続点まで切り詰めて描画
    /// nodeRelativeY は常に0(地表交差点)。
    ///
    /// lineCount &gt; 1 の場合は、交差点全体を lateralDir 方向へライン幅ずつオフセットした
    /// 平行コピーを lineCount 個作る。各コピーのアーム(同方向どうし)は近接して並走するため、
    /// 線路バリアントでは結合描画(複線見え化)される。アームの描画は結合判定に全アームの
    /// 点列が必要なので、ノードメッシュを先に実体化し、全アームの点列を集めてから最後にまとめて描く。
    /// </summary>
    private void GenerateIntersectionSample(GameObject container, int variant, int type, (Vector2 dir, float length)[] arms, int lineCount, Vector2 lateralDir)
    {
        lineCount = Mathf.Clamp(lineCount, 1, 4);
        float width = GetPathWidth(variant);
        Vector2 lat = lateralDir.sqrMagnitude > 1e-6f ? lateralDir.normalized : new Vector2(0f, 1f);

        // 平行コピーのアーム点列をためておき、結合情報(_siblingTracks)確定後にまとめて描画する。
        var armPieces = new List<(PathSummary path, List<Vector3> points, string name)>();
        var siblings = new List<TrackNeighborInfo>();

        for (int c = 0; c < lineCount; c++)
        {
            float latOff = (c - (lineCount - 1) * 0.5f) * width;
            Vector3 nodePos = new Vector3(lat.x * latOff, 0f, lat.y * latOff);
            int nodeId = 900 + c;

            var paths = new List<PathSummary>(arms.Length);
            for (int i = 0; i < arms.Length; i++)
            {
                Vector2 dir = arms[i].dir.normalized;
                float len = arms[i].length;
                Vector3 outer = new Vector3(nodePos.x + dir.x * len, 0f, nodePos.z + dir.y * len);

                int pathId = c * 100 + i + 1;
                var path = MakePath(variant, type, pathId, curved: false);
                path.start_node_id = nodeId;
                path.end_node_id = 20000 + c * 100 + i; // ノード以外の一意な端点ID(片端のみ接続)
                path.start_point = Pt(nodePos.x, 0f, nodePos.z);
                path.end_point = Pt(outer.x, 0f, outer.z);
                paths.Add(path);
            }

            NodeMeshResult res = NodeMeshFactory.GenerateNodeMesh(nodeId, nodePos, paths, 0f);
            if (res == null || res.Mesh == null)
            {
                Debug.LogWarning($"[MeshTest] 交差点メッシュの生成に失敗しました(copy={c})。");
                continue;
            }

            // 交差点メッシュ本体(メッシュはノード基準のローカル座標なのでノード位置に配置する)。
            // ノードメッシュ・歩道・レールは結合判定に依存しないため即時実体化する。
            var nodeGO = new GameObject($"Intersection_{c}");
            nodeGO.transform.position = nodePos;
            var mf = nodeGO.AddComponent<MeshFilter>();
            mf.mesh = res.Mesh;
            var mr = nodeGO.AddComponent<MeshRenderer>();
            if (res.Material != null) mr.material = res.Material;

            // 歩道コネクタ(通常道路と同じ sidewalk プレハブを曲げて配置する)
            if (res.SidewalkConnectors != null && res.SidewalkConnectors.Count > 0)
            {
                var sidewalkGO = new GameObject("Sidewalk");
                sidewalkGO.transform.SetParent(nodeGO.transform, false);
                for (int i = 0; i < res.SidewalkConnectors.Count; i++)
                {
                    RoadPath.CreateSidewalkConnectorObject(sidewalkGO.transform, res.SidewalkConnectors[i], i);
                }
            }

            // レールコネクタ(線路バリアントのポイント・平面交差)
            if (res.RailConnectors != null && res.RailConnectors.Count > 0)
            {
                var railGO = new GameObject("Rail");
                railGO.transform.SetParent(nodeGO.transform, false);
                for (int i = 0; i < res.RailConnectors.Count; i++)
                {
                    TrackPath.CreateRailConnectorObject(railGO.transform, res.RailConnectors[i], i);
                }
            }

            nodeGO.transform.SetParent(container.transform, true);

            // 各枝の道路を接続点(交差点の縁)まで切り詰めた点列を作り、あとで描くためバッファに積む。
            for (int i = 0; i < arms.Length; i++)
            {
                PathSummary path = paths[i];
                Vector2 dir = arms[i].dir.normalized;
                float len = arms[i].length;

                Vector3 start;
                if (res.ConnectionPoints != null && res.ConnectionPoints.TryGetValue(path.path_id, out Vector3 cut))
                {
                    start = cut;
                }
                else
                {
                    // 接続点が計算できなかった枝はノード位置から描く
                    start = new Vector3(nodePos.x, GROUND_Y, nodePos.z);
                }
                Vector3 outer = new Vector3(nodePos.x + dir.x * len, GROUND_Y, nodePos.z + dir.y * len);

                var points = new List<Vector3> { start, outer };
                armPieces.Add((path, points, $"Arm_{c}_{i}"));
                siblings.Add(new TrackNeighborInfo { PathId = path.path_id, Points = points, Width = width });
            }
        }

        // 平行コピーが複数あるときだけ、同方向アームどうしの複線結合描画を有効化する。
        _siblingTracks = lineCount > 1 ? siblings : null;

        foreach (var piece in armPieces)
        {
            GenerateRoadPiece(container, piece.path, piece.points, piece.name);
        }
    }

    /// <summary>
    /// 複線合流(高架橋)。単線同士が離れた位置から近づいてきて、最終的に並走する複線になる様子を
    /// 描く。線路が交わるのではなく、並走区間で結合描画(複線見え化)が働くかを確認するためのサンプル。
    /// 高架橋のマージ検証用に、水平な高架桁(相対Y=8m)の上で収束させ、柱も立てる。
    ///
    /// 線路数ぶんのラインを、手前(z=-40)では広く離し、中央(z=0)までに隣り合わせ(ライン幅間隔)へ
    /// 収束させて、以降は並走させる。4本なら中央へ寄せながら最終的に4本が隣接並走になる。
    /// </summary>
    private void GenerateConvergingSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        lineCount = Mathf.Clamp(lineCount, 1, 4);
        const float deckRelY = 8f;
        float deckY = deckRelY + RoadPartHeights.ROAD_SPLINE_OFFSET;

        // 手前で離す間隔。結合閾値(TRACK_MERGE_MAX_EDGE_GAP)より十分広くして、
        // 収束前は結合せず・並走後に結合する遷移が見えるようにする。
        float startSpacing = width + 10f;
        float endSpacing = width; // 並走時は隣接(道床が接する)

        // z を細かくサンプリングし、各ラインの横位置を start→end へなめらかに補間する。
        var sets = new List<List<Vector3>>(lineCount);
        for (int k = 0; k < lineCount; k++)
        {
            sets.Add(new List<Vector3>());
        }
        for (float z = -40f; z <= 40.001f; z += 4f)
        {
            // 収束の進み具合 t: z=-40 で0(最大に離れる)、z=0 以降で1(並走)。smoothstep でなめらかに。
            float raw = Mathf.Clamp01((z + 40f) / 40f);
            float t = raw * raw * (3f - 2f * raw);
            float spacing = Mathf.Lerp(startSpacing, endSpacing, t);
            for (int k = 0; k < lineCount; k++)
            {
                float centered = k - (lineCount - 1) * 0.5f;
                sets[k].Add(new Vector3(centered * spacing, deckY, z));
            }
        }

        // 複線の線路は左右で進行方向を対にする（逆方向側は点列を反転）。道路は向きを変えない。
        ApplyTravelDirections(sets, type);

        // 並走区間の結合描画のため、全ラインの近接情報を供給する。
        _siblingTracks = lineCount > 1 ? BuildSiblingInfos(sets, width) : null;

        for (int k = 0; k < sets.Count; k++)
        {
            GenerateLinePiece(container, variant, type, PARALLEL_ID_BASE + k, curved: true, sets[k], withPillars: true, $"07_Converge_{k}");
        }

        CreateLabel(container, "複線合流 Converge", new Vector3(0f, 11f, 45f));
    }

    /// <summary>
    /// 地下トンネル入口。地表から滑らかに1回だけ潜って地下へ入るプロファイルを、
    /// PathManager.GenerateUndergroundPathMesh と同じロジック
    /// (TunnelSectionUtil.SplitDrawableRuns による描画区間分割 + 坑口へのportal配置)で描く。
    /// トンネル区間(深さ&gt;クリアランス)はメッシュを描かず、境界の坑口に portal パーツを置く。
    ///
    /// 入口と出口の2箇所に潜るのではなく片側だけを見せる。地表→地下の遷移は、勾配が
    /// 徐々に急になるよう中間点を細かく置いて滑らかにする(角ばった折れ曲がりを防ぐ)。
    /// 線路数ぶん平行に敷く(複線対応)。ライン中心の間隔は他サンプルと同じくライン幅(width)で、
    /// 開削区間では隣接ライン情報を供給して結合描画(複線見え化=路盤結合)も働かせる。
    /// </summary>
    private void GenerateUndergroundSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        // (z, 相対Y) のプロファイル。手前(-z)の地表から、勾配を徐々に立てながら地下(-8m)へ
        // 一度だけ潜り、そのまま地下で終わる。深さがクリアランスを跨ぐ点が唯一の坑口になる。
        var profile = new (float z, float y)[]
        {
            (-45f, 0f),
            (-38f, 0f),
            (-30f, -1f),   // ゆるやかに勾配を付け始める
            (-20f, -4f),
            (-10f, -7f),
            (  0f, -8f),
            ( 20f, -8f),
            ( 45f, -8f),   // 地下のまま終端(反対側の坑口は作らない)
        };
        var basePoints = new List<Vector3>(profile.Length);
        foreach (var (z, y) in profile)
        {
            // worldY = 地形標高(0) + 相対Y + ROAD_SPLINE_OFFSET。地表(相対Y=0)は GROUND_Y と一致する。
            basePoints.Add(new Vector3(0f, y + RoadPartHeights.ROAD_SPLINE_OFFSET, z));
        }

        float clearance = TunnelSectionUtil.GetClearanceHeight(variant);

        List<List<Vector3>> sets = BuildParallelPointSets(basePoints, Mathf.Clamp(lineCount, 1, 4), width);
        // 複線の線路は左右で進行方向を対にする（逆方向側は点列を反転）。道路は向きを変えない。
        ApplyTravelDirections(sets, type);

        // 開削区間(地表に出ている描画区間)の路盤結合(複線見え化)のため、全ラインの近接情報を供給する。
        // 近接判定は XZ 近接 + Y近接で行われ、トンネル(未描画)区間は描画点が無いので結合対象にならない。
        // 各ランは同じ path_id(PARALLEL_ID_BASE + k)で描かれるため、自ライン同士は除外される。
        _siblingTracks = lineCount > 1 ? BuildSiblingInfos(sets, width) : null;

        var underground = new GameObject("UndergroundEntrance");
        for (int k = 0; k < sets.Count; k++)
        {
            var path = MakePath(variant, type, PARALLEL_ID_BASE + k, curved: true);
            var portals = new List<TunnelSectionUtil.PortalInfo>();
            List<List<Vector3>> runs = TunnelSectionUtil.SplitDrawableRuns(sets[k], clearance, portals);

            foreach (var run in runs)
            {
                PathMeshResult runResult = PathMeshFactory.GeneratePathMesh(path, run);
                if (runResult?.GeneratedObject != null)
                {
                    runResult.GeneratedObject.transform.SetParent(underground.transform, true);
                }
            }
            PlaceTunnelPortals(underground, variant, type, portals);
        }
        underground.transform.SetParent(container.transform, true);

        CreateLabel(container, "地下トンネル入口 Tunnel", new Vector3(0f, 3f, 50f));
    }

    /// <summary>
    /// 坑口(トンネル出入口)に portal パーツを配置する。
    /// プレハブは Models/Paths/p{variant}/portal → p{type}/portal の順で検索し、
    /// 無ければ配置しない(テストシーンにモデルが無い場合はメッシュの終端が坑口を表す)。
    /// </summary>
    private void PlaceTunnelPortals(GameObject container, int variant, int type, List<TunnelSectionUtil.PortalInfo> portals)
    {
        if (portals == null || portals.Count == 0 || ModelManager.Instance == null) return;

        GameObject portalPrefab = ModelManager.Instance.GetModel($"Models/Paths/p{variant}/portal")
            ?? ModelManager.Instance.GetModel($"Models/Paths/p{type}/portal");
        if (portalPrefab == null)
        {
            Debug.Log($"[MeshTest] portal プレハブなし (variant={variant}, type={type})。坑口の配置をスキップします。");
            return;
        }

        foreach (var portal in portals)
        {
            GameObject portalGO = Instantiate(portalPrefab, container.transform);
            portalGO.transform.position = portal.position;
            portalGO.transform.rotation = Quaternion.LookRotation(portal.direction, Vector3.up);
        }
    }

    /// <summary>
    /// 高架道路。中央を高架桁(相対Y=8m)とし、両端は地表へ降りるアプローチ(取付道路)を付ける。
    /// 高架区間には柱(pillar/pillar_cap)を立て、アプローチでは路面が下がるにつれて柱が短くなり、
    /// 地表では柱が消える。線路数ぶん平行に敷く(高架複線対応)。
    ///
    /// 柱の配置は GenerateLinePiece が担う(マスターに柱設定があるバリアントは RoadPath 本体、
    /// 無ければ RoadPath.GeneratePillars の直接呼び出し。どちらか一方だけで立てる)。
    /// </summary>
    private void GenerateElevatedSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        const float deckRelY = 8f;
        // (z, 相対Y)。両端の地表(相対Y=0)から緩やかに立ち上げ、中央を水平な高架桁にする。
        var profile = new (float z, float y)[]
        {
            (-45f, 0f),
            (-40f, 0f),
            (-20f, deckRelY),   // 取付道路を登り切って高架へ
            ( 20f, deckRelY),
            ( 40f, 0f),
            ( 45f, 0f),
        };
        var points = new List<Vector3>(profile.Length);
        foreach (var (z, y) in profile)
        {
            points.Add(new Vector3(0f, y + RoadPartHeights.ROAD_SPLINE_OFFSET, z));
        }

        GenerateParallelSample(container, variant, type, lineCount, width, points, curved: true, withPillars: true, "06_Elevated");
        CreateLabel(container, "高架 Elevated", new Vector3(0f, 11f, 45f));
    }

    /// <summary>
    /// 三次ベジエ曲線を約 interval メートル間隔でサンプリングする。
    /// テストシーンには地形がないため、Y座標は forcedY で一定にする。
    /// </summary>
    private static List<Vector3> SampleBezierFlat(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float forcedY, float interval)
    {
        float approxLength = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        int samples = Mathf.Max(1, Mathf.FloorToInt(approxLength / interval));

        var result = new List<Vector3>(samples + 2);
        for (int s = 0; s <= samples + 1; s++)
        {
            float t = (float)s / (samples + 1);
            Vector3 p = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t);
            p.y = forcedY;
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// サンプルの識別用に、ワールド空間の簡易テキストラベルを配置する。
    /// Unity組み込みフォントが取得できない環境ではラベルを作らない(必須の表示ではない)。
    /// </summary>
    private void CreateLabel(GameObject container, string text, Vector3 worldPos)
    {
        Font font = null;
        try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (font == null)
        {
            try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        }
        if (font == null) return;

        var labelGO = new GameObject("Label");
        var tm = labelGO.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 48;
        tm.characterSize = 0.4f;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.black;
        labelGO.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        labelGO.transform.position = worldPos;
        labelGO.transform.SetParent(container.transform, true);
    }

    private void CleanupGenerated()
    {
        if (_generatedObject != null)
        {
            Destroy(_generatedObject);
            _generatedObject = null;
        }
        // ダミー海岸線は港湾サンプルの表示中だけ存在させる(次の生成が港湾なら再注入される)
        ClearDummyCoastline();
    }

    private BuildingSummary CreateBuildingSummary(int variant, int floors, float[][] polygon, float vv, float vv2, float vv3)
    {
        var polyList = new List<object>(polygon.Length);
        foreach (var coord in polygon)
        {
            var coordList = new List<object>();
            foreach (float f in coord) coordList.Add(f);
            polyList.Add(coordList);
        }

        return new BuildingSummary
        {
            agent_id = 0,
            variant = variant,
            floors = floors,
            polygon = polyList,
            center_point = new List<object> { 0f, 0f },
            visual_variant = vv,
            visual_variant2 = vv2,
            visual_variant3 = vv3,
        };
    }

    private int GetPathType(int pathVariant)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            try
            {
                var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
                if (master != null) return master.type;
            }
            catch (System.Collections.Generic.KeyNotFoundException) { }
        }
        return Constants.PATH_MOTORWAY;
    }

    /// <summary>
    /// 生成物の bounds に合わせてカメラを斜め見下ろしでフレーミングする。
    /// カメラ操作は本編と同じ <see cref="CameraController"/> に統一しており、
    /// ここでは初期視点の一発配置(SetView)だけを行う。以降の移動・回転・ズームは
    /// CameraController が本編と同じ操作感で処理する。
    /// </summary>
    private void PositionCamera(Bounds bounds)
    {
        var camCtrl = CameraController.Instance;
        if (camCtrl == null) return;

        Vector3 center = bounds.center;
        float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        float distance = Mathf.Max(maxExtent * 2.5f, 5f);

        // +Z側の上空(仰角40度)から生成物の中心を注視する視点を組む。
        float pitch = 40f * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(0f, Mathf.Sin(pitch), Mathf.Cos(pitch));
        Vector3 camPos = center + dir * distance;
        camPos.y = Mathf.Max(camPos.y, 1f);
        Quaternion rot = Quaternion.LookRotation(center - camPos, Vector3.up);

        camCtrl.SetView(camPos, rot);
    }
}
