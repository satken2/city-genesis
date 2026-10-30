using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Shape synchronization with Tauri's 2D path editor (path_shape_editor).
/// This class (the 3D editor) remains authoritative; the 2D panel sends node/control operations
/// as <see cref="ShapeCommand"/> and receives results in existing edit_path:update_shape (full state).
/// 2D operations move only XZ, preserving 3D node elevation/embankment. Control heights
/// come from anchor longitudinal grade as in 3D dragging. No snapping is applied.
/// </summary>
public partial class RoadPlanManager
{
    /// <summary>Parameters for edit_path:shape_command. Fields used depend on op.</summary>
    public struct ShapeCommand
    {
        /// <summary>select_node / move_node / move_control / insert_node / remove_node</summary>
        public string op;
        /// <summary>Target node index (select_node / move_node / move_control / remove_node).</summary>
        public int index;
        /// <summary>Target edge index: between points[edgeIndex] and points[edgeIndex+1] (insert_node).</summary>
        public int edgeIndex;
        /// <summary>"in" (control_in) / "out" (control_out). move_control only.</summary>
        public string which;
        /// <summary>World X (move_node / move_control / insert_node).</summary>
        public float x;
        /// <summary>World Z (move_node / move_control / insert_node).</summary>
        public float z;
        public string revision;
        public string routeJson;
        public string alignmentJson;
        public string junctionJson;
        /// <summary>apply_layout: sequence of generated [start, end] path pairs from construction method expansion (JSON).</summary>
        public string layoutJson;
    }

    /// <summary>Alignment sample spacing sent to 2D (m). Downsamples 1m estimates to limit edit_path:update_shape size.</summary>
    private const float AlignmentSyncStrideM = 5f;

    /// <summary>Radii above this are sent to 2D as null, meaning straight (JSON cannot represent infinity).</summary>
    private const float AlignmentSyncStraightRadius = 100000f;

    // Most recent uniformly spaced 1m world samples and their radii/grades evaluated by CheckAlignmentViolations.
    // Shape changes invoke InvalidateShapeCache → cachedAlignmentViolations=null; rebuilt on the next evaluation.
    private List<Vector3> cachedAlignmentSamplesWorld;
    private float[] cachedAlignmentRadius;
    private float[] cachedAlignmentGradePct;
    private float cachedAlignmentRadiusLimit;

    private sealed class ValidatedAlignment
    {
        public float? min_curve_radius;
        public float max_grade;
        public float curve_radius_limit;
        public float grade_limit;
        public float[][] curve_violations;
        public float[][] grade_violations;
        public AlignmentPreview preview;
    }

    private sealed class AlignmentPreview
    {
        public double?[][] samples;
        public AlignmentWarning[] warnings;
    }

    private sealed class AlignmentWarning
    {
        public bool curve;
        public float value;
        public float limit;
        public float[] position;
        public float[][] points;
    }

    /// <summary>Track connection violation callout. text is the reason translated by Tauri; position is the world contact point.</summary>
    private sealed class JunctionLabel
    {
        public float[] position;
        public string text;
    }

    private ValidatedAlignment[] authoritativeAlignments;

    /// <summary>Accepts only the full-validation generation. Re-notification changes no geometry and requests no revalidation.</summary>
    private void ApplyAuthoritativeAlignments(string revision, string json, string junctionJson)
    {
        // Unused for grids: validation concerns alignment of expanded paths, not the polyline of placed points.
        if (revision != routeRevision || IsGridBuild) return;
        ValidatedAlignment[] alignments;
        JunctionLabel[] junctions;
        try
        {
            alignments = JsonConvert.DeserializeObject<ValidatedAlignment[]>(json ?? "null");
            junctions = JsonConvert.DeserializeObject<JunctionLabel[]>(junctionJson ?? "null");
        }
        catch (JsonException) { return; }
        authoritativeAlignments = alignments;
        junctionLabels.Clear();
        foreach (var junction in junctions ?? System.Array.Empty<JunctionLabel>())
        {
            if (junction?.position == null || junction.position.Length < 3) continue;
            junctionLabels.Add(new AlignmentLabel {
                junction = true, text = junction.text,
                position = new Vector3(junction.position[0], junction.position[1], junction.position[2]),
            });
        }
        cachedAlignmentViolations = null;
        EmitShapeUpdate();
    }

    private List<List<Vector3>> ApplyAuthoritativeAlignmentCache(List<List<Vector3>> violations)
    {
        foreach (var line in authoritativeAlignments)
        {
            cachedAlignmentMinRadius = Mathf.Min(cachedAlignmentMinRadius, line.min_curve_radius ?? float.PositiveInfinity);
            cachedAlignmentMaxGradePct = Mathf.Max(cachedAlignmentMaxGradePct, line.max_grade);
            cachedCurveViolation |= line.curve_violations?.Length > 0;
            cachedGradeViolation |= line.grade_violations?.Length > 0;
            foreach (var warning in line.preview?.warnings ?? System.Array.Empty<AlignmentWarning>())
            {
                var run = new List<Vector3>();
                foreach (var p in warning.points) run.Add(new Vector3(p[0], p[1], p[2]));
                violations.Add(run);
                alignmentLabels.Add(new AlignmentLabel {
                    curve = warning.curve, value = warning.value, limit = warning.limit,
                    position = new Vector3(warning.position[0], warning.position[1], warning.position[2]),
                    text = warning.curve
                        ? $"カーブ  R {warning.value:0.00}m\n下限 {warning.limit:0.##}m 未満"
                        : $"勾配  G {warning.value:0.00}%\n上限 {warning.limit:0.##}% 超過",
                });
            }
        }
        if (authoritativeAlignments.Length == 0) return violations;
        var first = authoritativeAlignments[0];
        cachedAlignmentRadiusLimit = first.curve_radius_limit;
        cachedAlignmentGradeLimit = first.grade_limit;
        var samples = first.preview?.samples;
        if (samples == null) return violations;
        cachedAlignmentSamplesWorld = new List<Vector3>();
        cachedAlignmentRadius = new float[samples.Length];
        cachedAlignmentGradePct = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            cachedAlignmentSamplesWorld.Add(new Vector3((float)s[0].Value, (float)s[5].Value, (float)s[1].Value));
            cachedAlignmentRadius[i] = s[2].HasValue ? (float)s[2].Value : float.PositiveInfinity;
            cachedAlignmentGradePct[i] = (float)s[3].Value;
        }
        return violations;
    }

    /// <summary>
    /// Applies shape operations from the 2D panel. Ignore outside Editing or while dragging in 3D
    /// (avoids simultaneous 3D/2D edits to the same point). Notify afterward as on 3D drag end.
    /// </summary>
    public void ApplyShapeCommand(ShapeCommand cmd)
    {
        if (phase != RoadEditorPhase.Editing) return;
        // Accept validation replies if a point was only grabbed without shape changes. Generation changes when it moves.
        if (cmd.op == "apply_alignment")
        {
            ApplyAuthoritativeAlignments(cmd.revision, cmd.alignmentJson, cmd.junctionJson);
            return;
        }
        if (cmd.op == "apply_layout")
        {
            ApplyGridLayout(cmd.revision, cmd.layoutJson);
            return;
        }
        if (isDragging) return;
        if (TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput) return;
        // Grids do not allow point editing (placed points specify an area).
        if (IsGridBuild) return;

        switch (cmd.op)
        {
            case "apply_route":
                ApplyRouteProposal(cmd.revision, cmd.routeJson);
                break;
            case "undo_route":
                UndoRouteProposal(cmd.revision);
                break;
            case "select_node":
                SelectPoint(cmd.index >= 0 && cmd.index < points.Count ? cmd.index : -1);
                EmitSelectNode();
                EmitShapeUpdate();
                break;

            case "move_node":
                {
                    if (cmd.index < 0 || cmd.index >= points.Count) return;
                    var pt = points[cmd.index];
                    // 2D moves only XZ. Preserve ground-relative height as with 3D dragging.
                    float relY = pt.position.y - Utils.GetPathSurfaceHeight(pt.position);
                    Vector3 newPos = new Vector3(cmd.x, 0f, cmd.z);
                    newPos.y = Utils.GetPathSurfaceHeight(newPos) + relY;
                    SelectPoint(cmd.index);
                    MoveAnchorPosition(cmd.index, newPos);
                    CommitShapeChange();
                    EmitSelectNode();
                }
                break;

            case "move_control":
                {
                    // Straight mode neither displays nor accepts edits to controls.
                    if (!CanEditControls || cmd.index < 0 || cmd.index >= points.Count) return;
                    bool moveIn = cmd.which == "in";
                    // Start has no control_in; end has no control_out.
                    if (moveIn && cmd.index == 0) return;
                    if (!moveIn && cmd.index == points.Count - 1) return;
                    MoveControlXZ(cmd.index, moveIn, cmd.x, cmd.z);
                    CommitShapeChange();
                }
                break;

            case "insert_node":
                // InsertPointOnEdge resolves inserted Y from the next placement height (same as a 3D edge click).
                InsertPointOnEdge(cmd.edgeIndex, new Vector3(cmd.x, 0f, cmd.z));
                break;

            case "remove_node":
                // RemovePoint guarantees at least 2 points remain.
                RemovePoint(cmd.index);
                break;

            default:
                Debug.LogWarning($"RoadPlanManager: 未知の shape_command op={cmd.op}");
                break;
        }
    }

    /// <summary>
    /// Reply to edit_path:query_shape. The 2D panel remounts whenever opened and misses change-only
    /// update_shape events, so resend the current shape once. Return phase=inactive outside a session as well.
    /// </summary>
    public void EmitShapeUpdateOnRequest()
    {
        EmitShapeUpdate();
    }

    /// <summary>
    /// Additional 2D synchronization fields in edit_path:update_shape:
    /// phase (placing/editing/inactive), selected_index, lateral_offsets, alignment (samples and labels).
    /// </summary>
    private Dictionary<string, object> BuildShapeSyncExtras()
    {
        string phaseName = phase == RoadEditorPhase.Placing ? "placing"
            : phase == RoadEditorPhase.Editing ? "editing" : "inactive";
        var extras = new Dictionary<string, object>
        {
            ["phase"] = phaseName,
            ["revision"] = routeRevision,
            ["selected_index"] = selectedPointIndex,
            ["lateral_offsets"] = new List<float>(LateralOffsets ?? System.Array.Empty<float>()),
            // Whether to show the 2D panel profile (height editing) strip and its lower height limit (master min_height).
            ["height_editable"] = IsHeightEditable(),
            ["min_height"] = GetMinHeightFromMaster(),
            ["route_proposal"] = BuildRouteProposalPayload(),
            ["max_span_violations"] = BuildMaxSpanViolationsPayload(),
        };
        var alignment = IsGridBuild ? null : BuildAlignmentPayload();
        if (alignment != null) extras["alignment"] = alignment;
        return extras;
    }

    /// <summary>Sends the same node-distance violations as the red lines. Send an empty array on resolution to clear UI warnings.</summary>
    private List<Dictionary<string, object>> BuildMaxSpanViolationsPayload()
    {
        var result = new List<Dictionary<string, object>>();
        var violations = CheckMaxSpanViolations();
        if (violations.Count == 0) return result;
        float limit = MasterDataStore.Instance.GetPathMaster(PathSubtype).max_span.Value;
        foreach (int index in violations)
        {
            Vector3 a = points[index].position, b = points[index + 1].position;
            float dx = b.x - a.x, dz = b.z - a.z;
            result.Add(new Dictionary<string, object>
            {
                ["segment_index"] = index,
                ["distance"] = Mathf.Sqrt(dx * dx + dz * dz),
                ["limit"] = limit,
            });
        }
        return result;
    }

    /// <summary>
    /// Returns alignment samples from the same local estimates as 3D section labels (1m spacing), downsampled to roughly 5m,
    /// worst-value labels for violations grouped by cause, and each anchor's position in the longitudinal profile.
    /// null for types not evaluated for alignment (infrastructure, etc.) or insufficient points.
    /// Each samples entry is [x, z, radius (null for straight), grade_pct, distance from start in m, absolute surface elevation, absolute ground elevation].
    /// Each nodes entry follows anchor order as [distance from start in m, absolute ground elevation] (horizontal position and reference height in the profile strip).
    /// </summary>
    private Dictionary<string, object> BuildAlignmentPayload()
    {
        CheckAlignmentViolations();
        var world = cachedAlignmentSamplesWorld;
        if (world == null || cachedAlignmentRadius == null || cachedAlignmentGradePct == null) return null;
        int n = world.Count;
        if (n < 2) return null;

        var dists = new float[n];
        var samples = new List<List<object>>();
        if (authoritativeAlignments != null)
        {
            // Send validated ground heights, distances, and metrics directly without local estimates.
            var validated = authoritativeAlignments[0].preview.samples;
            for (int i = 0; i < n; i++)
            {
                dists[i] = (float)validated[i][4].Value;
                var row = new List<object>();
                foreach (var value in validated[i]) row.Add(value.HasValue ? (object)value.Value : null);
                samples.Add(row);
            }
        }
        else
        {
            for (int i = 1; i < n; i++)
            {
                float dx = world[i].x - world[i - 1].x, dz = world[i].z - world[i - 1].z;
                dists[i] = dists[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
            }
            int stride = Mathf.Max(1, Mathf.RoundToInt(AlignmentSyncStrideM / AlignmentSampleStep));
            for (int i = 0; i < n; i += stride)
                samples.Add(BuildAlignmentSample(world[i], cachedAlignmentRadius[i], cachedAlignmentGradePct[i], dists[i]));
            if ((n - 1) % stride != 0)
                samples.Add(BuildAlignmentSample(world[n - 1], cachedAlignmentRadius[n - 1], cachedAlignmentGradePct[n - 1], dists[n - 1]));
        }

        // Anchor profile position = distance of the nearest XZ 1m sample. Ground height is sampled directly beneath the anchor.
        var nodes = new List<List<float>>(points.Count);
        foreach (var pt in points)
        {
            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                float dx = world[i].x - pt.position.x, dz = world[i].z - pt.position.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            nodes.Add(new List<float> { dists[best], Utils.GetGroundHeight(pt.position) });
        }

        var labels = new List<Dictionary<string, object>>();
        foreach (var label in alignmentLabels)
        {
            labels.Add(new Dictionary<string, object>
            {
                ["curve"] = label.curve,
                ["x"] = label.position.x,
                ["z"] = label.position.z,
                ["value"] = label.value,
                ["limit"] = label.limit,
            });
        }

        var warnings = new List<List<float[]>>();
        if (authoritativeAlignments != null)
        {
            foreach (var run in cachedAlignmentViolations)
            {
                var line = new List<float[]>();
                foreach (var p in run) line.Add(new[] { p.x, p.y, p.z });
                warnings.Add(line);
            }
        }

        return new Dictionary<string, object>
        {
            ["radius_limit"] = cachedAlignmentRadiusLimit,
            ["grade_limit"] = cachedAlignmentGradeLimit,
            ["min_curve_radius"] = float.IsInfinity(cachedAlignmentMinRadius) ? null : (object)cachedAlignmentMinRadius,
            ["max_grade"] = cachedAlignmentMaxGradePct,
            ["curve_violated"] = cachedCurveViolation,
            ["grade_violated"] = cachedGradeViolation,
            ["authoritative"] = authoritativeAlignments != null,
            ["warnings"] = authoritativeAlignments == null ? null : warnings,
            ["samples"] = samples,
            ["labels"] = labels,
            ["nodes"] = nodes,
        };
    }

    private static List<object> BuildAlignmentSample(Vector3 p, float radius, float gradePct, float dist)
    {
        object r = (float.IsInfinity(radius) || float.IsNaN(radius) || radius >= AlignmentSyncStraightRadius) ? null : (object)radius;
        return new List<object> { p.x, p.z, r, gradePct, dist, p.y, Utils.GetGroundHeight(p) };
    }
}
