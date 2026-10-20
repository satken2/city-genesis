using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using GeneratedModels;

/// <summary>
/// Central manager for bulldoze (object deletion) mode.
///
/// During STATE_REMOVE_OBJECTS, left click removes eligible objects
/// (empty parcels, planned buildings/roads, terrain props).
///
/// - Unity caches (ParcelSummary.usage_id / BuildingSummary.status /
///   PathSummary.status) provide preliminary eligibility; backend DELETE guards are authoritative.
/// - In scenario authoring mode, the UI sets instant_remove at start (Unity does not know the scenario mode).
///   Buildings/roads are then deleted immediately without status checks or confirmation (except protected types; see IsRemovable).
///   Backend DELETE branches remove associated tenants/citizens and clean isolated nodes.
/// - Unity calls backend DELETE endpoints directly
///   (same direct REST approach as NodeInputHandler).
/// - Deduplicate (type, id) pairs to avoid repeated DELETE requests for the same object.
/// - Play the trash sound on deletion clicks (clip supplied by IpcStateCoordinator).
/// - After successful deletion, briefly debounce incremental reload (CellDataLoader.ReloadAllData)
///   into one call to update 3D data. Parcels are removed locally immediately.
/// - When a deleted section belonged to a named road,
///   send world:roads_changed to the UI once at the same debounce end to refresh road lists.
///   UI road operations call notifyRoadsChanged themselves, but bulldozing bypasses the UI
///   (backend deletes roads with no remaining sections, so open detail panels need notification).
///
/// Terrain props use GPU instancing without per-instance GameObjects/Colliders,
/// so InputHandler raycasts cannot hit them. On click/hover, InputHandler passes screen rays here;
/// PropManager.TryPick uses AABB tests to find props closer than foreground buildings/terrain.
/// Individual PropInstance placements delete immediately; PropScatter removal irreversibly removes an entire forest,
/// so ask the cockpit UI through remove_object:request_confirm and wait for remove_object:confirm_result
/// before DELETE. Red hover outlines mark props (also the scatter perimeter for area placements).
///
/// As with dynamic managers such as STATE_EDIT_RESOURCE, IpcStateCoordinator
/// creates a GameObject and adds this component when needed.
/// </summary>
public class RemovalManager : MonoBehaviour
{
    public static RemovalManager Instance { get; private set; }

    /// <summary>Confirmed deletion target type (object_type in remove_object:request_confirm / confirm_result).</summary>
    public const string OBJECT_TYPE_PROP_SCATTER = "prop_scatter";

    // Hover outline drawing parameters. Use warning red for deletion.
    private const float HIGHLIGHT_LINE_THICKNESS = 2.5f;
    private static readonly Color HighlightColor = new Color(1f, 0.35f, 0.3f, 0.95f);
    private static readonly Color ScatterRegionColor = new Color(1f, 0.35f, 0.3f, 0.6f);

    /// <summary>Handled (including in-flight) (objectType, id) pairs. Prevents duplicate DELETEs.</summary>
    private readonly HashSet<(int type, int id)> _processed = new HashSet<(int, int)>();
    /// <summary>Handled scatter placement IDs (separate set because IDs may collide with individual placements).</summary>
    private readonly HashSet<int> _processedScatters = new HashSet<int>();

    /// <summary>Whether bulldoze mode is active. Never delete while inactive.</summary>
    private bool _active = false;
    /// <summary>Whether instant deletion is active. Does not filter building/road eligibility by status.</summary>
    private bool _instantRemove = false;

    // Manager references (ParcelManager / PathManager are not singletons; find with FindAnyObjectByType).
    private ParcelManager _parcelManager;
    private PathManager _pathManager;

    // Deletion click sound (set from IpcStateCoordinator's SerializeField).
    private AudioSource _audioSource;
    private AudioClip _clickSound;

    // Incremental reload debounce control
    private bool _reloadScheduled = false;
    private float _reloadDeadline = 0f;
    private const float RELOAD_DEBOUNCE_SECONDS = 0.35f;
    /// <summary>Whether to send world:roads_changed at the next debounce end (a road member section was successfully deleted).</summary>
    private bool _roadsChangedPending = false;

    // Scatter placement ID awaiting UI confirmation (0=none). Reject other scatter clicks while waiting.
    private int _pendingScatterId = 0;

    // Hovered prop (for outline drawing)
    private bool _hasHover = false;
    private PropManager.PickResult _hover;
    private OverlayMeshRenderer _overlayRenderer;
    private GameObject _overlayObj;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
        }
        _audioSource.playOnAwake = false;
    }

    /// <summary>Sets the deletion click sound.</summary>
    public void SetClickSound(AudioClip clip)
    {
        _clickSound = clip;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (_overlayObj != null) Destroy(_overlayObj);
    }

    /// <summary>
    /// Starts deletion mode, clearing handled sets and refreshing manager references.
    /// instantRemove=true enables scenario authoring's immediate deletion (buildings/roads regardless of status).
    /// </summary>
    public void BeginRemovalMode(bool instantRemove = false)
    {
        _active = true;
        _instantRemove = instantRemove;
        _processed.Clear();
        _processedScatters.Clear();
        _pendingScatterId = 0;
        _hasHover = false;
        _parcelManager = FindAnyObjectByType<ParcelManager>();
        _pathManager = FindAnyObjectByType<PathManager>();
    }

    /// <summary>Ends deletion mode, leaving a clean state for the next start.</summary>
    public void EndRemovalMode()
    {
        _active = false;
        _instantRemove = false;
        _processed.Clear();
        _processedScatters.Clear();
        _pendingScatterId = 0;
        _hasHover = false;
        _overlayRenderer?.ClearMesh();
    }

    /// <summary>
    /// Tests preliminary removal eligibility using Unity caches.
    /// Empty parcel: usage_id == VALUE_NONE(-1)
    /// Planned building: status ∈ {PENDING, SCHEDULED_CONSTRUCTION}
    /// Planned road: status ∈ {TEMPORARY, SCHEDULED_CONSTRUCTION, PENDING}
    /// Instant deletion does not filter buildings/roads by status, but excludes city government/central bank (TYPE_CITY),
    /// proxy agents/border checkpoints (TYPE_DEFAULT), and public transport section paths (PATH_TRANSPORT, owned by lines).
    /// </summary>
    public bool IsRemovable(int objectType, int id)
    {
        switch (objectType)
        {
            case InputObjectType.PARCEL:
            {
                if (_parcelManager == null) _parcelManager = FindAnyObjectByType<ParcelManager>();
                ParcelSummary s = _parcelManager != null ? _parcelManager.GetParcelSummary(id) : null;
                return s != null && s.usage_id == Constants.VALUE_NONE;
            }
            case InputObjectType.BUILDING:
            {
                BuildingSummary b = BuildingManager.Instance != null ? BuildingManager.Instance.GetBuildingSummary(id) : null;
                if (b == null) return false;
                if (_instantRemove) return b.type != Constants.TYPE_CITY && b.type != Constants.TYPE_DEFAULT;
                return b.status == Constants.STATUS_PENDING || b.status == Constants.STATUS_SCHEDULED_CONSTRUCTION;
            }
            case InputObjectType.PATH:
            {
                if (_pathManager == null) _pathManager = FindAnyObjectByType<PathManager>();
                PathSummary p = _pathManager != null ? _pathManager.GetPathData(id) : null;
                if (p == null) return false;
                if (_instantRemove) return p.path_type != Constants.PATH_TRANSPORT;
                return p.status == Constants.STATUS_TEMPORARY
                    || p.status == Constants.STATUS_SCHEDULED_CONSTRUCTION
                    || p.status == Constants.STATUS_PENDING;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts single-object deletion (left-click entry point).
    /// Issues DELETE and plays trash only for eligible, unhandled targets.
    /// </summary>
    public void TryRemove(int objectType, int id)
    {
        if (!_active) return;
        if (id < 0) return;

        var key = (objectType, id);
        if (_processed.Contains(key)) return;
        if (!IsRemovable(objectType, id)) return;

        string endpoint;
        switch (objectType)
        {
            case InputObjectType.PARCEL: endpoint = "parcels"; break;
            case InputObjectType.BUILDING: endpoint = "agents"; break;
            case InputObjectType.PATH: endpoint = "paths"; break;
            default: return;
        }

        // Resolve membership before deletion (it is absent from the refreshed list afterward). Also check path road_id
        // for sections added before the list refreshed (stale extra notifications only cause harmless UI refreshes).
        bool isRoadMember = objectType == InputObjectType.PATH && IsRoadMemberPath(id);

        _processed.Add(key);
        PlayClickSound();
        StartCoroutine(DeleteRequest(endpoint, id, () =>
        {
            if (isRoadMember) _roadsChangedPending = true;
            // Remove parcels locally immediately, clearing their overlays/colliders
            // (GetParcelIdAtPoint no longer finds them). Incremental reload removes building/road 3D objects.
            if (objectType == InputObjectType.PARCEL)
            {
                if (_parcelManager == null) _parcelManager = FindAnyObjectByType<ParcelManager>();
                _parcelManager?.RemoveParcel(id);
            }
        }));
    }

    private bool IsRoadMemberPath(int pathId)
    {
        if (RoadManager.Instance != null && RoadManager.Instance.GetRoadIdOfPath(pathId) > 0) return true;
        if (_pathManager == null) _pathManager = FindAnyObjectByType<PathManager>();
        PathSummary p = _pathManager != null ? _pathManager.GetPathData(pathId) : null;
        return p != null && p.road_id.HasValue && p.road_id.Value > 0;
    }

    // ======================================================================
    // Terrain props
    // ======================================================================

    /// <summary>
    /// Handles the nearest prop on the screen ray for deletion (left-click entry point).
    /// maxDistance is the distance to the building/parcel/terrain hit on the same ray; ignore farther props occluded
    /// by foreground objects. Return true if a prop is picked, so the caller skips
    /// normal building/parcel click dispatch.
    /// Individual placements issue DELETE immediately; scatters await UI confirmation.
    /// </summary>
    public bool TryRemovePropAt(Ray ray, float maxDistance)
    {
        if (!_active || PropManager.Instance == null) return false;
        if (!PropManager.Instance.TryPick(ray, maxDistance, out var hit)) return false;

        if (!hit.scatter)
        {
            var key = (InputObjectType.PROP, hit.id);
            if (_processed.Contains(key)) return true;
            _processed.Add(key);
            PlayClickSound();
            StartCoroutine(DeleteRequest("props/instances", hit.id, () => PropManager.Instance?.HideSource(false, hit.id)));
            return true;
        }

        // Scatters: do nothing while awaiting confirmation or if already handled (avoid stacked dialogs on repeated clicks).
        if (_pendingScatterId != 0 || _processedScatters.Contains(hit.id)) return true;
        _pendingScatterId = hit.id;
        IpcClient.Instance?.EmitRemoveObjectRequestConfirm(OBJECT_TYPE_PROP_SCATTER, hit.id, hit.type);
        return true;
    }

    /// <summary>
    /// UI confirmation reply (remove_object:confirm_result). Issue DELETE only if it matches the requested target
    /// and is accepted. Ignore delayed replies after mode exit.
    /// </summary>
    public void OnConfirmResult(string objectType, int id, bool accepted)
    {
        if (objectType != OBJECT_TYPE_PROP_SCATTER || id != _pendingScatterId) return;
        _pendingScatterId = 0;
        if (!_active || !accepted) return;
        if (!_processedScatters.Add(id)) return;
        PlayClickSound();
        StartCoroutine(DeleteRequest("props/scatters", id, () => PropManager.Instance?.HideSource(true, id)));
    }

    /// <summary>
    /// Updates the hovered prop (InputHandler calls on ray changes). LateUpdate draws outlines.
    /// </summary>
    public void UpdatePropHover(Ray ray, float maxDistance)
    {
        if (!_active || PropManager.Instance == null) { _hasHover = false; return; }
        _hasHover = PropManager.Instance.TryPick(ray, maxDistance, out _hover);
    }

    private void LateUpdate()
    {
        if (!_active || !_hasHover) { _overlayRenderer?.ClearMesh(); return; }
        Camera cam = Camera.main;
        if (cam == null) return;
        EnsureOverlayRenderer();
        _overlayRenderer.BeginFrame();
        DrawBoundsEdges(_hover.bounds);
        if (_hover.scatter && PropManager.Instance != null
            && PropManager.Instance.TryGetScatterRegions(_hover.id, out var regions))
        {
            // Draw the scatter perimeter along terrain to show that deleting it removes the entire forest.
            Vector3 offset = PropManager.Instance.SceneOffset;
            foreach (var region in regions)
            {
                if (region.rings.Count == 0) continue;
                var ring = region.rings[0];
                for (int i = 0; i < ring.Length; i++)
                {
                    Vector3 a = GroundPoint(ring[i], offset);
                    Vector3 b = GroundPoint(ring[(i + 1) % ring.Length], offset);
                    _overlayRenderer.AddSolidLine(a, b, ScatterRegionColor, HIGHLIGHT_LINE_THICKNESS);
                }
            }
        }
        _overlayRenderer.EndFrame(cam);
    }

    private static Vector3 GroundPoint(Vector2 saved, Vector3 offset)
    {
        var p = new Vector3(saved.x, 0f, saved.y) + offset;
        p.y = Utils.GetGroundHeight(p) + 0.3f;
        return p;
    }

    /// <summary>Draws the 12 AABB edges as lines.</summary>
    private void DrawBoundsEdges(Bounds b)
    {
        Vector3 min = b.min, max = b.max;
        Vector3[] c =
        {
            new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        for (int i = 0; i < edges.GetLength(0); i++)
            _overlayRenderer.AddSolidLine(c[edges[i, 0]], c[edges[i, 1]], HighlightColor, HIGHLIGHT_LINE_THICKNESS);
    }

    private void EnsureOverlayRenderer()
    {
        if (_overlayObj != null) return;
        // Always draw in front of crowns/buildings (same as other editor gizmos).
        _overlayRenderer = OverlayMeshRenderer.Create("RemovalOverlayMesh", alwaysOnTop: true);
        _overlayObj = _overlayRenderer.gameObject;
    }

    // ======================================================================
    // Shared
    // ======================================================================

    /// <summary>Plays the deletion click sound (does nothing if unset).</summary>
    private void PlayClickSound()
    {
        if (_clickSound != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(_clickSound);
        }
    }

    /// <summary>
    /// Sends a deletion request to backend DELETE {endpoint}/{id}.
    /// On success, calls onSuccess for immediate local updates and schedules a debounced incremental reload.
    /// Logs/discards 4xx rejections (e.g. state changed), retaining the handled-set entry.
    /// </summary>
    private IEnumerator DeleteRequest(string endpoint, int id, System.Action onSuccess)
    {
        using (ApiRequest call = ApiClient.Request("DELETE", $"/{endpoint}/{id}"))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

            if (request.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke();
                RequestReload();
            }
            else
            {
                Debug.LogWarning($"RemovalManager: 削除に失敗しました ({endpoint}/{id}): "
                    + $"{request.responseCode} {request.error}");
            }
        }
    }

    /// <summary>Schedules a briefly debounced incremental reload, batching successive deletions into one reload.</summary>
    private void RequestReload()
    {
        _reloadDeadline = Time.unscaledTime + RELOAD_DEBOUNCE_SECONDS;
        if (!_reloadScheduled)
        {
            _reloadScheduled = true;
            StartCoroutine(ReloadAfterIdle());
        }
    }

    private IEnumerator ReloadAfterIdle()
    {
        while (Time.unscaledTime < _reloadDeadline)
        {
            yield return null;
        }
        _reloadScheduled = false;
        bool roadsChanged = _roadsChangedPending;
        _roadsChangedPending = false;
        if (roadsChanged) IpcClient.Instance?.EmitRoadsChanged();
        if (CellDataLoader.Instance != null)
        {
            // ReloadAllData also includes road list refresh (RoadManager.RequestRefresh).
            CellDataLoader.Instance.ReloadAllData();
        }
        else if (roadsChanged)
        {
            RoadManager.Instance?.RequestRefresh();
        }
    }
}
