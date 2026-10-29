using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using ElevationSegment = RoadShapeGeometry.ElevationSegment;

public partial class RoadPlanManager
{
    private List<ElevationSegment> cachedEditingElevationSegments;
    private List<(int segIndex, PathSegment otherSeg)> cachedElevationViolations;
    // Cached local checks for rivers crossed at ground level and their violating sample positions (absolute coordinates).
    private List<(RiverRecord river, List<Vector3> points)> cachedRiverViolations;
    // Cached local checks for sections passing over sea/lakes at ground level (small centerline surface sections, absolute coordinates).
    private List<(Vector3 a, Vector3 b)> cachedSeaLakeViolations;
    // Path centerline sample spacing for river/sea/lake crossing checks (m). The backend uses 1m, but
    // real-time drag checks use coarser spacing sufficient to detect rivers at least a dozen metres wide.
    private const float RiverCrossingSampleStep = 2f;
    // River occupied-box extent: show only cells within this radius (m) of violating samples in red.
    private const float RiverOccupancyBoxRadius = 40f;
    private List<Vector3> cachedCollisionVolume;
    private OccupancyVolumeUtil.BuildingVolumes cachedBuildingVolume;
    // Cached local estimates of building footprints (world XZ polygons) interfering with the current path.
    private List<List<Vector3>> cachedBuildingInterference;

    // Cached existing path segments for elevation intersection checks.
    // Existing paths do not change during editing, so reuse built segments.
    private Dictionary<int, List<ElevationSegment>> existingElevSegCache = new Dictionary<int, List<ElevationSegment>>();

    // Extra margin for corridor BBOX filtering of building interference.
    private const float BuildingInterferenceMargin = 2f;

    // =====================================================================
    // Elevation intersection validation
    // =====================================================================

    /// <summary>
    /// Gets height (occupied overhead height) from master data for path_variant.
    /// Defaults to 0.0m (road adhering to the ground) if undefined.
    /// </summary>
    private float GetPathHeightFromMaster(int variant)
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.height.HasValue)
                return master.height.Value;
        }
        catch
        {
            // Ignore when undefined in master data.
        }
        return 0f;
    }

    /// <summary>
    /// Gets the minimum permitted height for PathSubtype (m) from master data.
    /// Only variants with negative values (underground roads/subway tracks, etc.) can be built
    /// underground (relative Y&lt;0). Defaults to 0 (underground forbidden) if undefined. Editor guard
    /// matching backend _validate_path_height_range.
    /// </summary>
    private float GetMinHeightFromMaster()
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            if (master != null && master.min_height.HasValue)
                return master.min_height.Value;
        }
        catch
        {
            // Default to 0 when undefined in master data.
        }
        return 0f;
    }

    /// <summary>
    /// Checks master data for elevation editing support (height_editable=true) on PathSubtype.
    /// Returns false if master data is unavailable.
    /// Used to prohibit elevation setting/changes for non-editable variants (e.g. streets).
    /// </summary>
    public bool IsHeightEditable()
    {
        if (MasterDataStore.Instance == null) return false;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            return master != null && master.height_editable.HasValue && master.height_editable.Value;
        }
        catch
        {
            // Treat as non-editable when undefined in master data.
        }
        return false;
    }

    /// <summary>
    /// Samples the edited path as Beziers to build ElevationSegments
    /// for elevation intersection checks.
    /// Uses the same sections/controls as rendering without render-only lift.
    /// </summary>
    private List<ElevationSegment> BuildEditingElevationSegments()
    {
        if (cachedEditingElevationSegments != null) return cachedEditingElevationSegments;
        var spans = new List<RoadShapeGeometry.Span>();
        bool placing = phase == RoadEditorPhase.Placing && points.Count > 0;
        for (int i = 0; i < points.Count - 1 + (placing ? 1 : 0); i++)
            spans.Add(GetShapeSpan(i, placing));
        return cachedEditingElevationSegments = RoadShapeGeometry.SampleElevationSegments(spans, bezierSamplesPerSegment + 1);
    }

    /// <summary>
    /// Builds ElevationSegments for intersection checks from existing path geometry.
    /// Restores saved controls to absolute coordinates using ground height beneath their owning anchors.
    /// </summary>
    private List<ElevationSegment> BuildExistingElevationSegments(PathSummary path)
    {
        List<ElevationSegment> segments = new List<ElevationSegment>();
        var startList = GeneratedModelUtils.ToFloatList(path.start_point);
        var endList = GeneratedModelUtils.ToFloatList(path.end_point);
        if (startList == null || endList == null || startList.Count < 2 || endList.Count < 2) return segments;

        Vector3 sp = new Vector3(startList[0], startList.Count > 2 ? startList[1] : 0f, startList[startList.Count - 1]);
        Vector3 ep = new Vector3(endList[0], endList.Count > 2 ? endList[1] : 0f, endList[endList.Count - 1]);

        var waypoints = GeneratedModelUtils.ToWaypointList(path.waypoints) ?? new List<PathWaypoint>();

        List<Vector3> relPoints = new List<Vector3>();
        List<Vector3?> relControlOut = new List<Vector3?>();
        List<Vector3?> relControlIn = new List<Vector3?>();

        relPoints.Add(sp);
        var sc = GeneratedModelUtils.ToFloatList(path.start_control);
        relControlOut.Add(sc != null && sc.Count >= 3 ? new Vector3(sc[0], sc[1], sc[2]) : (Vector3?)null);
        relControlIn.Add(null);

        foreach (var wp in waypoints)
        {
            if (wp.position == null || wp.position.Length < 2) continue;
            Vector3 pos = new Vector3(wp.position[0], wp.position.Length > 2 ? wp.position[1] : 0f, wp.position[wp.position.Length - 1]);
            relPoints.Add(pos);

            if (wp.control_in != null && wp.control_in.Length >= 2)
                relControlIn.Add(new Vector3(wp.control_in[0], wp.control_in.Length > 2 ? wp.control_in[1] : 0f, wp.control_in[wp.control_in.Length - 1]));
            else
                relControlIn.Add(null);

            if (wp.control_out != null && wp.control_out.Length >= 2)
                relControlOut.Add(new Vector3(wp.control_out[0], wp.control_out.Length > 2 ? wp.control_out[1] : 0f, wp.control_out[wp.control_out.Length - 1]));
            else
                relControlOut.Add(null);
        }

        relPoints.Add(ep);
        var ec = GeneratedModelUtils.ToFloatList(path.end_control);
        relControlIn.Add(ec != null && ec.Count >= 3 ? new Vector3(ec[0], ec[1], ec[2]) : (Vector3?)null);
        relControlOut.Add(null);

        var result = SampleElevationSegments(relPoints, relControlOut, relControlIn, path.curved, PathManager.GetPathFollowTerrain(path), PathManager.GetPathBaseHeight(path.path_variant));
        if (path.station_grade_profile != null && path.station_grade_profile.Count >= 2)
        {
            for (int i = 0; i < result.Count; i++)
            {
                var seg = result[i];
                seg.absYA = Utils.GetGroundHeight(seg.a) + path.GetRelativeHeightAtPoint(seg.a.x, seg.a.z);
                seg.absYB = Utils.GetGroundHeight(seg.b) + path.GetRelativeHeightAtPoint(seg.b.x, seg.b.z);
                seg.follows = false;
                result[i] = seg;
            }
        }
        return result;
    }

    // Interpolate non-following absolute elevations from anchors. Do not bend bridges/tunnels with intermediate terrain.
    private List<ElevationSegment> SampleElevationSegments(List<Vector3> anchors,
        List<Vector3?> controlsOut, List<Vector3?> controlsIn, bool curved, bool followsTerrain, float groundRelativeHeight)
    {
        var spans = new List<RoadShapeGeometry.Span>();
        PathBuildPoint RestorePoint(int index)
        {
            Vector3 position = anchors[index];
            position.y += Utils.GetGroundHeight(position);
            var point = new PathBuildPoint(position, PathPointRole.Waypoint);
            if (controlsOut[index].HasValue)
            {
                var c = controlsOut[index].Value;
                point.control_out = RestoreControl(c.x, c.y, c.z, position);
            }
            if (controlsIn[index].HasValue)
            {
                var c = controlsIn[index].Value;
                point.control_in = RestoreControl(c.x, c.y, c.z, position);
            }
            return point;
        }
        for (int i = 0; i < anchors.Count - 1; i++)
            spans.Add(RoadShapeGeometry.CreateSpan(RestorePoint(i), RestorePoint(i + 1), curved, followsTerrain, groundRelativeHeight));
        return RoadShapeGeometry.SampleElevationSegments(spans, bezierSamplesPerSegment + 1);
    }

    /// <summary>
    /// Tests whether two XZ-projected segments intersect, returning
    /// each segment's parameter t at the crossing through out values.
    /// Returns false if they do not intersect.
    /// </summary>
    private static bool SegmentsIntersectXZ(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2, out float tA, out float tB)
    {
        return RoadShapeGeometry.SegmentsIntersectXZ(a1, a2, b1, b2, out tA, out tB);
    }

    /// <summary>
    /// Returns whether segments contain a non-following span (height-edited or follow_terrain=false
    /// variant). ElevationSegment.follows already accounts for both
    /// the variant flag and relY≈0, so its negation suffices.
    /// Equivalent to backend geo_path_depth.path_has_non_follow_span.
    /// </summary>
    private static bool HasNonFollowSegment(List<ElevationSegment> segments)
    {
        foreach (var seg in segments)
        {
            if (!seg.follows) return true;
        }
        return false;
    }

    /// <summary>
    /// Detects elevation collisions between the edited path and existing segments.
    ///
    /// Rules:
    /// - If two segments cross in XZ, check whether their height ranges
    ///   [Y, Y + height] overlap at the crossing.
    /// - Allow crossings when their height difference is within tolerance
    ///   as same-height intersections/level crossings (1cm for relative comparisons / 0.5m for absolute).
    /// - For following × following pairs, ignore terrain elevation and compare relative heights
    ///   on a virtual flat plane.
    /// - For pairs involving non-following spans (height-edited or follow_terrain=false
    ///   bridge variants), use absolute elevation (terrain + relative height), matching backend
    ///   validate_path_elevation_intersections. This correctly allows tunnels beneath hills versus
    ///   surface roads above, and bridges crossing cuttings versus roads at their bottoms.
    ///   Following segments use terrain elevation at the crossing.
    /// - Also check the path itself (when editing an existing path) and new path self-intersections.
    /// </summary>
    private List<(int segIndex, PathSegment otherSeg)> CheckElevationIntersections()
    {
        if (cachedElevationViolations != null)
            return cachedElevationViolations;

        var violations = new List<(int segIndex, PathSegment otherSeg)>();
        if (pathManager == null || points.Count < 2 || PathType == Constants.PATH_STATION_HOUSE)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        float myHeight = GetPathHeightFromMaster(PathSubtype);
        List<ElevationSegment> mySegments = BuildEditingElevationSegments();
        if (mySegments.Count == 0)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        // Whether the edited path contains non-following spans (height-edited or follow_terrain=false
        // variants). Interpolated relY on these spans is not a physical height at the crossing,
        // so use absolute elevation for involved pairs (same test as backend
        // path_has_non_follow_span).
        bool editingNonFollow = HasNonFollowSegment(mySegments);

        var freezeProbe = System.Diagnostics.Stopwatch.StartNew(); // [Freeze investigation] Remove after investigation.
        int freezeProbeBuilt = 0; // [Freeze investigation] Remove after investigation.
        HashSet<int> candidatePathIds = new HashSet<int>();
        for (int i = 0; i < mySegments.Count; i++)
        {
            var seg = mySegments[i];
            Vector3 mid = (seg.a + seg.b) * 0.5f;
            float radius = Vector3.Distance(seg.a, seg.b) * 0.5f + 5.0f;

            var nearby = pathManager.QueryNearbySegments(new Vector3(mid.x, 0, mid.z), radius);
            if (nearby != null)
            {
                foreach (var other in nearby)
                {
                    if (other.path != null)
                        candidatePathIds.Add(other.path.path_id);
                }
            }
        }

        // --- Existing path intersection checks ---
        foreach (int otherPathId in candidatePathIds)
        {
            if (otherPathId == pathId) continue;

            PathSummary exPath = pathManager.GetPathData(otherPathId);
            if (exPath == null || exPath.path_type == Constants.PATH_STATION_HOUSE) continue;

            // Cache existing path segments because they do not change during editing.
            if (!existingElevSegCache.TryGetValue(otherPathId, out List<ElevationSegment> exSegments))
            {
                exSegments = BuildExistingElevationSegments(exPath);
                existingElevSegCache[otherPathId] = exSegments;
                freezeProbeBuilt++; // [Freeze investigation] Remove after investigation.
            }

            float otherHeight = GetPathHeightFromMaster(exPath.path_variant);

            if (myHeight <= 0f && otherHeight <= 0f) continue;

            // Compare absolute elevations (terrain + relative height) for pairs involving non-following spans.
            bool useAbsolute = editingNonFollow || HasNonFollowSegment(exSegments);

            for (int i = 0; i < mySegments.Count; i++)
            {
                for (int j = 0; j < exSegments.Count; j++)
                {
                    var mySeg = mySegments[i];
                    var exSeg = exSegments[j];

                    if (RoadShapeGeometry.ElevationOverlaps(mySeg, myHeight, exSeg, otherHeight, useAbsolute))
                    {
                        // Create a dummy PathSegment converted to absolute coordinates for drawing.
                        Vector3 aAbs = new Vector3(exSeg.a.x, exSeg.absYA, exSeg.a.z);
                        Vector3 bAbs = new Vector3(exSeg.b.x, exSeg.absYB, exSeg.b.z);
                        PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs, path = exPath };
                        violations.Add((i, dummyOther));
                    }
                }
            }
        }

        // --- Self-intersection checks ---
        // Use absolute elevations (terrain + relative height) for paths with non-following spans.
        for (int i = 0; i < mySegments.Count; i++)
        {
            for (int j = i + 2; j < mySegments.Count; j++) // Ignore adjacent segments.
            {
                var mySeg1 = mySegments[i];
                var mySeg2 = mySegments[j];

                if (RoadShapeGeometry.ElevationOverlaps(mySeg1, myHeight, mySeg2, myHeight, editingNonFollow))
                {
                    Vector3 aAbs = new Vector3(mySeg2.a.x, mySeg2.absYA, mySeg2.a.z);
                    Vector3 bAbs = new Vector3(mySeg2.b.x, mySeg2.absYB, mySeg2.b.z);
                    PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs };
                    violations.Add((i, dummyOther));
                }
            }
        }

        // [Freeze investigation] Emit only when existing path geometry is rebuilt, not every frame. Remove after investigation.
        if (freezeProbeBuilt > 0) Debug.Log($"[フリーズ調査] 立体交差チェック {freezeProbe.Elapsed.TotalMilliseconds:F1}ms (候補パス{candidatePathIds.Count}件・形状を新規作成{freezeProbeBuilt}件)");

        cachedElevationViolations = violations;
        return cachedElevationViolations;
    }

    /// <summary>
    /// Shows colliding path sections' width/occupied height, rivers crossed at ground level near crossings,
    /// and road sections passing over sea/lakes at ground level as translucent occupied tubes/boxes.
    /// For path collisions, cover only the other section without obscuring the current preview mesh
    /// (the later section is the other side for self-intersections).
    /// </summary>
    private void DrawElevationIntersections(Camera cam)
    {
        if (overlayRenderer == null) return;

        if (cachedCollisionVolume == null)
        {
            cachedCollisionVolume = new List<Vector3>();
            var drawn = new HashSet<(Vector3, Vector3)>();
            foreach (var (_, other) in CheckElevationIntersections())
            {
                if (!drawn.Add((other.a, other.b))) continue;
                int variant = other.path != null ? other.path.path_variant : PathSubtype;
                OccupancyVolumeUtil.AddTube(cachedCollisionVolume, new List<Vector3> { other.a, other.b },
                    pathManager.GetRoadWidth(variant), GetPathHeightFromMaster(variant));
            }
            // Show river areas near ground-level crossings as occupied boxes in the same warning color.
            foreach (var (river, violationPoints) in CheckRiverCrossings())
            {
                RiverStore.AddOccupancyBox(cachedCollisionVolume, river, violationPoints, RiverOccupancyBoxRadius);
            }
            // Sea/lakes have no area grid unlike rivers, so show occupied tubes along the road sections over water themselves.
            foreach (var (a, b) in CheckSeaLakeCrossings())
            {
                OccupancyVolumeUtil.AddTube(cachedCollisionVolume, new List<Vector3> { a, b },
                    pathManager.GetRoadWidth(PathSubtype), GetPathHeightFromMaster(PathSubtype));
            }
        }
        overlayRenderer.AddPreTriangulated(cachedCollisionVolume, OccupancyVolumeUtil.WarningColor);
    }

    /// <summary>
    /// Locally checks whether the edited path crosses RiverStore areas at ground level.
    ///
    /// Same rules as backend geo_rivers.validate_path_river_crossings:
    /// each sample within a river must satisfy either surface absolute elevation ≥ water elevation + WATER_BRIDGE_MIN_CLEARANCE (bridge)
    /// or terrain elevation − surface elevation > variant clearance height (fully buried tunnel).
    /// Terrain-following spans always violate this because their surface is the riverbed below water level.
    /// Infrastructure paths (power lines, water conduits, pipelines) do not occupy the ground and are exempt.
    ///
    /// Subdivide each BuildEditingElevationSegments segment (whole spans for straight lines, Bezier pieces
    /// for curves) at RiverCrossingSampleStep intervals. Following segments use terrain height;
    /// non-following segments linearly interpolate endpoint absolute elevations as the surface elevation.
    /// Returns violating samples by river (absolute coordinates, used to determine occupied-box extents).
    /// </summary>
    private List<(RiverRecord river, List<Vector3> points)> CheckRiverCrossings()
    {
        if (cachedRiverViolations != null)
            return cachedRiverViolations;

        var violations = new List<(RiverRecord river, List<Vector3> points)>();
        if (!RiverStore.HasRivers || points.Count < 2 || InfraNetworkUtil.IsInfraPathType(PathType))
        {
            cachedRiverViolations = violations;
            return cachedRiverViolations;
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        var byRiver = new Dictionary<RiverRecord, List<Vector3>>();
        foreach (var seg in BuildEditingElevationSegments())
        {
            float dx = seg.b.x - seg.a.x;
            float dz = seg.b.z - seg.a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / RiverCrossingSampleStep));
            for (int k = 0; k <= steps; k++)
            {
                float t = (float)k / steps;
                float x = seg.a.x + dx * t;
                float z = seg.a.z + dz * t;
                var river = RiverStore.FindAt(x, z);
                if (river == null) continue;
                float terrainY = Utils.GetGroundHeight(new Vector3(x, 0f, z));
                float roadY = seg.HeightAt(t, true, terrainY);
                if (terrainY - roadY > clearance) continue; // Fully buried tunnel
                float waterY = river.WaterLevelAt(x, z);
                if (roadY >= waterY + Constants.WATER_BRIDGE_MIN_CLEARANCE) continue; // Bridge
                if (!byRiver.TryGetValue(river, out var list))
                {
                    list = new List<Vector3>();
                    byRiver[river] = list;
                }
                list.Add(new Vector3(x, roadY, z));
            }
        }
        foreach (var kv in byRiver) violations.Add((kv.Key, kv.Value));

        cachedRiverViolations = violations;
        return cachedRiverViolations;
    }

    /// <summary>
    /// Whether (x, z) is over water by the backend's definition (geo_sea_lake): outside land and rivers, with terrain below
    /// the water level (the lake level inside a lake footprint, otherwise sea level 0). Callers check city bounds.
    /// </summary>
    private static bool IsOnWater(float x, float z, float terrainY, out float waterY)
    {
        var lake = WaterSurfaceStore.FindAt(x, z);
        waterY = lake != null ? lake.Level : 0f;
        return terrainY < waterY && RiverStore.FindAt(x, z) == null && !CoastlineStore.IsLand(x, z);
    }

    /// <summary>
    /// Surface that relative height 0 refers to while placing. Variants without height editing that may be laid on water (ferries)
    /// sit on the water surface there, as the backend normalizes them (crud_paths._normalize_path_elevations); otherwise the ground.
    /// </summary>
    private float PlacementSurfaceY(Vector3 p)
    {
        float ground = Utils.GetGroundHeight(p);
        if (!CoastlineStore.HasCoastline || IsHeightEditable()) return ground;
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(PathSubtype) : null;
        if (master == null || !master.over_water_allowed) return ground;
        return IsOnWater(p.x, p.z, ground, out float waterY) ? waterY : ground;
    }

    /// <summary>
    /// Locally checks whether the edited path crosses sea/lakes at ground level.
    ///
    /// Same rules as backend geo_sea_lake.validate_path_sea_lake_crossings:
    /// each sample over water must satisfy surface absolute elevation ≥ water elevation + WATER_BRIDGE_MIN_CLEARANCE (bridge) or
    /// terrain elevation − surface elevation > variant clearance height (fully buried tunnel).
    /// Over water means inside city bounds, outside land (CoastlineStore), and terrain below water level. Water elevation
    /// is the lake level inside a lake footprint (WaterSurfaceStore), otherwise sea level 0. Simplified coastlines
    /// may differ from the shoreline by several metres; without the terrain condition, roads on dry shores would be rejected.
    /// CheckRiverCrossings handles rivers. Excludes infrastructure and over_water_allowed variants.
    ///
    /// Sampling matches CheckRiverCrossings. Returns short surface sections of one sample interval centered on violating points
    /// (absolute coordinates), used to draw occupied boxes.
    /// </summary>
    private List<(Vector3 a, Vector3 b)> CheckSeaLakeCrossings()
    {
        if (cachedSeaLakeViolations != null)
            return cachedSeaLakeViolations;

        var violations = new List<(Vector3 a, Vector3 b)>();
        cachedSeaLakeViolations = violations;
        if (!CoastlineStore.HasCoastline || points.Count < 2 || InfraNetworkUtil.IsInfraPathType(PathType))
            return violations;
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(PathSubtype) : null;
        if (master != null && master.over_water_allowed)
            return violations;
        if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();
        // Skip until city bounds are known, since sea cannot be distinguished from outside the city (backend is authoritative).
        if (terrainManager == null || !terrainManager.HasTerrainSettings)
            return violations;
        float halfW = terrainManager.GetMapWidth() * 0.5f;
        float halfH = terrainManager.GetMapHeight() * 0.5f;

        Vector3 RoadPoint(ElevationSegment seg, float t)
        {
            float x = Mathf.Lerp(seg.a.x, seg.b.x, t);
            float z = Mathf.Lerp(seg.a.z, seg.b.z, t);
            float y = seg.HeightAt(t, true, Utils.GetGroundHeight(new Vector3(x, 0f, z)));
            return new Vector3(x, y, z);
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        foreach (var seg in BuildEditingElevationSegments())
        {
            float dx = seg.b.x - seg.a.x;
            float dz = seg.b.z - seg.a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / RiverCrossingSampleStep));
            for (int k = 0; k <= steps; k++)
            {
                float t = (float)k / steps;
                float x = seg.a.x + dx * t;
                float z = seg.a.z + dz * t;
                if (Mathf.Abs(x) > halfW || Mathf.Abs(z) > halfH) continue;
                float terrainY = Utils.GetGroundHeight(new Vector3(x, 0f, z));
                if (!IsOnWater(x, z, terrainY, out float waterY)) continue;
                float roadY = seg.HeightAt(t, true, terrainY);
                if (terrainY - roadY > clearance) continue; // Fully buried tunnel
                if (roadY >= waterY + Constants.WATER_BRIDGE_MIN_CLEARANCE) continue; // Bridge
                float half = 0.5f / steps;
                violations.Add((RoadPoint(seg, Mathf.Max(0f, t - half)), RoadPoint(seg, Mathf.Min(1f, t + half))));
            }
        }
        return violations;
    }

    /// <summary>
    /// Locally estimates buildings overlapping the current path corridor (centerline expanded by path width)
    /// and returns their footprints.
    ///
    /// Uses BuildingManager's spatial index and building footprints without a backend request
    /// (BuildingSummary.polygonArray contains local XZ relative to center_point;
    /// add center_point to convert to world XZ before geometric tests),
    /// supporting real-time drag feedback. Does not evaluate parcel ownership or expropriation;
    /// only estimates geometric overlap (backend validation on confirmation is authoritative). Cached during editing.
    /// </summary>
    private List<List<Vector3>> CheckBuildingInterference()
    {
        if (cachedBuildingInterference != null)
            return cachedBuildingInterference;

        var result = new List<List<Vector3>>();
        cachedBuildingInterference = result;
        cachedBuildingVolume = new OccupancyVolumeUtil.BuildingVolumes();

        if (BuildingManager.Instance == null || pathManager == null) return result;

        if (points.Count == 0) return result;
        var centerline = SamplePreviewSpans(GetRenderPoints(), 0, points.Count - 1,
            phase == RoadEditorPhase.Placing, false);
        if (centerline.Count < 2) return result;

        float halfWidth = pathManager.GetRoadWidth(PathSubtype) * 0.5f;
        if (halfWidth <= 0f) return result;

        // Filter candidates with the corridor BBOX (centerline XZ bounds expanded by halfWidth + margin).
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in centerline)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
        float margin = halfWidth + BuildingInterferenceMargin;
        var candidates = BuildingManager.Instance.QueryBuildingIdsInBounds(
            minX - margin, minZ - margin, maxX + margin, maxZ + margin);

        // When laying variants carrying infrastructure (power lines, water conduits, electrified tracks, etc.), exclude
        // same-network infrastructure supply/demand facilities from interference. Terminals lie within facility parcels,
        // so incoming paths normally overlap facility buildings when connecting to them;
        // warning about this would be meaningless. Backend authoritative validation
        // (compute_path_interference building_ids) uses the same exclusions, matching local estimates and highlights.
        bool drawingInfraCarrier = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.GetPathInfraProducts(PathSubtype) != null;

        foreach (int buildingId in candidates)
        {
            var summary = BuildingManager.Instance.GetBuildingSummary(buildingId);
            float[][] poly = summary?.polygonArray;
            if (poly == null || poly.Length < 3) continue;

            // Determine same-network facilities using terminal node network bits and the current variant's
            // network compatibility. Snap candidate path-type restrictions do not apply here.
            // Do not exclude if the terminal node is unloaded (conservative: keep highlighting as usual).
            if (drawingInfraCarrier && summary.infra_node_id > 0)
            {
                var terminalNode = pathManager.GetNodeData(summary.infra_node_id);
                if (terminalNode != null
                    && InfraNetworkUtil.IsNodeConnectable(terminalNode, PathSubtype))
                {
                    continue;
                }
            }

            // Public facility sites are city-owned; infrastructure paths may cross without removing parcels (same exemption as core
            // classify_parcel_overlaps). Do not show buildings that will remain as interference.
            if (summary.public_facility && InfraNetworkUtil.IsInfraPathType(PathType)) continue;

            // polygonArray is relative to center_point (same convention as minimaps/mesh generation),
            // so add center_point to obtain world XZ. Skip buildings without center_point because their location
            // cannot be determined (using relative coordinates directly would cause false hits at the map center).
            float[] cp = summary.centerPointArray;
            if (cp == null || cp.Length < 2) continue;

            var footprint = new List<Vector2>(poly.Length);
            foreach (var v in poly)
            {
                if (v != null && v.Length >= 2) footprint.Add(new Vector2(v[0] + cp[0], v[1] + cp[1]));
            }
            if (footprint.Count < 3) continue;

            float buildingBottom = Utils.GetGroundHeight(new Vector3(cp[0], 0f, cp[1]));
            float buildingTop = buildingBottom + (summary.height ?? 0f);
            bool overlaps = false;
            for (int i = 1; i < centerline.Count && !overlaps; i++)
            {
                var a = centerline[i - 1];
                var b = centerline[i];
                if (Mathf.Min(a.y, b.y) >= buildingTop || Mathf.Max(a.y, b.y) + GetPathHeightFromMaster(PathSubtype) <= buildingBottom) continue;
                overlaps = CorridorOverlapsPolygon(new List<Vector3> { a, b }, halfWidth, footprint);
            }
            if (overlaps)
            {
                var worldPoly = new List<Vector3>(footprint.Count);
                foreach (var f in footprint)
                {
                    worldPoly.Add(new Vector3(f.x, 0f, f.y));
                }
                result.Add(worldPoly);
                cachedBuildingVolume.Add(summary);
            }
        }

        return result;
    }

    /// <summary>
    /// Approximates overlap between the centerline corridor expanded by halfWidth and an XZ footprint polygon.
    /// Overlap occurs if (a) a footprint vertex is within halfWidth of the centerline, (b) a centerline vertex is inside the footprint,
    /// or (c) a centerline segment crosses a footprint edge.
    /// </summary>
    private static bool CorridorOverlapsPolygon(List<Vector3> centerline, float halfWidth, List<Vector2> footprint)
    {
        float halfWidthSq = halfWidth * halfWidth;

        // (a) A footprint vertex lies in the centerline corridor (within halfWidth of any segment).
        for (int fi = 0; fi < footprint.Count; fi++)
        {
            Vector2 fp = footprint[fi];
            for (int ci = 0; ci < centerline.Count - 1; ci++)
            {
                Vector2 a = new Vector2(centerline[ci].x, centerline[ci].z);
                Vector2 b = new Vector2(centerline[ci + 1].x, centerline[ci + 1].z);
                if (PolylineUtils.DistanceToSegmentSq(fp, a, b) <= halfWidthSq) return true;
            }
        }

        // (b) A centerline vertex is inside the footprint.
        for (int ci = 0; ci < centerline.Count; ci++)
        {
            if (PolygonUtils.Contains(footprint, new Vector2(centerline[ci].x, centerline[ci].z))) return true;
        }

        // (c) A centerline segment crosses a footprint edge.
        for (int ci = 0; ci < centerline.Count - 1; ci++)
        {
            Vector3 a = centerline[ci];
            Vector3 b = centerline[ci + 1];
            for (int fi = 0; fi < footprint.Count; fi++)
            {
                Vector2 f0 = footprint[fi];
                Vector2 f1 = footprint[(fi + 1) % footprint.Count];
                if (SegmentsIntersectXZ(a, b,
                        new Vector3(f0.x, 0f, f0.y), new Vector3(f1.x, 0f, f1.y),
                        out _, out _))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shows occupied boxes for locally estimated interfering buildings: translucent red for public facilities, orange for private buildings.
    /// HighlightManager adds its display when authoritative backend results arrive.
    /// </summary>
    private void DrawBuildingInterference(Camera cam)
    {
        if (overlayRenderer == null) return;

        CheckBuildingInterference();
        cachedBuildingVolume?.Draw(overlayRenderer);
    }
}
