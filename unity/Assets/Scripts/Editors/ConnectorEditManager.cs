using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Singleton manager for highlighting and drag-editing facility terminal nodes
/// (infrastructure connection = infra_node_id, vehicle network connection = depot_node_id).
///
/// - <see cref="ShowConnectors"/>: While AgentDetailPanel displays a facility, displays colored markers
///   rotating and bobbing above its terminal nodes. Read-only (cannot be dragged).
/// - <see cref="EnterEditMode"/>: Called in STATE_EDIT_CONNECTOR. In addition to markers, attaches
///   <see cref="NodeInputHandler"/> and terminal roles to the target nodes to support dragging and PATCH in MODE_EDIT.
///   Terminals are equipment within the facility site, so dragging is clamped
///   to the facility's parcel, which is highlighted during editing (clamping is disabled if
///   the parcel is not loaded; backend PATCH validation rejects moves outside the parcel).
/// - <see cref="Hide"/> / <see cref="ExitEditMode"/>: Removes markers and restores node visibility.
///
/// Displays only one marker when infrastructure and vehicle network connections share a node
/// (e.g. an electrified rail depot).
/// </summary>
public class ConnectorEditManager : MonoBehaviour
{
    public static ConnectorEditManager Instance { get; private set; }

    // Vehicle network connection color (sidings, etc.), distinct from infrastructure connections (red).
    private static readonly Color DepotColor = new Color(1f, 0.6f, 0.1f);

    // During editing, the flat node object (scale 0.5,0.01,0.5) makes the ground connection hard to locate,
    // so temporarily enlarge it as a marker base (grab detection uses the marker's screen position,
    // so this scale does not affect hit detection).
    private static readonly Vector3 EditClickableScale = new Vector3(2f, 0.5f, 2f);

    // Minimum screen-space hit radius (pixels) for grabbing a marker (inverted square pyramid).
    // Markers float above node objects as overlays, so grab detection uses distance to
    // the screen-space segment from the node (ground) to the marker's top, instead of collider raycasts.
    // The actual radius increases with zoom (the marker's apparent size); this is its lower limit.
    private const float ConnectorHitRadius = 45f;

    // Marker top height above the node (m). Used as the upper endpoint of the hit-test segment.
    private const float MarkerTopHeight =
        InfrastructureMarkerUtility.APEX_OFFSET + InfrastructureMarkerUtility.HEIGHT;

    private class MarkerEntry
    {
        public GameObject node;
        public bool addedHandler; // Whether this manager attached NodeInputHandler
        public Vector3 originalScale; // Node scale before enlargement for editing
        public bool scaled;          // Whether the node scale was enlarged
    }

    private readonly Dictionary<int, MarkerEntry> entries = new Dictionary<int, MarkerEntry>();
    private int infraNodeId;
    private int depotNodeId;
    private bool editMode;

    // Edited facility agent_id and the parcel ID containing it (-1=unresolved).
    // Terminals are equipment within the facility site, so clamp drag movement
    // to this parcel (the backend also authoritatively rejects PATCH requests
    // outside the parcel). If the parcel cannot be resolved, e.g. because it is unloaded,
    // skip clamping and rely on backend rejection and the existing rollback.
    private int ownerAgentId;
    private int ownerParcelId = -1;

    private ParcelManager parcelManager;

    // Terminal node ID being actively dragged (0=none). Cleared at drag end (ProcessMouseDragEnd).
    private int draggingNodeId;

    // Terminal node ID whose marker follows the node object's transform.position (0=none).
    // PathManager's cached coordinates remain at the old position during dragging and until the post-drop PATCH completes
    // (GetNodeData returns the cache), so make the grabbed node follow its transform.
    // Retain this after dropping until markers are rebuilt (ClearMarkers), preventing a brief jump
    // to the old position during the PATCH round trip (harmless afterward because transform and cached absolute coordinates agree).
    private int pinnedNodeId;

    // Whether grab detection has been attempted for this drag gesture.
    // InputHandler terrain drags start without choosing a target; the first ProcessMouseDrag call
    // performs deferred grab detection (as in RoadPlanManager.ProcessMouseDrag). If it fails,
    // do not retry until the gesture ends (prevents grabbing accidentally when crossing a marker mid-drag).
    private bool grabAttemptedThisGesture;

    /// <summary>Whether connector edit mode is active (accepting drag movement).</summary>
    public bool IsEditMode => editMode;

    private PathManager pathManager;

    // Overlay drawing component (uses OverlayLine material as in RoadPlanManager)
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

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
        if (Instance == this)
        {
            ClearMarkers();
            if (overlayMeshObj != null)
            {
                Destroy(overlayMeshObj);
            }
            Instance = null;
        }
    }

    private PathManager PathMgr => pathManager != null
        ? pathManager
        : (pathManager = FindAnyObjectByType<PathManager>());

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj != null) return;
        overlayRenderer = OverlayMeshRenderer.Create("ConnectorOverlayMesh", alwaysOnTop: true);
        overlayMeshObj = overlayRenderer.gameObject;
    }

    /// <summary>
    /// Highlights terminal nodes read-only.
    /// Displays one marker if infraNodeId / depotNodeId are identical (a shared connection node).
    /// Displays nothing if both are 0 or below.
    /// </summary>
    public void ShowConnectors(int infraNodeId, int depotNodeId)
    {
        ClearMarkers();
        editMode = false;
        this.infraNodeId = infraNodeId;
        this.depotNodeId = depotNodeId;

        bool shared = infraNodeId > 0 && infraNodeId == depotNodeId;
        if (shared)
        {
            HighlightNode(infraNodeId);
        }
        else
        {
            if (infraNodeId > 0) HighlightNode(infraNodeId);
            if (depotNodeId > 0) HighlightNode(depotNodeId);
        }
    }

    /// <summary>
    /// Enters connector edit mode, enabling node dragging as well as highlighting.
    /// agentId identifies the edited facility. Resolves and highlights its parcel
    /// and restricts terminal dragging to its boundaries.
    /// </summary>
    public void EnterEditMode(int infraNodeId, int depotNodeId, int agentId)
    {
        ShowConnectors(infraNodeId, depotNodeId);
        editMode = true;
        ResolveOwnerParcel(agentId, infraNodeId);
        foreach (var kv in entries)
        {
            EnsureDraggable(kv.Key, kv.Value);
        }
        Debug.Log($"ConnectorEditManager: 接続点編集モード開始 -> infra={infraNodeId}, depot={depotNodeId}, agent={agentId}, parcel={ownerParcelId}");
    }

    /// <summary>
    /// Leaves edit mode and returns to read-only highlighting (the panel stays open).
    /// </summary>
    public void ExitEditMode()
    {
        if (editMode)
        {
            ClearOwnerParcel();
            // Re-highlight (keep the attached NodeInputHandler, but editMode=false makes
            // IsMouseOverConnector return false, disabling drag grabs).
            ShowConnectors(infraNodeId, depotNodeId);
        }
    }

    /// <summary>
    /// Removes all markers and restores node visibility. Called when the panel closes.
    /// </summary>
    public void Hide()
    {
        ClearMarkers();
        ClearOwnerParcel();
        editMode = false;
        infraNodeId = 0;
        depotNodeId = 0;
    }

    /// <summary>
    /// Resolves the edited facility's parcel and highlights the movable area.
    /// If agentId is unspecified (0 or below), fall back to reverse lookup from the terminal node ID
    /// (BuildingManager.TryGetInfraTerminalOwner).
    /// Leave ownerParcelId = -1 if the facility/parcel cannot be resolved, e.g. because it is unloaded
    /// (clamping is disabled and backend authoritative validation decides whether movement is allowed).
    /// </summary>
    private void ResolveOwnerParcel(int agentId, int infraNodeId)
    {
        ownerAgentId = agentId;
        ownerParcelId = -1;

        var bm = BuildingManager.Instance;
        BuildingSummary summary = null;
        if (bm != null)
        {
            if (agentId > 0)
            {
                summary = bm.GetBuildingSummary(agentId);
            }
            if (summary == null && infraNodeId > 0)
            {
                bm.TryGetInfraTerminalOwner(infraNodeId, out summary);
            }
        }
        if (summary == null)
        {
            Debug.LogWarning($"ConnectorEditManager: 施設 {agentId} が未ロードのため可動範囲(区画)を解決できません(クランプ無効)");
            return;
        }
        if (summary.parcel_id <= 0)
        {
            // Facilities without a parcel (terminals created using center_point fallback) have no restriction.
            return;
        }

        ownerParcelId = summary.parcel_id;
        ownerAgentId = summary.agent_id;

        // Highlight the target parcel to show the player the movable area.
        var pm = ParcelMgr;
        if (pm != null)
        {
            pm.SetHighlightedParcel(ownerParcelId);
        }
    }

    /// <summary>Clears parcel highlighting and resets the resolved movable area.</summary>
    private void ClearOwnerParcel()
    {
        if (ownerParcelId > 0)
        {
            var pm = ParcelMgr;
            if (pm != null)
            {
                pm.ClearHighlightedParcel();
            }
        }
        ownerAgentId = 0;
        ownerParcelId = -1;
    }

    /// <summary>
    /// Clamps drag coordinates to the facility's parcel.
    /// Returns input coordinates unchanged if the parcel is unresolved/unloaded (backend rejects out-of-parcel
    /// PATCH requests, and NodeInputHandler.SaveNodePosition rolls the node back).
    /// </summary>
    private Vector3 ClampToOwnerParcel(Vector3 worldPos)
    {
        if (ownerParcelId <= 0) return worldPos;
        var pm = ParcelMgr;
        if (pm == null) return worldPos;
        if (pm.TryClampPointToParcel(ownerParcelId, worldPos, out Vector3 clamped))
        {
            return clamped;
        }
        return worldPos;
    }

    private ParcelManager ParcelMgr => parcelManager != null
        ? parcelManager
        : (parcelManager = FindAnyObjectByType<ParcelManager>());

    /// <summary>
    /// Tests whether screen coordinates are over a terminal marker (inverted square pyramid) or the column directly below it to the node.
    /// CameraController uses the same area to suppress camera dragging
    /// as drag grab detection uses (enabled only in edit mode).
    /// </summary>
    public bool IsMouseOverConnector(Vector2 screenPos, Camera cam)
    {
        if (!editMode) return false;
        return HitTestConnector(screenPos, cam, "IsMouseOverConnector") > 0;
    }

    /// <summary>Starts a drag by grabbing the terminal node of the marker under the cursor.</summary>
    public void StartDrag(Vector2 screenPos)
    {
        TryBeginDrag(screenPos);
    }

    /// <summary>
    /// Grabs the marker at the specified screen coordinates and starts dragging; false if none can be grabbed.
    /// Delegates node movement and PATCH requests to NodeInputHandler (updates /nodes/{id} using the terminal role).
    /// </summary>
    private bool TryBeginDrag(Vector2 screenPos)
    {
        if (!editMode)
        {
            Debug.Log("ConnectorEditManager: TryBeginDrag 編集モードではないため掴めません");
            return false;
        }
        int nodeId = HitTestConnector(screenPos, Camera.main, "TryBeginDrag");
        if (nodeId <= 0) return false;
        if (!entries.TryGetValue(nodeId, out var e) || e.node == null) return false;

        draggingNodeId = nodeId;
        pinnedNodeId = nodeId;
        var handler = e.node.GetComponent<NodeInputHandler>();
        handler?.onDragStart(InputMode.MODE_EDIT);
        Debug.Log($"ConnectorEditManager: 端子ノード {nodeId} のドラッグを開始しました");
        return true;
    }

    /// <summary>
    /// During dragging, moves the grabbed terminal node to the mouse position (terrain XZ).
    ///
    /// Note that InputHandler terrain drags begin without choosing a grab target.
    /// When a raycast hits a terrain collider, dragging starts through pendingClick→StartDrag(TERRAIN),
    /// rather than StartTerrainDrag, so the manager's StartDrag is not called.
    /// Therefore, perform deferred grab detection on the first call (as in RoadPlanManager.ProcessMouseDrag).
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (draggingNodeId <= 0)
        {
            if (grabAttemptedThisGesture) return;
            grabAttemptedThisGesture = true;
            if (!TryBeginDrag(InputUtil.MousePosition)) return;
        }
        if (!entries.TryGetValue(draggingNodeId, out var e) || e.node == null) return;

        // Terminals are equipment within the facility site, so clamp the drag position to its parcel.
        worldPos = ClampToOwnerParcel(worldPos);

        var handler = e.node.GetComponent<NodeInputHandler>();
        handler?.onDragUpdate(worldPos);

        // InputHandler passes Y=0 and onDragUpdate does not change Y, so
        // correct it here to terrain height at the current XZ + relative height to follow uneven terrain
        // (see the root AGENTS.md for the relative height convention).
        ApplyTerrainFollowY(e.node, draggingNodeId);
    }

    /// <summary>Ends dragging. NodeInputHandler sends PATCH if the move is committed.</summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        grabAttemptedThisGesture = false;
        if (draggingNodeId <= 0) return;
        if (entries.TryGetValue(draggingNodeId, out var e) && e.node != null)
        {
            // Clamp the final position to the facility parcel as well (same restriction as during dragging).
            worldPos = ClampToOwnerParcel(worldPos);

            // onDragEnd uses transform.position.y as the final Y, so
            // first set the transform to terrain height at the final XZ + relative height.
            Vector3 p = e.node.transform.position;
            p.x = worldPos.x;
            p.z = worldPos.z;
            e.node.transform.position = p;
            ApplyTerrainFollowY(e.node, draggingNodeId);

            var handler = e.node.GetComponent<NodeInputHandler>();
            handler?.onDragEnd(worldPos);
            Debug.Log($"ConnectorEditManager: 端子ノード {draggingNodeId} のドラッグを終了 -> {e.node.transform.position}");
        }
        // Keep pinnedNodeId (prevents flicker during the PATCH round trip); clear only the active drag.
        draggingNodeId = 0;
    }

    /// <summary>
    /// Corrects node Y to terrain height at the current XZ + backend relative height.
    /// Keeps the terminal from sinking into or floating above uneven terrain during dragging.
    /// </summary>
    private void ApplyTerrainFollowY(GameObject nodeGO, int nodeId)
    {
        float rel = 0f;
        var pm = PathMgr;
        var nd = pm != null ? pm.GetNodeData(nodeId) : null;
        if (nd != null && nd.position != null && nd.position.Length >= 3)
        {
            rel = nd.position[1];
        }
        Vector3 p = nodeGO.transform.position;
        p.y = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.z)) + rel;
        nodeGO.transform.position = p;
    }

    /// <summary>
    /// Returns the nearest marker's terminal node ID within the hit area (0 if none).
    /// The hit area surrounds the screen-space segment from node (ground) to marker top;
    /// its radius increases with zoom (apparent marker size), with a minimum of ConnectorHitRadius px.
    /// Testing distance only to the marker center would miss parts of the pyramid as zoom enlarges it,
    /// so a segment with variable radius makes the entire pyramid and space directly below it grabbable.
    /// </summary>
    private int HitTestConnector(Vector2 screenPos, Camera cam, string debugContext)
    {
        if (cam == null)
        {
            Debug.LogWarning($"ConnectorEditManager: HitTest({debugContext}) カメラが取得できません");
            return 0;
        }
        if (entries.Count == 0)
        {
            Debug.Log($"ConnectorEditManager: HitTest({debugContext}) マーカーエントリが0件です(ノード未ロード?)");
            return 0;
        }

        int best = 0;
        float bestDist = float.MaxValue;
        foreach (var kv in entries)
        {
            if (kv.Value == null || kv.Value.node == null) continue;

            Vector3 nodePos = GetNodeWorldPos(kv.Key, kv.Value);
            Vector3 spNode = cam.WorldToScreenPoint(nodePos);
            Vector3 spTop = cam.WorldToScreenPoint(nodePos + Vector3.up * MarkerTopHeight);
            if (spNode.z < 0f || spTop.z < 0f) continue; // Behind the camera

            Vector2 a = new Vector2(spNode.x, spNode.y);
            Vector2 b = new Vector2(spTop.x, spTop.y);
            float d = Mathf.Sqrt(PolylineUtils.DistanceToSegmentSq(screenPos, a, b));

            // Hit radius based on apparent size: estimate pixels/metre from the projected column length,
            // allowing the pyramid base radius plus 1.5x padding. Fall back to the minimum radius at a distance.
            float pxPerMeter = Vector2.Distance(a, b) / MarkerTopHeight;
            float radius = Mathf.Max(ConnectorHitRadius, pxPerMeter * InfrastructureMarkerUtility.BASE_HALF * 1.5f);

            // Debug log identifying grab detection failures (always emitted because this runs only on click).
            Debug.Log($"ConnectorEditManager: HitTest({debugContext}) node={kv.Key} 距離={d:F1}px 半径={radius:F1}px 線分={a}→{b} マウス={screenPos}");

            if (d <= radius && d < bestDist)
            {
                bestDist = d;
                best = kv.Key;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns the world position used to draw the terminal node marker.
    /// Normally uses PathManager node data (absolute coordinates accounting for terrain height),
    /// but uses the transform for a node whose cache retains its old position during dragging/PATCH (pinnedNodeId).
    /// </summary>
    private Vector3 GetNodeWorldPos(int nodeId, MarkerEntry entry)
    {
        if (nodeId == pinnedNodeId && entry.node != null)
        {
            return entry.node.transform.position;
        }
        var pm = PathMgr;
        if (pm != null)
        {
            var nd = pm.GetNodeData(nodeId);
            if (nd != null)
            {
                return nd.GetAbsolutePosition();
            }
        }
        return entry.node != null ? entry.node.transform.position : Vector3.zero;
    }

    private void HighlightNode(int nodeId)
    {
        if (nodeId <= 0 || entries.ContainsKey(nodeId)) return;
        var pm = PathMgr;
        if (pm == null) return;

        GameObject nodeGO = pm.GetNodeObject(nodeId);
        if (nodeGO == null)
        {
            Debug.LogWarning($"ConnectorEditManager: ノード {nodeId} の GameObject が見つかりません（未ロード）");
            return;
        }

        // Force normally hidden nodes (ShowNodes=false) visible to show their connection points.
        nodeGO.SetActive(true);

        entries[nodeId] = new MarkerEntry
        {
            node = nodeGO,
            addedHandler = false,
            originalScale = nodeGO.transform.localScale,
            scaled = false,
        };
    }

    private void EnsureDraggable(int nodeId, MarkerEntry entry)
    {
        if (entry == null || entry.node == null) return;

        // Set the terminal role (needed for NodeInputHandler.SaveNodePosition to use PATCH /nodes/{id}).
        var idInfo = entry.node.GetComponent<ObjectIdInfo>();
        if (idInfo != null && !NodeInputHandler.IsTerminalRole(idInfo.role))
        {
            idInfo.role = "connector_terminal";
        }

        // Attach NodeInputHandler if absent (e.g. nodes used only by non-electrified lines).
        var handler = entry.node.GetComponent<NodeInputHandler>();
        if (handler == null)
        {
            handler = entry.node.AddComponent<NodeInputHandler>();
            handler.nodeId = nodeId;
            entry.addedHandler = true;
        }

        // Grab detection itself uses the marker's screen coordinates (IsMouseOverConnector),
        // independent of node collider size. However, a flat node object is hard to locate,
        // so enlarge it as a ground-level base marker while editing.
        if (!entry.scaled)
        {
            entry.node.transform.localScale = EditClickableScale;
            entry.scaled = true;
        }
    }

    private void ClearMarkers()
    {
        draggingNodeId = 0;
        pinnedNodeId = 0;
        grabAttemptedThisGesture = false;
        var pm = PathMgr;
        bool showNodes = pm != null && pm.ShowNodes;
        foreach (var kv in entries)
        {
            if (kv.Value.node != null)
            {
                // Restore the scale enlarged for editing.
                if (kv.Value.scaled)
                {
                    kv.Value.node.transform.localScale = kv.Value.originalScale;
                }
                // Restore node visibility according to ShowNodes.
                if (kv.Value.node.activeSelf != showNodes)
                {
                    kv.Value.node.SetActive(showNodes);
                }
            }
        }
        entries.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    private void LateUpdate()
    {
        if (entries.Count == 0)
        {
            if (overlayRenderer != null)
            {
                overlayRenderer.ClearMesh();
            }
            return;
        }

        EnsureOverlayRenderer();
        overlayRenderer.BeginFrame();

        foreach (var kv in entries)
        {
            var e = kv.Value;
            if (e.node == null) continue;

            // Determine the marker color.
            // Infrastructure and shared nodes use the same red as RoadPlanManager.
            // Vehicle-network-only nodes (depot_node_id) use orange.
            Color color = InfrastructureMarkerUtility.COLOR_BRIGHT;
            if (kv.Key == depotNodeId && kv.Key != infraNodeId)
            {
                color = DepotColor;
            }

            // Dynamically recalculate absolute node coordinates (world coordinates accounting for terrain height) when drawing.
            // Use GetAbsolutePosition() to avoid nodeGO.transform.position.y being flattened
            // to 0 due to initial load timing (e.g. terrain not loaded).
            // Only the node being dragged or awaiting PATCH (pinnedNodeId) follows its transform,
            // making the grabbed marker follow the cursor.
            Vector3 worldPos = GetNodeWorldPos(kv.Key, e);

            InfrastructureMarkerUtility.DrawPyramid(overlayRenderer, worldPos, color);
        }

        overlayRenderer.EndFrame(null);
    }
}

/// <summary>
/// Utility class centralizing marker drawing for infrastructure and vehicle network connections
/// (inverted red square pyramids / red arrows).
/// </summary>
public static class InfrastructureMarkerUtility
{
    /// <summary>Square pyramid base radius (center to corner, metres).</summary>
    public const float BASE_HALF = 2.0f;
    /// <summary>Square pyramid height (metres).</summary>
    public const float HEIGHT = 4.0f;
    /// <summary>Height of the downward apex above the node (metres).</summary>
    public const float APEX_OFFSET = 2.5f;
    /// <summary>Angular velocity around Y (degrees/second).</summary>
    public const float ROTATE_SPEED = 40f;
    /// <summary>Vertical bobbing amplitude (metres).</summary>
    public const float BOB_AMPLITUDE = 0.3f;
    /// <summary>Vertical bobbing angular velocity (radians/second).</summary>
    public const float BOB_SPEED = 2f;

    /// <summary>Light side color (standard brightness of the red base).</summary>
    public static readonly Color COLOR_BRIGHT = new Color(0.95f, 0.10f, 0.10f);
    /// <summary>Dark side and top cap color (lower brightness of the red base).</summary>
    public static readonly Color COLOR_DARK = new Color(0.75f, 0.05f, 0.05f);

    /// <summary>
    /// Uses the specified OverlayMeshRenderer to draw an inverted square pyramid at world coordinates
    /// with rotation and bobbing animation.
    /// </summary>
    public static void DrawPyramid(OverlayMeshRenderer renderer, Vector3 worldPos, Color color, float alpha = 1.0f)
    {
        float angle = Time.time * ROTATE_SPEED * Mathf.Deg2Rad;
        float bob = Mathf.Sin(Time.time * BOB_SPEED) * BOB_AMPLITUDE;

        // Base corner offsets. Calculate only the first with trigonometry and derive the rest by 90° XZ rotations.
        Vector3 first = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * BASE_HALF;
        Vector3[] offsets = new Vector3[4];
        offsets[0] = first;
        offsets[1] = new Vector3(-first.z, 0f, first.x);
        offsets[2] = new Vector3(-first.x, 0f, -first.z);
        offsets[3] = new Vector3(first.z, 0f, -first.x);

        // Inverted square pyramid: downward apex floats above the node, with the base farther above.
        Vector3 apex = worldPos + Vector3.up * (APEX_OFFSET + bob);
        Vector3 baseCenter = apex + Vector3.up * HEIGHT;

        var brightTris = new List<Vector3>(6);
        var darkTris = new List<Vector3>(12);

        for (int k = 0; k < 4; k++)
        {
            Vector3 c0 = baseCenter + offsets[k];
            Vector3 c1 = baseCenter + offsets[(k + 1) % 4];
            // Alternate light and dark side colors to convey depth.
            var target = (k % 2 == 0) ? brightTris : darkTris;
            target.Add(apex); target.Add(c0); target.Add(c1);
        }

        // Close the top (square base) with the dark color.
        darkTris.Add(baseCenter + offsets[0]);
        darkTris.Add(baseCenter + offsets[1]);
        darkTris.Add(baseCenter + offsets[2]);
        darkTris.Add(baseCenter + offsets[0]);
        darkTris.Add(baseCenter + offsets[2]);
        darkTris.Add(baseCenter + offsets[3]);

        // Apply color and alpha.
        Color brightColor = color;
        Color darkColor = color * 0.8f; // Make the darker color 80% as bright.
        brightColor.a = alpha;
        darkColor.a = alpha;

        renderer.AddPreTriangulated(brightTris, brightColor);
        renderer.AddPreTriangulated(darkTris, darkColor);
    }
}
