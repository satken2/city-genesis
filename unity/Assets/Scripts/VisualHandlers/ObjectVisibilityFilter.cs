using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Filter hiding buildings and roads per object.
///
/// Independent of layer-level controls such as VisualModeHandler color modes and visibility flags,
/// it directly stores hidden agent_id / path_id sets. Its only input is the full set sent by Tauri's
/// construction-project and detail panels via view:set_object_filter.
///
/// State exists only in this session, the Unity process. The backend stores no visibility state
/// and saves none; tags persist, but whether a tag is hidden does not.
/// The UI sends ID sets rather than tag names, so graphics code need not know about tags.
///
/// For objects created later through bbox-delta streaming,
/// BuildingManager / PathManager query IsHidden immediately after creation to set initial visibility.
/// </summary>
public static class ObjectVisibilityFilter
{
    private static readonly HashSet<int> hiddenBuildings = new HashSet<int>();
    private static readonly HashSet<int> hiddenPaths = new HashSet<int>();

    /// <summary>Number of hidden buildings.</summary>
    public static int HiddenBuildingCount => hiddenBuildings.Count;

    /// <summary>Number of hidden roads.</summary>
    public static int HiddenPathCount => hiddenPaths.Count;

    /// <summary>Revision incremented on every hidden-set change, used to decide whether citywide ultra-far combined meshes need rebuilding.</summary>
    public static int Revision { get; private set; }

    /// <summary>Whether the filter hides the specified object.</summary>
    public static bool IsHidden(int objectType, int id)
    {
        if (objectType == InputObjectType.BUILDING) return hiddenBuildings.Contains(id);
        if (objectType == InputObjectType.PATH) return hiddenPaths.Contains(id);
        return false;
    }

    /// <summary>
    /// Replace the full hidden set and apply it to existing objects.
    /// Use full replacement because the UI can always compute the complete current hidden set,
    /// eliminating visibility inconsistencies caused by missed additions/removals.
    /// </summary>
    public static void SetHidden(IEnumerable<int> buildingIds, IEnumerable<int> pathIds)
    {
        hiddenBuildings.Clear();
        hiddenPaths.Clear();
        if (buildingIds != null) foreach (int id in buildingIds) hiddenBuildings.Add(id);
        if (pathIds != null) foreach (int id in pathIds) hiddenPaths.Add(id);
        Revision++;
        ApplyToExistingObjects();
    }

    /// <summary>Clear all hidden-object selections, such as on session reset.</summary>
    public static void Clear()
    {
        if (hiddenBuildings.Count == 0 && hiddenPaths.Count == 0) return;
        hiddenBuildings.Clear();
        hiddenPaths.Clear();
        Revision++;
        ApplyToExistingObjects();
    }

    /// <summary>Return a copy of the current hidden set for UI state notifications.</summary>
    public static List<int> GetHiddenBuildings() => new List<int>(hiddenBuildings);

    /// <summary>Return a copy of the current hidden set for UI state notifications.</summary>
    public static List<int> GetHiddenPaths() => new List<int>(hiddenPaths);

    /// <summary>
    /// Apply the current filter to existing objects.
    /// BuildingManager / PathManager caches already enumerate the targets,
    /// so do not scan the entire scene.
    /// </summary>
    private static void ApplyToExistingObjects()
    {
        if (BuildingManager.Instance != null) BuildingManager.Instance.RefreshVisibilityFilter();
        var pathManager = Object.FindAnyObjectByType<PathManager>();
        if (pathManager != null) pathManager.RefreshVisibilityFilter();
    }

    /// <summary>
    /// Apply the filter immediately after object creation.
    /// Distant buildings have no VisualModeObject because FarBuildingBatcher combines them,
    /// so FarBuildingMember applies layer settings and filtering together and notifies the combined batch.
    /// For objects still being created without members, toggle MeshRenderer directly.
    /// </summary>
    public static void ApplyToObject(GameObject target, int objectType, int id)
    {
        if (target == null) return;
        bool hidden = IsHidden(objectType, id);
        var visual = target.GetComponent<VisualModeObject>();
        if (visual != null)
        {
            visual.SetFilterHidden(hidden);
            return;
        }
        var far = target.GetComponent<FarBuildingMember>();
        if (far != null)
        {
            far.ApplyVisualMode(VisualModeHandler.Instance?.GetMode(objectType) ?? VisualMode.MODE_NORMAL);
            return;
        }
        var renderer = target.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = !hidden;
        var collider = target.GetComponent<Collider>();
        if (collider != null) collider.enabled = !hidden;
    }
}
