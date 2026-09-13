using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>エポックの確定値だけを読み、Unity内の空・照明を管理する。</summary>
public sealed class WeatherController : MonoBehaviour
{
    public static WeatherController Instance { get; private set; }
    public int CurrentIndex { get; private set; }
    public bool Locked { get; private set; }
    public float TransitionSeconds = 3f;
    private readonly System.Random random = new System.Random();
    private readonly WeatherEpochTracker epochs = new WeatherEpochTracker();
    private IpcClient ipc;
    private GameStateManager state;
    private Coroutine query;
    private bool pending, suspended, panel;
    private float retryAt;
    private Material sky;
    private Light sun;
    private Texture2D cookie;
    private UniversalAdditionalLightData sunData;
    private WeatherPreset from, target;
    private float blend = 1f;
    private float cloudSeed;
    private EnvironmentSnapshot original;
    private GUIStyle panelStyle;
    private ReflectionProbe reflection;
    private WeatherRain precipitation;

    private sealed class CityResponse { public CityEpoch city; }
    private sealed class CityEpoch { public int? epoch; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start()
    {
        var shader = Resources.Load<Shader>("Weather/Atmosphere");
        if (shader == null || !shader.isSupported)
        {
            Debug.LogError("Weather: 空のシェーダーを読み込めません。");
            enabled = false;
            return;
        }
        sun = RenderSettings.sun;
        if (sun == null)
        {
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Directional && light.isActiveAndEnabled &&
                    (sun == null || light.intensity > sun.intensity)) sun = light;
        }
        if (sun == null)
        {
            Debug.LogError("Weather: シーンに有効なDirectional Lightがありません。");
            enabled = false;
            return;
        }
        sunData = sun.GetUniversalAdditionalLightData();
        original = new EnvironmentSnapshot(sun, sunData);
        sky = new Material(shader) { name = "Weather sky (runtime)" };
        cookie = new Texture2D(128, 128, TextureFormat.RGBA32, false, true)
            { name = "Weather cloud shadows", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        RenderSettings.skybox = sky;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Custom;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        sun.shadows = LightShadows.Soft;
        sun.useColorTemperature = false;
        sunData.lightCookieSize = new Vector2(2600, 2600);
        var reflectionObject = new GameObject("Weather sky reflection");
        reflectionObject.transform.SetParent(transform, false);
        reflection = reflectionObject.AddComponent<ReflectionProbe>();
        reflection.mode = ReflectionProbeMode.Realtime;
        reflection.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        reflection.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
        reflection.clearFlags = ReflectionProbeClearFlags.Skybox;
        reflection.cullingMask = 0;
        reflection.resolution = 64;
        reflection.size = Vector3.one * 1000000f;
        reflection.importance = 0;
        precipitation = gameObject.AddComponent<WeatherRain>();
        SelectWeather(WeatherEpochTracker.Next(random), false, true);
    }

    private void Update()
    {
        if (sky == null) return;
        BindEvents();
        if (pending && query == null && !suspended && Time.unscaledTime >= retryAt)
            query = StartCoroutine(ReadEpoch());
        if (blend < 1f)
        {
            blend = Mathf.Min(1, blend + Time.unscaledDeltaTime / Mathf.Max(0.01f, TransitionSeconds));
            ApplyEnvironment(Mathf.SmoothStep(0, 1, blend));
            if (blend >= 1f) reflection.RenderProbe();
        }
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.f9Key.wasPressedThisFrame) panel = !panel;
        if (!panel) return;
        if (keyboard.f6Key.wasPressedThisFrame)
            SelectWeather((CurrentIndex + WeatherPreset.All.Count - 1) % WeatherPreset.All.Count, true, true);
        if (keyboard.f7Key.wasPressedThisFrame)
            SelectWeather((CurrentIndex + 1) % WeatherPreset.All.Count, true, true);
        if (keyboard.f10Key.wasPressedThisFrame) { Locked = !Locked; PublishWeather(); }
        if (keyboard.f11Key.wasPressedThisFrame) { Locked = false; Randomize(1); }
    }

    private void BindEvents()
    {
        if (ipc != IpcClient.Instance)
        {
            UnbindIpc();
            ipc = IpcClient.Instance;
            if (ipc != null)
            {
                ipc.OnSessionReload += RequestEpoch;
                ipc.OnSessionForceReload += RequestEpoch;
                ipc.OnSessionReset += Suspend;
                ipc.OnGameEnd += Suspend;
                ipc.OnGameStart += BeginSession;
                ipc.OnWeatherQuery += PublishWeather;
                ipc.OnWeatherSet += SetWeatherFromUi;
                PublishWeather();
            }
        }
        if (state != GameStateManager.Instance)
        {
            if (state != null) state.OnStateChanged -= StateChanged;
            state = GameStateManager.Instance;
            if (state != null)
            {
                state.OnStateChanged += StateChanged;
                if (state.IsGameStarted()) BeginSession();
            }
        }
    }

    private void StateChanged(GameStateManager.GameState value)
    {
        if (value == GameStateManager.GameState.Game) BeginSession();
        else Suspend();
    }

    private void BeginSession()
    {
        CancelQuery();
        epochs.Reset();
        suspended = false;
        pending = true;
        retryAt = 0;
        if (precipitation != null && target != null) precipitation.SetRain(target.Rain);
    }

    private void Suspend()
    {
        CancelQuery();
        epochs.Reset();
        suspended = true;
        pending = false;
        if (precipitation != null) precipitation.SetRain(0);
    }

    private void CancelQuery()
    {
        if (query != null) StopCoroutine(query);
        query = null;
    }

    private void RequestEpoch() { pending = true; retryAt = 0; }

    private IEnumerator ReadEpoch()
    {
        // 同時要求は直列化し、取得中に届いたリロードも次の1回として残す。
        pending = false;
        bool received = false;
        yield return ApiClient.Get<CityResponse>("/city/", response =>
        {
            if (response?.city?.epoch == null) return;
            received = true;
            int advanced = epochs.Observe(response.city.epoch.Value);
            if (advanced > 0 && !Locked) Randomize(advanced);
        }, _ => { });
        if (!received) { pending = true; retryAt = Time.unscaledTime + 5f; }
        query = null;
    }

    private void Randomize(int count)
    {
        int next = CurrentIndex;
        for (int i = 0; i < count; i++) next = WeatherEpochTracker.Next(random);
        SelectWeather(next, false, false);
    }

    public void SelectWeather(int index, bool lockWeather = true, bool immediate = true)
    {
        if (sky == null || index < 0 || index >= WeatherPreset.All.Count) return;
        // 連続選択では遷移を確定してから次の遷移を始め、参照元を常にプリセットに保つ。
        if (target != null && blend < 1f) ApplyEnvironment(1);
        from = target ?? WeatherPreset.All[index];
        target = WeatherPreset.All[index];
        CurrentIndex = index;
        Locked = lockWeather;
        cloudSeed = index * 13.71f;
        blend = immediate ? 1f : 0f;
        BuildCookie();
        ApplyEnvironment(blend);
        if (immediate) reflection.RenderProbe();
        precipitation.SetRain(target.Rain);
        PublishWeather();
    }

    public void SetWeatherFromUi(string id)
    {
        if (string.IsNullOrEmpty(id)) { Locked = false; PublishWeather(); return; }
        for (int i = 0; i < WeatherPreset.All.Count; i++)
            if (WeatherPreset.All[i].Id == id) { SelectWeather(i); return; }
        PublishWeather();
    }

    public void PublishWeather()
    {
        if (sky == null || target == null || IpcClient.Instance == null) return;
        var options = new object[WeatherPreset.All.Count];
        for (int i = 0; i < options.Length; i++)
        {
            var preset = WeatherPreset.All[i];
            options[i] = new { id = preset.Id, weight = preset.Weight, rain = preset.Rain };
        }
        IpcClient.Instance.EmitWeatherState(target.Id, Locked, target.Rain, options);
    }

    private void ApplyEnvironment(float t)
    {
        Color horizon = Color.Lerp(from.Horizon, target.Horizon, t);
        Color zenith = Color.Lerp(from.Zenith, target.Zenith, t);
        Color sunlight = Color.Lerp(from.Sun, target.Sun, t);
        float haze = Mathf.Lerp(from.Haze, target.Haze, t);
        // プリセットの方位は太陽のある方向。Light.forwardは光の進む向きなので反転する。
        sun.transform.rotation = Quaternion.Slerp(Quaternion.Euler(from.Elevation, from.Azimuth + 180f, 0),
            Quaternion.Euler(target.Elevation, target.Azimuth + 180f, 0), t);
        sun.color = sunlight;
        sun.intensity = Mathf.Lerp(from.Intensity, target.Intensity, t);
        sun.shadowStrength = Mathf.Lerp(from.Shadow, target.Shadow, t);
        sky.SetColor("_Zenith", zenith);
        sky.SetColor("_Horizon", horizon);
        sky.SetColor("_SunColor", sunlight);
        sky.SetColor("_CloudColor", Color.Lerp(from.Cloud, target.Cloud, t));
        sky.SetVector("_SunDirection", -sun.transform.forward);
        sky.SetFloat("_Coverage", Mathf.Lerp(from.Coverage, target.Coverage, t));
        sky.SetFloat("_Thickness", Mathf.Lerp(from.Thickness, target.Thickness, t));
        sky.SetFloat("_Seed", cloudSeed);
        RenderSettings.ambientSkyColor = Color.Lerp(zenith, horizon, 0.55f) * 0.75f;
        RenderSettings.ambientEquatorColor = horizon * 0.48f;
        RenderSettings.ambientGroundColor = horizon * 0.22f;
        // URPの動的メッシュへ渡すSHも明示する。空の再ベイクを待たず、曇天の拡散光を反映する。
        float coverage = Mathf.Lerp(from.Coverage, target.Coverage, t);
        var ambient = new SphericalHarmonicsL2();
        ambient.AddAmbientLight(horizon.linear * Mathf.Lerp(0.28f, 0.48f, coverage));
        ambient.AddDirectionalLight(Vector3.up, zenith.linear, 0.12f);
        RenderSettings.ambientProbe = ambient;
        RenderSettings.fogColor = horizon;
        RenderSettings.fogStartDistance = Mathf.Lerp(1000, 60, haze);
        RenderSettings.fogEndDistance = Mathf.Lerp(6500, 1200, haze);
    }

    private void BuildCookie()
    {
        // 周期境界をブレンドして、都市上で雲影のタイルの継ぎ目が見えないようにする。
        var pixels = new Color[128 * 128];
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float u = x / 128f, v = y / 128f;
            float Noise(float a, float b) => Mathf.PerlinNoise(a * 5 + cloudSeed, b * 5 + cloudSeed);
            float n = Mathf.Lerp(Mathf.Lerp(Noise(u,v), Noise(u-1,v), u),
                Mathf.Lerp(Noise(u,v-1), Noise(u-1,v-1), u), v);
            float shade = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1-target.Coverage, 1.25f-target.Coverage, n));
            float value = 1 - shade * Mathf.Min(0.5f, target.Thickness * 0.45f);
            pixels[y * 128 + x] = new Color(value, value, value, 1);
        }
        cookie.SetPixels(pixels);
        cookie.Apply(false, false);
        sun.cookie = cookie;
    }

    private void OnGUI()
    {
        if (!panel || sky == null) return;
        if (panelStyle == null) panelStyle = new GUIStyle(GUI.skin.box)
            { font = UiFontProvider.GetFont(16), fontSize = 16, alignment = TextAnchor.UpperLeft, padding = new RectOffset(16,16,12,12) };
        GUI.Box(new Rect(20, 70, 430, 132),
            $"天気 / Weather   {CurrentIndex + 1:00}/{WeatherPreset.All.Count}\n{WeatherPreset.All[CurrentIndex].Name}   {(Locked ? "固定 / Locked" : "自動 / Auto")}\nF6/F7 選択   F10 固定   F11 再抽選\nF9 パネルを閉じる（撮影前に非表示）", panelStyle);
    }

    private void UnbindIpc()
    {
        if (ipc == null) return;
        ipc.OnSessionReload -= RequestEpoch;
        ipc.OnSessionForceReload -= RequestEpoch;
        ipc.OnSessionReset -= Suspend;
        ipc.OnGameEnd -= Suspend;
        ipc.OnGameStart -= BeginSession;
        ipc.OnWeatherQuery -= PublishWeather;
        ipc.OnWeatherSet -= SetWeatherFromUi;
    }

    private void OnDisable()
    {
        UnbindIpc(); ipc = null;
        if (state != null) state.OnStateChanged -= StateChanged;
        state = null;
        CancelQuery();
        if (original != null) original.Restore(sun, sunData);
        if (reflection != null) reflection.enabled = false;
        if (precipitation != null) precipitation.enabled = false;
    }

    private void OnEnable()
    {
        if (sky == null) return;
        RenderSettings.skybox = sky;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Custom;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        sun.shadows = LightShadows.Soft;
        sun.useColorTemperature = false;
        sunData.lightCookieSize = new Vector2(2600, 2600);
        sun.cookie = cookie;
        reflection.enabled = true;
        ApplyEnvironment(blend);
        reflection.RenderProbe();
        if (precipitation != null) { precipitation.enabled = true; precipitation.SetRain(target.Rain); }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (sky != null) Destroy(sky);
        if (cookie != null) Destroy(cookie);
        if (reflection != null) Destroy(reflection.gameObject);
        if (precipitation != null) Destroy(precipitation);
    }

    private sealed class EnvironmentSnapshot
    {
        private readonly Material skybox = RenderSettings.skybox;
        private readonly Light originalSun = RenderSettings.sun;
        private readonly AmbientMode ambientMode = RenderSettings.ambientMode;
        private readonly SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;
        private readonly Color ambientSky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor,
            ground = RenderSettings.ambientGroundColor, fogColor = RenderSettings.fogColor;
        private readonly bool fog = RenderSettings.fog;
        private readonly FogMode fogMode = RenderSettings.fogMode;
        private readonly float fogStart = RenderSettings.fogStartDistance, fogEnd = RenderSettings.fogEndDistance;
        private readonly Quaternion rotation;
        private readonly Color color;
        private readonly float intensity, strength;
        private readonly bool temperature;
        private readonly LightShadows shadows;
        private readonly Texture cookie;
        private readonly Vector2 cookieSize;
        public EnvironmentSnapshot(Light light, UniversalAdditionalLightData data)
        {
            rotation = light.transform.rotation; color = light.color; intensity = light.intensity;
            strength = light.shadowStrength; shadows = light.shadows; cookie = light.cookie;
            temperature = light.useColorTemperature; cookieSize = data.lightCookieSize;
        }
        public void Restore(Light light, UniversalAdditionalLightData data)
        {
            RenderSettings.skybox = skybox; RenderSettings.sun = originalSun; RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientSkyColor = ambientSky; RenderSettings.ambientEquatorColor = equator; RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientProbe = ambientProbe;
            RenderSettings.fog = fog; RenderSettings.fogMode = fogMode; RenderSettings.fogColor = fogColor;
            RenderSettings.fogStartDistance = fogStart; RenderSettings.fogEndDistance = fogEnd;
            if (light == null) return;
            light.transform.rotation = rotation; light.color = color; light.intensity = intensity;
            light.shadowStrength = strength; light.shadows = shadows; light.cookie = cookie;
            light.useColorTemperature = temperature;
            if (data != null) data.lightCookieSize = cookieSize;
        }
    }
}
