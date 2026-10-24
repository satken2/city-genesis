using UnityEngine;

/// <summary>Rendering distance per decoration type. Independent of saved data and model dimensions.</summary>
public static class PropRenderPolicy
{
    public const int Grass = 4;
    public const float DetailResidentDistance = 125f;   // Prepare candidates beyond display distance to hide generation waits at patch boundaries
    public const int DetailCandidateBudget = 65536;

    public static bool NearOnly(int type) => type == Grass;
    public static float MaxDistance(int type) => NearOnly(type) ? 100f : DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
    public static bool Hidden(int type, float distance, DrawLod previous) => NearOnly(type)
        ? distance >= MaxDistance(type) * (previous == DrawLod.Hidden ? .9f : 1f)
        : DrawLodEvaluator.PropHiddenByDistance(distance, previous);

    /// <summary>Whether props whose bounds are this far from the camera cast shadows: everything the sun's shadow maps reach, at every render LOD.</summary>
    public static bool WithinShadowDistance(Bounds bounds, Vector3 camera)
    {
        var asset = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
        float distance = asset != null ? asset.shadowDistance : 0f;
        return bounds.SqrDistance(camera) < distance * distance;
    }

    // Divide by an upper bound on circle-intersecting patches to prevent grass replacement as movement changes allocations.
    public static int DetailQuota(float chunkSize)
    {
        int side = Mathf.CeilToInt(2f * DetailResidentDistance / chunkSize) + 2;
        return DetailCandidateBudget / (side * side);
    }
}
