using UnityEngine;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using GeneratedModels;

/// <summary>
/// Container for building mesh-generation results.
/// </summary>
public class BuildingMeshResult
{
    public Mesh Mesh { get; }
    public Material[] Materials { get; }
    public Mesh ColliderMesh { get; }
    public List<BuildingExteriorRenderer.Build> ExteriorJobs { get; set; }
    /// <summary>Scatter parts left out of Mesh for <see cref="BuildingScatterRenderer"/> (generation with separateExterior only).</summary>
    public List<BuildingScatterRenderer.Instance> Scatter { get; set; }
    public ConstructionCraneAnimator.Placement CraneTop { get; set; }

    public BuildingMeshResult(Mesh mesh, Material[] materials, Mesh colliderMesh = null)
    {
        Mesh = mesh;
        Materials = materials;
        ColliderMesh = colliderMesh;
    }
}

/// <summary>
/// Building mesh-generator interface.
/// </summary>
public interface IBuildingMeshGenerator
{
    /// <param name="isDynamic">
    /// Whether to generate for an ultra-near (Dynamic) LOD cell.
    /// True deforms fences/ground panels to follow terrain.
    /// Caller (BuildingManager) determines/passes the tier. Generators must not consult the tier table
    /// (it may still be stale during LOD loading).
    /// </param>
    BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false);
}

/// <summary>
/// Parcel mesh-generator interface.
/// </summary>
public interface IParcelMeshGenerator
{
    /// <summary>
    /// Generate/return a mesh from parcel vertices, center, and thickness.
    /// </summary>
    /// <param name="topVertexLocalY">
    /// Per-vertex local top Y offsets (meters). Null preserves Y=0 for every vertex.
    /// To follow slopes, pass each vertex's ground height minus centroid ground height.
    /// </param>
    Mesh Generate(Vector2[] points, Vector2 centroid, float thickness, float[] topVertexLocalY);
}

/// <summary>
/// Example factory generating meshes from parcel local vertices, etc.
/// </summary>
public static class ParcelMeshFactory
{
    // Mapping table for parcel generators.
    // All usages currently use standard polygon mesh generation,
    // so only default is registered.
    private static Dictionary<string, IParcelMeshGenerator> meshGenerators
        = new Dictionary<string, IParcelMeshGenerator>()
        {
            { "default", new DefaultParcel() },
        };

    /// <summary>
    /// Generate/return parcel mesh from vertices (world/local), thickness, etc.
    /// </summary>
    /// <param name="points">Parcel vertices (convex/concave supported, with care).</param>
    /// <param name="centroid">Center (for local conversion).</param>
    /// <param name="thickness">Extrusion thickness.</param>
    /// <param name="usageId">Parcel building-usage ID (vacant land uses VALUE_NONE=-1).</param>
    public static Mesh GenerateParcelMesh(Vector2[] points, Vector2 centroid, float thickness, int usageId, float[] topVertexLocalY = null)
    {
        if (points == null || points.Length < 3)
        {
            Debug.LogError("ParcelMeshFactory.GenerateMesh: points が null または頂点数が3未満です。");
            return null;
        }

        // Get generator by usage ID; use default when unregistered.
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
    /// Start-node sidewalk setback (endcaps where crossing roads lack sidewalks;
    /// see NodeMeshResult.SidewalkSetbacks). Generators without sidewalks may ignore it.
    /// </param>
    /// <param name="endSidewalkSetback">End-node sidewalk setback.</param>
    PathMeshResult Generate(GeneratedModels.PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default);
}

/// <summary>
/// Factory generating road/track/etc. meshes from PathSummary.
///
/// Select generators by path_master model_type:
///   "default" → RoadPath, "track" → TrackPath, "station_house" → StationHousePath, "pier" → PierPath,
///   "outline" → OutlinePath, "none" → no mesh generation.
/// </summary>
public static class PathMeshFactory
{
    // Power lines have per-span sag/shared towers, separate from repeated road-part placement.
    private static Dictionary<int, IPathMeshGenerator> variantGenerators
        = new Dictionary<int, IPathMeshGenerator>()
        {
            { Constants.PATH_VARIANT_GRID, new PowerLinePath() },
            { 1102, new PowerLinePath() },
        };

    // model_type → generator mapping.
    private static readonly RoadPath roadGenerator = new RoadPath();
    private static readonly TrackPath trackGenerator = new TrackPath();
    private static readonly StationHousePath stationHouseGenerator = new StationHousePath();
    private static readonly PierPath pierGenerator = new PierPath();
    private static readonly OutlinePath outlineGenerator = new OutlinePath();

    public static PathMeshResult GeneratePathMesh(GeneratedModels.PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (pathData == null)
        {
            Debug.LogWarning("[PathMeshFactory] pathData is null.");
            return null;
        }

        // Variant-specific generators take highest priority.
        if (variantGenerators.TryGetValue(pathData.path_variant, out IPathMeshGenerator variantGen))
        {
            return variantGen.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
        }

        // Select generator by model_type.
        string modelType = GetModelType(pathData);

        switch (modelType)
        {
            case "track":
                return trackGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "station_house":
                // Station-house path (footbridge + terminal buildings). Receives clicks as PathObject, like other paths.
                return stationHouseGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "pier":
                return pierGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "outline":
                return outlineGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
            case "none":
                // No mesh generation (e.g. pathfinding-only paths).
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathMeshFactory] model_type=none: メッシュ生成スキップ (variant={pathData.path_variant})");
                return null;
            case "default":
            default:
                return roadGenerator.Generate(pathData, points, startSidewalkSetback, endSidewalkSetback);
        }
    }

    /// <summary>
    /// Get path_master model_type.
    /// Use "default" (road mesh) for unloaded master data, unknown variants, and buried infrastructure without underground models.
    /// Public because PathManager also uses it to identify paths never drawn even during construction (model_type=none).
    /// </summary>
    public static string GetModelType(GeneratedModels.PathSummary pathData)
    {
        var master = MasterDataStore.Instance != null
            ? MasterDataStore.Instance.GetPathMaster(pathData.path_variant)
            : null;

        if (master != null && !string.IsNullOrEmpty(master.model_type))
        {
            if (master.model_type == "none" && UndergroundView.IsActive && InfraNetworkUtil.IsInfraPathType(pathData.path_type)
                && TunnelSectionUtil.IsUndergroundPath(pathData))
                return "default";
            return master.model_type;
        }
        return "default";
    }

    /// <summary>
    /// Generate a strip mesh from a point list (e.g. for colliders).
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
    /// Node height relative to ground. Zero creates a surface intersection with vertices following terrain.
    /// Nonzero (e.g. at-grade intersections of elevated paths) skips terrain sampling,
    /// creating a horizontal intersection at nodePosition.y (absolute height).
    /// </param>
    NodeMeshResult Generate(int nodeId, Vector3 nodePosition, List<GeneratedModels.PathSummary> paths, float nodeRelativeY = 0f);
}

/// <summary>
/// Node (intersection) mesh result.
/// </summary>
public class NodeMeshResult
{
    public Mesh Mesh { get; }
    public Material Material { get; }
    public List<SidewalkConnectorSpec> SidewalkConnectors { get; }

    /// <summary>
    /// Rail geometry within track nodes (switches/at-grade crossings).
    /// Connect track ends selected by travel direction for each movement satisfying turn-angle constraints and direction rules.
    /// TrackPath.CreateRailConnectorObject instantiates it.
    /// </summary>
    public List<RailConnectorSpec> RailConnectors { get; }

    public Dictionary<int, Vector3> ConnectionPoints { get; }
    public MotorwayMergeLayout MotorwayMerge { get; set; }

    /// <summary>Straight connection of same-type/same-width roads. No plate; draw both paths to the node.</summary>
    public RoadContinuousJoin RoadJoin { get; set; }

    /// <summary>Straight track connection to the node without an intersection plate (TrackIntersectionMeshGenerator.TryContinuousJoin). Null if absent.</summary>
    public TrackContinuousJoin TrackJoin { get; set; }

    /// <summary>Node containing only tracks without beds (monorail, etc.). No plate (Mesh); only rail connectors and attached parts.</summary>
    public bool RailsOnly { get; set; }
    public RoadMarkingPlan Markings { get; set; }

    /// <summary>Intersection traffic signals (TrafficSignalLayout). Null where omitted. Instantiated by TrafficSignalBuilder.Apply.</summary>
    public List<TrafficSignalPlacement> TrafficSignals { get; set; }

    /// <summary>
    /// path_id → sidewalk setbacks. Used to terminate sidewalks of roads crossing sidewalk-free roads
    /// farther from the node than the roadway cut (ConnectionPoint).
    /// Roadway cut positions stay unchanged; only straight sidewalk parts shorten,
    /// leaving strips filled with rounded endcaps by the intersection generator.
    /// Keys match ConnectionPoints path_id. Missing entries mean no setback.
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
/// Left/right sidewalk setbacks (meters) for one road at an intersection.
/// Left/right are viewed outward from the node (node frame).
/// These may reverse relative to spline left/right depending on whether this is the start/end node;
/// the consumer (RoadPath.CreateRoadParts) maps them. Zero means no setback.
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
/// Geometry of one rail connector inside an intersection.
/// Points are node-local (Y includes reference plane + TRACK_ABOVE_SPLINE),
/// smoothly joining two track ends of an allowed movement (track centers at cuts) along endpoint tangents.
/// Rails use actual-size placement (no width scaling), so unlike sidewalk connectors, no width array is needed.
/// </summary>
public class RailConnectorSpec
{
    public bool EmbeddedLrt;
    /// <summary>Add overhead wire only when both connected tracks are electrified.</summary>
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
/// Node (intersection) mesh-generation factory.
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
