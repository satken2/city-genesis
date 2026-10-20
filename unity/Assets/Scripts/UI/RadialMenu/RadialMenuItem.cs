using System;
using UnityEngine;

/// <summary>
/// Definition of one radial-menu item.
/// State (ON/OFF, enabled/disabled, icon) is evaluated every frame while displayed,
/// so pass Func/Action rather than fixed values. This is pure data independent of editing modes;
/// callers such as road editors supply closures over their own manager state.
/// </summary>
public class RadialMenuItem
{
    /// <summary>Identifier for logging and debugging.</summary>
    public string Id;

    /// <summary>Short label shown at the menu center on hover. ASCII is recommended because a Japanese font is not installed.</summary>
    public string Label;

    /// <summary>Display icon, supplied as a Func to support state-dependent icons such as straight/curve toggles.</summary>
    public Func<Sprite> GetIcon;

    /// <summary>Current ON/OFF state. ON items are highlighted with an accent-colored ring.</summary>
    public Func<bool> GetState;

    /// <summary>Enable condition; null means always enabled. Disabled items are gray and committing them does nothing.</summary>
    public Func<bool> IsEnabled;

    /// <summary>Toggle action executed when the selection is committed by releasing the trigger key.</summary>
    public Action OnToggle;

    /// <summary>Check whether enabled; always true if IsEnabled is unspecified.</summary>
    public bool Enabled => IsEnabled == null || IsEnabled();
}
