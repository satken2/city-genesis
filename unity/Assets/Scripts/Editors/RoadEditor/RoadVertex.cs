using UnityEngine;

/// <summary>
/// Role of a point placed in the road editor.
/// Distinguishes start, waypoints, and end.
/// Exactly one Start is first and one End is last.
/// </summary>
public enum PathPointRole
{
    Start,
    Waypoint,
    End
}

/// <summary>
/// Road editor placement point data.
///
/// Each point stores cubic Bezier controls:
///   control_out ... outgoing control toward the next point (Bezier P1)
///   control_in  ... incoming control from the previous point (Bezier P2)
///
/// Start has only control_out; end has only control_in.
/// Waypoints have both.
/// Automatically calculated from Catmull-Rom splines; horizontal XZ can be edited manually by dragging.
/// Unless control_height_manual, Y comes from the anchor's automatic longitudinal grade.
///
/// Heights are stored relative to the anchor's Utils.GetPathSurfaceHeight, as confirmed paths and the backend store them.
/// position and the controls read and write absolute coordinates against the terrain loaded now. Terrain LOD changes
/// with camera distance, so storing absolute Y would shift the relative height sent for distant anchors.
/// </summary>
[System.Serializable]
public struct PathBuildPoint
{
    private float x;
    private float z;
    private float relativeHeight;
    private Vector3? controlOutRelative;
    private Vector3? controlInRelative;

    public PathPointRole role;

    private float AnchorSurface => Utils.GetPathSurfaceHeight(new Vector3(x, 0f, z));

    public Vector3 position
    {
        get => new Vector3(x, AnchorSurface + relativeHeight, z);
        set
        {
            x = value.x;
            z = value.z;
            relativeHeight = value.y - AnchorSurface;
        }
    }

    /// <summary>Point inserted by a route proposal. Moving it promotes it to a player-specified point.</summary>
    public bool proposalGenerated;

    /// <summary>Outgoing control, only for start/waypoints. Corresponds to segment Bezier P1. Y is relative to the anchor's surface.</summary>
    public Vector3? control_out
    {
        get => ToAbsolute(controlOutRelative);
        set => controlOutRelative = ToRelative(value);
    }

    /// <summary>Incoming control, only for end/waypoints. Corresponds to segment Bezier P2. Y is relative to the anchor's surface.</summary>
    public Vector3? control_in
    {
        get => ToAbsolute(controlInRelative);
        set => controlInRelative = ToRelative(value);
    }

    /// <summary>
    /// True when the user manually adjusted control_out XZ.
    /// Automatic RecomputeAllControlPoints does not overwrite flagged controls' XZ.
    /// Anchor movement translates XZ by the same amount rather than recalculating it.
    /// Height is recalculated automatically unless control_height_manual.
    /// </summary>
    public bool control_out_manual;

    /// <summary>Manual flag for control_in. Same meaning as control_out_manual.</summary>
    public bool control_in_manual;

    /// <summary>
    /// True to fix manually adjusted control heights (Y). Set only for route proposal results and controls whose saved
    /// heights differ from the automatic longitudinal grade when reopening existing paths; not for handle dragging.
    /// Clear when this anchor or either neighbor moves (automatic grade changes).
    /// </summary>
    public bool control_height_manual;

    /// <summary>
    /// Embankment offset (m), -5 to +5.
    /// Road Y remains 0; Dynamic terrain deforms by this height at construction start.
    /// Preview road meshes are raised by this amount.
    /// Negative values allow cuttings.
    /// </summary>
    public float embankmentHeight;

    public PathBuildPoint(Vector3 pos, PathPointRole r)
    {
        x = pos.x;
        z = pos.z;
        relativeHeight = 0f;
        controlOutRelative = null;
        controlInRelative = null;
        role = r;
        proposalGenerated = false;
        control_out_manual = false;
        control_in_manual = false;
        control_height_manual = false;
        embankmentHeight = 0f;
        position = pos;
    }

    private Vector3? ToAbsolute(Vector3? relative)
    {
        return relative.HasValue ? relative.Value + Vector3.up * AnchorSurface : (Vector3?)null;
    }

    private Vector3? ToRelative(Vector3? absolute)
    {
        return absolute.HasValue ? absolute.Value - Vector3.up * AnchorSurface : (Vector3?)null;
    }
}
