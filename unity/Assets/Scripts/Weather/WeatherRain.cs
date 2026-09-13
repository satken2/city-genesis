using UnityEngine;
using UnityEngine.Rendering;

/// <summary>カメラ近傍だけに雨粒を配置し、都市の大きさに依存しない描画負荷に保つ。</summary>
public sealed class WeatherRain : MonoBehaviour
{
    private ParticleSystem rain;
    private Material material;
    private Camera view;
    private int level;
    private bool emitting;

    private void Awake()
    {
        var shader = Resources.Load<Shader>("Weather/RainStreak");
        if (shader == null) { enabled = false; return; }
        material = new Material(shader) { name = "Weather rain (runtime)" };
        var holder = new GameObject("Camera rain");
        holder.transform.SetParent(transform, false);
        rain = holder.AddComponent<ParticleSystem>();
        rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = rain.main;
        main.playOnAwake = false;
        main.loop = true;
        main.maxParticles = 6000;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 3f;
        main.startSpeed = 22f;
        main.startSize = 0.035f;
        main.startColor = new Color(0.78f, 0.87f, 0.95f, 0.24f);
        var shape = rain.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(60f, 60f, 1f);
        var velocity = rain.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = 1.4f; velocity.y = 0f; velocity.z = 0.6f;
        var renderer = rain.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.035f;
        renderer.lengthScale = 3f;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        SetRain(0);
    }

    public void SetRain(int value)
    {
        level = Mathf.Clamp(value, 0, 3);
        if (rain == null) return;
        var emission = rain.emission;
        emission.rateOverTime = level == 1 ? 160 : level == 2 ? 750 : level == 3 ? 1800 : 0;
        var main = rain.main;
        main.startSize = level == 1 ? 0.02f : level == 2 ? 0.035f : 0.05f;
        main.startSpeed = level == 1 ? 12f : level == 2 ? 22f : 30f;
        main.startColor = new Color(0.78f, 0.87f, 0.95f, level == 1 ? 0.16f : 0.28f);
    }

    private void LateUpdate()
    {
        if (rain == null) return;
        if (view == null) view = Camera.main;
        bool active = level > 0 && view != null && GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted();
        if (active)
        {
            Vector3 position = view.transform.position + view.transform.forward * 20f + Vector3.up * 22f;
            if ((rain.transform.position - position).sqrMagnitude > 3600f) rain.Clear();
            rain.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, 0, 0));
        }
        if (active == emitting) return;
        emitting = active;
        if (active) rain.Play();
        else rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnDisable()
    {
        emitting = false;
        if (rain != null) rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnDestroy()
    {
        if (rain != null) Destroy(rain.gameObject);
        if (material != null) Destroy(material);
    }
}
