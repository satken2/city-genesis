using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Spawn rain/snow only near the camera, keeping rendering cost independent of city size.
/// Accept continuous intensity from 0 to 3 and interpolate precipitation between weather states during transitions.
/// </summary>
public sealed class WeatherPrecipitation : MonoBehaviour
{
    // Indices are intensity levels: 0=none, 1=light, 2=moderate, 3=heavy, interpolated linearly between levels.
    private static readonly float[] RainRate = { 0f, 160f, 750f, 1800f };
    private static readonly float[] RainSize = { 0.02f, 0.02f, 0.035f, 0.05f };
    private static readonly float[] RainSpeed = { 12f, 12f, 22f, 30f };
    private static readonly float[] RainAlpha = { 0.16f, 0.16f, 0.28f, 0.28f };
    private static readonly float[] SnowRate = { 0f, 300f, 800f, 1600f };
    private static readonly float[] SnowSize = { 0.22f, 0.22f, 0.3f, 0.26f }; // Larger than real flakes so snow tens of meters away still covers a few pixels.
    private static readonly float[] SnowFall = { 0.9f, 0.9f, 1.2f, 1.6f };
    private static readonly float[] SnowWind = { 0.6f, 0.6f, 1.2f, 7f };
    private static readonly float[] SnowLife = { 7f, 7f, 7f, 4f };
    /// <summary>Prevailing wind (unit, horizontal). Snow and chimney smoke both drift along it.</summary>
    public static readonly Vector3 WindDirection = new Vector3(0.92f, 0f, 0.38f);

    private ParticleSystem rain, snow;
    private Material rainMaterial, snowMaterial;
    private Camera view;
    private float rainLevel, snowLevel, visibility = 1f;
    private bool rainEmitting, snowEmitting, cleared = true;

    public float RainLevel => rainLevel;
    public float SnowLevel => snowLevel;

    private void Awake()
    {
        rain = CreateSystem("Camera rain", "Weather/RainStreak", 6000, out rainMaterial);
        snow = CreateSystem("Camera snow", "Weather/Snowflake", 8000, out snowMaterial);
        if (rain != null)
        {
            var shape = rain.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(60f, 60f, 1f);
            var main = rain.main;
            main.startLifetime = 3f;
            var velocity = rain.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = 1.4f; velocity.y = 0f; velocity.z = 0.6f;
            var renderer = rain.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.035f;
            renderer.lengthScale = 3f;
        }
        if (snow != null)
        {
            // Spawn snow within a 26m-high box and use short lifetimes. Slow descent from above would require long lifetimes and excessive particle counts.
            var shape = snow.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(80f, 80f, 26f);
            shape.randomDirectionAmount = 0.1f;
            var main = snow.main;
            main.startColor = new Color(0.94f, 0.96f, 1f, 0.9f);
            var velocity = snow.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            var noise = snow.noise;
            noise.enabled = true;
            noise.strength = 0.4f;
            noise.frequency = 0.3f;
            noise.scrollSpeed = 0.2f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            var fade = snow.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
            var renderer = snow.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            // Clamp apparent size as a fraction of screen height so distant particles remain visible and near-lens particles do not become large white circles.
            renderer.minParticleSize = 0.0015f;
            renderer.maxParticleSize = 0.012f;
        }
        Apply();
    }

    private ParticleSystem CreateSystem(string name, string shaderPath, int maxParticles, out Material material)
    {
        material = null;
        var shader = Resources.Load<Shader>(shaderPath);
        if (shader == null) { Debug.LogError("Weather: 降水のシェーダーを読み込めません: " + shaderPath); return null; }
        material = new Material(shader) { name = name + " (runtime)" };
        var holder = new GameObject(name);
        holder.transform.SetParent(transform, false);
        var system = holder.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = system.main;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = maxParticles;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return system;
    }

    /// <summary>Rain/snow intensity (0 to 3) and visibility (0 while dark). Visibility scales only particle count.</summary>
    public void SetLevels(float rainValue, float snowValue, float visibilityValue)
    {
        rainValue = Mathf.Clamp(rainValue, 0f, 3f);
        snowValue = Mathf.Clamp(snowValue, 0f, 3f);
        visibilityValue = Mathf.Clamp01(visibilityValue);
        if (Mathf.Abs(rainValue - rainLevel) < 0.005f && Mathf.Abs(snowValue - snowLevel) < 0.005f
            && Mathf.Abs(visibilityValue - visibility) < 0.005f) return;
        rainLevel = rainValue;
        snowLevel = snowValue;
        visibility = visibilityValue;
        Apply();
    }

    private void Apply()
    {
        if (rain != null)
        {
            var emission = rain.emission;
            emission.rateOverTime = Curve(RainRate, rainLevel) * visibility;
            var main = rain.main;
            main.startSize = Curve(RainSize, rainLevel);
            main.startSpeed = Curve(RainSpeed, rainLevel);
            main.startColor = new Color(0.78f, 0.87f, 0.95f, Curve(RainAlpha, rainLevel));
        }
        if (snow != null)
        {
            float size = Curve(SnowSize, snowLevel);
            var main = snow.main;
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size * 1.3f);
            main.startSpeed = Curve(SnowFall, snowLevel);
            main.startLifetime = Curve(SnowLife, snowLevel);
            var emission = snow.emission;
            emission.rateOverTime = Curve(SnowRate, snowLevel) * visibility;
            Vector3 wind = SnowWindVector();
            var velocity = snow.velocityOverLifetime;
            velocity.x = wind.x; velocity.y = 0f; velocity.z = wind.z;
        }
    }

    /// <summary>On immediate weather changes, clear lingering particles for precipitation that stopped, avoiding rain remaining after switching to snow.</summary>
    public void ClearStopped()
    {
        if (rain != null && rainLevel <= 0.01f) rain.Clear();
        if (snow != null && snowLevel <= 0.01f) snow.Clear();
    }

    private Vector3 SnowWindVector() => WindDirection * Curve(SnowWind, snowLevel);

    private static float Curve(float[] values, float level)
    {
        int low = Mathf.Clamp(Mathf.FloorToInt(level), 0, values.Length - 1);
        int high = Mathf.Min(low + 1, values.Length - 1);
        return Mathf.Lerp(values[low], values[high], level - low);
    }

    private void LateUpdate()
    {
        if (view == null) view = Camera.main;
        bool started = view != null && GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted();
        if (!started) { StopAll(); return; }
        Vector3 focus = view.transform.position + view.transform.forward * 20f;
        Place(rain, ref rainEmitting, rainLevel > 0.01f && visibility > 0.01f, focus + Vector3.up * 22f);
        // Shift upwind by the expected wind drift so drifting snow collects around the camera.
        Vector3 snowAnchor = focus + Vector3.up * 4f - SnowWindVector() * Curve(SnowLife, snowLevel) * 0.5f;
        Place(snow, ref snowEmitting, snowLevel > 0.01f && visibility > 0.01f, snowAnchor);
    }

    private void Place(ParticleSystem system, ref bool emitting, bool active, Vector3 position)
    {
        if (system == null) return;
        if (active)
        {
            // Clear abandoned particles after large camera movements.
            if ((system.transform.position - position).sqrMagnitude > 3600f) system.Clear();
            system.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, 0f, 0f));
        }
        if (active == emitting) return;
        emitting = active;
        // When precipitation stops, stop only emission and let airborne particles finish their lifetimes.
        if (active) { system.Play(); cleared = false; }
        else system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    /// <summary>Outside gameplay or when disabled, immediately clear all remaining airborne particles.</summary>
    private void StopAll()
    {
        rainEmitting = snowEmitting = false;
        if (cleared) return;
        cleared = true;
        if (rain != null) rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        if (snow != null) snow.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnDisable() => StopAll();

    private void OnDestroy()
    {
        if (rain != null) Destroy(rain.gameObject);
        if (snow != null) Destroy(snow.gameObject);
        if (rainMaterial != null) Destroy(rainMaterial);
        if (snowMaterial != null) Destroy(snowMaterial);
    }
}
