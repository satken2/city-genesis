using System;
using System.Collections.Generic;
using UnityEngine;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>Construction proceeds start→end independently of traffic direction. Progress is fixed to the epoch's fetched value.</summary>
public static class ConstructionPathBuilder
{
    public static float Progress(PathSummary path)
    {
        float value = path is ConstructionPathSummary p ? p.construction_progress.GetValueOrDefault() : 0f;
        return float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value / 100f);
    }

    public static float PierProgress(float progress) => Mathf.Clamp01(progress / .25f);
    public static float DeckProgress(float progress) => Mathf.Clamp01((progress - .25f) / .65f);

    /// <summary>
    /// End of completed section (fraction from start): progress for surface paths; last installed girder span for elevated paths.
    /// Approximate spans by equal division at pier spacing (actual boundaries depend on pier placement).
    /// Used to limit neighboring constructing tracks to completed sections during joining.
    /// </summary>
    public static float BuiltFraction(PathSummary path, List<Vector3> points)
    {
        float progress = Progress(path);
        if (points == null || points.Count < 2 || !IsElevated(path, points, false)) return progress;
        RoadPath.GetPathMasterParams(path, out _, out _, out _, out _, out _, out _, out _, out float? spacing, out _, out _, out _);
        float total = 0f;
        for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
        int spans = Mathf.Max(1, Mathf.CeilToInt(total / Mathf.Max(1f, spacing.GetValueOrDefault(total))));
        return Mathf.Floor(DeckProgress(progress) * spans + .00001f) / spans;
    }

    /// <summary>Construction-front position (fraction from start). Elevated paths use pier progress during the pier stage, then girder progress.</summary>
    public static float FrontFraction(float progress, bool elevated)
        => elevated ? (progress < .25f ? PierProgress(progress) : DeckProgress(progress)) : progress;

    /// <summary>
    /// Whether construction is elevated. Require points clearly above reference height so rendering lift alone does not classify zero-relative-height roads as elevated.
    /// </summary>
    public static bool IsElevated(PathSummary path, List<Vector3> points, bool underground)
    {
        RoadPath.GetPathMasterParams(path, out _, out _, out _, out _, out _, out _, out _,
            out float? spacing, out bool heightEditable, out float baseHeight, out _);
        return !underground && heightEditable && spacing.GetValueOrDefault() > 0f
            && points.Exists(p => path.GetRelativeHeightAtPoint(p.x, p.z) - baseHeight > .1f);
    }

    /// <summary>
    /// Approximate construction-front centerline position, used for construction audio.
    /// Elevated vehicles sit an actual vehicle length back from installed girder ends, differing from this continuous progress position.
    /// </summary>
    public static Vector3 FrontPoint(PathSummary path, List<Vector3> points, bool underground)
    {
        float front = FrontFraction(Progress(path), IsElevated(path, points, underground));
        float total = 0f;
        for (int i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
        float target = front * total, walked = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            float segment = Vector3.Distance(points[i - 1], points[i]);
            if (walked + segment >= target)
                return Vector3.Lerp(points[i - 1], points[i], segment > 1e-6f ? Mathf.Clamp01((target - walked) / segment) : 0f);
            walked += segment;
        }
        return points[points.Count - 1];
    }

    private static List<TunnelSectionUtil.DrawSection> DrawSections(List<Vector3> points, PathSummary path, bool underground)
        => points.Count < 2 ? new List<TunnelSectionUtil.DrawSection>() : underground
            ? TunnelSectionUtil.GetDrawSections(points, TunnelSectionUtil.GetClearanceHeight(path.path_variant),
                UndergroundView.IsActive)
            : new List<TunnelSectionUtil.DrawSection> { new TunnelSectionUtil.DrawSection(points, false) };

    /// <param name="points">For underground paths, use the rendered centerline (PathManager.CalculateSectionCenterline).</param>
    public static PathMeshResult Generate(PathSummary path, List<Vector3> points, bool underground,
        out List<TunnelSectionUtil.PortalInfo> portals)
    {
        var root = new GameObject("ConstructingPath");
        portals = new List<TunnelSectionUtil.PortalInfo>();
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out _, out _, out _,
            out float wallWidth, out _, out float? spacing, out bool heightEditable, out float baseHeight, out bool singleSpan);
        float progress = Progress(path);
        bool elevated = IsElevated(path, points, underground);
        var spline = RoadPath.BuildSpline(root, points, path.curved);
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, RoadPath.MAX_EXTRUDE_TILE_LENGTH));
        string variant = $"Models/Paths/p{path.path_variant}", type = $"Models/Paths/p{path.path_type}";
        var supports = new List<(GameObject obj, double percent)>();
        if (elevated)
        {
            RoadPath.GenerateConstructionPillars(root, spline, spacing.Value, baseHeight, path, variant, type,
                (obj, percent) => supports.Add((obj, percent)));
            supports.Sort((a, b) => a.percent.CompareTo(b.percent));
            for (int i = 0; i < supports.Count; i++)
            {
                float amount = Mathf.Clamp01(PierProgress(progress) * supports.Count - i);
                supports[i].obj.SetActive(amount > 0f);
                supports[i].obj.transform.localScale = new Vector3(1f, amount, 1f);
            }
        }

        float built = elevated ? DeckProgress(progress) : progress;
        List<float> deckBoundaries = null;
        // Place girders in pier-to-pier construction units. Retain unsupported endpoints as abutment-side boundaries.
        if (elevated)
        {
            var boundaries = new List<float> { 0f };
            float total = spline.CalculateLength();
            foreach (var support in supports)
            {
                float fraction = total > .001f ? spline.CalculateLength(0, support.percent) / total : 0f;
                if (fraction > boundaries[boundaries.Count - 1] + .0001f && fraction < .9999f) boundaries.Add(fraction);
            }
            boundaries.Add(1f);
            deckBoundaries = boundaries;
            built = boundaries[Mathf.Min(boundaries.Count - 1, Mathf.FloorToInt(built * (boundaries.Count - 1) + .00001f))];
        }

        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        if (underground) TunnelSectionUtil.SplitDrawableRuns(points, clearance, portals);
        var completeRuns = DrawSections(PathDrawContext.SlicePolylineByFraction(points, 0, built), path, underground);
        if (elevated && singleSpan)
        {
            // Stretching one continuous girder across spans would cross bends at intermediate anchors.
            completeRuns.Clear();
            for (int i = 1; i < deckBoundaries.Count && deckBoundaries[i] <= built + .00001f; i++)
                completeRuns.Add(new TunnelSectionUtil.DrawSection(PathDrawContext.SlicePolylineByFraction(points, deckBoundaries[i - 1], deckBoundaries[i]), false));
        }
        var dirtRuns = elevated ? new List<TunnelSectionUtil.DrawSection>() : DrawSections(PathDrawContext.SlicePolylineByFraction(points, built, 1), path, underground);
        var colliderParts = new List<Mesh>();
        // Retain only design-plane hit geometry so paths remain selectable before piers exist.
        if (elevated)
        {
            var selection = PathMeshFactory.BuildStripMesh(points, width);
            if (selection != null) colliderParts.Add(selection);
        }
        Material dirt = RoadPath.GetOrCreateRoadMaterialByName("ConstructionPathDirt");
        if (dirt == null) dirt = RoadPath.GetOrCreateRoadMaterial(BuildingMeshBuilder.GetConstructionDirtMaterial());
        foreach (var section in dirtRuns)
        {
            Mesh mesh = ConstructionMeshUtil.BuildDirtStripMesh(section.Points, width + LocalConstants.CONSTRUCTION_PATH_DIRT_EXTRA_WIDTH, !section.IsUnderground);
            var go = AddMesh(root, "ConstructionDirt", mesh, dirt);
            if (go != null) go.AddComponent<PathSection>().IsUnderground = section.IsUnderground;
            if (mesh != null) colliderParts.Add(mesh);
        }
        foreach (var section in completeRuns)
        {
            var run = section.Points;
            var complete = new GameObject("CompletedSurface");
            complete.transform.SetParent(root.transform, false);
            complete.AddComponent<PathSection>().IsUnderground = section.IsUnderground;
            var roadSpline = RoadPath.BuildSpline(complete, run, false);
            bool track = PathMeshFactory.GetModelType(path) == "track";
            if (track)
            {
                TrackPath.CreateConstructionParts(complete, roadSpline, run, path);
            }
            else
            {
                // Variants with base_height preserve model ground clearance as when completed (see RoadPath.CreateRoadParts).
                GameObject body = RoadPath.CreateSplineMesh(complete, roadSpline, variant + "/body", width, 0,
                    RoadPartHeights.BODY_ABOVE_SPLINE, fallbackPrefabName: type + "/body", singleSpan: singleSpan,
                    normalizeBodySurface: baseHeight <= 0f);
                if (sidewalk > 0f)
                {
                    for (int side = -1; side <= 1; side += 2)
                        RoadPath.CreateSplineMesh(complete, roadSpline, variant + "/sidewalk", sidewalk,
                            side * (width - sidewalk) * .5f, RoadPartHeights.SIDEWALK_ABOVE_SPLINE,
                            mirrorX: side > 0, fallbackPrefabName: type + "/sidewalk");
                }
                var tunnels = PathTunnelParts.Ranges(roadSpline, path);
                if (wallWidth > 0f)
                {
                    for (int side = -1; side <= 1; side += 2)
                        foreach (var range in PathTunnelParts.Outside(0, 1, tunnels))
                            RoadPath.CreateSplineMesh(complete, roadSpline, variant + "/wall", wallWidth,
                                side * (width + wallWidth) * .5f, RoadPartHeights.WALL_ABOVE_SPLINE,
                                mirrorX: side < 0, fallbackPrefabName: type + "/wall", clipFrom: range.From, clipTo: range.To);
                }
                PathTunnelParts.GenerateWall(complete, roadSpline, path, width * .5f, wallWidth, true, tunnels);
                PathTunnelParts.GenerateWall(complete, roadSpline, path, width * .5f, wallWidth, false, tunnels);
                PathTunnelParts.GenerateCeiling(complete, roadSpline, path, width * .5f, width * .5f, tunnels);
                if (body == null) RoadPath.CreateFallbackRoadBody(complete, roadSpline, width);
            }
            var collision = RoadPath.BuildColliderMeshFromSpline(roadSpline, width);
            if (collision != null) colliderParts.Add(collision);
        }
        if (elevated ? progress > 0f : progress < 1f)
            PlaceVehicles(root, points, width, elevated && progress >= .25f ? built : FrontFraction(progress, elevated), elevated, PathMeshFactory.GetModelType(path) == "track", underground ? clearance : float.PositiveInfinity, progress >= .25f);

        Mesh collider = null;
        if (colliderParts.Count > 0)
        {
            var combine = new CombineInstance[colliderParts.Count];
            for (int i = 0; i < combine.Length; i++) combine[i] = new CombineInstance { mesh = colliderParts[i], transform = Matrix4x4.identity };
            collider = new Mesh { name = "ConstructionCollider", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            collider.CombineMeshes(combine, true, false);
            // Release only completed-section collider meshes unused for rendering.
            var visible = new HashSet<Mesh>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) visible.Add(filter.sharedMesh);
            foreach (var mesh in colliderParts) if (!visible.Contains(mesh)) UnityEngine.Object.Destroy(mesh);
        }
        return new PathMeshResult(collider, true, root);
    }

    private static GameObject AddMesh(GameObject root, string name, Mesh mesh, Material material)
    {
        if (mesh == null) return null;
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        return go;
    }

    private static void PlaceVehicles(GameObject root, List<Vector3> points, float width, float front,
        bool elevated, bool track, float tunnelClearance, bool installingDeck)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        if (length < 5f || width < 2.6f || ModelManager.Instance == null) return;
        string[] models = elevated ? new[] { installingDeck ? "construction_crane" : "construction_excavator", "construction_excavator" }
            : track ? new[] { "construction_excavator", "construction_roller" }
            : new[] { "construction_paver", "construction_roller", "construction_excavator" };
        bool onDeck = elevated && installingDeck;
        float availableEnd = front * length;
        for (int i = 0; i < models.Length; i++)
        {
            var prefab = ModelManager.Instance.GetModel("Models/Props/" + models[i]);
            if (prefab == null) continue;
            Bounds bounds = default;
            float distance = front * length + (i == 0 ? 3f : i == 1 ? -5f : 11f);
            if (onDeck)
            {
                var prefabBounds = PillarClearance.GetPrefabBounds(prefab);
                if (!prefabBounds.HasValue) continue;
                bounds = Utils.TransformBounds(prefabBounds.Value, Matrix4x4.Scale(prefab.transform.localScale));
                if (Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)) > width * .5f) continue;
                // Reserve actual vehicle length/margin behind the installed girder end; place each following vehicle behind its predecessor's rear.
                distance = availableEnd - Mathf.Max(4f, bounds.max.z + .5f);
                if (distance + bounds.min.z < .5f) continue;
            }
            // Do not clamp vehicles to short-road ends and overlap them.
            if (distance < 3f || distance > length - 3f) continue;
            var sample = PathDrawContext.SlicePolylineByFraction(points, distance / length, Math.Min(1, (distance + .5f) / length));
            if (sample.Count < 2) continue;
            Vector3 forward = sample[sample.Count - 1] - sample[0];
            if (!onDeck) forward.y = 0f;
            if (forward.sqrMagnitude < .0001f) continue;
            Quaternion rotation = Quaternion.LookRotation(forward);
            Vector3 position = sample[0];
            bool inTunnel = TunnelSectionUtil.IsTunnelPoint(position, tunnelClearance);
            if (inTunnel && !UndergroundView.IsActive) continue;
            if (onDeck)
            {
                position += Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE - rotation * Vector3.up * bounds.min.y;
                availableEnd = distance + bounds.min.z;
            }
            else
            {
                position.y = inTunnel ? sample[0].y : Utils.GetGroundHeight(position) + RoadPartHeights.ROAD_SPLINE_OFFSET;
                if (!elevated && distance >= front * length)
                    position.y += LocalConstants.CONSTRUCTION_DIRT_THICKNESS + ConstructionMeshUtil.DirtNoise(position.x, position.z) * LocalConstants.CONSTRUCTION_DIRT_BUMP_AMPLITUDE;
                else if (!elevated) position.y = sample[0].y + RoadPartHeights.BODY_ABOVE_SPLINE;
            }
            var vehicle = UnityEngine.Object.Instantiate(prefab, root.transform);
            vehicle.name = models[i];
            vehicle.transform.SetPositionAndRotation(position, rotation);
            vehicle.AddComponent<PathSection>().IsUnderground = inTunnel;
            foreach (var renderer in vehicle.GetComponentsInChildren<MeshRenderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++) materials[m] = RoadPath.GetOrCreateRoadMaterial(materials[m]);
                renderer.sharedMaterials = materials;
            }
        }
    }
}

/// <summary>Rendering extension for lists. Keep progress beside normal PathSummary without editing generated schemas.</summary>
[Serializable]
public sealed class ConstructionPathSummary : PathSummary
{
    public float? construction_progress;
}
