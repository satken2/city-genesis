using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Parcel shape edit manager for editing existing parcel polygon vertices.
///
/// The shared base <see cref="PolygonAreaEditManager"/> handles polygon vertex editing:
/// dragging vertices, inserting them by clicking edges, right-click deletion, and overlay drawing.
/// This class loads a saved polygon and starts directly in Editing (skipping vertex Placing),
/// reports to the UI through edit_parcel:update_shape, and controls the original parcel object's visibility.
///
/// The UI (Tauri) directly confirms changes (PATCH /parcels/{id}); after a successful save,
/// `edit_parcel:finish_shape_edit` invokes CompleteEditMode to end editing.
/// </summary>
public class ParcelShapeEditManager : PolygonAreaEditManager
{
    [Header("Parcel")]
    public ParcelManager parcelManager;

    /// <summary>Edited parcel ID, or -1 when not editing.</summary>
    private int editingParcelId = -1;

    protected override string OverlayCanvasName => "ParcelShapeEditOverlayCanvas";

    // Draw parcel boundaries in road-like blue (distinct from warm resource area colors).
    protected override Color EdgeColor => new Color(0.3f, 0.6f, 1f);
    protected override Color VertexColor => new Color(0.54f, 0.71f, 0.98f);

    protected override void Start()
    {
        base.Start();
        if (parcelManager == null)
        {
            parcelManager = FindAnyObjectByType<ParcelManager>();
        }
    }

    // =====================================================================
    // Public API: mode transitions
    // =====================================================================

    /// <summary>
    /// Initializes shape editing.
    /// Builds edit state from the specified parcel_id and polygon ([x, z] sequence), starting in Editing.
    /// </summary>
    public void InitializeEditMode(int parcelId, float[][] polygon)
    {
        Debug.Log($"ParcelShapeEditManager: 形状編集モード開始 parcel={parcelId}");

        var points = new List<Vector3>();
        if (polygon != null)
        {
            foreach (var pt in polygon)
            {
                if (pt == null || pt.Length < 2) continue;
                float x = pt[0];
                float z = pt[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                points.Add(new Vector3(x, y, z));
            }
        }

        editingParcelId = parcelId;

        // Hide the existing parcel object so only the editing overlay is visible.
        HideOriginalParcelObject(parcelId);

        InitializeEditingWithPolygon(points);
    }

    /// <summary>
    /// Ends edit mode and clears state, also restoring the hidden original parcel object.
    /// </summary>
    public override void FinishEditMode()
    {
        base.FinishEditMode();
        ShowOriginalParcelObject(editingParcelId);
        editingParcelId = -1;
    }

    /// <summary>
    /// Ends editing after the UI successfully saves the polygon (PATCH /parcels/{id}).
    /// The UI (Tauri) sends the backend request directly, so this only clears edit state,
    /// refetches current data, and redraws parcel outlines.
    /// </summary>
    public void CompleteEditMode()
    {
        FinishEditMode();

        // Incremental redraw. Version comparison skips unchanged objects.
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }

        // Request a forced redraw to update parcel outlines immediately even if the camera has not moved.
        if (parcelManager != null)
        {
            parcelManager.ForceOutlineRedraw();
        }
    }

    // =====================================================================
    // IPC
    // =====================================================================

    /// <summary>
    /// Reports the current polygon to the UI through edit_parcel:update_shape.
    /// Always in Editing, so finalized is unused.
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditParcelUpdateShape(editingParcelId, polygon);
    }

    // =====================================================================
    // Existing parcel object visibility control
    // =====================================================================

    private void HideOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(false);
        }
    }

    private void ShowOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(true);
        }
    }
}
