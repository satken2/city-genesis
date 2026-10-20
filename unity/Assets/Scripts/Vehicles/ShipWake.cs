using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Ship wakes: bow waves, hull foam, Kelvin arms spreading 19.5 degrees from the bow, and stern turbulence, rendered as one horizontal strip mesh.
/// Component strengths depend on forward speed: at low port speeds only faint stern turbidity remains; sideways berthing and mooring produce none.
/// Shaders/ShipWake shapes foam with noise in water-fixed coordinates. Store coordinates without FloatingOrigin displacement
/// and add the offset when rendering.
/// </summary>
public sealed class ShipWake
{
    /// <summary>Planar waterline shape, measured by slicing the model mesh at origin height, the waterline.</summary>
    public sealed class Hull
    {
        public float bow, stern;    // Bow/stern waterline positions along model Z [m].
        public float halfBeam;      // Maximum waterline half-width [m].
        private float z0, step;
        private float[] halfWidths;

        public float Length => bow - stern;

        public float HalfWidthAt(float z)
        {
            if (z > bow || z < stern) return 0f;
            float k = Mathf.Clamp((z - z0) / step, 0f, halfWidths.Length - 1);
            int i = Mathf.Min((int)k, halfWidths.Length - 2);
            return Mathf.Lerp(halfWidths[i], halfWidths[i + 1], k - i);
        }

        /// <summary>
        /// Use the unsuffixed mesh, rather than close/distant models, taking maximum half-width per longitudinal bin from edge/waterline intersections.
        /// Interpolate missing bins in coarsely meshed parallel midbodies. Use the bounding box for models not crossing the waterline.
        /// </summary>
        public static Hull Measure(PropRenderModel model)
        {
            const int Stations = 64;
            var hull = new Hull { z0 = model.bounds.min.z, step = Mathf.Max(model.bounds.size.z / (Stations - 1), 1e-3f), halfWidths = new float[Stations] };
            var found = new bool[Stations];
            hull.bow = float.NegativeInfinity;
            hull.stern = float.PositiveInfinity;
            var vertices = new Dictionary<Mesh, Vector3[]>();
            foreach (var part in model.normal)
            {
                if (part.mesh == null || !part.mesh.isReadable || part.mesh.GetTopology(part.submesh) != MeshTopology.Triangles) continue;
                if (!vertices.TryGetValue(part.mesh, out var source)) vertices.Add(part.mesh, source = part.mesh.vertices);
                int[] triangles = part.mesh.GetTriangles(part.submesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 a = part.local.MultiplyPoint3x4(source[triangles[i + e]]);
                        Vector3 b = part.local.MultiplyPoint3x4(source[triangles[i + (e + 1) % 3]]);
                        if ((a.y <= 0f) == (b.y <= 0f)) continue;
                        Vector3 p = Vector3.Lerp(a, b, a.y / (a.y - b.y));
                        int k = Mathf.Clamp(Mathf.RoundToInt((p.z - hull.z0) / hull.step), 0, Stations - 1);
                        hull.halfWidths[k] = Mathf.Max(hull.halfWidths[k], Mathf.Abs(p.x));
                        found[k] = true;
                        hull.bow = Mathf.Max(hull.bow, p.z);
                        hull.stern = Mathf.Min(hull.stern, p.z);
                    }
            }
            if (hull.bow - hull.stern < 1f)
            {
                hull.bow = model.bounds.max.z;
                hull.stern = model.bounds.min.z;
                for (int k = 0; k < Stations; k++) { hull.halfWidths[k] = model.bounds.extents.x; found[k] = true; }
            }
            int previous = -1;
            for (int k = 0; k < Stations; k++)
            {
                if (!found[k]) continue;
                for (int j = previous + 1; previous >= 0 && j < k; j++)
                    hull.halfWidths[j] = Mathf.Lerp(hull.halfWidths[previous], hull.halfWidths[k], (j - previous) / (float)(k - previous));
                previous = k;
            }
            foreach (float w in hull.halfWidths) hull.halfBeam = Mathf.Max(hull.halfBeam, w);
            return hull;
        }
    }

    private struct Sample
    {
        public Vector3 position;    // Stern waterline position in saved coordinates.
        public Vector3 forward;     // Bow heading when recorded.
        public float time, along, speed, foam, wash, arm;
    }

    private const float Height = .1f;                // Height above the Y=0 sea-surface plane [m].
    private const float KelvinTan = .354f;           // Tangent of the 19.47-degree Kelvin angle; arms spread sideways from the bow by traveled distance times this value.
    private const float ArmLife = .4f;               // Lifetime fraction when arm crests disappear; turbidity remains for the full lifetime.
    private const float SpeedSmoothing = 1.5f;       // Forward-speed smoothing time constant [s].
    private const float FoamStartSpeed = .4f;        // Forward speed where turbulent foam begins [m/s].
    private const float FullFoamSpeed = 5f;          // Forward speed of maximum turbulent foam intensity [m/s].
    private const float FullWashSpeed = 2.5f;        // Forward speed of maximum turbidity [m/s].
    private const float GrainHalfBeam = 13.8f;       // Half-width giving unit noise scale for large ships; smaller ships use finer foam noise.
    private const int HullStations = 18;

    private readonly Hull hull;
    private readonly float life, grain;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly Mesh mesh = new Mesh { name = "ShipWake" };
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector4> wake = new List<Vector4>();
    private readonly List<Vector4> widths = new List<Vector4>();
    private readonly List<Vector4> foams = new List<Vector4>();
    private readonly List<Vector2> tints = new List<Vector2>();
    private readonly List<int> indices = new List<int>();
    private Vector3 lastStern;
    private bool hasLast;
    private float speed;
    private float along;    // Accumulated forward stern distance [m], the longitudinal coordinate fixing foam noise to the water.

    /// <param name="life">Wake lifetime [s].</param>
    /// <param name="speed">Forward speed at creation [m/s].</param>
    public ShipWake(Hull hull, float life, float speed)
    {
        this.hull = hull;
        this.life = life;
        this.speed = speed;
        grain = Mathf.Clamp(hull.halfBeam / GrainHalfBeam, .25f, 1f);
        along = Random.Range(0f, 500f);
        mesh.MarkDynamic();
    }

    public void Dispose() => Object.Destroy(mesh);

    /// <summary>
    /// Record stern passage points. Project stern displacement onto bow direction for forward speed, so sideways berthing produces no wake.
    /// Scale point spacing with speed to follow turns at consistent angular resolution.
    /// </summary>
    public void Record(Vector3 position, Vector3 forward, float time, float dt)
    {
        Vector3 stern = position + forward * hull.stern;
        if (hasLast && dt > 0f)
        {
            float advance = Mathf.Max(0f, Vector3.Dot(stern - lastStern, forward));
            along += advance;
            speed = Mathf.Lerp(speed, advance / dt, 1f - Mathf.Exp(-dt / SpeedSmoothing));
        }
        lastStern = stern;
        hasLast = true;
        float spacing = Mathf.Clamp(speed * 1.2f, 1f, 10f);
        if (speed > .05f && (samples.Count == 0 || (samples[samples.Count - 1].position - stern).sqrMagnitude >= spacing * spacing))
            samples.Add(new Sample { position = stern, forward = forward, time = time, along = along, speed = speed,
                                     foam = FoamStrength(speed), wash = WashStrength(speed), arm = ArmStrength(speed) });
        while (samples.Count > 0 && time - samples[0].time > life) samples.RemoveAt(0);
    }

    public void Draw(Vector3 position, Vector3 forward, float time, Vector3 offset, Material material)
    {
        if (material == null) return;
        vertices.Clear(); wake.Clear(); widths.Clear(); foams.Clear(); tints.Clear(); indices.Clear();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        float bow = BowStrength(speed), arm = ArmStrength(speed);
        if (bow > .01f || arm > .01f) AddHull(position + offset, forward, right, bow, arm);
        AddSection(position + forward * hull.stern + offset, forward, 0f, speed, FoamStrength(speed), WashStrength(speed), arm, along, false);
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            Sample s = samples[i];
            AddSection(s.position + offset, s.forward, time - s.time, s.speed, s.foam, s.wash, s.arm, s.along, true);
        }
        if (indices.Count == 0) return;
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, wake);
        mesh.SetUVs(1, widths);
        mesh.SetUVs(2, foams);
        mesh.SetUVs(3, tints);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
        var rp = new RenderParams(material)
        {
            worldBounds = mesh.bounds,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = true,
        };
        Graphics.RenderMesh(rp, mesh, 0, Matrix4x4.identity);
    }

    /// <summary>
    /// Hull-fixed section from just ahead of the bow to the stern. Bow waves are widest at the bow, narrow along the hull, and widen slightly before the stern as stern waves.
    /// Arms spread from the bow and remain occluded while inside the hull.
    /// </summary>
    private void AddHull(Vector3 center, Vector3 forward, Vector3 right, float bow, float arm)
    {
        float waveMax = Mathf.Clamp(hull.Length * .06f, 2f, 9f);
        float ahead = waveMax * .5f;
        for (int i = 0; i < HullStations; i++)
        {
            float z = hull.bow + ahead - (hull.Length + ahead) * Mathf.Pow(i / (HullStations - 1f), 1.5f);   // Use finer subdivisions toward the bow.
            float fromBow = hull.bow - z;
            float u = Mathf.Clamp01(fromBow / hull.Length);
            float profile = fromBow < 0f ? 1f + fromBow / ahead : Mathf.Max(Mathf.Exp(-u / .12f), .3f + .35f * Smooth(.8f, 1f, u));
            float halfWidth = Mathf.Max(0f, hull.HalfWidthAt(z) - .3f);   // Place the foam's inner edge slightly inside the waterline to avoid visible water gaps against the hull.
            float waveWidth = waveMax * (.4f + .6f * bow) * profile;
            float armOffset = KelvinTan * Mathf.Max(0f, fromBow);
            float extent = Mathf.Max(halfWidth + waveWidth * 1.2f + 1f, arm > .01f ? ArmExtent(armOffset) : 0f);
            AddVertices(center + forward * z, right, extent, along + (z - hull.stern), 0f,
                        new Vector4(0f, armOffset, halfWidth, waveWidth), new Vector4(0f, arm, bow, 0f), new Vector2(0f, arm), i > 0);
        }
    }

    /// <summary>
    /// Cross-section behind the stern. Turbulence starts about propeller-wash width, expands toward ship width over roughly 40% of ship length, then widens with the square root of time.
    /// White foam thins into net-like streaks over time proportional to ship length / speed, disappearing at lifetime end while turbidity remains. Arms spread outward in proportion to travel
    /// from the bow, using recorded speed x elapsed time from the stern position, so arms created at high speed keep spreading even after deceleration.
    /// </summary>
    private void AddSection(Vector3 stern, Vector3 forward, float age, float sectionSpeed, float foam, float wash, float arm, float sectionAlong, bool connect)
    {
        float passage = hull.Length / Mathf.Max(sectionSpeed, .5f);   // Time to travel one ship length [s].
        float flare = 1f - Mathf.Exp(-age / (.4f * passage));
        float turbHalf = hull.halfBeam * (.3f + .5f * wash * flare + .12f * Mathf.Sqrt(age) * (.4f + .6f * wash));
        float a = Mathf.Clamp01(age / life);
        float tail = Mathf.Clamp01((1f - a) / .3f);
        float turbFoam = foam * Mathf.Exp(-age / (passage + 4f)) * tail;
        float armFoam = arm * Mathf.Exp(-age / (.5f * passage + 3f)) * tail;
        float armSheen = arm * Mathf.Clamp01(1f - age / (life * ArmLife));
        float armOffset = KelvinTan * (hull.Length + sectionSpeed * age);
        float extent = Mathf.Max(turbHalf * 1.25f + 1f, armSheen > .01f ? ArmExtent(armOffset) : 0f);
        AddVertices(stern, Vector3.Cross(Vector3.up, forward), extent, sectionAlong, a,
                    new Vector4(turbHalf, armOffset, 0f, 0f), new Vector4(turbFoam, armFoam, 0f, 0f), new Vector2(wash, armSheen), connect);
    }

    private void AddVertices(Vector3 center, Vector3 right, float extent, float sectionAlong, float age, Vector4 width, Vector4 foam, Vector2 tint, bool connect)
    {
        center.y += Height;
        int b = vertices.Count;
        vertices.Add(center - right * extent);
        vertices.Add(center + right * extent);
        wake.Add(new Vector4(-extent, sectionAlong, age, grain));
        wake.Add(new Vector4(extent, sectionAlong, age, grain));
        widths.Add(width); widths.Add(width);
        foams.Add(foam); foams.Add(foam);
        tints.Add(tint); tints.Add(tint);
        if (!connect) return;
        indices.Add(b - 2); indices.Add(b - 1); indices.Add(b);
        indices.Add(b - 1); indices.Add(b + 1); indices.Add(b);
    }

    /// <summary>Lateral bounds containing the arm line, whose shader width is 0.8m + lateral position x 0.035.</summary>
    private static float ArmExtent(float armOffset) => armOffset * 1.15f + 4f;

    private static float FoamStrength(float speed) => Mathf.Clamp01((speed - FoamStartSpeed) / (FullFoamSpeed - FoamStartSpeed));

    private static float WashStrength(float speed) => Mathf.Clamp01(speed / FullWashSpeed);

    /// <summary>Kelvin-wave strength depends on speed relative to ship length (Froude number), faint on cruising large ships and distinct on small fishing boats.</summary>
    private float ArmStrength(float speed) => Smooth(.07f, .3f, Froude(speed));

    private float BowStrength(float speed) => Smooth(.04f, .2f, Froude(speed));

    private float Froude(float speed) => speed / Mathf.Sqrt(9.81f * Mathf.Max(hull.Length, 1f));

    private static float Smooth(float from, float to, float x)
    {
        float t = Mathf.Clamp01((x - from) / (to - from));
        return t * t * (3f - 2f * t);
    }
}
