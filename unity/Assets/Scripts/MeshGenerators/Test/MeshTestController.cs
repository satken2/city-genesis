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

    // Neighbor information for all parallel lines in the current sample (each track in double-track previews).
    // Set immediately before sample generation to provide track-variant joined rendering (TrackAdjacency).
    // Reset null for samples outside single/double-track cases (intersections/underground), disabling joins.
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
        // Clean up injected neighbor delegate for double-track join tests.
        if (TrackAdjacency.NeighborProvider == (System.Func<PathSummary, List<TrackNeighborInfo>>)ProvideSiblingNeighbors)
        {
            TrackAdjacency.NeighborProvider = null;
        }

        // Clean up dummy port-test coastline, preventing carryover into gameplay scenes.
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

    // Fixed footprint (=parcel) size for mashup tests.
    // Reserve 9×6 cells (72m×48m) to fit largest wing (4×6 cells) plus others.
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

        // model_type depends only on variant, so resolve before building footprint polygons.
        int modelType = BuildingMeshBuilder.GetModelType(new BuildingSummary { variant = variant });

        // Mashup/open-pit footprints are parcels containing multiple wings, not building bodies;
        // input size alone cannot fit even the largest wing (4×6 cells=32m×48m), causing placement failure.
        // Supply a fixed parcel large enough for primary wings.
        bool hasSubBuildings = modelType == Constants.MODEL_MASHUP || modelType == Constants.MODEL_OPEN_PIT;
        if (hasSubBuildings)
        {
            width = MASHUP_PARCEL_WIDTH;
            height = MASHUP_PARCEL_DEPTH;
            Debug.Log($"[MeshTest] マッシュアップ建物のため、底面を土地区画サイズ({width}m×{height}m)に固定します。");
        }

        float[][] polygonCoords = GeneratePolygonCoords(width, height, shapeType);

        var buildingData = CreateBuildingSummary(variant, floors, polygonCoords, vv, vv2, vv3);

        // Mashups without sub_buildings render white boxes;
        // generate plans locally through the backend-algorithm port TestSubBuildingLayout.
        // Tests treat footprints directly as parcels.
        if (hasSubBuildings)
        {
            buildingData.sub_buildings = TestSubBuildingLayout.Generate(
                variant, ToVector2Polygon(polygonCoords), floors, modelType == Constants.MODEL_OPEN_PIT);
            if (buildingData.sub_buildings == null)
            {
                Debug.LogWarning($"[MeshTest] variant={variant} は subbuilding_master に棟定義が無いため、サブ建物なしで生成します。");
            }
            else
            {
                Debug.Log($"[MeshTest] マッシュアップ配置計画を生成: {buildingData.sub_buildings.Count}棟");
            }
        }

        // Inject dummy coastline before port generation so straight quay clipping works.
        // Clean up previous dummy coastlines for other buildings (Version increments, invalidating quay caches).
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

    /// <summary>Convert footprint polygons (arrays of [x,z]) to Vector2 arrays.</summary>
    private static Vector2[] ToVector2Polygon(float[][] coords)
    {
        var polygon = new Vector2[coords.Length];
        for (int i = 0; i < coords.Length; i++)
        {
            polygon[i] = new Vector2(coords[i][0], coords[i][1]);
        }
        return polygon;
    }

    // Dummy coastline: sea lies +Z of diagonal z = _coastBaseZ + COAST_SLOPE * x across footprint.
    // Slope helps expose implementations that clip correctly only along axis-parallel coastlines.
    private const float COAST_SLOPE = 0.08f;
    // Sea/land panel colors. Solid colors only to clarify spatial relationships.
    private static readonly Color SEA_PANEL_COLOR = new Color(0.25f, 0.5f, 0.7f);
    private static readonly Color LAND_PANEL_COLOR = new Color(0.45f, 0.5f, 0.42f);
    private bool _dummyCoastlineActive;
    private float _coastBaseZ;

    /// <summary>
    /// Inject port-test dummy coastline into CoastlineStore.
    /// Land is a huge rectangular ring whose top edge is the coastline; place it at 30% of footprint depth
    /// so it crosses the origin-centered footprint (-Z land, +Z sea).
    /// Gameplay TerrainManager.FetchCoastline obtains/stores backend coastline;
    /// tests substitute through the same SetFromGeoJson.
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

    /// <summary>Remove injected coastline and restore CoastlineStore to unloaded state.</summary>
    private void ClearDummyCoastline()
    {
        if (_dummyCoastlineActive)
        {
            CoastlineStore.Clear();
            _dummyCoastlineActive = false;
        }
    }

    /// <summary>
    /// Show dummy coastline with blue plane seaward (+Z) and green-gray plane landward (-Z).
    /// They meet at coastline without overlap (avoid Z-fighting). Terrain/sea are both zero-height
    /// in tests; sink both planes slightly to avoid quay-slab interference.
    /// </summary>
    private void CreateSeaAndLandVisual(GameObject container)
    {
        const float extent = 120f;  // Extent along coastline (±X).
        const float span = 150f;    // Sea/land depth per side.
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

    /// <summary>Create a solid-color four-vertex plane (CCW, upward) under container.</summary>
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
    /// Create a solid-color material. Built-in Standard renders magenta in this URP project,
    /// so prefer URP Lit
    /// (same approach as WaterSurfaceManager.ResolveWaterMaterial).
    /// Set URP _BaseColor and built-in _Color only where present.
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
    /// Generate test polygon coordinates by shape type.
    /// Coordinates are two-element [x,z] arrays centered at origin.
    /// </summary>
    private float[][] GeneratePolygonCoords(float width, float height, int shapeType)
    {
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        switch (shapeType)
        {
            case 1: // Triangle.
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { 0f, halfH }
                };
            case 2: // Trapezoid (isosceles with longer bottom base).
                float topHalfW = halfW * 0.5f;
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { topHalfW, halfH },
                    new float[] { -topHalfW, halfH }
                };
            case 3: // Pentagon (regular pentagon scaled to reference size).
                // Place regular-pentagon vertices scaled to reference width/height bounds.
                // Normalize to CCW like other shapes to avoid inverted faces.
                return new float[][]
                {
                    new float[] { -0.618034f * halfW, -halfH },
                    new float[] { 0.618034f * halfW, -halfH },
                    new float[] { halfW, 0.236068f * halfH },
                    new float[] { 0f, halfH },
                    new float[] { -halfW, 0.236068f * halfH }
                };
            default: // Rectangle.
                return new float[][]
                {
                    new float[] { -halfW, -halfH },
                    new float[] { halfW, -halfH },
                    new float[] { halfW, halfH },
                    new float[] { -halfW, halfH }
                };
        }
    }

    // Showcase sample grid spacing (m), arranged in three columns/two rows.
    // Use a grid rather than one row to limit X spread,
    // fitting within maximum camera zoom distance (200m in scene settings).
    private const float SAMPLE_SPACING_X = 78f;
    private const float SAMPLE_SPACING_Z = 95f;
    // Spline-point world Y for roads resting on ground (Y=0).
    // This scene has no terrain; assume ground height=0 and use normal-road
    // ROAD_SPLINE_OFFSET lift as surface reference (root AGENTS.md Y-coordinate conventions).
    private const float GROUND_Y = RoadPartHeights.ROAD_SPLINE_OFFSET;

    /// <summary>
    /// Arrange road variations using the selected variant in a grid: straight/curved/cross intersections,
    /// diagonal merges (railway-switch style), underground entrances, and elevated paths.
    ///
    /// Without terrain in this test scene, ground height is uniformly zero (Y=0).
    /// Relative Y=0 renders near surface (Y≈0.1); positive elevates, negative goes underground.
    /// Generate each sample around origin, then translate its parent container to the grid cell.
    /// </summary>
    private void GeneratePath()
    {
        if (pathPanel == null) return;

        int variant = pathPanel.SelectedPathVariant;
        int type = GetPathType(variant);
        int lineCount = Mathf.Clamp(pathPanel.SelectedLineCount, 1, 4);
        float width = GetPathWidth(variant);
        Debug.Log($"[MeshTest] 道路ショーケース生成: variant={variant}, type={type}, lineCount={lineCount}, width={width}");

        // Inject neighbor providers for track-variant joined rendering (TrackAdjacency).
        // Gameplay PathManager normally sets them; this scene lacks PathManager,
        // so inject a local provider using _siblingTracks.
        TrackAdjacency.NeighborProvider = ProvideSiblingNeighbors;

        var showcase = new GameObject("TestPathShowcase");
        _generatedObject = showcase;

        // Grid (column,row). Straight/curved/underground/elevated/double-track merge/diagonal merge samples place
        // the selected count of parallel paths. Cross intersections use single track because point-node
        // modeling cannot represent four-way double-track crossings without overlapping opposite arms.
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

            // Generate samples around origin and move whole containers to cells.
            var cell = new GameObject($"Sample_{i}");
            cell.transform.SetParent(showcase.transform, false);
            // Reset per sample, preventing leaked double-track settings and incorrect joins.
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

        // Frame cameras from manually constructed known layout bounds (grid extent),
        // remaining valid even if renderer generation is deferred.
        float halfSample = 45f;
        var min = new Vector3(-halfSample, -8f, -halfSample);
        var max = new Vector3((columns - 1) * SAMPLE_SPACING_X + halfSample, 10f, (rows - 1) * SAMPLE_SPACING_Z + halfSample);
        var bounds = new Bounds((min + max) * 0.5f, max - min);
        PositionCamera(bounds);
    }

    /// <summary>Create [x,relativeY,z] path coordinates (List&lt;object&gt;).</summary>
    private static List<object> Pt(float x, float relY, float z)
    {
        return new List<object> { x, relY, z };
    }

    /// <summary>Create a test PathSummary stub.</summary>
    private static PathSummary MakePath(int variant, int type, int pathId, bool curved)
    {
        return new PathSummary
        {
            path_id = pathId,
            path_type = type,
            path_variant = variant,
            width = 4f,
            curved = curved,
            follow_terrain = Constants.VALUE_NONE, // Explicitly mark unset because int default 0 means non-following.
        };
    }

    /// <summary>
    /// Generate road meshes from world points and parent generated objects under container.
    /// Generated meshes use origin-relative world coordinates,
    /// so parent with worldPositionStays=true.
    /// Return the rendering container with SplineComputer for callers needing post-generation access,
    /// such as extra pillar placement; null on failure.
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

    /// <summary>Get selected variant full width (m) from master data; default 4m if unavailable.</summary>
    private float GetPathWidth(int variant)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.width > 0f) return master.width;
        }
        return 4f;
    }

    // Base path_id for parallel lines. Line k uses PARALLEL_ID_BASE + k.
    // Unique IDs are required to exclude self from nearby-track candidates.
    private const int PARALLEL_ID_BASE = 100;

    /// <summary>
    /// Replicate reference points into lineCount centered lateral copies.
    /// Compute horizontal right perpendicular to path progression per point, preserving parallelism
    /// on curves/elevated/underground profiles. spacing is neighboring centerline distance.
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
            // Centering: even counts symmetric across centerline; odd counts include a center line.
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

    /// <summary>Whether path_type is railway/monorail; determines double-track direction assignment.</summary>
    private static bool IsTrackType(int type)
    {
        return type == Constants.PATH_TRACK || type == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// Return line travel direction (Constants.DIR_*), currently fixed left-hand traffic.
    /// Same as UI RailVariantSelector (railBuild.defaultTravelDirections):
    /// left half (lower indices) forward, right half backward; odd center bidirectional.
    /// Index 0 is leftmost (minimum offset). One line is necessarily bidirectional.
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
            // Left-hand traffic: left-of-center forward, right backward.
            dirs[i] = (i < mid) ? Constants.DIR_FORWARD : Constants.DIR_BACKWARD;
        }
        return dirs;
    }

    /// <summary>
    /// Orient each line to travel direction. Reverse points only for DIR_BACKWARD;
    /// forward=start→end. Forward/bidirectional (single or odd center) retain original orientation.
    /// Internal double track uses two one-way single paths; reverse the backward one to pair directions.
    /// Apply only to PATH_TRACK/PATH_MONORAIL, leaving roads unchanged.
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
    /// Convert parallel lines into TrackAdjacency neighbor candidates.
    /// Unique path_id per line (PARALLEL_ID_BASE + k), matching GenerateLinePiece.
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
    /// TrackAdjacency.NeighborProvider implementation. Return current sample parallel lines
    /// except the requesting path (exclude self).
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
    /// Shared placement of lineCount parallel copies.
    /// For track variants, supply neighbors to enable joined ballast/pole rendering.
    /// withPillars=true adds elevated posts (elevated samples).
    /// </summary>
    private void GenerateParallelSample(GameObject container, int variant, int type, int lineCount, float width, List<Vector3> basePoints, bool curved, bool withPillars, string baseName)
    {
        // Spacing equals full line width (neighbor beds/surfaces touch; tracks render as joined corridors).
        List<List<Vector3>> sets = BuildParallelPointSets(basePoints, lineCount, width);
        // Pair track directions across sides (reverse backward points). Do not reorient roads.
        ApplyTravelDirections(sets, type);

        // Prepare all neighbor data first; providers read it during joined generation.
        _siblingTracks = sets.Count > 1 ? BuildSiblingInfos(sets, width) : null;

        for (int k = 0; k < sets.Count; k++)
        {
            int pathId = PARALLEL_ID_BASE + k;
            GenerateLinePiece(container, variant, type, pathId, curved, sets[k], withPillars, $"{baseName}_{k}");
        }
    }

    /// <summary>
    /// Generate one line and optional elevated posts.
    ///
    /// For withPillars=true, also assign points to start_point/end_point/waypoints
    /// in mesh path data. This preserves approach geometry as straight anchor spans
    /// for single_span variants (girder bridges, no terrain conformity).
    ///
    /// Only one source places posts to avoid duplicates:
    ///   - Variants with master post settings (height_editable and pillar_spacing):
    ///     RoadPath CreateRoadParts automatically uses anchors.
    ///   - Without post settings: directly call RoadPath.GeneratePillars here
    ///     to show posts in elevated tests regardless of master setup.
    /// </summary>
    private void GenerateLinePiece(GameObject container, int variant, int type, int pathId, bool curved, List<Vector3> points, bool withPillars, string name)
    {
        PathSummary meshPath = MakePath(variant, type, pathId, curved);

        float pillarSpacing = GetPillarSpacing(variant);
        if (withPillars)
        {
            // Use sparse post anchors reduced at pillar-spacing intervals, not every point.
            // Prevents clustered posts for dense samples such as converging tracks.
            // These anchors serve both (a) automatic RoadPath post placement with configured variants and
            // (b) single_span approaches (straight spans between anchors).
            List<Vector3> pillarAnchors = BuildSparseAnchors(points, pillarSpacing);
            ApplyAnchorsFromPoints(meshPath, pillarAnchors);
        }

        GameObject genObj = GenerateRoadPiece(container, meshPath, points, name);

        // Explicitly place posts when master settings do not make RoadPath generate them.
        // Configured variants already generate from sparse anchors; omit here to avoid duplication.
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
    /// Assign points as path start_point/end_point/waypoints.
    /// Relative Y = worldY − ROAD_SPLINE_OFFSET (test terrain elevation=0).
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
    /// Return sparse anchors every spacing of arc length, always including endpoints.
    /// Used to derive suitable post-placement anchors from dense curves.
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

    /// <summary>Post spacing (m), preferring master pillar_spacing, otherwise 12m.</summary>
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
    /// Whether master settings enable automatic posts (height_editable and
    /// pillar_spacing &gt; 0), matching RoadPath conditions.
    /// </summary>
    private bool VariantAutoGeneratesPillars(int variant)
    {
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized) return false;
        var master = MasterDataStore.Instance.GetPathMaster(variant);
        return master != null
            && master.height_editable.HasValue && master.height_editable.Value
            && master.pillar_spacing.HasValue && master.pillar_spacing.Value > 0f;
    }

    /// <summary>Straight surface road, with selected parallel-line count.</summary>
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

    /// <summary>Curved surface road, sampling cubic Bézier S curves with selected parallel-line count.</summary>
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
    /// Cross intersection: four roads at 90°.
    /// Point-node modeling cannot represent four-way double-track crossings without overlap (offset opposite arms
    /// overlap), so use single track regardless of count. Use diagonal/double-track merges for join validation.
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
    /// Seamless diagonal merge, shaped like railway switches.
    /// Three-arm Y: one shallow branch from east-west main line.
    /// Track variants lay connectors for pairs satisfying deflection ≤45°.
    ///
    /// Arrange parallel Y junctions along Z, perpendicular to main line, to inspect double-track switches
    /// whose main lines and branches each join into continuous corridors.
    /// </summary>
    private void GenerateMergeIntersectionSample(GameObject container, int variant, int type, int lineCount)
    {
        // Main line: west (-X)/east (+X). Branch tilts +25° eastward.
        float rad = 25f * Mathf.Deg2Rad;
        var arms = new (Vector2 dir, float length)[]
        {
            (new Vector2(-1f, 0f), 40f),
            (new Vector2( 1f, 0f), 40f),
            (new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), 40f),
        };
        // Arrange copies along Z perpendicular to east-west/X main line.
        GenerateIntersectionSample(container, variant, type, arms, lineCount, new Vector2(0f, 1f));
        CreateLabel(container, "斜め融合 Points", new Vector3(0f, 3f, 45f));
    }

    /// <summary>
    /// Shared creation of a node/intersecting roads.
    /// Self-contained test reproduction of PathManager.UpdateNodeIntersectionMesh:
    ///   1. Create each arm as PathSummary with endpoint at node.
    ///   2. Use NodeMeshFactory for intersection mesh/cuts/sidewalk and rail connectors.
    ///   3. Instantiate intersection/sidewalks/rails.
    ///   4. Draw each road trimmed to connection points.
    /// nodeRelativeY is always zero (surface intersection).
    ///
    /// With lineCount &gt; 1, make lineCount parallel copies offset by full line width
    /// along lateralDir. Same-direction arms run nearby/parallel,
    /// joining track variants into continuous corridors. Arm joining requires all arm point lists,
    /// so instantiate node meshes first, collect points, then draw all arms together.
    /// </summary>
    private void GenerateIntersectionSample(GameObject container, int variant, int type, (Vector2 dir, float length)[] arms, int lineCount, Vector2 lateralDir)
    {
        lineCount = Mathf.Clamp(lineCount, 1, 4);
        float width = GetPathWidth(variant);
        Vector2 lat = lateralDir.sqrMagnitude > 1e-6f ? lateralDir.normalized : new Vector2(0f, 1f);

        // Buffer parallel-copy arm points; draw after _siblingTracks neighbor data is finalized.
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
                path.end_node_id = 20000 + c * 100 + i; // Unique endpoint ID outside the node (only one end connects).
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

            // Intersection mesh body uses node-local coordinates, so place at node.
            // Node meshes/sidewalks/rails do not depend on joining tests; instantiate immediately.
            var nodeGO = new GameObject($"Intersection_{c}");
            nodeGO.transform.position = nodePos;
            var mf = nodeGO.AddComponent<MeshFilter>();
            mf.mesh = res.Mesh;
            var mr = nodeGO.AddComponent<MeshRenderer>();
            if (res.Material != null) mr.material = res.Material;

            // Sidewalk connectors (bend ordinary sidewalk prefabs).
            if (res.SidewalkConnectors != null && res.SidewalkConnectors.Count > 0)
            {
                var sidewalkGO = new GameObject("Sidewalk");
                sidewalkGO.transform.SetParent(nodeGO.transform, false);
                for (int i = 0; i < res.SidewalkConnectors.Count; i++)
                {
                    RoadPath.CreateSidewalkConnectorObject(sidewalkGO.transform, res.SidewalkConnectors[i], i);
                }
            }

            // Rail connectors (track switches/at-grade crossings).
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

            // Trim arm point lists to connections (intersection edges) and buffer for later rendering.
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
                    // Draw arms from node if no connection point was calculated.
                    start = new Vector3(nodePos.x, GROUND_Y, nodePos.z);
                }
                Vector3 outer = new Vector3(nodePos.x + dir.x * len, GROUND_Y, nodePos.z + dir.y * len);

                var points = new List<Vector3> { start, outer };
                armPieces.Add((path, points, $"Arm_{c}_{i}"));
                siblings.Add(new TrackNeighborInfo { PathId = path.path_id, Points = points, Width = width });
            }
        }

        // Enable same-direction-arm double-track joins only with multiple parallel copies.
        _siblingTracks = lineCount > 1 ? siblings : null;

        foreach (var piece in armPieces)
        {
            GenerateRoadPiece(container, piece.path, piece.points, piece.name);
        }
    }

    /// <summary>
    /// Double-track merge (elevated bridge). Show separated single tracks converging into parallel double track,
    /// testing joined rendering in parallel sections rather than crossing tracks.
    /// Converge over horizontal elevated girders (relative Y=8m) with posts for bridge-merge validation.
    ///
    /// Selected line count starts widely spaced at z=-40, converges to full-line-width spacing by z=0,
    /// then stays parallel. Four lines converge into four adjacent tracks.
    /// </summary>
    private void GenerateConvergingSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        lineCount = Mathf.Clamp(lineCount, 1, 4);
        const float deckRelY = 8f;
        float deckY = deckRelY + RoadPartHeights.ROAD_SPLINE_OFFSET;

        // Initial spacing well exceeds TRACK_MERGE_MAX_EDGE_GAP,
        // making the unjoined→joined transition visible during convergence.
        float startSpacing = width + 10f;
        float endSpacing = width; // Parallel lines are adjacent (beds touch).

        // Sample z finely, smoothly interpolating each lateral position from start→end.
        var sets = new List<List<Vector3>>(lineCount);
        for (int k = 0; k < lineCount; k++)
        {
            sets.Add(new List<Vector3>());
        }
        for (float z = -40f; z <= 40.001f; z += 4f)
        {
            // Convergence t=0 at z=-40 (widest), 1 from z=0 onward (parallel), smoothed by smoothstep.
            float raw = Mathf.Clamp01((z + 40f) / 40f);
            float t = raw * raw * (3f - 2f * raw);
            float spacing = Mathf.Lerp(startSpacing, endSpacing, t);
            for (int k = 0; k < lineCount; k++)
            {
                float centered = k - (lineCount - 1) * 0.5f;
                sets[k].Add(new Vector3(centered * spacing, deckY, z));
            }
        }

        // Pair track directions (reverse backward points). Do not reorient roads.
        ApplyTravelDirections(sets, type);

        // Supply all neighbor data for parallel-section joins.
        _siblingTracks = lineCount > 1 ? BuildSiblingInfos(sets, width) : null;

        for (int k = 0; k < sets.Count; k++)
        {
            GenerateLinePiece(container, variant, type, PARALLEL_ID_BASE + k, curved: true, sets[k], withPillars: true, $"07_Converge_{k}");
        }

        CreateLabel(container, "複線合流 Converge", new Vector3(0f, 11f, 45f));
    }

    /// <summary>
    /// Underground tunnel entrance. One smooth surface-to-underground descent uses
    /// the same section classification as normal PathManager.GenerateSectionedPathMesh rendering:
    /// TunnelSectionUtil.SplitDrawableRuns plus portal placement.
    /// Tunnel sections deeper than clearance omit meshes; place portal parts at boundary mouths.
    ///
    /// Show one entrance, not descent at both ends. Densely sampled intermediate points
    /// gradually steepen grade, avoiding angular bends.
    /// Place selected parallel-line count at full-width spacing,
    /// supplying neighbor data in cuttings for joined roadbed rendering.
    /// </summary>
    private void GenerateUndergroundSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        // (z,relativeY) profile descends once from foreground (-z) surface to -8m,
        // then ends underground. Its single depth/clearance crossing is the only portal.
        var profile = new (float z, float y)[]
        {
            (-45f, 0f),
            (-38f, 0f),
            (-30f, -1f),   // Begin a gentle grade.
            (-20f, -4f),
            (-10f, -7f),
            (  0f, -8f),
            ( 20f, -8f),
            ( 45f, -8f),   // End underground (no opposite portal).
        };
        var basePoints = new List<Vector3>(profile.Length);
        foreach (var (z, y) in profile)
        {
            // worldY=terrain elevation (0)+relativeY+ROAD_SPLINE_OFFSET. Surface relativeY=0 matches GROUND_Y.
            basePoints.Add(new Vector3(0f, y + RoadPartHeights.ROAD_SPLINE_OFFSET, z));
        }

        float clearance = TunnelSectionUtil.GetClearanceHeight(variant);

        List<List<Vector3>> sets = BuildParallelPointSets(basePoints, Mathf.Clamp(lineCount, 1, 4), width);
        // Pair track directions (reverse backward points). Do not reorient roads.
        ApplyTravelDirections(sets, type);

        // Supply all neighbor data for bed joins in visible cuttings.
        // Joining requires XZ/Y proximity; unrendered tunnels have no rendering points and cannot join.
        // Each run uses the same path_id (PARALLEL_ID_BASE + k), excluding self-line joins.
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
    /// Place portal parts at tunnel mouths.
    /// Resolve Models/Paths/p{variant}/portal → p{type}/portal;
    /// omit if absent (mesh ends indicate portals in scenes without models).
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
    /// Elevated road: central girder at relative Y=8m, approaches descend to surface at both ends.
    /// Posts (pillar/pillar_cap) support elevated sections, shorten downhill,
    /// and disappear at ground. Selected parallel-line count supports double-track bridges.
    ///
    /// GenerateLinePiece handles posts through RoadPath for configured variants,
    /// otherwise direct RoadPath.GeneratePillars calls; only one source places them.
    /// </summary>
    private void GenerateElevatedSample(GameObject container, int variant, int type, int lineCount, float width)
    {
        const float deckRelY = 8f;
        // (z,relativeY). Rise gently from surface at both ends to a level central elevated girder.
        var profile = new (float z, float y)[]
        {
            (-45f, 0f),
            (-40f, 0f),
            (-20f, deckRelY),   // Approach reaches the elevated section.
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
    /// Sample cubic Bézier curves about every interval meters.
    /// No terrain in tests; fix Y to forcedY.
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
    /// Add simple world-space labels identifying samples.
    /// Omit if Unity built-in fonts are unavailable (optional display).
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
        // Dummy coastline exists only while displaying port samples (reinjected for subsequent port generation).
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
    /// Frame generated bounds from an oblique overhead angle.
    /// Use the same <see cref="CameraController"/> as gameplay;
    /// only set the initial view once via SetView here. Subsequent movement/rotation/zoom
    /// use normal CameraController controls.
    /// </summary>
    private void PositionCamera(Bounds bounds)
    {
        var camCtrl = CameraController.Instance;
        if (camCtrl == null) return;

        Vector3 center = bounds.center;
        float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        float distance = Mathf.Max(maxExtent * 2.5f, 5f);

        // Look at generated center from +Z above, at 40° elevation.
        float pitch = 40f * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(0f, Mathf.Sin(pitch), Mathf.Cos(pitch));
        Vector3 camPos = center + dir * distance;
        camPos.y = Mathf.Max(camPos.y, 1f);
        Quaternion rot = Quaternion.LookRotation(center - camPos, Vector3.up);

        camCtrl.SetView(camPos, rot);
    }
}
