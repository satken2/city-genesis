using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using UnityEngine;

/// <summary>
/// Landward port (MODEL_PORT) placement plan. Pure data determining facility modules (port_building),
/// handling cranes (port_crane), and yard cargo (stacked port_container or individual ground port_cargo)
/// from water edge, yard, and parcel edge 0 (road side).
///
/// All coordinates use local XZ relative to center_point. Placement depends only on standard-model dimensions and input geometry,
/// remaining unchanged across rendering LODs (close-up switching does not shift buildings/cargo).
/// Facility module placement depends only on parcel and standard model, independent of coastline/reclamation,
/// because opening indexing (<see cref="BuildingRoadAccess"/>) needs the same placement when building data is registered.
/// Overview: city-genesis/docs/ja/geometries/F1_建物メッシュの生成方式.md.
/// </summary>
public static class PortLayout
{
    /// <summary>Handling-crane spacing (multiple of actual part width). 1.0 makes neighbors touch; leave margin.</summary>
    public const float CRANE_SPACING_RATIO = 4.5f;

    /// <summary>Maximum handling cranes per port. Prevents excessive draw calls on huge quays.</summary>
    public const int MAX_CRANE_COUNT = 8;

    /// <summary>Handling roadway width behind the crane's landward end (m).</summary>
    public const float APRON_LANE = 6f;

    /// <summary>Cargo exclusion margin around facility modules/rooftop props (m).</summary>
    public const float FACILITY_CLEARANCE = 3f;

    /// <summary>Internal road width from gate (parcel edge-0 midpoint) to waterside roadway (m). No cargo here.</summary>
    public const float DRIVE_WIDTH = 12f;

    /// <summary>Yard-edge cargo setback (m).</summary>
    public const float YARD_MARGIN = 1.5f;

    /// <summary>20ft ISO container length/width/height (m). Author port_container to these dimensions.</summary>
    public const float CONTAINER_LENGTH = 6.06f;
    public const float CONTAINER_WIDTH = 2.44f;
    public const float CONTAINER_HEIGHT = 2.59f;

    /// <summary>Height per stacked tier (m), slightly above actual size for corner-fitting gaps.</summary>
    public const float CONTAINER_TIER_PITCH = CONTAINER_HEIGHT + 0.02f;

    private const float CONTAINER_GAP = 0.35f;          // Gap between neighboring containers (m).
    private const int BLOCK_BAYS = 8;                    // Containers per block along the water edge.
    private const int BLOCK_ROWS = 6;                    // Rows per block in the depth direction.
    private const float BLOCK_AISLE = 8f;                // Passage width between blocks (m).
    private const int MAX_TIERS = 4;                     // Maximum container tiers.
    private const float FILL_RATIO = 0.8f;               // Fraction of slots actually occupied.
    private const int MAX_STACKS = 800;                  // Maximum piles per port (limits vertices on huge parcels).
    private const float FOOTPRINT_TOLERANCE = 0.05f;     // Tolerance for checking facility-module fit within the parcel (m).
    private const float CARGO_GAP = 1f;                  // Gap between adjacent ground cargo items (m).
    private const float MIN_CARGO_SIZE = 0.5f;           // Minimum ground-cargo slot side length (m), preventing a collapsed grid for dimensionless parts.

    /// <summary>
    /// Target ground-cargo block size (m). Match container blocks to keep passage spacing consistent across cargo types.
    /// </summary>
    private const float CARGO_BLOCK_LENGTH = BLOCK_BAYS * (CONTAINER_LENGTH + CONTAINER_GAP);
    private const float CARGO_BLOCK_DEPTH = BLOCK_ROWS * (CONTAINER_WIDTH + CONTAINER_GAP);

    /// <summary>
    /// Yard cargo layout: slot (grid cell) size/gap, bays/rows per block, and maximum tiers.
    /// Length follows the main water edge; Width is depth (m).
    /// </summary>
    public struct CargoSpec
    {
        public float Length;
        public float Width;
        public float Gap;
        public int Bays;
        public int Rows;
        public int MaxTiers;
    }

    /// <summary>Stacked 20ft container layout (port_container).</summary>
    public static readonly CargoSpec ContainerSpec = new CargoSpec
    {
        Length = CONTAINER_LENGTH, Width = CONTAINER_WIDTH, Gap = CONTAINER_GAP, Bays = BLOCK_BAYS, Rows = BLOCK_ROWS, MaxTiers = MAX_TIERS,
    };

    /// <summary>
    /// Ground cargo layout (port_cargo: bulk piles/tanks/fish boxes, etc.). bounds is the union of part bounds (local +Z along water edge).
    /// Slots symmetrically expand this rectangle around origin (prevent neighboring overlaps even with off-center origins); no stacking.
    /// </summary>
    public static CargoSpec GroundCargoSpec(Bounds bounds)
    {
        float length = Mathf.Max(MIN_CARGO_SIZE, 2f * Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z)));
        float width = Mathf.Max(MIN_CARGO_SIZE, 2f * Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)));
        return new CargoSpec
        {
            Length = length,
            Width = width,
            Gap = CARGO_GAP,
            Bays = Mathf.Max(1, Mathf.RoundToInt(CARGO_BLOCK_LENGTH / length)),
            Rows = Mathf.Max(1, Mathf.RoundToInt(CARGO_BLOCK_DEPTH / width)),
            MaxTiers = 1,
        };
    }

    /// <summary>
    /// One water edge for cranes. P→Q is its segment, Normal is a unit seaward normal, Y is local top height.
    /// </summary>
    public struct Waterline
    {
        public Vector2 P;
        public Vector2 Q;
        public Vector2 Normal;
        public float Y;
    }

    /// <summary>Facility module placement. Rotation faces model local +X (Blender −X=road side) toward parcel edge-0 outward normal.</summary>
    public sealed class Module
    {
        public Vector2 Position;    // Model origin.
        public Quaternion Rotation;
        public Vector2[] Corners;   // Occupied rectangle (CCW).
        public Vector2 Gate;        // Parcel edge-0 midpoint. Module contact-height reference and internal-road origin.
        public Vector2 Inward;      // Parcel edge-0 unit inward normal.
    }

    /// <summary>One handling crane. Origin on water edge; local +Z faces Normal (sea).</summary>
    public struct Crane
    {
        public Vector2 Position;
        public Vector2 Normal;
        public float Y;
    }

    /// <summary>
    /// One cargo pile (stacked containers or one ground cargo item). Center is bottom center; Along is its long direction (main water edge).
    /// Hash seeds per-tier colors; RowHash is shared within a block row. Both are deterministic for identical input.
    /// </summary>
    public struct Stack
    {
        public Vector2 Center;
        public Vector2 Along;
        public int Tiers;
        public uint Hash;
        public uint RowHash;
    }

    public sealed class Plan
    {
        public Module Module;
        public readonly List<Crane> Cranes = new List<Crane>();
        public readonly List<Stack> Stacks = new List<Stack>();
        public CargoSpec Cargo;     // Stacks layout (irrelevant when Stacks is empty).
    }

    /// <summary>
    /// Create a placement plan.
    /// footprint is the parcel (SHAPE_FULL footprint, CCW, edge 0 road-facing); yard is cargo ground (quay footprint,
    /// or parcel land portion for ports on reclamation); waterlines are crane water edges.
    /// moduleBounds/craneBounds are standard-model local bounds (null if part absent).
    /// obstacle is an additional cargo-exclusion polygon such as a rooftop prop (null if absent).
    /// Place cargo only when provided and the port has at least one crane (fills handling space between cranes/buildings).
    /// </summary>
    public static Plan Create(Vector2[] footprint, Vector2[] yard, IReadOnlyList<Waterline> waterlines,
        Bounds? moduleBounds, Bounds? craneBounds, Vector2[] obstacle, CargoSpec? cargo, float visualVariant2)
    {
        var plan = new Plan();
        if (footprint == null || footprint.Length < 3)
        {
            return plan;
        }
        if (moduleBounds.HasValue)
        {
            plan.Module = PlaceModule(footprint, moduleBounds.Value);
        }
        if (craneBounds.HasValue && waterlines != null)
        {
            PlaceCranes(plan, waterlines, craneBounds.Value);
        }
        if (cargo.HasValue && plan.Cranes.Count > 0 && yard != null && yard.Length >= 3)
        {
            uint seed = (uint)Mathf.RoundToInt(Mathf.Clamp01(visualVariant2) * 1000000f);
            float craneDepth = craneBounds.HasValue ? Mathf.Max(0f, -craneBounds.Value.min.z) : 0f;
            plan.Cargo = cargo.Value;
            PlaceCargo(plan, cargo.Value, yard, waterlines, craneDepth, obstacle, seed);
        }
        return plan;
    }

    /// <summary>
    /// Place facility module at parcel edge-0 (road-side) midpoint, with its road-side face (local +X end) aligned to edge 0.
    /// Use prebuilt orientation <see cref="BuildingRoadAccess.PrebuiltRotation"/> (local +Z along edge 0).
    /// Return null (omit placement) if its occupied rectangle does not fit the parcel.
    /// </summary>
    public static Module PlaceModule(Vector2[] footprint, Bounds bounds)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }
        Vector2 a = footprint[0];
        Vector2 b = footprint[1];
        float length = (b - a).magnitude;
        if (length < 1e-3f)
        {
            return null;
        }
        Vector2 along = (b - a) / length;
        var outward = new Vector2(along.y, -along.x);
        Vector2 gate = (a + b) * 0.5f;
        float centerZ = (bounds.min.z + bounds.max.z) * 0.5f;
        Quaternion rotation = BuildingRoadAccess.PrebuiltRotation(footprint);
        Vector2 position = gate - outward * bounds.max.x - along * centerZ;
        Vector2[] corners = RotatedRect(position, rotation, bounds);

        try
        {
            var gf = new GeometryFactory();
            Geometry parcel = ToPolygon(gf, footprint);
            if (!parcel.IsValid)
            {
                parcel = parcel.Buffer(0);
            }
            if (!parcel.Buffer(FOOTPRINT_TOLERANCE).Covers(ToPolygon(gf, corners)))
            {
                return null;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortLayout] 施設モジュールの収まりの判定に失敗しました: {ex.Message}。置かないものとして扱います。");
            return null;
        }

        return new Module { Position = position, Rotation = rotation, Corners = corners, Gate = gate, Inward = -outward };
    }

    /// <summary>
    /// Space handling cranes evenly along water edges. Count per edge = length ÷ (actual width × <see cref="CRANE_SPACING_RATIO"/>);
    /// distribute over the whole edge with equal end margins. Omit edges shorter than actual width; process water edges in order
    /// until total reaches <see cref="MAX_CRANE_COUNT"/>. Omit cranes intersecting facility modules
    /// (prevents legs piercing buildings on shallow parcels; modules remain fixed because they define openings).
    /// </summary>
    private static void PlaceCranes(Plan plan, IReadOnlyList<Waterline> waterlines, Bounds craneBounds)
    {
        float width = Mathf.Max(craneBounds.size.x, 0.5f);
        float spacing = width * CRANE_SPACING_RATIO;
        Geometry module = null;
        var gf = new GeometryFactory();
        if (plan.Module != null)
        {
            module = ToPolygon(gf, plan.Module.Corners);
        }

        foreach (Waterline waterline in waterlines)
        {
            if (plan.Cranes.Count >= MAX_CRANE_COUNT)
            {
                break;
            }
            float length = (waterline.Q - waterline.P).magnitude;
            if (length < width)
            {
                continue;
            }
            Vector2 normal = waterline.Normal.normalized;
            Quaternion rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y), Vector3.up);
            int count = Mathf.Clamp(Mathf.FloorToInt(length / spacing), 1, MAX_CRANE_COUNT - plan.Cranes.Count);
            Vector2 dir = (waterline.Q - waterline.P) / length;
            for (int c = 0; c < count; c++)
            {
                Vector2 at = waterline.P + dir * (length * (c + 0.5f) / count);
                if (module != null && module.Intersects(ToPolygon(gf, RotatedRect(at, rotation, craneBounds))))
                {
                    continue;
                }
                plan.Cranes.Add(new Crane { Position = at, Normal = normal, Y = waterline.Y });
            }
        }
    }

    /// <summary>
    /// Place cargo in vacant yard space. Align a grid's long axis to the main (longest) water edge; from the landward side of its roadway,
    /// divide inward into spec.Rows × spec.Bays blocks with passages. Place only slots
    /// within the yard inset by <see cref="YARD_MARGIN"/> that avoid:
    /// - Crane strips at each water edge (water edge to crane landward end + <see cref="APRON_LANE"/>).
    /// - <see cref="FACILITY_CLEARANCE"/> around facility modules/additional obstacles.
    /// - Internal road extending inward from the gate with width <see cref="DRIVE_WIDTH"/>.
    /// Slot grid indices and seed determine occupancy/tiers, independent of other slots and enumeration order.
    /// </summary>
    private static void PlaceCargo(Plan plan, CargoSpec spec, Vector2[] yard, IReadOnlyList<Waterline> waterlines,
        float craneDepth, Vector2[] obstacle, uint seed)
    {
        try
        {
            var gf = new GeometryFactory();
            Geometry yardGeom = ToPolygon(gf, yard);
            if (!yardGeom.IsValid)
            {
                yardGeom = yardGeom.Buffer(0);
            }
            Geometry inner = yardGeom.Buffer(-YARD_MARGIN);
            if (inner.IsEmpty)
            {
                return;
            }
            IPreparedGeometry allowed = PreparedGeometryFactory.Prepare(inner);

            // Main water edge and cargo-grid axes: u=along water edge, v=landward.
            Waterline main = waterlines[0];
            float mainLength = -1f;
            foreach (Waterline w in waterlines)
            {
                float l = (w.Q - w.P).magnitude;
                if (l > mainLength)
                {
                    mainLength = l;
                    main = w;
                }
            }
            if (mainLength < 1e-3f)
            {
                return;
            }
            Vector2 u = (main.Q - main.P) / mainLength;
            Vector2 v = -main.Normal.normalized;
            float zoneDepth = craneDepth + APRON_LANE;

            var blocked = new List<Geometry>();
            foreach (Waterline w in waterlines)
            {
                Vector2 inward = -w.Normal.normalized * zoneDepth;
                blocked.Add(ToPolygon(gf, new[] { w.P, w.Q, w.Q + inward, w.P + inward }));
            }
            if (plan.Module != null)
            {
                blocked.Add(ToPolygon(gf, plan.Module.Corners).Buffer(FACILITY_CLEARANCE));
                Envelope env = yardGeom.EnvelopeInternal;
                float reach = (float)(env.Width + env.Height) + DRIVE_WIDTH;
                Vector2 side = new Vector2(-plan.Module.Inward.y, plan.Module.Inward.x) * (DRIVE_WIDTH * 0.5f);
                Vector2 end = plan.Module.Gate + plan.Module.Inward * reach;
                blocked.Add(ToPolygon(gf, new[] { plan.Module.Gate - side, end - side, end + side, plan.Module.Gate + side }));
            }
            if (obstacle != null && obstacle.Length >= 3)
            {
                blocked.Add(ToPolygon(gf, obstacle).Buffer(FACILITY_CLEARANCE));
            }
            IPreparedGeometry blockedAll = PreparedGeometryFactory.Prepare(gf.BuildGeometry(blocked).Union());

            // Grid origin: main water-edge start, shifted along edge by yard margin and landward by crane-strip depth.
            // The start lies at the yard edge; without the margin shift, end slots always overlap its margin and disappear (leaving small ports without cargo).
            // Measure/scan yard extents in (u, v).
            Vector2 origin = main.P + u * YARD_MARGIN + v * zoneDepth;
            float uMin = float.PositiveInfinity, uMax = float.NegativeInfinity;
            float vMin = float.PositiveInfinity, vMax = float.NegativeInfinity;
            foreach (Vector2 p in yard)
            {
                float pu = Vector2.Dot(p - origin, u);
                float pv = Vector2.Dot(p - origin, v);
                uMin = Mathf.Min(uMin, pu); uMax = Mathf.Max(uMax, pu);
                vMin = Mathf.Min(vMin, pv); vMax = Mathf.Max(vMax, pv);
            }

            float slotU = spec.Length + spec.Gap;
            float slotV = spec.Width + spec.Gap;
            float pitchU = spec.Bays * slotU + BLOCK_AISLE;
            float pitchV = spec.Rows * slotV + BLOCK_AISLE;
            int blockU0 = Mathf.FloorToInt(uMin / pitchU), blockU1 = Mathf.FloorToInt(uMax / pitchU);
            int blockV0 = Mathf.FloorToInt(vMin / pitchV), blockV1 = Mathf.FloorToInt(vMax / pitchV);
            Vector2 halfU = u * (spec.Length * 0.5f);
            Vector2 halfV = v * (spec.Width * 0.5f);

            for (int bv = blockV0; bv <= blockV1; bv++)
            {
                for (int row = 0; row < spec.Rows; row++)
                {
                    float cv = bv * pitchV + row * slotV + slotV * 0.5f;
                    for (int bu = blockU0; bu <= blockU1; bu++)
                    {
                        uint rowHash = Hash(Hash(Hash(seed, bu), bv), -1 - row);
                        for (int bay = 0; bay < spec.Bays; bay++)
                        {
                            if (plan.Stacks.Count >= MAX_STACKS)
                            {
                                return;
                            }
                            float cu = bu * pitchU + bay * slotU + slotU * 0.5f;
                            uint hash = Hash(Hash(Hash(Hash(seed, bu), bay), bv), row);
                            if (Unit(hash) >= FILL_RATIO)
                            {
                                continue;
                            }
                            Vector2 center = origin + u * cu + v * cv;
                            Geometry slot = ToPolygon(gf, new[]
                            {
                                center - halfU - halfV, center + halfU - halfV, center + halfU + halfV, center - halfU + halfV,
                            });
                            if (!allowed.Contains(slot) || blockedAll.Intersects(slot))
                            {
                                continue;
                            }
                            int maxTiers = Mathf.Max(1, spec.MaxTiers);
                            int tiers = Mathf.Min(maxTiers, 1 + Mathf.FloorToInt(Unit(Hash(hash, 1)) * maxTiers));
                            plan.Stacks.Add(new Stack { Center = center, Along = u, Tiers = tiers, Hash = hash, RowHash = rowHash });
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortLayout] ヤードの荷の配置に失敗しました: {ex.Message}。荷を置きません。");
            plan.Stacks.Clear();
        }
    }

    /// <summary>Color index (0–count−1) of pile tier t (0=bottom).</summary>
    public static int TierColor(Stack stack, int tier, int count)
    {
        return count <= 0 ? 0 : (int)(Hash(stack.Hash, 2 + tier) % (uint)count);
    }

    /// <summary>
    /// Ground-cargo kind index (0–count−1), constant within a block row.
    /// Bulk piles/tanks normally group by commodity; changing kind per slot would produce a patchwork.
    /// </summary>
    public static int CargoKind(Stack stack, int count)
    {
        return count <= 0 ? 0 : (int)(Hash(stack.RowHash, 2) % (uint)count);
    }

    /// <summary>
    /// Bounds of all prefab meshes relative to the prefab root. Use the same transform as <see cref="BuildingMeshUtils.CombineModuleMeshes"/>
    /// without creating meshes. Null if no mesh exists.
    /// </summary>
    public static Bounds? GetModelBounds(GameObject prefab)
    {
        if (prefab == null)
        {
            return null;
        }
        Matrix4x4 rootInverse = prefab.transform.localToWorldMatrix.inverse;
        Bounds? result = null;
        foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
            {
                continue;
            }
            Matrix4x4 toRoot = rootInverse * mf.transform.localToWorldMatrix;
            Bounds b = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = toRoot.MultiplyPoint3x4(corner);
                if (result.HasValue)
                {
                    Bounds grown = result.Value;
                    grown.Encapsulate(p);
                    result = grown;
                }
                else
                {
                    result = new Bounds(p, Vector3.zero);
                }
            }
        }
        return result;
    }

    /// <summary>Four corners (local XZ) of horizontal bounds placed at origin at with rotation.</summary>
    public static Vector2[] RotatedRect(Vector2 at, Quaternion rotation, Bounds bounds)
    {
        var corners = new Vector2[4];
        float[] xs = { bounds.min.x, bounds.max.x, bounds.max.x, bounds.min.x };
        float[] zs = { bounds.min.z, bounds.min.z, bounds.max.z, bounds.max.z };
        for (int i = 0; i < 4; i++)
        {
            Vector3 r = rotation * new Vector3(xs[i], 0f, zs[i]);
            corners[i] = at + new Vector2(r.x, r.z);
        }
        if (PolygonUtils.SignedArea(corners) < 0f)
        {
            System.Array.Reverse(corners);
        }
        return corners;
    }

    internal static Polygon ToPolygon(GeometryFactory gf, IReadOnlyList<Vector2> ring)
    {
        var coords = new Coordinate[ring.Count + 1];
        for (int i = 0; i < ring.Count; i++)
        {
            coords[i] = new Coordinate(ring[i].x, ring[i].y);
        }
        coords[ring.Count] = new Coordinate(ring[0].x, ring[0].y);
        return gf.CreatePolygon(coords);
    }

    private static uint Hash(uint a, int b)
    {
        uint h = a ^ ((uint)b * 0x9E3779B1u);
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static float Unit(uint h)
    {
        return (h & 0xFFFFFF) / 16777216f;
    }
}
