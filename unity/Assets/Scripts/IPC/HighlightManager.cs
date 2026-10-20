using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages interference highlighting for parcels, buildings, and existing paths affected by a planned path.
/// Driven by IpcEventHandler on world:highlight_objects / world:clear_highlights.
///
/// - Parcels: ParcelManager 2D overlay fills by classification (blocking=red/removable=orange/shavable=yellow,
///   exempt=none).
/// - Buildings: translucent occupied boxes (OccupancyVolumeUtil.BuildingVolumes), red for public facilities and orange for private buildings.
/// - Existing paths: translucent occupied tubes (OccupancyVolumeUtil.AddTube).
/// - Rivers: occupied boxes in the same warning color for rivers crossed at ground level (RiverStore.AddOccupancyBox).
///
/// Each source (construction="path_build" / detail panel="path_detail") has an independent
/// highlight layer; compose all sources on each draw so clearing one window's
/// highlights does not erase another's.
///
/// Backend compute_path_interference is authoritative. This is separate from construction local estimates
/// (RoadPlanManager), overlaying the confirmed results.
/// </summary>
public class HighlightManager
{
    // Parcel classification colors (translucent fills)
    private static readonly Color BlockingParcelColor = new Color(1.0f, 0.20f, 0.20f, 0.55f); // Red: cannot start construction (land acquisition required)
    private static readonly Color RemovableParcelColor = new Color(1.0f, 0.55f, 0.10f, 0.50f); // Orange: construction possible after deletion
    private static readonly Color ShavableParcelColor = new Color(1.0f, 0.85f, 0.20f, 0.50f); // Yellow: survives partial expropriation
    // Do not highlight exempt parcels (construction needs no changes).

    private OverlayMeshRenderer occupancyOverlay;

    // Interference payload per source, used for composite drawing.
    private readonly Dictionary<string, PathInterferencePayload> bySource =
        new Dictionary<string, PathInterferencePayload>();

    private ParcelManager parcelManager;
    private PathManager pathManager;

    /// <summary>Sets one source's interference highlights and redraws the composition of all sources.</summary>
    public void Apply(string source, PathInterferencePayload interference)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        if (interference == null || interference.IsEmpty) bySource.Remove(source);
        else bySource[source] = interference;
        Recompose();
    }

    /// <summary>Clears one source's interference highlights and redraws remaining sources.</summary>
    public void Clear(string source)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        if (bySource.Remove(source)) Recompose();
    }

    /// <summary>Composes all sources' interference and redraws parcel/building/path highlights.</summary>
    private void Recompose()
    {
        // Compose parcel classifications across sources (priority: blocking > removable > shavable).
        var parcelColors = new Dictionary<int, Color>();
        var buildingIds = new HashSet<int>();
        var pathIds = new HashSet<int>();
        var riverIds = new HashSet<string>();
        foreach (var interference in bySource.Values)
        {
            foreach (int id in interference.ShavableParcels) parcelColors[id] = ShavableParcelColor;
            foreach (int id in interference.RemovableParcels) parcelColors[id] = RemovableParcelColor;
            foreach (int id in interference.BlockingParcels) parcelColors[id] = BlockingParcelColor;
            foreach (int id in interference.BuildingIds) buildingIds.Add(id);
            foreach (int id in interference.CollidingPathIds) pathIds.Add(id);
            foreach (string id in interference.CollidingRiverIds) riverIds.Add(id);
        }

        GetParcelManager()?.SetInterferenceParcels(parcelColors);

        var buildings = new OccupancyVolumeUtil.BuildingVolumes();
        if (BuildingManager.Instance != null)
            foreach (int id in buildingIds)
                buildings.Add(BuildingManager.Instance.GetBuildingSummary(id));
        var triangles = new List<Vector3>();
        var pm = GetPathManager();
        if (pm != null)
            foreach (int id in pathIds)
                if (id >= 0) OccupancyVolumeUtil.AddPath(triangles, pm, id);
        // Authoritative river validation does not return crossing sections, so show occupied boxes over the entire river area.
        // During construction, RoadPlanManager overlays local boxes only near the crossing.
        foreach (string id in riverIds)
            RiverStore.AddOccupancyBox(triangles, RiverStore.FindById(id), null, 0f);
        // Depth-test occupied boxes for natural 3D occlusion by buildings/terrain.
        bool hasVolume = triangles.Count > 0 || !buildings.IsEmpty;
        if (occupancyOverlay == null && hasVolume)
            occupancyOverlay = OverlayMeshRenderer.Create("InterferenceOccupancy", alwaysOnTop: false);
        if (occupancyOverlay != null)
        {
            occupancyOverlay.BeginFrame();
            occupancyOverlay.AddPreTriangulated(triangles, OccupancyVolumeUtil.WarningColor);
            buildings.Draw(occupancyOverlay);
            occupancyOverlay.EndFrame(Camera.main);
            occupancyOverlay.gameObject.SetActive(hasVolume);
        }
    }

    private ParcelManager GetParcelManager()
    {
        if (parcelManager == null) parcelManager = Object.FindAnyObjectByType<ParcelManager>();
        return parcelManager;
    }

    private PathManager GetPathManager()
    {
        if (pathManager == null) pathManager = Object.FindAnyObjectByType<PathManager>();
        return pathManager;
    }
}
