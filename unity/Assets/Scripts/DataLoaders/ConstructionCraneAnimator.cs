using UnityEngine;

/// <summary>Rotate the crane top around the connection point's Y axis as a visual effect independent of simulation.</summary>
public sealed class ConstructionCraneAnimator : MonoBehaviour
{
    public sealed class Placement
    {
        public readonly GameObject Prefab;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public Placement(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            Prefab = prefab;
            Position = position;
            Rotation = rotation;
        }
    }

    private Quaternion baseRotation;
    private int seed;
    private MeshRenderer craneRenderer;

    public static void Attach(GameObject building, Placement placement, int buildingId)
    {
        if (placement == null || !SharedPartMesh.TryGet(placement.Prefab, out Mesh mesh, out Material[] materials)) return;

        var top = new GameObject("ConstructionCraneTop");
        top.layer = building.layer;
        top.transform.SetParent(building.transform, false);
        top.transform.localPosition = placement.Position;
        top.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = top.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        var animator = top.AddComponent<ConstructionCraneAnimator>();
        animator.baseRotation = placement.Rotation;
        animator.seed = buildingId;
        animator.craneRenderer = renderer;
        animator.ApplyPose();
    }

    private void OnEnable()
    {
        if (craneRenderer != null) ApplyPose();
    }

    private void Update()
    {
        if (craneRenderer == null || !craneRenderer.enabled || craneRenderer.forceRenderingOff) return;
        ApplyPose();
    }

    private void ApplyPose()
    {
        transform.localRotation = baseRotation * Quaternion.Euler(0f, EvaluateYaw(seed, Time.unscaledTimeAsDouble), 0f);
    }

    /// <summary>
    /// Calculate orientation from ID and shared time, preserving orientation at a given time through LOD switches and regeneration.
    /// Each segment starts at rest and interpolates toward a random heading with zero acceleration at both endpoints.
    /// </summary>
    public static float EvaluateYaw(int buildingId, double seconds)
    {
        double cycleSeconds = Mathf.Lerp(100f, 160f, Sample(buildingId, 0, 1));
        double time = seconds + Sample(buildingId, 0, 2) * cycleSeconds;
        long cycle = (long)System.Math.Floor(time / cycleSeconds);
        double phase = time - cycle * cycleSeconds;
        float pause = Mathf.Lerp(8f, 20f, Sample(buildingId, cycle, 3));
        float t = Mathf.Clamp01((float)((phase - pause) / (cycleSeconds - pause)));
        float eased = t * t * t * (t * (t * 6f - 15f) + 10f);
        float from = Sample(buildingId, cycle, 4) * 360f;
        float to = Sample(buildingId, cycle + 1, 4) * 360f;
        return from + Mathf.DeltaAngle(from, to) * eased;
    }

    private static float Sample(int buildingId, long cycle, uint salt)
    {
        // Do not alter UnityEngine.Random state, keeping this independent of other effects and generation order.
        unchecked
        {
            uint h = (uint)buildingId * 747796405u ^ (uint)cycle * 2891336453u ^ salt * 277803737u;
            h = (h ^ (h >> 16)) * 2246822519u;
            h = (h ^ (h >> 13)) * 3266489917u;
            h ^= h >> 16;
            return (h & 0x00ffffffu) / 16777216f;
        }
    }
}
