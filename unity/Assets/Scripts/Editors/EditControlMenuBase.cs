using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shared base driver for the Ctrl-hold radial menu and cursor HUD in edit mode.
/// Resides on the manager's GameObject and independently checks each frame whether editing is active
/// (polling covers all exit paths more reliably than adding a hook at every exit).
///
/// Controls:
/// - Hold Ctrl: expand the radial menu from the cursor. Hover to highlight an item,
///   then release Ctrl to select/toggle it (RadialMenuController).
/// - While active, continuously show the current state beside the cursor (CursorStatusHud).
///
/// Derived classes supply only the active-state check, menu items, and HUD content;
/// this base owns HUD registration, menu opening/closing, and cleanup of menus it opened.
/// </summary>
public abstract class EditControlMenuBase : MonoBehaviour
{
    private bool hudActive = false;

    /// <summary>Ownership flag to close only this driver's menu without affecting menus opened by other drivers.</summary>
    private bool menuOpenedByThis = false;

    /// <summary>List returned by the HUD provider each frame. Reused to avoid GC.</summary>
    protected readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>Whether an edit session is active. Return false if the target manager is uninitialized.</summary>
    protected abstract bool IsSessionActive { get; }

    /// <summary>
    /// Radial menu items. State is read/written via Func/Action, so build them once during initialization.
    /// Do not open the menu while null (uninitialized).
    /// </summary>
    protected abstract List<RadialMenuItem> MenuItems { get; }

    /// <summary>Cursor HUD content. Keep this lightweight because it is called every frame.</summary>
    protected abstract (List<CursorHudIcon> icons, string text) BuildHudContent();

    /// <summary>Hook immediately after releasing Ctrl to commit an item (e.g. recalculate a snapped preview).</summary>
    protected virtual void OnMenuCommitted() { }

    protected virtual void Update()
    {
        bool active = IsSessionActive;

        // Register/unregister the HUD (detect session start/end).
        if (active && !hudActive)
        {
            CursorStatusHud.EnsureInstance().SetProvider(BuildHudContent);
            hudActive = true;
        }
        else if (!active && hudActive)
        {
            ReleaseUi();
        }

        if (!active) return;
        if (TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput)
        {
            if (menuOpenedByThis) { RadialMenuController.Instance?.CloseAndCancel(); menuOpenedByThis = false; }
            return;
        }

        bool ctrlHeld = InputUtil.KeyPressed(Key.LeftCtrl) || InputUtil.KeyPressed(Key.RightCtrl);
        bool ctrlPressedNow = InputUtil.KeyWasPressedThisFrame(Key.LeftCtrl) || InputUtil.KeyWasPressedThisFrame(Key.RightCtrl);

        if (ctrlPressedNow && !RadialMenuController.IsOpen)
        {
            // Do not open while a mouse button is held (would interrupt held-button gestures such as dragging, rotation, or angle selection).
            if (!InputUtil.LeftMouseIsPressed && !InputUtil.RightMouseIsPressed && !InputUtil.MiddleMouseIsPressed)
            {
                List<RadialMenuItem> items = MenuItems;
                if (items == null) return;
                RadialMenuController.EnsureInstance().Open(items, InputUtil.MousePosition);
                menuOpenedByThis = true;
            }
        }
        else if (menuOpenedByThis && RadialMenuController.IsOpen && !ctrlHeld)
        {
            RadialMenuController.Instance.CloseAndCommit();
            menuOpenedByThis = false;
            OnMenuCommitted();
        }
    }

    protected virtual void OnDisable()
    {
        ReleaseUi();
    }

    /// <summary>Unregisters the HUD and closes its own menu without selecting an item.</summary>
    private void ReleaseUi()
    {
        if (hudActive)
        {
            CursorStatusHud.Instance?.ClearProvider();
            hudActive = false;
        }
        if (menuOpenedByThis)
        {
            RadialMenuController.Instance?.CloseAndCancel();
            menuOpenedByThis = false;
        }
    }
}
