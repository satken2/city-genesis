using UnityEngine;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using GeneratedModels;

/// <summary>
/// 建物メッシュ生成結果の入れ物
/// </summary>
public class BuildingMeshResult
{
    public Mesh Mesh { get; }
    public Material[] Materials { get; }
    public Mesh ColliderMesh { get; }
    public List<BuildingExteriorRenderer.Build> ExteriorJobs { get; set; }
    public ConstructionCraneAnimator.Placement CraneTop { get; set; }

    public BuildingMeshResult(Mesh mesh, Material[] materials, Mesh colliderMesh = null)
    {
        Mesh = mesh;
        Materials = materials;
        ColliderMesh = colliderMesh;
    }
}

/// <summary>
/// 建物メッシュ生成器インターフェース
/// </summary>
public interface IBuildingMeshGenerator
{
    /// <param name="isDynamic">
    /// 超近景（Dynamic）LODセルの建物として生成するかどうか。
    /// true の場合、フェンスと地面パネルを地形に追従させて変形する。
    /// 呼び出し側（BuildingManager）がティアを確定して渡す。生成器側でティア表を参照してはいけない
    /// （LODロード中はティア表が未更新のため正しい値が取れない）。
    /// </param>
    BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false);
}

/// <summary>
/// パーセルメッシュ生成器インターフェース
/// </summary>
public interface IParcelMeshGenerator
{
    /// <summary>
    /// 指定されたパーセルの頂点、中心、厚みからメッシュを生成して返す
    /// </summary>
    /// <param name="topVertexLocalY">
    /// 各頂点の上面におけるローカルYオフセット（メートル）。null のときは従来どおり全頂点Y=0。
    /// 地形の傾斜に沿わせる場合、頂点ごとの地表高さから重心位置の地表高さを差し引いた配列を渡す。
    /// </param>
    Mesh Generate(Vector2[] points, Vector2 centroid, float thickness, float[] topVertexLocalY);
}

/// <summary>
/// 土地区画の頂点座標(ローカル)等からメッシュを生成するファクトリクラス例
/// </summary>
public static class ParcelMeshFactory
{
    // パーセル生成処理の切り替え用マッピングテーブル。
    // 現状はすべての用途で標準のポリゴンメッシュ生成ロジックを使用するため、
    // default のみ登録されている。
    private static Dictionary<string, IParcelMeshGenerator> meshGenerators
        = new Dictionary<string, IParcelMeshGenerator>()
        {
            { "default", new DefaultParcel() },
        };

    /// <summary>
    /// パーセルの頂点(ワールドorローカル座標系)と厚みなどからメッシュを生成して返す
    /// </summary>
    /// <param name="points">パーセルの頂点座標(凸/凹でも可だが注意)</param>
    /// <param name="centroid">中心座標(ローカル化する場合)</param>
    /// <param name="thickness">押し出す厚み</param>
    /// <param name="usageId">パーセルの建物用途ID(空き地は VALUE_NONE=-1)</param>
    public static Mesh GenerateParcelMesh(Vector2[] points, Vector2 centroid, float thickness, int usageId, float[] topVertexLocalY = null)
    {
        if (points == null || points.Length < 3)
        {
            Debug.LogError("ParcelMeshFactory.GenerateMesh: points が null または頂点数が3未満です。");
            return null;
        }

        // 用途IDに応じた生成器を取得し、未登録の場合はデフォルトの生成器を使用する
        if (!meshGenerators.TryGetValue(usageId.ToString(), out IParcelMeshGenerator generator))
        {
            generator = meshGenerators["default"];
        }

        return generator.Generate(points, centroid, thickness, topVertexLocalY);
    }
}

public interface IPathMeshGenerator
{
    /// <param name="startSidewalkSetback">
    /// 始点ノード側の歩道セットバック（交差相手に歩道がない場合のエンドキャップ用。
    /// NodeMeshResult.SidewalkSetbacks 参照）。歩道を持たない生成器は無視してよい。
    /// </param>
    /// <param name="endSidewalkSetback">終点ノード側の歩道セットバック。</param>
    PathMeshResult Generate(GeneratedModels.PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default);
}

/// <summary>
/// PathSummary から道路(または線路等)のメッシュを生成するファクトリクラス。
///
/// メッシュ生成器は path_master の model_type で切り替える:
///   "default" → RoadPath, "track" → TrackPath, "station_house" → StationHousePath,
///   "none" → メッシュ生成なし。
/// </summary>
public static class PathMeshFactory
{
    // 送電線は径間ごとのたるみと共有鉄塔を持つため、道路用の反復配置から分離する。
    private static Dictionary<int, IPathMeshGenerator> variantGenerators
        = new Dictionary<int, IPathMeshGenerator>()
        {
            { Constants.PATH_VARIANT_GRID, new PowerLinePath() },
            { 1102, new PowerLinePath() },
        };

    // model_type → 生成器のマッピング
    private static readonly RoadPath roadGenerator = new RoadPath();
    private static readonly TrackPath trackGenerator = new TrackPath();
    private static readonly StationHousePath stationHouseGenerator = new StationHousePath();

    public static PathMeshResult GeneratePathMesh(GeneratedModels.PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (pathData == null)
        {
            Debug.LogWarning("[PathMeshFactory] pathData is null.");
            return null;
        }

        // variant固有の生成器があれば最優先
        if (variantGenerators.TryGetValue(pathData.path_variant, out IPathMeshGenerator variantGen))
        {
            return variantGen.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
        }

        // model_type で生成器を切り替える
        string modelType = GetModelType(pathData);

        switch (modelType)
        {
            case "track":
                return trackGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "station_house":
                // 駅舎パス（連絡橋 + 両終端の建屋）。クリックは他のパスと同じ PathObject として受ける。
                return stationHouseGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "none":
                // メッシュ生成なし（経路探索専用パス等）
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathMeshFactory] model_type=none: メッシュ生成スキップ (variant={pathData.path_variant})");
                return null;
            case "default":
            default:
                return roadGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
        }
    }

    /// <summary>
    /// path_master の model_type を取得する。
    /// マスター未ロード・未知バリアントのときだけ "default"（道路メッシュ）を返す。
    /// PathManager が「工事中でも描画しないパス(model_type=none)」の判定にも使うため public。
    /// </summary>
    public static string GetModelType(GeneratedModels.PathSummary pathData)
    {
        var master = MasterDataStore.Instance != null
            ? MasterDataStore.Instance.GetPathMaster(pathData.path_variant)
            : null;

        if (master != null && !string.IsNullOrEmpty(master.model_type))
        {
            return master.model_type;
        }
        return "default";
    }

    /// <summary>
    /// ポイントリストから帯状のメッシュを生成する（コライダー用など）
    /// </summary>
    public static Mesh BuildStripMesh(List<Vector3> points, float width)
    {
        if (points == null || points.Count < 2 || width <= 0f)
        {
            return null;
        }

        int pointCount = points.Count;
        float halfWidth = width * 0.5f;

        Vector3[] leftOffsets = new Vector3[pointCount];
        Vector3[] rightOffsets = new Vector3[pointCount];

        float[] cumulative = new float[pointCount];
        cumulative[0] = 0f;
        for (int i = 1; i < pointCount; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }

        for (int i = 0; i < pointCount; i++)
        {
            Vector3 direction = PolylineUtils.SmoothedDirection(points, i);
            Vector3 binormal = Vector3.Cross(Vector3.up, direction);
            if (binormal.sqrMagnitude < 1e-6f)
            {
                binormal = Vector3.right;
            }
            binormal.Normalize();

            Vector3 offset = binormal * halfWidth;
            leftOffsets[i] = points[i] + offset;
            rightOffsets[i] = points[i] - offset;
        }

        List<Vector3> vertices = new List<Vector3>(pointCount * 2);
        List<Vector3> normals = new List<Vector3>(pointCount * 2);
        List<Vector2> uvs = new List<Vector2>(pointCount * 2);
        List<int> triangles = new List<int>((pointCount - 1) * 6);

        float totalLength = cumulative[pointCount - 1];
        for (int i = 0; i < pointCount; i++)
        {
            vertices.Add(leftOffsets[i]);
            vertices.Add(rightOffsets[i]);

            normals.Add(Vector3.up);
            normals.Add(Vector3.up);

            float vCoord = totalLength > 0f ? cumulative[i] / totalLength : (float)i / (pointCount - 1);
            uvs.Add(new Vector2(0f, vCoord));
            uvs.Add(new Vector2(1f, vCoord));
        }

        for (int i = 0; i < pointCount - 1; i++)
        {
            int baseIndex = i * 2;
            int nextIndex = (i + 1) * 2;

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(nextIndex);

            triangles.Add(nextIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(nextIndex + 1);
        }

        Mesh mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        return mesh;
    }
}

public class PathMeshResult
{
    public Mesh Mesh { get; }
    public bool ModulesCreated { get; }
    public GameObject GeneratedObject { get; }

    public PathMeshResult(Mesh mesh, bool modulesCreated, GameObject generatedObject = null)
    {
        Mesh = mesh;
        ModulesCreated = modulesCreated;
        GeneratedObject = generatedObject;
    }
}

public interface INodeMeshGenerator
{
    /// <param name="nodeRelativeY">
    /// ノードの地面からの相対高さ。0なら地表の交差点として各頂点を地形標高に追従させる。
    /// 0以外（高架同士の平面交差など）なら地形をサンプリングせず、
    /// nodePosition.y（絶対高さ）の水平面として交差点を生成する。
    /// </param>
    NodeMeshResult Generate(int nodeId, Vector3 nodePosition, List<GeneratedModels.PathSummary> paths, float nodeRelativeY = 0f);
}

/// <summary>
/// ノード（交差点）メッシュ生成結果
/// </summary>
public class NodeMeshResult
{
    public Mesh Mesh { get; }
    public Material Material { get; }
    public List<SidewalkConnectorSpec> SidewalkConnectors { get; }

    /// <summary>
    /// 線路ノード（分岐器・平面交差）で交差点内に敷くレールの幾何情報。
    /// 転回制約（偏向角）と通行方向を満たす動きごとに、進む向きで決まる軌道端どうしを結ぶ。
    /// 実体化は TrackPath.CreateRailConnectorObject が行う。
    /// </summary>
    public List<RailConnectorSpec> RailConnectors { get; }

    public Dictionary<int, Vector3> ConnectionPoints { get; }
    public MotorwayMergeLayout MotorwayMerge { get; set; }

    /// <summary>交差点の板なしで2本の線路をノードまでつなぐ直進接続(TrackIntersectionMeshGenerator.TryContinuousJoin)。無ければ null。</summary>
    public TrackContinuousJoin TrackJoin { get; set; }

    /// <summary>道床の無い線路(モノレール等)だけのノード。板(Mesh)を作らず、レールのつなぎと付帯パーツだけを持つ。</summary>
    public bool RailsOnly { get; set; }
    public RoadMarkingPlan Markings { get; set; }

    /// <summary>交差点に置く信号機(TrafficSignalLayout)。置かない交差点は null。実体化は TrafficSignalBuilder.Apply が行う。</summary>
    public List<TrafficSignalPlacement> TrafficSignals { get; set; }

    /// <summary>
    /// path_id → 歩道のセットバック量。交差相手に歩道がない道路の歩道端を、
    /// 車道の切断面（ConnectionPoint）より道路外側へ後退させて終端するために使う。
    /// 車道のカット位置は変えず、直線側の歩道パーツだけがこの分だけ短くなり、
    /// 空いた帯に交差点生成側がエンドキャップ（丸め終端）を敷く。
    /// キーは ConnectionPoints と同じ path_id。エントリがない道路は後退なし。
    /// </summary>
    public Dictionary<int, SidewalkSetback> SidewalkSetbacks { get; }

    public NodeMeshResult(Mesh mesh, Material material, Dictionary<int, Vector3> connectionPoints = null, List<SidewalkConnectorSpec> sidewalkConnectors = null, List<RailConnectorSpec> railConnectors = null, Dictionary<int, SidewalkSetback> sidewalkSetbacks = null)
    {
        Mesh = mesh;
        Material = material;
        ConnectionPoints = connectionPoints;
        SidewalkConnectors = sidewalkConnectors;
        RailConnectors = railConnectors;
        SidewalkSetbacks = sidewalkSetbacks;
    }
}

/// <summary>
/// 交差点ノードにおける1本の道路の、左右歩道それぞれのセットバック量（メートル）。
/// 左右は「ノードから道路外側へ向かって見たとき」の左右（node-frame）。
/// 道路スプラインの始端/終端どちらのノードかによってスプライン視点の左右とは反転し得るため、
/// 消費側（RoadPath.CreateRoadParts）で写像する。0 は後退なし。
/// </summary>
public struct SidewalkSetback
{
    public float LeftOutwardSetback;
    public float RightOutwardSetback;
}

public class SidewalkConnectorSpec
{
    public string Part = "sidewalk";
    public bool Dashed;
    public List<Vector2> Directions;
    public Vector3 StartNormal = Vector3.up;
    public Vector3 EndNormal = Vector3.up;
    public Vector2 StartDirection;
    public Vector2 EndDirection;
    public int EndPrefabVariant = -1;
    public int EndFallbackType = -1;
    public List<Vector3> Points { get; }
    public List<float> Widths { get; }
    public int PrefabVariant { get; }
    public int FallbackType { get; }

    public SidewalkConnectorSpec(List<Vector3> points, List<float> widths, int prefabVariant, int fallbackType)
    {
        Points = points;
        Widths = widths;
        PrefabVariant = prefabVariant;
        FallbackType = fallbackType;
    }
}

/// <summary>
/// 交差点内に敷く1本分のレールコネクタの幾何情報。
/// Points はノード基準のローカル座標（Yは基準面 + TRACK_ABOVE_SPLINE 込み）で、
/// 通行可能な動きの2つの軌道端（切断点での軌道中心）を端点接線に沿って滑らかに結ぶ。
/// レールは実寸配置（幅の伸縮なし）のため、歩道コネクタと違い幅の配列は持たない。
/// </summary>
public class RailConnectorSpec
{
    public bool EmbeddedLrt;
    /// <summary>両端の線路が電化されている接続だけに架線を渡す。</summary>
    public bool HasWire;
    public float StartRailBase;
    public float EndRailBase;
    public float StartWireOffset;
    public float EndWireOffset;
    public float StartSlope;
    public float EndSlope;
    public Vector2 StartDirection;
    public Vector2 EndDirection;
    public List<Vector3> Points { get; }
    public int PrefabVariant { get; }
    public int FallbackType { get; }

    public RailConnectorSpec(List<Vector3> points, int prefabVariant, int fallbackType)
    {
        Points = points;
        PrefabVariant = prefabVariant;
        FallbackType = fallbackType;
    }
}

/// <summary>
/// ノード（交差点）メッシュ生成ファクトリクラス
/// </summary>
public static class NodeMeshFactory
{
    private static RoadIntersectionMeshGenerator defaultGenerator = new RoadIntersectionMeshGenerator();
    
    public static NodeMeshResult GenerateNodeMesh(int nodeId, Vector3 nodePosition, List<GeneratedModels.PathSummary> paths, float nodeRelativeY = 0f, System.Func<PathSummary, List<Vector3>> pointResolver = null, System.Func<PathSummary, float> cutLimitResolver = null)
    {
        if (paths == null || paths.Count < 2)
        {
            return null;
        }

        return defaultGenerator.GenerateWithSections(nodeId, nodePosition, paths, nodeRelativeY, pointResolver, cutLimitResolver);
    }
}
