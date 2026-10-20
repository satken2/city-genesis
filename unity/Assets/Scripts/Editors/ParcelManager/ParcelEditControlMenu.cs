using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Driver that switches parcel creation modes entirely within Unity.
/// Resides on the ParcelEditorManager GameObject and independently polls whether creation mode is active
/// (the shared base <see cref="EditControlMenuBase"/> opens/closes the Ctrl radial menu and cursor HUD).
/// The ON ring indicates the current creation mode.
///
/// ParcelEditorManager.CurrentMode is the source of truth; switching here
/// notifies the creation panel through `edit_parcel:set_mode` (panel tiles reflect the same state).
/// The panel owns numeric parameters such as width, depth, and angle.
/// </summary>
public class ParcelEditControlMenu : EditControlMenuBase
{
    private ParcelEditorManager manager;
    private List<RadialMenuItem> items;

    /// <summary>Icons by mode ID (Resources/UIIcons; null when absent, falling back to the first label character).</summary>
    private readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();

    protected override bool IsSessionActive => manager != null && manager.IsActive;
    protected override List<RadialMenuItem> MenuItems => items;

    /// <summary>Initialization called from IpcStateCoordinator.ApplyStateEditParcel.</summary>
    public void Initialize(ParcelEditorManager editorManager)
    {
        manager = editorManager;

        items = new List<RadialMenuItem>();
        foreach (var info in ParcelEditorManager.Modes)
        {
            if (info.HiddenFromMenu) continue;

            icons[info.Id] = Utils.LoadUIIcon(info.IconName);

            // Copy to a local variable to capture the loop variable in the closure.
            ParcelEditorManager.ModeInfo mode = info;
            items.Add(new RadialMenuItem
            {
                Id = mode.Id,
                Label = mode.Label,
                GetIcon = () => icons.TryGetValue(mode.Id, out var sprite) ? sprite : null,
                GetState = () => manager != null && manager.CurrentMode == mode.Id,
                OnToggle = () => manager?.SetCreationMode(mode.Id),
            });
        }
    }

    /// <summary>
    /// Cursor HUD content: the current creation mode icon and text in the form mode name / next action.
    ///
    /// After selection, prompts confirmation in the panel (the confirmation button is in Tauri,
    /// so the next action is unclear when looking only at the 3D view).
    /// </summary>
    protected override (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();
        if (manager == null) return (hudIcons, "");

        ParcelEditorManager.ModeInfo info = manager.CurrentModeInfo;
        hudIcons.Add(new CursorHudIcon(
            icons.TryGetValue(info.Id, out var sprite) ? sprite : null, true));

        string action = manager.HasSelection() ? "パネルで確定 (Escで選び直し)" : info.Hint;
        return (hudIcons, $"{info.Label}｜{action}");
    }
}
