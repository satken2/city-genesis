
using UnityEngine;

/// <summary>
/// Interface implemented by all input handlers.
/// Enables polymorphic dispatch without switch branches for object types.
/// </summary>
public interface IInputHandler
{
    /// <summary>InputObjectType constants (BUILDING=1, PARCEL=2, PATH=3, NODE=4, TERRAIN=5, ZONE=6).</summary>
    int ObjectTypeId { get; }

    void onClick(int mode);
    void onHoverStart(int mode);
    void onHoverEnd(int mode);
    void onHover(int mode);
    void onRightClick(int mode);
}
