
using UnityEngine;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;
using UnityEngine.EventSystems;

/// <summary>
/// Object type constants
/// </summary>
public static class InputObjectType
{
    public const int BUILDING = 1;
    public const int PARCEL = 2;
    public const int PATH = 3;
    public const int NODE = 4;
    public const int TERRAIN = 5;
    public const int ZONE = 6;
    public const int PROP = 7; // Has no collider and is absent from raycast classification. Used only as a key in the bulldozer's removed set.

    /// <summary>Japanese names for logs/debug displays.</summary>
    public static string GetName(int objectType)
    {
        switch (objectType)
        {
            case BUILDING: return "建物";
            case PARCEL: return "土地区画";
            case PATH: return "道路";
            case NODE: return "ノード";
            case TERRAIN: return "地面";
            case ZONE: return "地区";
            case PROP: return "プロップ";
            default: return "不明";
        }
    }
}

/// <summary>
/// Selection mode constants
/// </summary>
public static class InputMode
{
    public const int MODE_NONE = 0;
    public const int MODE_NORMAL = 1;
    public const int MODE_ROAD_CONSTRUCTION = 2;
    public const int MODE_BUILDING_CONSTRUCTION = 3;
    public const int MODE_PARCEL_CREATION_POINT = 4;
    public const int MODE_PARCEL_CREATION_PATH = 5;
    public const int MODE_PARCEL_CREATION_SPLIT = 6;
    public const int MODE_EDIT = 7;
    public const int MODE_PARCEL_SHAPE_EDIT = 10;
    public const int MODE_SET_GRID = 11;
    public const int MODE_EDIT_RESOURCE = 12;
    public const int MODE_EDIT_TRANSIT = 13;
    public const int MODE_EDIT_PROP = 14;
    // Drag edit mode for facility terminal nodes (infrastructure / vehicle network connections).
    // Treated as terrain dragging, as in stop placement (MODE_EDIT_TRANSIT);
    // ConnectorEditManager selects/moves targets through screen-space marker hit tests.
    public const int MODE_EDIT_CONNECTOR = 15;
    // Bulldoze (object deletion) mode. Left click removes empty parcels and planned buildings/roads
    // (dragging does not delete). RemovalManager handles the operation.
    public const int MODE_REMOVE = 17;
    // Path merge partner selection mode. Only candidates connected by a shared node to the path
    // open in the detail panel are clickable. PathTopologyEditManager checks candidates and reports selection.
    public const int MODE_PATH_MERGE = 18;
    // Path split-point selection mode. Selects the point projected onto the target centerline.
    // PathTopologyEditManager handles projection, marker drawing, and selection notification.
    public const int MODE_PATH_SPLIT = 19;
    // Source building selection for constructing a copy. Enables only building clicks,
    // reporting the clicked building's variant / floors / visual_variant to the UI without constructing anything.
    // BuildingInputHandler sends the notification as building_construction:copy_source.
    public const int MODE_PICK_BUILDING = 20;
    // Named-road section edit mode. Only reports street/highway section clicks to the UI;
    // the UI changes membership through the API. RoadMemberEditManager handles it.
    public const int MODE_EDIT_ROAD_MEMBERS = 22;
    // Earthworks area drawing mode. Terrain clicks and vertex dragging match resource areas; GradingAreaEditManager handles it.
    public const int MODE_EDIT_GRADING = 23;
}

/// <summary>
/// Mode preset constants
/// </summary>
public static class InputModePreset
{
    public const int ALL_NONE = 0;
    public const int ALL_NORMAL = 1;
    public const int ROAD_CONSTRUCTION = 2;
    public const int BUILDING_CONSTRUCTION = 3;
    public const int PARCEL_CREATION_POINT = 4;
    public const int PARCEL_CREATION_PATH = 5;
    public const int PARCEL_CREATION_SPLIT = 6;
    public const int PARCEL_SHAPE_EDIT = 9;
    public const int SET_GRID = 10;
    public const int EDIT_RESOURCE = 11;
    public const int TRANSIT_CONSTRUCTION = 12;
    public const int PROP_PLACEMENT = 13;
    public const int CONNECTOR_EDIT = 14;
    // Bulldoze (object deletion) preset. Sets buildings, parcels, roads, and terrain
    // to MODE_REMOVE so they can be picked for deletion.
    public const int REMOVE_OBJECTS = 16;
    // Merge partner selection preset. Only paths are clickable.
    public const int PATH_MERGE = 17;
    // Split-point selection preset. Paths and terrain are clickable
    // (so points slightly off the path can select the perpendicular projection).
    public const int PATH_SPLIT = 18;
    // Source selection preset for copying an existing building. Only buildings are clickable.
    public const int PICK_BUILDING = 19;
    // Road section edit preset. Only paths are clickable (same as merge partner selection).
    public const int ROAD_MEMBER_EDIT = 21;
    // Earthworks area drawing preset. Only terrain is clickable (same as resource area drawing).
    public const int EDIT_GRADING = 22;
}

/// <summary>
/// Global click/hover event management system
/// </summary>
public class InputHandler : MonoBehaviour
{
    public static InputHandler Instance { get; private set; }

    [Header("Ray Settings")]
    public float rayDistance = 5000f;
    public LayerMask rayLayerMask = -1;

    [Header("Terrain Settings")]
    public LayerMask terrainLayerMask = 1;

    // Mouse position (with snapping)
    public float posX { get; private set; }
    public float posZ { get; private set; }

    // Current selection mode by object type
    private Dictionary<int, int> objectTypeModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, InputMode.MODE_NORMAL },
        { InputObjectType.PARCEL, InputMode.MODE_NORMAL },
        { InputObjectType.PATH, InputMode.MODE_NORMAL },
        { InputObjectType.NODE, InputMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, InputMode.MODE_NORMAL },
        { InputObjectType.ZONE, InputMode.MODE_NORMAL }
    };

    /// <summary>Do not enable photography/riding controls alongside edit modes with their own key handling.</summary>
    public bool IsNormalMode
    {
        get
        {
            foreach (int mode in objectTypeModes.Values)
                if (mode != InputMode.MODE_NORMAL) return false;
            return true;
        }
    }

    // Event callbacks
    private Action<int, Vector3> clickEventCallback;
    private Action<int, Vector3, GameObject> hoverEventCallback;
    private Action<int, Vector3> rightClickEventCallback;

    // Currently hovered object
    private GameObject currentHoveredObject;
    private IInputHandler currentHoveredHandler;
    private int currentHoveredObjectType = -1;

    // Dragging
    private bool isDragging = false;
    private Vector3 dragStartPosition;
    private GameObject draggedObject;
    private int draggedObjectType = -1;

    // Pending click (record on mouse-down; fire on mouse-up if movement is below the threshold).
    // Store the target as GameObject + hit point rather than RaycastHit.
    // When uneven terrain buries a parcel collider, dispatch must target the parcel
    // rather than the raycast hit (terrain).
    private bool hasPendingClick = false;
    private GameObject pendingClickObject;
    private Vector3 pendingClickPoint;
    private int pendingClickObjectType = -1;
    private Vector3 pendingClickScreenPos;
    private bool hasPendingTerrainClick = false;
    private Vector3 pendingTerrainClickScreenPos;
    private const float CLICK_DRAG_THRESHOLD = 5f; // Pixels

    // Pending right click (record on press; fire on release only if camera rotation dragging
    // was not confirmed, meaning movement stayed below the threshold).
    // Camera rotation waits for threshold crossing, so movement distinguishes right clicks from rotation drags.
    private bool hasPendingRightClick = false;
    private GameObject pendingRightClickObject;
    private Vector3 pendingRightClickPoint;
    private int pendingRightClickObjectType = -1;
    private bool hasPendingTerrainRightClick = false;
    private Vector3 pendingRightClickScreenPos;

    private Ray lastRay;
    private bool lastUndergroundView;
    private PathManager cachedPathManager;

    // Cached manager references
    private RoadPlanManager cachedRoadPlanManager;
    private TerrainManager cachedTerrainManager;
    private ParcelEditorManager cachedParcelEditorManager;
    private ParcelShapeEditManager cachedParcelShapeEditManager;
    private ResourceAreaEditManager cachedResourceAreaEditManager;
    private ParcelManager cachedParcelManager;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        cachedRoadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        cachedTerrainManager = FindAnyObjectByType<TerrainManager>();
        cachedParcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        cachedParcelShapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        cachedResourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        cachedParcelManager = FindAnyObjectByType<ParcelManager>();

        // Attach TerrainInputHandler to the sea surface (Sea).
        // Sea is a planar polygon outside TerrainManager and has no Terrain component,
        // so GetObjectType does not classify it as TERRAIN by default and clicks do nothing.
        // Assign the SeaObject tag and attach TerrainInputHandler
        // to enable terrain-like sea clicks (e.g. placing parcel creation points).
        // Do not duplicate an existing TerrainInputHandler (e.g. placed in the scene beforehand).
        GameObject seaObject = GameObject.FindWithTag("SeaObject");
        if (seaObject != null && seaObject.GetComponent<TerrainInputHandler>() == null)
        {
            seaObject.AddComponent<TerrainInputHandler>();
        }
    }

    private void Update()
    {
        if (!ApiClient.BackendConnected && isDragging) EndDrag();
        // Block all mouse input (click/hover/drag) in fly/photography modes.
        // Disable 3D object selection and hover (including tooltips) completely during free flight,
        // returning before any other checks.
        if (!ApiClient.BackendConnected || TutorialRuntime.BlocksWorldInput || DriveMode.IsOpen || (CameraController.Instance != null && CameraController.Instance.BlocksWorldInput))
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // Also block all mouse input to 3D objects while the radial menu is open.
        // Use the same nondestructive gate as fly mode, restoring original input state when it closes
        // (the menu Canvas has no GraphicRaycaster, so EventSystem UI checks do not block it).
        if (RadialMenuController.IsOpen)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // Stop processing clicks over UI.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            // Discard pending clicks on UI clicks.
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // Block all object selection/dragging while a camera drag is confirmed.
        // Exclude IsPendingDrag; InputHandler distinguishes mouse-down from dragging itself.
        // This allows recording click candidates while CameraController is waiting for a drag.
        bool isCameraDragging = CameraController.Instance != null &&
            CameraController.Instance.IsMouseDragging;
        if (isCameraDragging)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            // Discard pending clicks when camera dragging is confirmed.
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // Stop if no main camera exists.
        // Prevent NullReferenceException when the camera is intentionally hidden,
        // such as on loading screens.
        if (Camera.main == null)
        {
            return;
        }

        if (GetModeForObjectType(InputObjectType.PATH) == InputMode.MODE_NORMAL
            && GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_NORMAL)
        {
            if (CitizenPickSelection.ProcessPointer(Camera.main) || TransitSelection.ProcessPointer(Camera.main))
            {
                ClearHover(); TooltipManager.Instance?.Hide();
                hasPendingClick = false; hasPendingTerrainClick = false;
                return;
            }
        }
        else { CitizenPickSelection.Cancel(); TransitSelection.Cancel(); }

        // Cast a ray from the camera through the mouse position.
        Vector3 mousePosition = InputUtil.MousePosition;
        Ray ray = Camera.main.ScreenPointToRay(mousePosition);

        // Check ray changes and input state.
        // Input triggers such as clicks/drags must continue processing even when stationary.
        bool hasInputEvent = InputUtil.LeftMouseWasPressedThisFrame ||
                             InputUtil.RightMouseWasPressedThisFrame ||
                             InputUtil.LeftMouseWasReleasedThisFrame ||
                             InputUtil.RightMouseWasReleasedThisFrame ||
                             isDragging;

        bool rayChanged = ray.origin != lastRay.origin || ray.direction != lastRay.direction || lastUndergroundView != UndergroundView.IsActive;

        if (!rayChanged && !hasInputEvent)
        {
            // Skip expensive checks if neither mouse/camera nor input state has changed.
            return;
        }

        lastRay = ray;
        lastUndergroundView = UndergroundView.IsActive;

        // First calculate the terrain intersection and update posX, posZ.
        UpdateTerrainPosition(ray);

        // Object hit testing
        // Use RaycastAll to pass through MODE_NONE objects.
        RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, rayLayerMask);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        GameObject validHitObject = null;
        Vector3 validHitPoint = Vector3.zero;
        int validHitObjectType = -1;

        if (TryGetObjectHit(hits, out var selectedHit))
        {
            validHitObject = selectedHit.collider.gameObject;
            validHitPoint = selectedHit.point;
            validHitObjectType = GetObjectType(validHitObject);
        }

        // Replace terrain hits with parcels:
        // The parcel MeshCollider top samples terrain only at polygon corners;
        // on uneven ground, raised terrain between vertices buries the collider, so
        // nearer terrain always wins the raycast and prevents selection. Parcels conceptually occupy ground areas,
        // so if the first valid hit is terrain, use a 2D containment test on hit XZ
        // and redirect dispatch only if it lies within a selectable parcel.
        // When parcel colliders protrude above terrain, raycasts still select them first as usual.
        if (validHitObjectType == InputObjectType.TERRAIN)
        {
            GameObject parcelObj = GetSelectableParcelAtPoint(validHitPoint);
            if (parcelObj != null)
            {
                validHitObject = parcelObj;
                validHitObjectType = InputObjectType.PARCEL;
            }
        }

        // In bulldoze mode, let RemovalManager update hover outlines for collider-free terrain props
        // (only props nearer than the hit; search to maximum ray distance if nothing is hit).
        if (GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_REMOVE && RemovalManager.Instance != null)
        {
            RemovalManager.Instance.UpdatePropHover(ray,
                UndergroundView.IsActive ? 0f :
                validHitObject != null ? Vector3.Distance(ray.origin, validHitPoint) : rayDistance);
        }

        if (validHitObject != null)
        {
            HandleObjectHit(validHitObject, validHitPoint, validHitObjectType);

            // Left-button down: record a pending click without firing immediately.
            if (InputUtil.LeftMouseWasPressedThisFrame)
            {
                hasPendingClick = true;
                pendingClickObject = validHitObject;
                pendingClickPoint = validHitPoint;
                pendingClickObjectType = validHitObjectType;
                pendingClickScreenPos = InputUtil.MousePosition;
            }

            // Record a pending right click without firing immediately.
            // Fire only on release with movement below threshold (not converted into camera rotation dragging).
            if (InputUtil.RightMouseWasPressedThisFrame)
            {
                hasPendingRightClick = true;
                hasPendingTerrainRightClick = false;
                pendingRightClickObject = validHitObject;
                pendingRightClickPoint = validHitPoint;
                pendingRightClickObjectType = validHitObjectType;
                pendingRightClickScreenPos = InputUtil.MousePosition;
            }
        }
        else
        {
            // Nothing was hit.
            ClearHover();

            // Left-button down: record a pending terrain click.
            if (InputUtil.LeftMouseWasPressedThisFrame)
            {
                hasPendingTerrainClick = true;
                pendingTerrainClickScreenPos = InputUtil.MousePosition;
            }

            // Record a pending right terrain click; fire on release only if movement stays below threshold.
            if (InputUtil.RightMouseWasPressedThisFrame)
            {
                hasPendingTerrainRightClick = true;
                hasPendingRightClick = false;
                pendingRightClickScreenPos = InputUtil.MousePosition;
            }
        }

        // Mouse movement: discard pending clicks beyond the threshold and confirm a drag.
        if (hasPendingClick)
        {
            if (CameraController.Instance != null && CameraController.Instance.IsMouseDragging)
            {
                hasPendingClick = false;
            }
            else if ((InputUtil.MousePosition - pendingClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingClick = false;
                StartDrag(pendingClickObject, pendingClickPoint, pendingClickObjectType);
            }
        }
        if (hasPendingTerrainClick)
        {
            if (CameraController.Instance != null && CameraController.Instance.IsMouseDragging)
            {
                hasPendingTerrainClick = false;
            }
            else if ((InputUtil.MousePosition - pendingTerrainClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingTerrainClick = false;
                StartTerrainDrag();
            }
        }

        // Drag update
        if (isDragging && InputUtil.LeftMouseIsPressed)
        {
            UpdateDrag();
        }

        // Mouse-up processing
        if (InputUtil.LeftMouseWasReleasedThisFrame)
        {
            // Fire a remaining pending click on mouse-up.
            // In bulldoze mode, test collider-free terrain props first; if nearer than the hit object,
            // delete only the prop (do not dispatch to the building/parcel behind it).
            if (hasPendingClick)
            {
                float hitDistance = Vector3.Distance(ray.origin, pendingClickPoint);
                if (IsUndergroundSelection(pendingClickObject, pendingClickPoint)
                    || !TryRemovePropAtScreenPoint(pendingClickScreenPos, hitDistance))
                {
                    HandleObjectClick(pendingClickObject, pendingClickPoint, pendingClickObjectType);
                }
                hasPendingClick = false;
            }
            if (hasPendingTerrainClick)
            {
                if (!TryRemovePropAtScreenPoint(pendingTerrainClickScreenPos, rayDistance))
                {
                    HandleTerrainClick();
                }
                hasPendingTerrainClick = false;
            }

            // Drag end
            if (isDragging)
            {
                EndDrag();
            }
        }

        // Right-button up: fire the recorded right-click candidate only if movement since press is below threshold
        // (not converted into camera rotation dragging).
        // This supports stationary right click for confirmation and right dragging for camera rotation.
        if (InputUtil.RightMouseWasReleasedThisFrame)
        {
            float rightMoveDist = (InputUtil.MousePosition - pendingRightClickScreenPos).magnitude;
            if (rightMoveDist < CLICK_DRAG_THRESHOLD)
            {
                if (hasPendingRightClick)
                {
                    HandleObjectRightClick(pendingRightClickObject, pendingRightClickPoint, pendingRightClickObjectType);
                }
                else if (hasPendingTerrainRightClick)
                {
                    HandleTerrainRightClick();
                }
            }
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
        }
    }

    private void UpdateTerrainPosition(Ray ray)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, terrainLayerMask);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        bool foundTerrain = false;
        foreach (var hit in hits)
        {
            GameObject hitObject = hit.collider.gameObject;
            if (hitObject.GetComponent<Terrain>() != null || IsTerrainSurfaceObject(hitObject))
            {
                posX = hit.point.x;
                posZ = hit.point.z;

                // Call terrain hover processing.
                HandleTerrainHover(hit);
                foundTerrain = true;
                break;
            }
        }

        if (!foundTerrain)
        {
            // If no terrain is hit, calculate the y=0 plane intersection.
            float distance = -ray.origin.y / ray.direction.y;
            if (distance > 0)
            {
                Vector3 point = ray.origin + ray.direction * distance;
                posX = point.x;
                posZ = point.z;
            }
        }
    }

    private void HandleObjectHit(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // Hover processing
        if (currentHoveredObject != hitObject)
        {
            if (currentHoveredHandler != null)
            {
                currentHoveredHandler.onHoverEnd(GetModeForObjectType(currentHoveredObjectType));
            }

            // Start hovering a new object.
            currentHoveredObject = hitObject;
            currentHoveredObjectType = objectType;
            currentHoveredHandler = hitObject.GetComponent<IInputHandler>();
            if (currentHoveredHandler != null)
            {
                currentHoveredHandler.onHoverStart(GetModeForObjectType(objectType));
            }
        }

        // Call continuous hover processing.
        if (currentHoveredHandler != null)
        {
            currentHoveredHandler.onHover(GetModeForObjectType(objectType));
        }

        // Hover event callback
        hoverEventCallback?.Invoke(objectType, hitPoint, hitObject);
    }

    private void HandleTerrainHover(RaycastHit hit)
    {
        GameObject hitObject = hit.collider.gameObject;

        // Call continuous hover for terrain.
        if (hitObject.GetComponent<Terrain>() != null || IsTerrainSurfaceObject(hitObject))
        {
            var terrainHandler = hitObject.GetComponent<IInputHandler>();
            terrainHandler?.onHover(GetModeForObjectType(InputObjectType.TERRAIN));
        }
    }

    private void HandleObjectClick(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // The target may have been destroyed by a cell reload, etc. between mouse-down and mouse-up.
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);
        var handler = hitObject.GetComponent<IInputHandler>();
        handler?.onClick(currentMode);

        // Click event callback
        clickEventCallback?.Invoke(objectType, hitPoint);
    }

    /// <summary>
    /// Only in bulldoze mode, attempts to delete terrain props using a ray through the press position.
    /// maxDistance is the distance to the ray's building/parcel/terrain hit (occlusion test).
    /// Returns true if a prop was handled (deletion issued, confirmation requested, or already handled), suppressing normal click dispatch.
    /// </summary>
    private bool TryRemovePropAtScreenPoint(Vector2 screenPos, float maxDistance)
    {
        if (UndergroundView.IsActive) return false; // In underground view, do not let foreground surface props steal underground path selection.
        if (GetModeForObjectType(InputObjectType.TERRAIN) != InputMode.MODE_REMOVE) return false;
        if (RemovalManager.Instance == null || Camera.main == null) return false;
        return RemovalManager.Instance.TryRemovePropAt(Camera.main.ScreenPointToRay(screenPos), maxDistance);
    }

    private void HandleTerrainClick()
    {
        Vector3 terrainPoint = new Vector3(posX, 0, posZ);

        // Get TerrainInputHandler from TerrainManager's terrain tiles.
        TerrainInputHandler terrainHandler = GetTerrainInputHandlerAtPosition(terrainPoint);
        if (terrainHandler != null)
        {
            int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);
            terrainHandler.onClick(currentMode);
        }

        clickEventCallback?.Invoke(InputObjectType.TERRAIN, terrainPoint);
    }

    private void HandleObjectRightClick(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // The target may have been destroyed by a cell reload, etc. between mouse-down and mouse-up.
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);
        var handler = hitObject.GetComponent<IInputHandler>();
        handler?.onRightClick(currentMode);

        // Right-click event callback
        rightClickEventCallback?.Invoke(objectType, hitPoint);
    }

    private void HandleTerrainRightClick()
    {
        Vector3 terrainPoint = new Vector3(posX, 0, posZ);

        // Get TerrainInputHandler from TerrainManager's terrain tiles.
        TerrainInputHandler terrainHandler = GetTerrainInputHandlerAtPosition(terrainPoint);
        if (terrainHandler != null)
        {
            int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);
            terrainHandler.onRightClick(currentMode);
        }

        rightClickEventCallback?.Invoke(InputObjectType.TERRAIN, terrainPoint);
    }

    private void ClearHover()
    {
        if (currentHoveredHandler != null)
        {
            currentHoveredHandler.onHoverEnd(GetModeForObjectType(currentHoveredObjectType));
        }

        // Also hide district tooltips when hover ends on non-path objects.
        if (currentHoveredObjectType != InputObjectType.PATH)
        {
            TooltipManager.Instance?.Hide();
        }

        currentHoveredObject = null;
        currentHoveredHandler = null;
        currentHoveredObjectType = -1;
    }

    /// <summary>
    /// Classify object types by tag (more robust than string parsing).
    /// </summary>
    private int GetObjectType(GameObject obj)
    {
        if (obj.CompareTag("BuildingObject")) return InputObjectType.BUILDING;
        if (obj.CompareTag("ParcelObject")) return InputObjectType.PARCEL;
        if (obj.CompareTag("PathObject")) return InputObjectType.PATH;
        if (obj.CompareTag("PathNode")) return InputObjectType.NODE;
        if (obj.CompareTag("ZoneObject")) return InputObjectType.ZONE;
        if (obj.CompareTag("SeaObject")) return InputObjectType.TERRAIN;
        if (obj.GetComponent<Terrain>() != null) return InputObjectType.TERRAIN;
        if (IsTerrainSurfaceObject(obj)) return InputObjectType.TERRAIN;

        return -1; // Unknown type
    }

    /// <summary>Prioritizes underground paths through terrain; otherwise returns the first selectable hit.</summary>
    private bool TryGetObjectHit(RaycastHit[] hits, out RaycastHit selected)
    {
        if (TryGetUndergroundHit(hits, out selected)) return true;
        foreach (var hit in hits)
        {
            var obj = hit.collider.gameObject;
            if (!CanSelectObject(obj, hit.point, GetObjectType(obj))) continue;
            selected = hit;
            return true;
        }
        return false;
    }

    private bool CanSelectObject(GameObject obj, Vector3 point, int objectType)
    {
        if (obj == null || GetModeForObjectType(objectType) == InputMode.MODE_NONE) return false;
        if (!UndergroundView.IsActive) return true;
        // Keep surface models as location guides, but exclude them from hover, clicks, and pending drags.
        if (objectType == InputObjectType.BUILDING || objectType == InputObjectType.PARCEL) return false;
        return objectType != InputObjectType.PATH || IsUndergroundSelection(obj, point);
    }

    private GameObject GetSelectableParcelAtPoint(Vector3 point)
    {
        if (GetModeForObjectType(InputObjectType.PARCEL) == InputMode.MODE_NONE || cachedParcelManager == null) return null;
        int parcelId = cachedParcelManager.GetParcelIdAtPoint(point);
        if (parcelId < 0) return null;
        GameObject parcel = cachedParcelManager.GetParcelObject(parcelId);
        // Apply collider-hit selection restrictions to parcels found by terrain containment too.
        return parcel != null && parcel.activeInHierarchy && CanSelectObject(parcel, point, InputObjectType.PARCEL) ? parcel : null;
    }

    private bool TryGetUndergroundHit(RaycastHit[] hits, out RaycastHit selected)
    {
        selected = default;
        if (!UndergroundView.IsActive || GetModeForObjectType(InputObjectType.PATH) == InputMode.MODE_NONE) return false;
        // Surface objects remain translucent location guides. Modes disabling PATH (e.g. construction) use normal terrain rays.
        foreach (var hit in hits)
        {
            if (!IsUndergroundSelection(hit.collider.gameObject, hit.point)) continue;
            if (selected.collider == null || hit.distance < selected.distance) selected = hit;
        }
        return selected.collider != null;
    }

    private bool IsUndergroundSelection(GameObject obj, Vector3 point)
    {
        if (!UndergroundView.IsActive || obj == null) return false;
        var handler = obj.GetComponent<PathInputHandler>();
        if (handler == null) return false;
        if (cachedPathManager == null) cachedPathManager = FindAnyObjectByType<PathManager>();
        var path = cachedPathManager?.GetPathData(handler.pathId);
        return path != null && TunnelSectionUtil.IsTunnelPoint(point, TunnelSectionUtil.GetClearanceHeight(path.path_variant));
    }

    /// <summary>
    /// Whether a surface without Terrain should behave like a terrain tile.
    /// Background terrain chunks outside the map allow placing/selecting external facilities (e.g. line stops at border freight stations).
    /// Reclaimed land quay walls (top and vertical faces) prevent the cursor from passing through their colliders to the seabed.
    /// </summary>
    private bool IsTerrainSurfaceObject(GameObject obj) =>
        (cachedTerrainManager != null && cachedTerrainManager.IsBackgroundTerrain(obj)) ||
        (ReclaimedLandManager.Instance != null && ReclaimedLandManager.Instance.IsQuayObject(obj));

    private int GetModeForObjectType(int objectType)
    {
        if (objectTypeModes.ContainsKey(objectType))
        {
            return objectTypeModes[objectType];
        }
        return InputMode.MODE_NORMAL;
    }

    private void StartDrag(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);

        // Bulldoze, merge/split selection, and road section editing support clicks only. Disable dragging
        // and return immediately to avoid falling back to RoadPlanManager, etc.
        if (currentMode == InputMode.MODE_REMOVE
            || currentMode == InputMode.MODE_PATH_MERGE
            || currentMode == InputMode.MODE_PATH_SPLIT
            || currentMode == InputMode.MODE_EDIT_ROAD_MEMBERS)
        {
            return;
        }

        isDragging = true;
        dragStartPosition = hitPoint;
        draggedObject = hitObject;
        draggedObjectType = objectType;

        // Notify drag start for nodes.
        if (objectType == InputObjectType.NODE)
        {
            var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
            nodeHandler?.onDragStart(currentMode);
        }
    }

    /// <summary>
    /// Called only when the ray misses everything. Terrain-hit drags start through StartDrag(TERRAIN) and bypass this,
    /// so terrain-drag managers choose the grabbed target from the first ProcessMouseDrag mouse position.
    /// </summary>
    private void StartTerrainDrag()
    {
        int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);

        // Bulldoze and split-point selection are click-only. Do not start terrain drags.
        if (currentMode == InputMode.MODE_REMOVE
            || currentMode == InputMode.MODE_PATH_SPLIT)
        {
            return;
        }

        if (currentMode == InputMode.MODE_ROAD_CONSTRUCTION || currentMode == InputMode.MODE_EDIT)
        {
            // Start an editor drag if the mouse is over a handle while editing roads.
            if (cachedRoadPlanManager != null && cachedRoadPlanManager.IsEditMode)
            {
                if (cachedRoadPlanManager.IsMouseOverEditHandle(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    return;
                }
            }

            isDragging = true;
            dragStartPosition = new Vector3(posX, 0, posZ);
            draggedObject = null;
            draggedObjectType = InputObjectType.TERRAIN;
        }
        else if (currentMode == InputMode.MODE_PARCEL_CREATION_POINT)
        {
            if (cachedParcelEditorManager != null && cachedParcelEditorManager.HasSelectedPoint())
            {
                isDragging = true;
                dragStartPosition = new Vector3(posX, 0, posZ);
                draggedObject = null;
                draggedObjectType = InputObjectType.TERRAIN;
                cachedParcelEditorManager.StartRotationDrag();
            }
        }
        else if (currentMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
        {
            var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
            if (shapeEditManager != null && shapeEditManager.IsEditMode)
            {
                // Start an editor drag when dragging begins over a vertex.
                if (shapeEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    shapeEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_RESOURCE)
        {
            var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
            if (resourceEditManager != null && resourceEditManager.IsEditingPhase)
            {
                // Start an editor drag when dragging begins over a vertex (Editing phase only).
                if (resourceEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    resourceEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_GRADING)
        {
            var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
            if (gradingEditManager != null && gradingEditManager.IsEditingPhase)
            {
                // Start an editor drag when dragging begins over a vertex (Editing phase only).
                if (gradingEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    gradingEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_TRANSIT)
        {
            var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
            if (transitPlanManager != null && transitPlanManager.IsEditingPhase)
            {
                // Start moving a stop/waypoint when over its marker, or insert/move a waypoint when over a section.
                if (transitPlanManager.IsMouseOverDraggable(InputUtil.MousePosition))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    transitPlanManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_CONNECTOR)
        {
            var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
            if (connectorEditManager != null && connectorEditManager.IsEditMode)
            {
                // Start a terminal drag when dragging begins over a terminal node marker.
                if (connectorEditManager.IsMouseOverConnector(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    connectorEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
    }

    private void UpdateDrag()
    {
        Vector3 currentPosition = new Vector3(posX, 0, posZ);

        if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
        {
            var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
            nodeHandler?.onDragUpdate(currentPosition);
        }
        else if (draggedObjectType == InputObjectType.TERRAIN)
        {
            int terrainMode = GetModeForObjectType(InputObjectType.TERRAIN);
            if (terrainMode == InputMode.MODE_PARCEL_CREATION_POINT)
            {
                cachedParcelEditorManager?.UpdateRotationPreview(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
            {
                var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
                shapeEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_RESOURCE)
            {
                var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
                resourceEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_GRADING)
            {
                var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
                gradingEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_TRANSIT)
            {
                var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
                transitPlanManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_CONNECTOR)
            {
                var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
                connectorEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (cachedRoadPlanManager != null)
            {
                cachedRoadPlanManager.ProcessMouseDrag(currentPosition);
            }
        }
        else
        {
            // Send drag information to RoadPlanManager.
            if (cachedRoadPlanManager != null)
            {
                cachedRoadPlanManager.ProcessMouseDrag(currentPosition);
            }
        }
    }

    private void EndDrag()
    {
        if (isDragging)
        {
            Vector3 endPosition = new Vector3(posX, 0, posZ);

            if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
            {
                var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
                nodeHandler?.onDragEnd(endPosition);
            }
            else if (draggedObjectType == InputObjectType.TERRAIN)
            {
                int terrainMode = GetModeForObjectType(InputObjectType.TERRAIN);
                if (terrainMode == InputMode.MODE_PARCEL_CREATION_POINT)
                {
                    cachedParcelEditorManager?.EndRotationDrag();
                }
                else if (terrainMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
                {
                    var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
                    shapeEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_RESOURCE)
                {
                    var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
                    resourceEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_GRADING)
                {
                    var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
                    gradingEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_TRANSIT)
                {
                    var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
                    transitPlanManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_CONNECTOR)
                {
                    var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
                    connectorEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (cachedRoadPlanManager != null)
                {
                    cachedRoadPlanManager.ProcessMouseDragEnd(endPosition);
                }
            }
            else
            {
                // Send drag-end information to RoadPlanManager.
                if (cachedRoadPlanManager != null)
                {
                    cachedRoadPlanManager.ProcessMouseDragEnd(endPosition);
                }
            }

            isDragging = false;
            draggedObject = null;
            draggedObjectType = -1;
        }
    }

    private TerrainInputHandler GetTerrainInputHandlerAtPosition(Vector3 position)
    {
        // 1. First try TerrainManager's terrain tiles.
        if (cachedTerrainManager != null)
        {
            Terrain terrain = cachedTerrainManager.GetTerrainAtPosition(position);
            if (terrain != null)
            {
                TerrainInputHandler handler = terrain.GetComponent<TerrainInputHandler>();
                if (handler != null)
                {
                    return handler;
                }
            }
        }

        // 2. Fallback: get any TerrainInputHandler in the scene.
        TerrainInputHandler fallbackHandler = FindAnyObjectByType<TerrainInputHandler>();
        if (fallbackHandler != null)
        {
            return fallbackHandler;
        }

        return null;
    }

    #region Public Methods

    public void SetClickEvent(Action<int, Vector3> callback)
    {
        clickEventCallback = callback;
    }

    public void SetHoverEvent(Action<int, Vector3, GameObject> callback)
    {
        hoverEventCallback = callback;
    }

    public void SetMode(int objectType, int mode)
    {
        if (objectTypeModes.ContainsKey(objectType))
        {
            objectTypeModes[objectType] = mode;
            Debug.Log($"InputHandler: オブジェクトタイプ {objectType} のモードを {mode} に変更しました");
        }
        else
        {
            Debug.LogWarning($"InputHandler: 不明なオブジェクトタイプ {objectType} です");
        }
    }

    public int GetMode(int objectType)
    {
        return GetModeForObjectType(objectType);
    }

    public void SetModePreset(int preset)
    {
        switch (preset)
        {
            case InputModePreset.ALL_NONE:
                SetAllObjectTypesMode(InputMode.MODE_NONE);
                Debug.Log("InputHandler: すべてのオブジェクトタイプをNONEモードに設定しました");
                break;

            case InputModePreset.ALL_NORMAL:
                SetAllObjectTypesMode(InputMode.MODE_NORMAL);
                Debug.Log("InputHandler: すべてのオブジェクトタイプをNORMALモードに設定しました");
                break;

            case InputModePreset.ROAD_CONSTRUCTION:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_ROAD_CONSTRUCTION);
                Debug.Log("InputHandler: 道路建設モードプリセットを設定しました");
                break;

            case InputModePreset.BUILDING_CONSTRUCTION:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_BUILDING_CONSTRUCTION);
                // BuildingPlanManager manages construction placement methods. Parcel placement is the default;
                // free/roadside placement sets PARCEL to MODE_NONE to pass through to terrain.
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_BUILDING_CONSTRUCTION);
                Debug.Log("InputHandler: 建物建設モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_POINT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_CREATION_POINT);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画作成（点選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_PATH:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_PARCEL_CREATION_PATH);
                Debug.Log("InputHandler: 土地区画作成（パス選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_SPLIT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_PARCEL_CREATION_SPLIT);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画分割モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_SHAPE_EDIT:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_SHAPE_EDIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画形状編集モードプリセットを設定しました");
                break;

            case InputModePreset.SET_GRID:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_SET_GRID);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: グリッド設定ノード選択モードプリセットを設定しました");
                break;

            case InputModePreset.EDIT_RESOURCE:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_RESOURCE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 資源エリア編集モードプリセットを設定しました");
                break;

            case InputModePreset.EDIT_GRADING:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_GRADING);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 造成範囲の作図モードプリセットを設定しました");
                break;


            case InputModePreset.TRANSIT_CONSTRUCTION:
                // Stop placement uses terrain clicks. Pass through underlying road/station paths
                // to select terrain hits, then TransitPlanManager projects/snaps to the underlying path.
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_TRANSIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 公共交通設置モードプリセットを設定しました");
                break;

            case InputModePreset.PROP_PLACEMENT:
                // Prop placement uses terrain clicks: single points for instances, polygon vertices for scatter areas.
                // PropPlacementManager dispatches by mode (individual/scatter).
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_PROP);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: プロップ配置モードプリセットを設定しました");
                break;

            case InputModePreset.CONNECTOR_EDIT:
                // Connector edit mode. Grab terminal node markers (inverted square pyramids) and drag to move.
                // Markers float above node objects as overlays, displaced from their colliders,
                // so receive terrain drags rather than collider raycasts;
                // ConnectorEditManager chooses targets by screen-space marker hit tests
                // (same as stop placement MODE_EDIT_TRANSIT). Pass raycasts through nodes
                // (MODE_NONE) and disable other object clicks to prevent accidental operations.
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_CONNECTOR);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 接続点編集モードプリセットを設定しました");
                break;

            case InputModePreset.REMOVE_OBJECTS:
                // Bulldoze mode. Pick buildings, parcels, and roads for deletion.
                // Setting TERRAIN to MODE_REMOVE also resolves empty parcel colliders buried in terrain
                // through terrain→parcel substitution (GetParcelIdAtPoint), and captures drag origins on bare terrain.
                SetMode(InputObjectType.BUILDING, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.PATH, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: ブルドーザ(オブジェクト削除)モードプリセットを設定しました");
                break;

            case InputModePreset.PATH_MERGE:
                // Merge partner selection. Accept only candidate path clicks; disable other targets to prevent mistakes.
                SetMode(InputObjectType.PATH, InputMode.MODE_PATH_MERGE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: パス結合の相手選択モードプリセットを設定しました");
                break;

            case InputModePreset.PATH_SPLIT:
                // Split-point selection. Accept clicks on both the target path and neighboring terrain,
                // using the perpendicular projection onto the centerline (same approach as the backend).
                SetMode(InputObjectType.PATH, InputMode.MODE_PATH_SPLIT);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PATH_SPLIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: パス分割の分割点選択モードプリセットを設定しました");
                break;

            case InputModePreset.PICK_BUILDING:
                // Source selection for copying buildings. Accept only building clicks.
                SetMode(InputObjectType.BUILDING, InputMode.MODE_PICK_BUILDING);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: コピー元建物選択モードプリセットを設定しました");
                break;

            case InputModePreset.ROAD_MEMBER_EDIT:
                // Road section editing. Accept only section clicks; disable other targets to prevent mistakes.
                SetMode(InputObjectType.PATH, InputMode.MODE_EDIT_ROAD_MEMBERS);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 道路の区間編集モードプリセットを設定しました");
                break;

            default:
                Debug.LogWarning($"InputHandler: 不明なプリセット {preset} です");
                break;
        }
    }

    private void SetAllObjectTypesMode(int mode)
    {
        var objectTypes = new int[]
        {
            InputObjectType.BUILDING,
            InputObjectType.PARCEL,
            InputObjectType.PATH,
            InputObjectType.NODE,
            InputObjectType.TERRAIN,
            InputObjectType.ZONE
        };

        foreach (int objectType in objectTypes)
        {
            SetMode(objectType, mode);
        }
    }

    public void OverrideMousePosition(float x, float z)
    {
        posX = x;
        posZ = z;
    }

    public string GetModeName(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NONE: return "何もしない";
            case InputMode.MODE_NORMAL: return "通常";
            case InputMode.MODE_ROAD_CONSTRUCTION: return "道路建設";
            case InputMode.MODE_BUILDING_CONSTRUCTION: return "建物建設";
            case InputMode.MODE_PARCEL_CREATION_POINT: return "土地区画作成（点選択）";
            case InputMode.MODE_PARCEL_CREATION_PATH: return "土地区画作成（パス選択）";
            case InputMode.MODE_PARCEL_CREATION_SPLIT: return "土地区画分割";
            case InputMode.MODE_EDIT: return "編集";
            case InputMode.MODE_PARCEL_SHAPE_EDIT: return "土地区画形状編集";
            case InputMode.MODE_SET_GRID: return "グリッド角度設定";
            case InputMode.MODE_EDIT_RESOURCE: return "資源エリア編集";
            case InputMode.MODE_EDIT_TRANSIT: return "公共交通設置";
            case InputMode.MODE_EDIT_PROP: return "プロップ配置";
            case InputMode.MODE_EDIT_CONNECTOR: return "接続点編集";
            case InputMode.MODE_EDIT_ROAD_MEMBERS: return "道路の区間編集";
            case InputMode.MODE_EDIT_GRADING: return "造成範囲の作図";
            default: return "UNKNOWN";
        }
    }

    #endregion
}
