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
/// </summary>
[System.Serializable]
public struct PathBuildPoint
{
    public Vector3 position;
    public PathPointRole role;

    /// <summary>Point inserted by a route proposal. Moving it promotes it to a player-specified point.</summary>
    public bool proposalGenerated;

    /// <summary>Outgoing control, only for start/waypoints. Corresponds to segment Bezier P1.</summary>
    public Vector3? control_out;

    /// <summary>Incoming control, only for end/waypoints. Corresponds to segment Bezier P2.</summary>
    public Vector3? control_in;

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
        position = pos;
        role = r;
        proposalGenerated = false;
        control_out = null;
        control_in = null;
        control_out_manual = false;
        control_in_manual = false;
        control_height_manual = false;
        embankmentHeight = 0f;
    }
}
