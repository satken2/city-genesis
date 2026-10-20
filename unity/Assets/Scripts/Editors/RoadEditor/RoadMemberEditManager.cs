using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GeneratedModels;

/// <summary>
/// Singleton managing the 3D side of named-road section editing (STATE_EDIT_ROAD_MEMBERS).
///
/// Starts from Edit sections in the road detail panel; shows member sections as road-colored bands and
/// candidates from automatic straight extensions as faint bands (RoadFocusOverlay draws them). Each click on
/// a street/highway section only sends edit_road:toggle_path to the UI. The UI itself adds/removes membership
/// via API (POST /roads/{id}/members) and returns the full result through edit_road:set_members
/// (same responsibility split as selecting merge partners). Bands update on the response, not immediately on click.
///
/// This mode can also end outside the panel (direct state transition, session reset, Esc, starting another road),
/// so every End() sends edit_road:ended {road_id, session_id} to the UI once to clear the panel's
/// editing-sections indicator. Echo the session_id assigned by the UI at start (lets the panel
/// distinguish delayed end notifications from previous sessions; omit if absent at start).
/// </summary>
public class RoadMemberEditManager : MonoBehaviour
{
    public static RoadMemberEditManager Instance { get; private set; }

    private readonly HashSet<int> members = new HashSet<int>();
    private readonly HashSet<int> preview = new HashSet<int>();
    private Color color = Color.white;
    private PathManager pathManager;

    public bool IsActive { get; private set; }
    public int RoadId { get; private set; } = Constants.VALUE_NONE;

    /// <summary>Initial params.session_id (JSON numbers remain long and strings remain string). null if absent.</summary>
    public object SessionId { get; private set; }

    public static RoadMemberEditManager Ensure()
    {
        if (Instance == null) new GameObject("RoadMemberEditManager").AddComponent<RoadMemberEditManager>();
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Starts section edit mode. pathIds are the initial member sections.
    /// If another road or a new session for the same road starts while editing, report the previous session's end first.
    /// </summary>
    public void Begin(int roadId, Color roadColor, IEnumerable<int> pathIds, object sessionId = null)
    {
        if (IsActive && (RoadId != roadId || !Equals(SessionId, sessionId))) End();
        IsActive = true;
        RoadId = roadId;
        SessionId = sessionId;
        color = roadColor;
        Replace(members, pathIds);
        preview.Clear();
        RoadFocusOverlay.Ensure().SetEditLayers(color, members, preview);
        Debug.Log($"RoadMemberEditManager: 区間編集モード開始 road={roadId}, members={members.Count}");
    }

    /// <summary>
    /// edit_road:set_members. Replaces the full member and candidate sets.
    /// Ignore messages outside this mode or for another road; they are delayed replies from a closing panel.
    /// </summary>
    public void SetMembers(int roadId, IEnumerable<int> pathIds, IEnumerable<int> previewIds)
    {
        if (!IsActive || roadId != RoadId) return;
        Replace(members, pathIds);
        Replace(preview, previewIds);
        RoadFocusOverlay.Ensure().SetEditLayers(color, members, preview);
    }

    /// <summary>
    /// Ends the mode and clears section-edit bands (retains road detail panel highlighting). Only if previously active,
    /// sends edit_road:ended regardless of exit reason. The caller (IpcStateCoordinator) restores input mode and display.
    /// </summary>
    public void End()
    {
        if (!IsActive) return;
        int endedRoadId = RoadId;
        object endedSessionId = SessionId;
        IsActive = false;
        RoadId = Constants.VALUE_NONE;
        SessionId = null;
        members.Clear();
        preview.Clear();
        RoadFocusOverlay.Instance?.ClearEditLayers();
        TooltipManager.Instance?.Hide();
        IpcClient.Instance?.EmitRoadMemberEditEnded(endedRoadId, endedSessionId);
        Debug.Log($"RoadMemberEditManager: 区間編集モードを終了しました road={endedRoadId}");
    }

    /// <summary>
    /// Esc in Unity also ends the mode and returns to normal (the panel follows via edit_road:ended).
    /// The mode does not bring the panel forward, so accept the key only while Unity has focus.
    /// </summary>
    private void Update()
    {
        if (!IsActive || !Application.isFocused || !InputUtil.WorldEscapeWasPressed) return;
        FindAnyObjectByType<IpcStateCoordinator>()?.ExitRoadMemberEditToNormal();
    }

    private static void Replace(HashSet<int> target, IEnumerable<int> source)
    {
        target.Clear();
        if (source == null) return;
        foreach (int id in source) if (id > 0) target.Add(id);
    }

    /// <summary>Whether the type can belong to a road (street/highway).</summary>
    public static bool IsNamedRoadType(int pathType)
    {
        return System.Array.IndexOf(Constants.PATH_TYPES_NAMED_ROAD, pathType) >= 0;
    }

    /// <summary>A section was clicked. Ask the UI to toggle membership if it is a street/highway.</summary>
    public void OnPathClicked(int pathId)
    {
        if (!IsActive) return;
        PathSummary path = GetPathManager()?.GetPathData(pathId);
        if (path == null) return;
        if (!IsNamedRoadType(path.path_type))
        {
            TooltipManager.Instance?.Show("道路に含められるのは街路と高速道路だけです");
            return;
        }
        bool shift = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
        IpcClient.Instance?.EmitRoadTogglePath(pathId, path.name ?? "", path.path_type, shift);
    }

    /// <summary>
    /// Cursor entered a section. Show guidance and return true if clickable (caller applies hover color).
    /// </summary>
    public bool OnPathHoverStart(int pathId)
    {
        if (!IsActive) return false;
        PathSummary path = GetPathManager()?.GetPathData(pathId);
        if (path == null) return false;
        if (!IsNamedRoadType(path.path_type))
        {
            TooltipManager.Instance?.Show("道路に含められるのは街路と高速道路だけです");
            return false;
        }
        TooltipManager.Instance?.Show(BuildHoverTooltip(pathId));
        RoadFocusOverlay.Instance?.SetHoverPath(pathId);
        return true;
    }

    public void OnPathHoverEnd(int pathId)
    {
        TooltipManager.Instance?.Hide();
        RoadFocusOverlay.Instance?.ClearHoverPath(pathId);
    }

    private string BuildHoverTooltip(int pathId)
    {
        if (members.Contains(pathId)) return "クリックでこの道路から外す";
        const string shiftHint = "\nShift+クリックで直進方向へまとめて追加";
        int otherRoadId = RoadManager.Instance != null ? RoadManager.Instance.GetRoadIdOfPath(pathId) : Constants.VALUE_NONE;
        if (otherRoadId > 0 && otherRoadId != RoadId && RoadManager.Instance.TryGetRoad(otherRoadId, out var other))
        {
            return $"クリックでこの道路へ付け替え(現在: {RoadManager.GetDisplayName(other)})" + shiftHint;
        }
        return "クリックでこの道路に追加" + shiftHint;
    }

    private PathManager GetPathManager()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        return pathManager;
    }
}
