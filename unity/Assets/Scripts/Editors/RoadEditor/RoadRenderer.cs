using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// RoadRenderer: generates only 3D road editor preview meshes.
/// RoadPlanManager handles 2D overlay drawing.
/// </summary>
public partial class RoadRenderer : MonoBehaviour
{
    private MeshFilter previewMeshFilter;
    private MeshRenderer previewMeshRenderer;
    private Mesh previewMesh;
    private Material previewMaterial;
    private GameObject currentPreviewObject;

    // Most recently generated preview geometry and parameters.
    // Used to detect duplicates and avoid destruction/regeneration when called again with identical geometry.
    private List<Vector3> lastPreviewPoints;
    private int lastPathType;
    private int lastPathSubtype;
    private bool lastCurved;
    private int lastPlatformSide;
    private string lastHouseEnds;
    private string lastPassageMode;

    private void OnGUI()
    {
        if (isSection || lastPathType != Constants.PATH_STATION_HOUSE ||
            lastPreviewPoints == null || lastPreviewPoints.Count < 2 || Camera.main == null) return;
        for (int i = 0; i < 2; i++)
        {
            Vector3 point = Camera.main.WorldToScreenPoint(lastPreviewPoints[i == 0 ? 0 : lastPreviewPoints.Count - 1]);
            if (point.z <= 0) continue;
            GUI.Box(new Rect(point.x - 12, Screen.height - point.y - 32, 24, 24), i == 0 ? "A" : "B");
        }
    }

    /// <summary>
    /// Hook receiving construction preview tracks as provisional partners in completed-track drawing (set by PathManager).
    /// Arguments are per-line temporary PathSummary (negative path_id), drawing points, and the path_id of the plan being shape-edited. null clears registration.
    /// </summary>
    public static System.Action<List<PathSummary>, List<List<Vector3>>, int> PreviewTrackSink;

    // Whether provisional partners are registered (call the hook only when clearing), and the last input signature (skip unchanged inputs).
    private bool registeredPreviewTracks;
    private int registeredPreviewHash;

    private float[] lastLateralOffsets;
    private int[] lastTravelDirections;
    private bool hasPreviewState = false;
    private List<List<Vector3>> lastProfiles;

    /// <summary>
    /// Create preview mesh components.
    /// </summary>
    public void CreatePreviewMeshComponents(Material material)
    {
        this.previewMaterial = material;

        GameObject previewObj = GameObject.Find("RoadPreview");
        if (previewObj == null)
        {
            previewObj = new GameObject("RoadPreview");
        }

        previewMeshFilter = previewObj.GetComponent<MeshFilter>();
        if (previewMeshFilter == null) previewMeshFilter = previewObj.AddComponent<MeshFilter>();

        previewMeshRenderer = previewObj.GetComponent<MeshRenderer>();
        if (previewMeshRenderer == null) previewMeshRenderer = previewObj.AddComponent<MeshRenderer>();

        // Ensure the existing preview mesh is cleared.
        // Prevent the previous mesh from remaining when reusing the same GameObject.
        ResetPreview();
    }

    /// <summary>
    /// Updates 3D preview meshes from subdivided points.
    /// platformSide is the station platform side (Constants.PLATFORM_SIDE_*).
    /// Makes station variant preview platforms extend on the specified side
    /// (TrackPath.ResolvePlatformExtents reads platform_side from the temporary PathSummary).
    /// lateralOffsets contains each compound path/parallel track's offset (m; right of construction direction is positive).
    /// If null/empty, draw a single path centered on the received points. Otherwise,
    /// shift the centerline by each offset and generate one preview per line (2 for double track).
    /// travelDirections contains each line's direction (Constants.DIR_*), indexed like lateralOffsets.
    /// Station platform resolution (TrackPath.ResolvePlatformExtents) references running direction,
    /// so this makes platforms extend on the same side as after confirmation. Missing entries
    /// use DIR_BOTH (bidirectional).
    /// Matches the appearance of UI expansion into parallel single-track paths on confirmation.
    /// profiles (post-earthworks profiles, one per line) replace only each line's drawn points. Waypoints of a straight
    /// path always come from subdividePoints: the profile is a dense sample, and towers and piers stand at waypoints.
    /// RegisterPreviewTracks registers preview tracks as provisional partners, joining existing tracks and each other
    /// through nearby-track stitching (TrackAdjacency) and junction S-curves (TrackEndTransition).
    /// </summary>
    public void UpdateRoadPreview(List<Vector3> subdividePoints, int pathType, int pathSubtype, bool curved = true, int platformSide = Constants.PLATFORM_SIDE_OUTSIDE, float[] lateralOffsets = null, int[] travelDirections = null, string houseEnds = "both", List<List<Vector3>> profiles = null, string passageMode = "elevated")
    {
        if (previewMeshFilter == null || previewMeshRenderer == null) return;

        if (subdividePoints == null || subdividePoints.Count < 2)
        {
            ResetPreview();
            return;
        }

        // Skip regeneration for unchanged geometry/parameters (avoids destruction/regeneration thrashing).
        if (hasPreviewState && lastProfiles == profiles &&
            lastPathType == pathType && lastPathSubtype == pathSubtype &&
            lastCurved == curved &&
            lastPlatformSide == platformSide && lastHouseEnds == houseEnds && lastPassageMode == passageMode &&
            OffsetsApproximatelyEqual(lastLateralOffsets, lateralOffsets) &&
            IntArraysEqual(lastTravelDirections, travelDirections) &&
            PointsApproximatelyEqual(lastPreviewPoints, subdividePoints))
        {
            return;
        }

        var freezeProbe = System.Diagnostics.Stopwatch.StartNew(); // [Freeze investigation] Remove after investigation.

        // Delete the previous preview object.
        if (currentPreviewObject != null)
        {
            currentPreviewObject.SetActive(false);
            Destroy(currentPreviewObject);
            currentPreviewObject = null;
        }
        ReleaseOwnedMeshes();
        previewMeshFilter.mesh = null;

        if (lateralOffsets == null || lateralOffsets.Length == 0)
        {
            // Single path: generate one path using the received points directly as its centerline.
            GenerateComponentPreview(profiles != null ? profiles[0] : subdividePoints, subdividePoints, pathType, pathSubtype, curved, platformSide, Constants.DIR_BOTH, previewMaterial, null, houseEnds, profiles != null, passageMode, 0);
        }
        else
        {
            // Compound path (parallel tracks): generate one line at each lateral centerline offset,
            // grouping them under one container (destroy the entire container together).
            GameObject container = new GameObject("RoadPreviewComponents");
            container.transform.SetParent(previewMeshFilter.transform, false);
            currentPreviewObject = container;
            for (int i = 0; i < lateralOffsets.Length; i++)
            {
                List<Vector3> anchorLine = OffsetPolylineLateral(subdividePoints, lateralOffsets[i]);
                List<Vector3> offsetPoints = profiles != null && i < profiles.Count ? profiles[i] : anchorLine;
                int travelDirection = (travelDirections != null && i < travelDirections.Length)
                    ? travelDirections[i] : Constants.DIR_BOTH;
                GenerateComponentPreview(offsetPoints, anchorLine, pathType, pathSubtype, curved, platformSide, travelDirection, previewMaterial, container, houseEnds, profiles != null, passageMode, i);
            }
        }

        // Record generated geometry/parameters to skip regeneration if the next input is identical.
        if (lastPreviewPoints == null) lastPreviewPoints = new List<Vector3>(subdividePoints.Count);
        lastPreviewPoints.Clear();
        lastPreviewPoints.AddRange(subdividePoints);
        lastPathType = pathType;
        lastPathSubtype = pathSubtype;
        lastCurved = curved;
        lastPlatformSide = platformSide;
        lastHouseEnds = houseEnds;
        lastPassageMode = passageMode;
        lastLateralOffsets = lateralOffsets != null ? (float[])lateralOffsets.Clone() : null;
        lastTravelDirections = travelDirections != null ? (int[])travelDirections.Clone() : null;
        lastProfiles = profiles;
        hasPreviewState = true;
        Debug.Log($"[フリーズ調査] 詳細プレビュー生成 {freezeProbe.Elapsed.TotalMilliseconds:F1}ms (点数={subdividePoints.Count})");
    }

    /// <summary>
    /// Generates and places one component's preview mesh.
    /// With parent=null, assigns currentPreviewObject / previewMeshFilter directly as a single path;
    /// otherwise parents it as one compound-path line.
    /// Passes a temporary PathSummary to PathMeshFactory through the same pipeline as real paths.
    /// Including travelDirection (Constants.DIR_*) in the temporary PathSummary ensures station
    /// platform sides and one-way track arrows match their confirmed appearance.
    /// componentPoints use design coordinates (ground + relative Y); subtract base_height
    /// for drawing points passed to the generator (ToDrawPoints). Keep temporary PathSummary start/end/waypoints as design values.
    /// </summary>
    private void GenerateComponentPreview(List<Vector3> componentPoints, List<Vector3> anchorLine, int pathType, int pathSubtype, bool curved, int platformSide, int travelDirection, Material previewMaterial, GameObject parent, string houseEnds, bool designed = false, string passageMode = "elevated", int componentIndex = 0)
    {
        PathSummary tempPathData = BuildPreviewSummary(componentPoints, anchorLine, pathType, pathSubtype, curved, platformSide, travelDirection, houseEnds, designed, passageMode);
        // Give each track a unique negative path_id matching its provisional registration (RegisterPreviewTracks).
        tempPathData.path_id = PreviewPathId(componentIndex, pathType);
        PathMeshResult meshResult = PathMeshFactory.GeneratePathMesh(tempPathData, ToDrawPoints(componentPoints, pathSubtype));
        if (meshResult == null) return;
        // The generator's band is an unused click mesh when a detailed GameObject exists.
        // This renderer manages its lifetime, including fallback rendering.
        if (meshResult.Mesh != null) ownedMeshes.Add(meshResult.Mesh);

        if (meshResult.GeneratedObject != null)
        {
            GameObject obj = meshResult.GeneratedObject;
            obj.transform.SetParent(parent != null ? parent.transform : previewMeshFilter.transform, false);
            if (parent == null)
            {
                currentPreviewObject = obj;
            }

            if (previewMaterial != null)
            {
                Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
                foreach (var rend in renderers)
                {
                    if (rend.GetComponent<OverlayMeshRenderer>() != null) continue; // Overlays keep their own material, as in VisualModeObject.
                    rend.sharedMaterial = previewMaterial;
                }
            }
        }
        else if (meshResult.Mesh != null)
        {
            if (parent == null)
            {
                previewMesh = meshResult.Mesh;
                previewMeshFilter.mesh = previewMesh;

                if (previewMaterial != null)
                {
                    int subMeshCount = previewMesh.subMeshCount;
                    Material[] mats = new Material[subMeshCount];
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        mats[i] = previewMaterial;
                    }
                    previewMeshRenderer.sharedMaterials = mats;
                }
            }
            else
            {
                // Compound path fallback white strips cannot share previewMeshFilter,
                // so create a child object and mesh for each component.
                GameObject fallbackObj = new GameObject("ComponentFallbackMesh");
                fallbackObj.transform.SetParent(parent.transform, false);
                MeshFilter mf = fallbackObj.AddComponent<MeshFilter>();
                MeshRenderer mr = fallbackObj.AddComponent<MeshRenderer>();
                mf.mesh = meshResult.Mesh;
                if (previewMaterial != null)
                {
                    int subMeshCount = meshResult.Mesh.subMeshCount;
                    Material[] mats = new Material[subMeshCount];
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        mats[i] = previewMaterial;
                    }
                    mr.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// Temporary PathSummary for one preview line. Start/end/waypoints (when straight) use design values (relative Y).
    /// Waypoints are the interior points of anchorLine (the line's anchors); componentPoints are the drawn points.
    /// Tracks retain endpoint directions as endpoint controls even on curves, for node direction checks (junction S-curves and plateless straight connections).
    /// </summary>
    private static PathSummary BuildPreviewSummary(List<Vector3> componentPoints, List<Vector3> anchorLine, int pathType, int pathSubtype, bool curved, int platformSide, int travelDirection, string houseEnds, bool designed, string passageMode)
    {
        Vector3 startPos = componentPoints[0];
        Vector3 endPos = componentPoints[componentPoints.Count - 1];
        float startGroundY = Utils.GetPathSurfaceHeight(startPos);
        float endGroundY = Utils.GetPathSurfaceHeight(endPos);
        float startRelY = startPos.y - startGroundY;
        float endRelY = endPos.y - endGroundY;

        List<object> waypointsList = new List<object>();
        if (!curved)
        {
            for (int i = 1; i < anchorLine.Count - 1; i++)
            {
                Vector3 wpPos = anchorLine[i];
                float wpGroundY = Utils.GetPathSurfaceHeight(wpPos);
                float wpRelY = wpPos.y - wpGroundY;
                // GetWaypointList reads only {position:[x,relY,z]} dictionaries (raw arrays are silently discarded).
                waypointsList.Add(new Dictionary<string, object> { ["position"] = new List<object> { (object)wpPos.x, (object)wpRelY, (object)wpPos.z } });
            }
        }

        PathSummary tempPathData = new PathSummary
        {
            path_type = pathType,
            path_variant = pathSubtype,
            waypoints = waypointsList,
            curved = curved,
            platform_side = platformSide,
            house_ends = houseEnds,
            passage_mode = passageMode,
            travel_direction = travelDirection,
            follow_terrain = Constants.VALUE_NONE, // Explicitly mark unset because the int default 0 means non-following.
            start_point = new List<object> { (object)startPos.x, (object)startRelY, (object)startPos.z },
            end_point = new List<object> { (object)endPos.x, (object)endRelY, (object)endPos.z }
        };
        if (curved && componentPoints.Count >= 3 && (pathType == Constants.PATH_TRACK || pathType == Constants.PATH_MONORAIL))
        {
            Vector3 startControl = componentPoints[1], endControl = componentPoints[componentPoints.Count - 2];
            tempPathData.start_control = new List<object> { (object)startControl.x, (object)(startControl.y - startGroundY), (object)startControl.z };
            tempPathData.end_control = new List<object> { (object)endControl.x, (object)(endControl.y - endGroundY), (object)endControl.z };
        }

        if (designed)
        {
            // Prevent the generator from reprojecting post-earthworks design samples onto raw terrain.
            tempPathData.follow_terrain = 0;
            tempPathData.station_grade_profile = new List<object>();
            foreach (var p in componentPoints)
                tempPathData.station_grade_profile.Add(new List<object> { p.x, p.y, p.z });
        }
        return tempPathData;
    }

    /// <summary>Temporary negative path_id for preview track index. Remains 0 for non-tracks.</summary>
    private static int PreviewPathId(int index, int pathType)
        => pathType == Constants.PATH_TRACK || pathType == Constants.PATH_MONORAIL ? -(index + 1) : 0;

    /// <summary>
    /// Registers construction preview tracks with PathManager as provisional partners in completed-track drawing
    /// (junction S-curves, parallel stitching, buffer stops) through PreviewTrackSink. centerPoints is the finalized centerline, excluding provisional endpoints/dragged shapes.
    /// null or non-track types clear registration. Register parallel tracks separately using lateralOffsets (or post-earthworks profiles).
    /// excludedPathId is the shape-edited plan (0 if none). Do nothing if inputs match the previous registration.
    /// </summary>
    public void RegisterPreviewTracks(List<Vector3> centerPoints, int pathType, int pathSubtype, bool curved, int platformSide,
        float[] lateralOffsets, int[] travelDirections, string houseEnds, string passageMode, int excludedPathId, List<List<Vector3>> profiles = null)
    {
        if (PreviewTrackSink == null) return;
        bool track = pathType == Constants.PATH_TRACK || pathType == Constants.PATH_MONORAIL;
        if (!track || centerPoints == null || centerPoints.Count < 2)
        {
            ClearPreviewTracks();
            return;
        }
        int hash = PreviewInputHash(centerPoints, pathSubtype, curved, platformSide, lateralOffsets, travelDirections, excludedPathId, profiles);
        if (registeredPreviewTracks && hash == registeredPreviewHash) return;

        int count = profiles != null ? profiles.Count : lateralOffsets != null && lateralOffsets.Length > 0 ? lateralOffsets.Length : 1;
        var summaries = new List<PathSummary>(count);
        var drawPoints = new List<List<Vector3>>(count);
        for (int i = 0; i < count; i++)
        {
            List<Vector3> anchorLine = lateralOffsets != null && i < lateralOffsets.Length
                ? OffsetPolylineLateral(centerPoints, lateralOffsets[i]) : centerPoints;
            List<Vector3> component = profiles != null ? profiles[i] : anchorLine;
            if (component == null || component.Count < 2) continue;
            int direction = travelDirections != null && i < travelDirections.Length ? travelDirections[i] : Constants.DIR_BOTH;
            PathSummary summary = BuildPreviewSummary(component, anchorLine, pathType, pathSubtype, curved, platformSide, direction, houseEnds, profiles != null, passageMode);
            summary.path_id = PreviewPathId(i, pathType);
            summaries.Add(summary);
            drawPoints.Add(ToDrawPoints(component, pathSubtype));
        }
        registeredPreviewTracks = true;
        registeredPreviewHash = hash;
        PreviewTrackSink(summaries, drawPoints, excludedPathId);
        // Even unchanged sections redraw stitching between preview lines using the new registration.
        foreach (var section in sections)
            if (section != null) section.hasPreviewState = false;
    }

    /// <summary>Clears provisional partner registration (redraws existing tracks in their original shapes).</summary>
    private void ClearPreviewTracks()
    {
        if (!registeredPreviewTracks) return;
        registeredPreviewTracks = false;
        PreviewTrackSink?.Invoke(null, null, 0);
    }

    private static int PreviewInputHash(List<Vector3> points, int variant, bool curved, int platformSide, float[] offsets, int[] directions, int excluded, List<List<Vector3>> profiles)
    {
        var hash = new System.HashCode();
        hash.Add(variant); hash.Add(curved); hash.Add(platformSide); hash.Add(excluded);
        hash.Add(points.Count);
        foreach (var p in points) { hash.Add(Mathf.RoundToInt(p.x * 10f)); hash.Add(Mathf.RoundToInt(p.y * 10f)); hash.Add(Mathf.RoundToInt(p.z * 10f)); }
        if (offsets != null) foreach (float o in offsets) hash.Add(Mathf.RoundToInt(o * 100f));
        if (directions != null) foreach (int d in directions) hash.Add(d);
        if (profiles != null)
            foreach (var line in profiles)
                if (line != null) foreach (var p in line) { hash.Add(Mathf.RoundToInt(p.x * 10f)); hash.Add(Mathf.RoundToInt(p.y * 10f)); hash.Add(Mathf.RoundToInt(p.z * 10f)); }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Subtracts base_height (3D render height offset, e.g. power lines) from design coordinates (ground + relative Y)
    /// to produce points for the generator. Matches PathManager.ComputeDesignedPathPoints for committed paths;
    /// returns points unchanged for variants with base_height=0.
    /// </summary>
    private static List<Vector3> ToDrawPoints(List<Vector3> points, int pathVariant)
    {
        float baseHeight = PathManager.GetPathBaseHeight(pathVariant);
        if (baseHeight == 0f) return points;
        var result = new List<Vector3>(points.Count);
        foreach (var p in points) result.Add(new Vector3(p.x, p.y - baseHeight, p.z));
        return result;
    }

    /// <summary>
    /// Returns new points shifted laterally by lateral (m; right of construction direction is positive).
    /// Each offset uses the miter average of adjacent segment right normals, scaled by 1/cos(θ/2)
    /// to preserve spacing around outer corners (capped at 2x for sharp angles).
    /// Matches UI expansion logic (city-genesis/tauri pathOffset.ts computeAnchorOffsetDirs)
    /// to align preview and confirmed placement. Preserves original Y.
    /// </summary>
    public static List<Vector3> OffsetPolylineLateral(List<Vector3> points, float lateral)
    {
        int n = points.Count;
        var result = new List<Vector3>(n);
        if (n < 2)
        {
            result.AddRange(points);
            return result;
        }

        // Right unit normal of each XZ segment (zero vector for zero-length segments).
        var segNormals = new Vector2[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = points[i + 1].x - points[i].x;
            float dz = points[i + 1].z - points[i].z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            segNormals[i] = len < 1e-6f ? Vector2.zero : new Vector2(dz / len, -dx / len);
        }

        for (int i = 0; i < n; i++)
        {
            Vector2 dir;
            if (i == 0)
            {
                dir = segNormals[0];
            }
            else if (i == n - 1)
            {
                dir = segNormals[n - 2];
            }
            else
            {
                Vector2 n1 = segNormals[i - 1];
                Vector2 n2 = segNormals[i];
                Vector2 m = n1 + n2;
                if (m.sqrMagnitude < 1e-12f)
                {
                    // Adjacent segments reverse exactly (180°). Use one side's normal instead.
                    dir = n1;
                }
                else
                {
                    m.Normalize();
                    // Miter correction: preserves parallel spacing inside/outside corners (capped at 2x for sharp angles).
                    float cosHalf = Vector2.Dot(m, n1);
                    dir = m * (1f / Mathf.Max(0.5f, cosHalf));
                }
            }
            Vector3 p = points[i];
            result.Add(new Vector3(p.x + dir.x * lateral, p.y, p.z + dir.y * lateral));
        }
        return result;
    }

    /// <summary>
    /// Tests whether two lateral offset arrays are identical (duplicate detection for preview generation).
    /// null and empty both mean a single path and are treated as equal.
    /// </summary>
    private static bool OffsetsApproximatelyEqual(float[] a, float[] b)
    {
        int lenA = a?.Length ?? 0;
        int lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
        }
        return true;
    }

    /// <summary>
    /// Tests whether two int arrays (travel directions) are identical (duplicate detection for preview generation).
    /// null and empty both mean unspecified and are treated as equal.
    /// </summary>
    private static bool IntArraysEqual(int[] a, int[] b)
    {
        int lenA = a?.Length ?? 0;
        int lenB = b?.Length ?? 0;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Tests whether two point sequences are nearly identical (duplicate detection for preview generation).
    /// </summary>
    private static bool PointsApproximatelyEqual(List<Vector3> a, List<Vector3> b)
    {
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if ((a[i] - b[i]).sqrMagnitude > 1e-6f) return false;
        }
        return true;
    }

    /// <summary>
    /// Clears the preview.
    /// </summary>
    public void ResetPreview()
    {
        if (!isSection) ClearPreviewTracks();
        foreach (var section in sections)
            if (section != null) section.ResetPreview();
        if (currentPreviewObject != null)
        {
            currentPreviewObject.SetActive(false);
            Destroy(currentPreviewObject);
            currentPreviewObject = null;
        }

        if (previewMesh != null)
        {
            previewMesh = null;
        }
        ReleaseOwnedMeshes();
        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }

        // Reset duplicate detection so the next call always regenerates.
        hasPreviewState = false;
        lastPreviewPoints?.Clear();
        lastStripLines = null;
    }
}
