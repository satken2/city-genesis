using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Moves the model parts of a shown building by its operation rate, as a visual effect independent of simulation
/// (<see cref="BuildingMotionParts"/>). While the building is active, rotors spin and smoke outlets emit in proportion to
/// the latest epoch's utilization (0-1); otherwise both rest. Only detailed objects carry it, so far buildings stay still and
/// the draw LOD doubles as the distance cutoff.
///
/// BuildingManager keeps a building's record while its version is unchanged, but utilization changes without a version bump.
/// Read it from CityDataCache when enabled (creation, restore from the parked cache) and after every synchronization (<see cref="RefreshAll"/>).
/// The cache does not change while an epoch runs, so parts keep moving at the last rate meanwhile.
/// </summary>
public sealed class BuildingMotion : MonoBehaviour
{
    /// <summary>Full-operation speed (rpm), about the rated speed of a 70 m rotor.</summary>
    private const float RATED_RPM = 15f;

    private static readonly HashSet<BuildingMotion> enabledMotions = new HashSet<BuildingMotion>();

    private int buildingId;
    private float fallbackRate;
    private bool configured;
    private float rpm;
    private float angle;
    private Transform[] rotors;
    private Quaternion[] rotorBases;
    private Renderer[] rotorRenderers;
    private ParticleSystem[] smoke;
    private float[] fullSmokeRates;

    /// <summary>Attach before VisualModeObject initializes so rotors take hover and color modes with the building.</summary>
    public static void Attach(GameObject building, BuildingMotionParts parts, BuildingSummary record)
    {
        if (parts == null || record == null) return;
        var motion = building.AddComponent<BuildingMotion>();
        motion.buildingId = record.agent_id;
        motion.fallbackRate = OperationRate(record);
        // Start angle from the ID keeps neighbouring turbines out of step.
        motion.angle = Mathf.Abs(record.agent_id * 137.508f) % 360f;

        var rotors = new List<Transform>();
        var bases = new List<Quaternion>();
        var renderers = new List<Renderer>();
        foreach (BuildingMotionParts.Rotor rotor in parts.Rotors)
        {
            if (!SharedPartMesh.TryGet(rotor.Node, out Mesh mesh, out Material[] materials)) continue;
            var go = new GameObject("Rotor");
            go.layer = building.layer;
            go.transform.SetParent(building.transform, false);
            go.transform.localPosition = rotor.Position;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            rotors.Add(go.transform);
            bases.Add(rotor.Rotation);
            renderers.Add(renderer);
        }
        motion.rotors = rotors.ToArray();
        motion.rotorBases = bases.ToArray();
        motion.rotorRenderers = renderers.ToArray();

        motion.smoke = new ParticleSystem[parts.Outlets.Count];
        motion.fullSmokeRates = new float[parts.Outlets.Count];
        for (int i = 0; i < parts.Outlets.Count; i++)
        {
            BuildingMotionParts.Outlet outlet = parts.Outlets[i];
            motion.smoke[i] = FireEffectFactory.CreateChimneySmoke(building.transform, outlet.Position, outlet.Radius);
            motion.fullSmokeRates[i] = motion.smoke[i].emission.rateOverTime.constant;
        }

        motion.configured = true;
        motion.ApplyPose(force: true);
        if (motion.isActiveAndEnabled) motion.Register();
    }

    /// <summary>Re-read every enabled building's operation rate. Call after the city cache synchronizes with the core.</summary>
    public static void RefreshAll()
    {
        foreach (BuildingMotion motion in enabledMotions) motion.Refresh();
    }

    /// <summary>Latest epoch's utilization while active, otherwise 0. For shared buildings (tenant floors) it is the occupancy.</summary>
    private static float OperationRate(BuildingSummary record)
    {
        if (record.status != Constants.STATUS_ACTIVE || record.utilization == null || record.utilization.Count == 0) return 0f;
        return Mathf.Clamp01(record.utilization[0]);
    }

    // AddComponent enables the component before Attach configures it, so Attach registers once configured.
    private void OnEnable()
    {
        if (configured) Register();
    }

    private void OnDisable() => enabledMotions.Remove(this);

    private void Register()
    {
        enabledMotions.Add(this);
        Refresh();
    }

    private void Refresh()
    {
        CityDataCache cache = CityDataCache.Instance;
        float rate = cache != null && cache.TryGetBuilding(buildingId, out BuildingSummary record) ? OperationRate(record) : fallbackRate;
        rpm = RATED_RPM * rate;
        for (int i = 0; i < smoke.Length; i++)
        {
            ParticleSystem ps = smoke[i];
            if (ps == null) continue;
            var emission = ps.emission;
            emission.rateOverTime = fullSmokeRates[i] * rate;
            if (rate > 0f && !ps.isEmitting) ps.Play();
            else if (rate <= 0f && ps.isEmitting) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    private void Update()
    {
        if (rpm <= 0f || rotors.Length == 0) return;
        angle = Mathf.Repeat(angle + rpm * 6f * Time.unscaledDeltaTime, 360f);
        ApplyPose(force: false);
    }

    /// <summary>Clockwise seen from the nose: positive about the nose axis, which is local -X after import.</summary>
    private void ApplyPose(bool force)
    {
        Quaternion spin = Quaternion.AngleAxis(angle, Vector3.left);
        for (int i = 0; i < rotors.Length; i++)
        {
            if (!force && !rotorRenderers[i].isVisible) continue;
            rotors[i].localRotation = rotorBases[i] * spin;
        }
    }
}
