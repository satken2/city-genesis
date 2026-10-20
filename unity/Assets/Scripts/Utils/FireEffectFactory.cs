using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Factory procedurally building a complete 3D fire effect sized to a building's footprint radius and height.
///
/// Types
/// ----
/// - Blaze (burning building): flames + sparks + black smoke + flickering point light.
/// - Smolder (burned rubble): thin white smoke only, without flames or lights.
///
/// Asset independence
/// --------------
/// Generate flame, smoke, and spark textures once at startup and share them statically with materials.
/// Designed to work without additional art assets. Even as particle counts increase,
/// only three materials exist, allowing draw-call batching.
/// Rendered with Custom/FireParticle (premultiplied alpha and soft particles).
///
/// Use local-space particle simulation (ParticleSystemSimulationSpace.Local).
/// Source buildings do not move, so world space offers no benefit. Local space applies parent scale
/// to the entire smoke column and prevents smoke trails at old positions after FloatingOrigin shifts.
/// </summary>
public static class FireEffectFactory
{
    /// <summary>Effect type.</summary>
    public enum PlumeKind
    {
        Blaze,
        Smolder,
    }

    /// <summary>Effect dimensions for one building.</summary>
    public readonly struct PlumeSpec
    {
        /// <summary>Building footprint circumradius (m), governing flame width, smoke thickness, and emission rate.</summary>
        public readonly float Radius;

        /// <summary>Building height (m), governing flame outlets and smoke emission height.</summary>
        public readonly float Height;

        /// <summary>Horizontal smoke drift direction (unit vector). Varies by building to avoid uniform smoke columns.</summary>
        public readonly Vector2 Drift;

        public PlumeSpec(float radius, float height, Vector2 drift)
        {
            Radius = Mathf.Clamp(radius, 2.5f, 45f);
            Height = Mathf.Clamp(height, 3f, 220f);
            Drift = drift.sqrMagnitude > 1e-4f ? drift.normalized : Vector2.right;
        }
    }

    private const int FLAME_TEXTURE_SIZE = 128;
    private const int SMOKE_TEXTURE_SIZE = 128;
    private const int SPARK_TEXTURE_SIZE = 32;

    // Smoke uses normal blending; flames and sparks use additive blending queued later to appear over smoke.
    private const int SMOKE_RENDER_QUEUE = 3000;
    private const int FLAME_RENDER_QUEUE = 3010;
    private const int SPARK_RENDER_QUEUE = 3020;

    private static Material _flameMaterial;
    private static Material _smokeMaterial;
    private static Material _sparkMaterial;

    /// <summary>
    /// Create the complete effect of the specified type and return its root GameObject.
    /// The root belongs at the building's base in world space; all internal heights are relative.
    /// </summary>
    public static GameObject Create(PlumeKind kind, in PlumeSpec spec, Transform parent, string name)
    {
        EnsureMaterials();

        var root = new GameObject(name);
        root.transform.SetParent(parent, false);

        if (kind == PlumeKind.Blaze)
        {
            BuildFlames(root.transform, spec);
            BuildEmbers(root.transform, spec);
            BuildBlazeSmoke(root.transform, spec);
            BuildFireLight(root.transform, spec);
        }
        else
        {
            BuildSmolderSmoke(root.transform, spec);
        }

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.Play();
        }
        return root;
    }

    /// <summary>One-shot plume for blasting. Shares only regular fire materials and creates no continuously burning smoke column.</summary>
    public static GameObject CreateExplosion(float radius, Transform parent)
    {
        EnsureMaterials();
        var root = new GameObject("BuildingExplosion");
        root.transform.SetParent(parent, false);
        float r = Mathf.Clamp(radius, 2.5f, 35f);

        var flames = AddExplosionSystem(root.transform, "BlastFlames", _flameMaterial, 30, r);
        var main = flames.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
        main.startSize = new ParticleSystem.MinMaxCurve(r * 0.3f, r * 0.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.5f, r * 1.1f);
        SetColorOverLifetime(flames,
            new[] {
                new GradientColorKey(new Color(1f, 0.42f, 0.08f), 0f),
                new GradientColorKey(new Color(0.95f, 0.09f, 0.015f), 0.3f),
                new GradientColorKey(new Color(0.32f, 0.025f, 0.01f), 1f),
            },
            new[] {
                new GradientAlphaKey(0.9f, 0f),
                new GradientAlphaKey(0.7f, 0.35f),
                new GradientAlphaKey(0f, 1f),
            });
        SetSizeOverLifetime(flames, new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0.2f)));

        var smoke = AddExplosionSystem(root.transform, "BlastSmoke", _smokeMaterial, 45, r);
        main = smoke.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(r * 0.35f, r * 0.65f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.38f);
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        var velocity = smoke.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.y = Mathf.Max(1.5f, r * 0.3f);
        SetColorOverLifetime(smoke,
            new[] {
                new GradientColorKey(new Color(0.15f, 0.13f, 0.12f), 0f),
                new GradientColorKey(new Color(0.38f, 0.36f, 0.34f), 0.4f),
                new GradientColorKey(new Color(0.6f, 0.58f, 0.55f), 1f),
            },
            new[] {
                new GradientAlphaKey(0.15f, 0f),
                new GradientAlphaKey(0.65f, 0.08f),
                new GradientAlphaKey(0.35f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            });
        SetSizeOverLifetime(smoke, AnimationCurve.Linear(0f, 0.45f, 1f, 1.8f));
        SetSwayNoise(smoke, Mathf.Max(0.3f, r * 0.08f), 0.35f, 0.5f);
        SetRotationOverLifetime(smoke, 18f);

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>()) ps.Play();
        return root;
    }

    private static ParticleSystem AddExplosionSystem(Transform root, string name, Material material, short count, float radius)
    {
        var ps = AddSystem(root, name, material, count);
        var main = ps.main;
        main.prewarm = false;
        main.loop = false;
        main.duration = 0.15f;
        main.useUnscaledTime = true;
        // Advance lifetime off-screen as well so the explosion does not resume when it returns into view.
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 55f;
        shape.radius = radius * 0.55f;
        shape.rotation = new Vector3(-90f, 0f, 0f);
        return ps;
    }

    // ------------------------------------------------------------------
    // Particle-system construction.
    // ------------------------------------------------------------------

    /// <summary>Main flames, rising from a box-shaped region near the building top.</summary>
    private static void BuildFlames(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Flames", _flameMaterial, 220);

        var main = ps.main;
        main.duration = 6f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1f, r * 0.26f), Mathf.Max(1.9f, r * 0.55f));
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(1.1f, r * 0.42f), Mathf.Max(2.1f, r * 0.8f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-0.22f, 0.22f);
        main.gravityModifier = -0.04f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * r * 0.85f, 24f, 150f);

        // Use a box with depth around the upper building so flames appear to emerge
        // from upper-floor openings as well as the roof. Keep its XZ extent inside the footprint;
        // covering the full footprint would make flames appear larger than the building.
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.85f, h * 0.3f, r * 0.85f);
        shape.position = new Vector3(0f, h * 0.8f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(1.00f, 0.96f, 0.80f), 0.00f),
                new GradientColorKey(new Color(1.00f, 0.82f, 0.38f), 0.18f),
                new GradientColorKey(new Color(1.00f, 0.48f, 0.10f), 0.45f),
                new GradientColorKey(new Color(0.72f, 0.16f, 0.03f), 0.78f),
                new GradientColorKey(new Color(0.25f, 0.05f, 0.01f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(1.00f, 0.10f),
                new GradientAlphaKey(0.85f, 0.42f),
                new GradientAlphaKey(0.35f, 0.75f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.45f),
            new Keyframe(0.28f, 1.00f),
            new Keyframe(1f, 0.20f)));

        SetNoise(ps, strength: Mathf.Max(0.4f, r * 0.18f), frequency: 0.4f, scrollSpeed: 0.55f, octaves: 2);
    }

    /// <summary>Sparks: small points of light rising from the flames.</summary>
    private static void BuildEmbers(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Embers", _sparkMaterial, 140);

        var main = ps.main;
        main.duration = 6f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 4.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1.3f, r * 0.45f), Mathf.Max(2.6f, r * 0.9f));
        main.startSize = new ParticleSystem.MinMaxCurve(0.09f, 0.28f);
        main.gravityModifier = -0.1f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 1.2f, 4f, 20f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.75f, h * 0.24f, r * 0.75f);
        shape.position = new Vector3(0f, h * 0.84f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(1.00f, 0.93f, 0.62f), 0.00f),
                new GradientColorKey(new Color(1.00f, 0.58f, 0.16f), 0.45f),
                new GradientColorKey(new Color(0.65f, 0.12f, 0.02f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(1.00f, 0.12f),
                new GradientAlphaKey(0.55f, 0.65f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetNoise(ps, strength: Mathf.Max(0.45f, r * 0.45f), frequency: 0.22f, scrollSpeed: 0.8f, octaves: 2);

        // Upward trails make them look like flying sparks instead of static dots.
        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.06f;
        renderer.lengthScale = 2.2f;
    }

    /// <summary>Black fire smoke, spreading as it rises and cooling to light gray.</summary>
    private static void BuildBlazeSmoke(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Smoke", _smokeMaterial, 180);

        var main = ps.main;
        // prewarm simulates only duration seconds, so match it to the maximum lifetime
        // or the smoke column will not reach its full height when first displayed.
        main.duration = 11f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 11f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1.1f, r * 0.24f), Mathf.Max(2f, r * 0.45f));
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(1.6f, r * 0.5f), Mathf.Max(2.8f, r * 0.85f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        main.gravityModifier = -0.03f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 0.9f, 5f, 26f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.5f, h * 0.06f, r * 0.5f);
        shape.position = new Vector3(0f, h * 1.0f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(0.10f, 0.09f, 0.09f), 0.00f),
                new GradientColorKey(new Color(0.30f, 0.29f, 0.28f), 0.45f),
                new GradientColorKey(new Color(0.55f, 0.54f, 0.52f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(0.85f, 0.15f),
                new GradientAlphaKey(0.55f, 0.60f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.50f),
            new Keyframe(0.45f, 1.10f),
            new Keyframe(1f, 1.70f)));

        SetRotationOverLifetime(ps, degreesPerSecond: 8f);
        SetSwayNoise(ps, strength: Mathf.Max(0.6f, r * 0.35f), frequency: 0.12f, scrollSpeed: 0.16f);
        SetDrift(ps, spec.Drift, speed: 0.6f);
    }

    /// <summary>White smoke after the fire goes out, rising in a thin column without flames.</summary>
    private static void BuildSmolderSmoke(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Smoke", _smokeMaterial, 90);

        var main = ps.main;
        main.duration = 9f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(0.8f, r * 0.26f), Mathf.Max(1.4f, r * 0.45f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        main.gravityModifier = -0.02f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 0.5f, 2.5f, 12f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.22f, h * 0.04f, r * 0.22f);
        shape.position = new Vector3(0f, h * 0.98f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(0.62f, 0.61f, 0.60f), 0.00f),
                new GradientColorKey(new Color(0.80f, 0.79f, 0.78f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(0.42f, 0.20f),
                new GradientAlphaKey(0.28f, 0.60f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.35f),
            new Keyframe(0.5f, 1.00f),
            new Keyframe(1f, 1.60f)));

        SetRotationOverLifetime(ps, degreesPerSecond: 6f);
        SetSwayNoise(ps, strength: Mathf.Max(0.35f, r * 0.2f), frequency: 0.13f, scrollSpeed: 0.14f);
        SetDrift(ps, spec.Drift, speed: 0.4f);
    }

    /// <summary>
    /// Point light for reflected fire glow, showing nearby surfaces that flames emit light at night or in shade.
    /// FireEffectManager handles flicker and limits active lights, so create it disabled here.
    /// </summary>
    private static void BuildFireLight(Transform root, in PlumeSpec spec)
    {
        var go = new GameObject("FireLight");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, spec.Height * 0.7f, 0f);

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.62f, 0.28f);
        light.range = Mathf.Clamp(spec.Radius * 7f, 25f, 140f);
        light.intensity = Mathf.Clamp(spec.Radius * 0.55f, 2.5f, 10f);
        light.shadows = LightShadows.None;
        go.SetActive(false);
    }

    // ------------------------------------------------------------------
    // Particle-module configuration helpers.
    // ------------------------------------------------------------------

    private static ParticleSystem AddSystem(Transform parent, string name, Material material, int maxParticles)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var ps = go.AddComponent<ParticleSystem>();
        // Immediately after AddComponent, playOnAwake has already started playback. duration and prewarm
        // can only change while stopped, so always stop before editing modules; Create starts playback at the end.
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        // Prevent smoke columns from regrowing from the base when effects are recreated as the camera crosses cells.
        main.prewarm = true;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = maxParticles;
        main.cullingMode = ParticleSystemCullingMode.Pause;

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        // Keep flames upright even when the camera rolls.
        renderer.allowRoll = false;

        return ps;
    }

    private static void SetColorOverLifetime(ParticleSystem ps, GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
    {
        var gradient = new Gradient();
        gradient.SetKeys(colorKeys, alphaKeys);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(gradient);
    }

    private static void SetSizeOverLifetime(ParticleSystem ps, AnimationCurve curve)
    {
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);
    }

    /// <summary>
    /// Set rotation speed, converting degrees to radians for the scripting API;
    /// the Inspector displays degrees, a known Unity API asymmetry.
    /// </summary>
    private static void SetRotationOverLifetime(ParticleSystem ps, float degreesPerSecond)
    {
        float rad = degreesPerSecond * Mathf.Deg2Rad;

        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-rad, rad);
    }

    private static void SetNoise(ParticleSystem ps, float strength, float frequency, float scrollSpeed, int octaves)
    {
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = strength;
        noise.frequency = frequency;
        noise.scrollSpeed = scrollSpeed;
        noise.octaveCount = octaves;
        // Reduce strength according to frequency so fine fluctuations do not become excessive.
        noise.damping = true;
    }

    /// <summary>
    /// Configure noise so smoke sways gently as it rises.
    ///
    /// Disable damping, which scales strength by frequency, to obtain broad, slow motion
    /// at low frequency and scroll speed; leaving damping enabled nearly removes low-frequency sway.
    /// Separate axes to make horizontal sway stronger. Equal strength on Y
    /// would make ascent speed oscillate, producing vertical twitching instead of gentle swaying.
    /// </summary>
    private static void SetSwayNoise(ParticleSystem ps, float strength, float frequency, float scrollSpeed)
    {
        var noise = ps.noise;
        noise.enabled = true;
        noise.frequency = frequency;
        noise.scrollSpeed = scrollSpeed;
        noise.octaveCount = 1;
        noise.damping = false;
        noise.separateAxes = true;
        noise.strengthX = strength;
        noise.strengthY = strength * 0.2f;
        noise.strengthZ = strength;
    }

    private static void SetDrift(ParticleSystem ps, Vector2 drift, float speed)
    {
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(drift.x * speed);
        vel.z = new ParticleSystem.MinMaxCurve(drift.y * speed);
    }

    // ------------------------------------------------------------------
    // Material and texture generation, shared by all effects.
    // ------------------------------------------------------------------

    private static void EnsureMaterials()
    {
        if (_flameMaterial != null) return;

        Shader shader = Shader.Find("Custom/FireParticle");
        if (shader == null)
        {
            Debug.LogWarning("FireEffectFactory: Custom/FireParticle が見つからないため Sprites/Default にフォールバックします。");
            shader = Shader.Find("Sprites/Default");
        }

        _flameMaterial = CreateMaterial(shader, BuildFlameTexture(), additive: true, renderQueue: FLAME_RENDER_QUEUE, softFade: 2.5f);
        _sparkMaterial = CreateMaterial(shader, BuildSparkTexture(), additive: true, renderQueue: SPARK_RENDER_QUEUE, softFade: 1.5f);
        _smokeMaterial = CreateMaterial(shader, BuildSmokeTexture(), additive: false, renderQueue: SMOKE_RENDER_QUEUE, softFade: 6f);
    }

    private static Material CreateMaterial(Shader shader, Texture2D texture, bool additive, int renderQueue, float softFade)
    {
        var mat = new Material(shader)
        {
            name = additive ? "FireParticleAdditive" : "FireParticleAlpha",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = renderQueue,
        };

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texture);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", texture);
        if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_SoftFadeDistance")) mat.SetFloat("_SoftFadeDistance", softFade);

        return mat;
    }

    /// <summary>
    /// Flame-particle texture: an upward-tapering teardrop with noisy edges resembling tongues of flame.
    /// View-aligned particles with roll disabled preserve this orientation, with the base at the bottom.
    /// </summary>
    private static Texture2D BuildFlameTexture()
    {
        int size = FLAME_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            float halfWidth = (1f - Mathf.Pow(v, 1.6f)) * SmoothStep01(0f, 0.20f, v);

            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float ax = Mathf.Abs(u - 0.5f) * 2f;

                float wobble = (Fbm(u * 4f + 3.1f, v * 3f + 7.7f, 3) - 0.5f) * 0.22f;
                float w = Mathf.Max(halfWidth + wobble, 0f);
                float alpha = 1f - SmoothStep01(w * 0.35f, w, ax);

                float core = (1f - ax) * (1f - SmoothStep01(0.15f, 0.75f, v));
                Color rgb = Color.Lerp(new Color(1f, 0.78f, 0.45f), Color.white, Mathf.Clamp01(core));

                pixels[y * size + x] = new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(alpha));
            }
        }

        return FinalizeTexture(pixels, size, "FireFlameTexture");
    }

    /// <summary>Smoke-particle texture: a cloud-like blob combining radial falloff and fbm noise.</summary>
    private static Texture2D BuildSmokeTexture()
    {
        int size = SMOKE_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float radius = new Vector2((u - 0.5f) * 2f, (v - 0.5f) * 2f).magnitude;

                float n = Fbm(u * 3.3f + 13.7f, v * 3.3f + 5.2f, 4);
                float edge = 1f - SmoothStep01(0.25f, 1f, radius + (n - 0.5f) * 0.75f);
                float alpha = Mathf.Pow(Mathf.Clamp01(edge), 1.35f) * Mathf.Lerp(0.7f, 1f, n);

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        return FinalizeTexture(pixels, size, "FireSmokeTexture");
    }

    /// <summary>Spark texture: a tiny light point with an overexposed white center.</summary>
    private static Texture2D BuildSparkTexture()
    {
        int size = SPARK_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float radius = new Vector2((u - 0.5f) * 2f, (v - 0.5f) * 2f).magnitude;
                float alpha = Mathf.Pow(Mathf.Clamp01(1f - radius), 3f);

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        return FinalizeTexture(pixels, size, "FireSparkTexture");
    }

    private static Texture2D FinalizeTexture(Color32[] pixels, int size, string name)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        tex.SetPixels32(pixels);
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return tex;
    }

    /// <summary>
    /// GLSL-style smoothstep. Mathf.SmoothStep interpolates from -> to and cannot be used for this.
    /// </summary>
    private static float SmoothStep01(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-5f));
        return t * t * (3f - 2f * t);
    }

    private static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f;
        float amplitude = 0.5f;
        float frequency = 1f;
        float normalizer = 0f;

        for (int i = 0; i < octaves; i++)
        {
            sum += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
            normalizer += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return sum / Mathf.Max(normalizer, 1e-5f);
    }
}
