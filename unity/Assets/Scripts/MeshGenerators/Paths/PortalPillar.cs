using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Portal piers spanning roadways/tracks, assembled from independent posts/beams.
/// Only ground-contact posts need interference tests; beams may span above passage space.
/// </summary>
public sealed class PortalPillar
{
    // Rendering limit. Avoid unlimited beams across intersections or wide passage bands.
    public const float MaxSpan = 40f;
    private readonly GameObject legPrefab, beamPrefab;
    private readonly Bounds legBounds, beamBounds;

    private PortalPillar(GameObject leg, GameObject beam, Bounds legBox, Bounds beamBox)
    {
        legPrefab = leg;
        beamPrefab = beam;
        legBounds = legBox;
        beamBounds = beamBox;
    }

    public static PortalPillar Load(string variantBase, string typeBase)
    {
        var models = ModelManager.Instance;
        if (models == null) return null;
        var leg = models.GetModel(variantBase + "/pillar_leg", suppressWarning: true)
            ?? models.GetModel(typeBase + "/pillar_leg", suppressWarning: true);
        var beam = models.GetModel(variantBase + "/pillar_crossbeam", suppressWarning: true)
            ?? models.GetModel(typeBase + "/pillar_crossbeam", suppressWarning: true);
        var legBox = PillarClearance.GetPrefabBounds(leg);
        var beamBox = PillarClearance.GetPrefabBounds(beam);
        if (!legBox.HasValue || !beamBox.HasValue || legBox.Value.size.y <= 0f || beamBox.Value.size.x <= 0f) return null;
        return new PortalPillar(leg, beam, legBox.Value, beamBox.Value);
    }

    public bool TryPlace(GameObject container, Vector3 position, Quaternion rotation, float supportTop,
        float supportHalfWidth, List<PillarClearance.Corridor> obstacles)
    {
        Vector3 legScale = legPrefab.transform.localScale;
        Vector3 beamScale = beamPrefab.transform.localScale;
        float beamHeight = beamBounds.size.y * beamScale.y;
        float legTop = supportTop - beamHeight;
        float ground = Utils.GetGroundHeight(position);
        if (legTop - ground <= 0.1f) return false;
        float halfLegWidth = legBounds.extents.x * legScale.x;
        // Final interference tests also use rotated world-bounds circumcircles, matching post-clearance approximation.
        var horizontalBounds = Utils.TransformBounds(legBounds, Matrix4x4.TRS(Vector3.zero, rotation,
            new Vector3(legScale.x, 1f, legScale.z)));
        float legRadius = new Vector2(horizontalBounds.extents.x, horizontalBounds.extents.z).magnitude;
        Vector3 right = rotation * Vector3.right;
        PillarClearance.FindPortalOffsets(position, right, ground, legTop, legRadius,
            Mathf.Max(halfLegWidth, supportHalfWidth - halfLegWidth), obstacles, out float left, out float rightOffset);
        float span = rightOffset - left + 2f * halfLegWidth;
        if (span > MaxSpan) return false;

        beamScale.x = span / beamBounds.size.x;
        Vector3 beamPosition = position + right * ((left + rightOffset) * 0.5f);
        beamPosition.y = supportTop - beamBounds.max.y * beamScale.y;
        // Align beam center with midpoint between posts even for models with lateral pivot offsets.
        beamPosition -= rotation * new Vector3(beamBounds.center.x * beamScale.x, 0f, beamBounds.center.z * beamScale.z);

        Vector3 leftPosition = position + right * left;
        Vector3 rightPosition = position + right * rightOffset;
        if (!TryLeg(leftPosition, legTop, rotation, obstacles, out leftPosition, out Vector3 leftScale)
            || !TryLeg(rightPosition, legTop, rotation, obstacles, out rightPosition, out Vector3 rightScale)) return false;

        InstantiatePart(beamPrefab, container, beamPosition, rotation, beamScale);
        InstantiatePart(legPrefab, container, leftPosition, rotation, leftScale);
        InstantiatePart(legPrefab, container, rightPosition, rotation, rightScale);
        return true;
    }

    private bool TryLeg(Vector3 center, float top, Quaternion rotation, List<PillarClearance.Corridor> obstacles,
        out Vector3 position, out Vector3 scale)
    {
        scale = legPrefab.transform.localScale;
        float height = top - Utils.GetGroundHeight(center);
        position = center;
        if (height <= 0.1f) return false;
        scale.y = height / legBounds.size.y;
        position.y = top - legBounds.max.y * scale.y;
        position -= rotation * new Vector3(legBounds.center.x * scale.x, 0f, legBounds.center.z * scale.z);
        return !PillarClearance.Intersects(Utils.TransformBounds(legBounds,
            Matrix4x4.TRS(position, rotation, scale)), obstacles);
    }

    private static void InstantiatePart(GameObject prefab, GameObject container, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var part = Object.Instantiate(prefab, container.transform);
        part.transform.position = position;
        part.transform.rotation = rotation;
        part.transform.localScale = scale;
    }
}
