using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// 天気・時刻・気候から、Unity内の空・照明・霧・降水を管理する。経済・マスター・セーブには書き込まない。
/// 天気はエポックと無関係に隣り合う天気へゆっくり移り変わり(WeatherChain)、時刻は WeatherClock が進める。
/// 気候名と月別の出現割合は GET /api/v1/city の climate と current_date から読む。
/// </summary>
public sealed class WeatherController : MonoBehaviour
{
    public static WeatherController Instance { get; private set; }
    public int CurrentIndex { get; private set; }
    public bool Locked { get; private set; }
    public float TransitionSeconds = 45f;
    public string Climate { get; private set; } = "";
    public int Season { get; private set; } = WeatherClimate.Spring;
    public WeatherClock Clock { get; } = new WeatherClock();

    /// <summary>移り変わりの途中を含む今の雨量(0〜3)。環境音が雨音の強さに使う。</summary>
    public float RainAmount => Blended().Rain;

    private readonly System.Random random = new System.Random();
    private readonly WeatherEpochTracker epochs = new WeatherEpochTracker();
    private IpcClient ipc;
    private GameStateManager state;
    private Coroutine query;
    private bool pending, suspended, panel, awaitingDawn, redrawAtDawn;
    private bool holdingEpoch, darkenedSent;
    private string presentationId;
    private int blackFrame = -1;
    private CanvasGroup fadeOverlay;
    private float retryAt, dwellLeft, probeAt, cookieAt;
    private Material sky;
    private Light sun;
    private Texture2D cookie;
    private UniversalAdditionalLightData sunData;
    private Look fromLook, toLook;
    private float blend = 1f, blendSeconds = 1f;
    private float cloudSeed, cookieCoverage = -1f, cookieThickness = -1f;
    private IReadOnlyList<int> weights = WeatherClimate.InitialWeights();
    private EnvironmentSnapshot original;
    private GUIStyle panelStyle;
    private ReflectionProbe reflection;
    private WeatherPrecipitation precipitation;

    private static readonly int SunScatterId = Shader.PropertyToID("_SunScatterFraction");
    private static readonly Color LowSunZenith = Hex("696E9B"), LowSunHorizon = Hex("ECA68D"), LowSunLight = Hex("FFA477"), LowSunCloud = Hex("EDB7AB");
    private static readonly Color NightZenith = Hex("05070D"), NightHorizon = Hex("0B1019"), NightCloud = Hex("0C1018");

    private sealed class CityResponse { public CityState city; }
    private sealed class CityState { public int? epoch; public WeatherClimateSettings climate; public int? current_date; }

    /// <summary>天気の見た目。移り変わりの途中は前後の天気の間を補間する。</summary>
    private struct Look
    {
        public Color Zenith, Horizon, Sun, Cloud;
        public float Intensity, Coverage, Thickness, Haze, Shadow, Rain, Snow;

        public static Look Of(WeatherPreset p) => new Look
        {
            Zenith = p.Zenith, Horizon = p.Horizon, Sun = p.Sun, Cloud = p.Cloud, Intensity = p.Intensity,
            Coverage = p.Coverage, Thickness = p.Thickness, Haze = p.Haze, Shadow = p.Shadow, Rain = p.Rain, Snow = p.Snow,
        };

        public static Look Lerp(Look a, Look b, float t) => new Look
        {
            Zenith = Color.Lerp(a.Zenith, b.Zenith, t), Horizon = Color.Lerp(a.Horizon, b.Horizon, t),
            Sun = Color.Lerp(a.Sun, b.Sun, t), Cloud = Color.Lerp(a.Cloud, b.Cloud, t),
            Intensity = Mathf.Lerp(a.Intensity, b.Intensity, t), Coverage = Mathf.Lerp(a.Coverage, b.Coverage, t),
            Thickness = Mathf.Lerp(a.Thickness, b.Thickness, t), Haze = Mathf.Lerp(a.Haze, b.Haze, t),
            Shadow = Mathf.Lerp(a.Shadow, b.Shadow, t), Rain = Mathf.Lerp(a.Rain, b.Rain, t), Snow = Mathf.Lerp(a.Snow, b.Snow, t),
        };
    }

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
        // 品質設定の Realtime Reflection Probes が無効だとこのプローブは使われず、
        // シーン既定の環境反射(編集時の既定スカイボックス=青空)が天気に関係なく映る。
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
        precipitation = gameObject.AddComponent<WeatherPrecipitation>();
        cloudSeed = NewSeed();
        StartWeather(WeatherChain.Sample(random, weights));
        CreateFadeOverlay();
    }

    private void Update()
    {
        if (sky == null) return;
        BindEvents();
        if (pending && query == null && !suspended && Time.unscaledTime >= retryAt)
            query = StartCoroutine(ReadCity());
        float seconds = Time.unscaledDeltaTime;
        if (Clock.Advance(seconds)) OnDawn();
        AdvanceWeather(seconds);
        ApplyEnvironment();
        fadeOverlay.alpha = Clock.FadeAlpha;
        // 黒幕を反映したフレームを挟んで通知し、UIが先に結果を表示しないようにする。
        if (presentationId != null && !darkenedSent && blackFrame >= 0 && Time.frameCount > blackFrame)
        {
            darkenedSent = true;
            ipc?.EmitEpochDarkened(presentationId);
        }
        if (Clock.FadeAlpha >= 1f && blackFrame < 0) blackFrame = Time.frameCount;
        RefreshSky(false);
        HandleKeys();
    }

    private void BindEvents()
    {
        if (ipc != IpcClient.Instance)
        {
            UnbindIpc();
            ipc = IpcClient.Instance;
            if (ipc != null)
            {
                ipc.OnSessionReload += RequestCity;
                ipc.OnSessionForceReload += RequestCity;
                ipc.OnSessionReset += Suspend;
                ipc.OnGameEnd += Suspend;
                ipc.OnGameStart += BeginSession;
                ipc.OnEpochExecuting += SetEpochExecuting;
                ipc.OnEpochPrepare += PrepareEpoch;
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
        awaitingDawn = redrawAtDawn = false;
        holdingEpoch = false;
        presentationId = null;
        RequestCity();
    }

    private void Suspend()
    {
        CancelQuery();
        epochs.Reset();
        suspended = true;
        pending = false;
        awaitingDawn = redrawAtDawn = false;
        holdingEpoch = false;
        presentationId = null;
        Clock.StartMorning();
    }

    private void CancelQuery()
    {
        if (query != null) StopCoroutine(query);
        query = null;
    }

    private void RequestCity() { pending = true; retryAt = 0; }

    /// <summary>
    /// エポック演出の開始と終了。結果画面を閉じた後は都市を読んでから日の出へ進める。
    /// 読み終える前に明けると、明るくなってから天気が変わって見える。
    /// </summary>
    private void SetEpochExecuting(bool executing)
    {
        if (suspended) return;
        holdingEpoch = executing;
        if (executing) { awaitingDawn = false; Clock.BeginNight(); return; }
        presentationId = null;
        awaitingDawn = true;
        RequestCity();
    }

    private void PrepareEpoch(string id)
    {
        if (suspended || string.IsNullOrEmpty(id) || presentationId == id) return;
        presentationId = id;
        darkenedSent = false;
        blackFrame = -1;
        SetEpochExecuting(true);
    }

    private IEnumerator ReadCity()
    {
        // 同時要求は直列化し、取得中に届いたリロードも次の1回として残す。
        pending = false;
        bool received = false;
        yield return ApiClient.Get<CityResponse>("/city/", response =>
        {
            if (response?.city?.epoch == null) return;
            received = true;
            ApplyCity(response.city);
        }, _ => { });
        query = null;
        if (!received) { pending = true; retryAt = Time.unscaledTime + 5f; }
        // 読めなかったときも日の出へ進め、バックエンドが止まっていても暗いままにしない。
        if (awaitingDawn && (!received || !pending))
        {
            awaitingDawn = false;
            Clock.RequestDawn();
        }
    }

    private void ApplyCity(CityState city)
    {
        bool sessionStart = !epochs.Epoch.HasValue;
        int advanced = epochs.Observe(city.epoch.Value);
        if (city.climate?.months?.Length != 12) return;
        Climate = city.climate.name;
        if (city.current_date.HasValue) Season = WeatherClimate.SeasonOf(city.current_date.Value);
        var next = WeatherClimate.Weights(city.climate, city.current_date ?? 0);
        bool changed = false;
        for (int i = 0; i < next.Count; i++) changed |= next[i] != weights[i];
        weights = next;
        if (sessionStart)
        {
            // セッション開始はロード画面の裏なので、その都市の気候・季節で天気を選び直して朝から始める。
            if (!Locked) { cloudSeed = NewSeed(); StartWeather(WeatherChain.Sample(random, weights)); }
            if (!holdingEpoch) Clock.StartMorning();
        }
        else if (advanced > 0)
        {
            // エポックが進んだ = 日をまたいだ。暗転前なら早送りで暗転させ、明けたところで天気を引き直す。
            redrawAtDawn = true;
            if (!holdingEpoch)
            {
                Clock.BeginNight();
                Clock.RequestDawn();
            }
        }
        if (changed) PublishWeather();
    }

    /// <summary>暗転が明けた瞬間。エポックが進んでいれば、日の出の前に新しい日の天気へ切り替える。</summary>
    private void OnDawn()
    {
        if (!redrawAtDawn) return;
        redrawAtDawn = false;
        if (Locked) return;
        cloudSeed = NewSeed();
        StartWeather(WeatherChain.Sample(random, weights));
    }

    private void AdvanceWeather(float seconds)
    {
        if (blend < 1f) blend = Mathf.Min(1f, blend + seconds / blendSeconds);
        // 暗転中の天気は日の出の直前に引き直すので進めない。
        if (Locked || Clock.IsDarkening) return;
        dwellLeft -= seconds;
        if (dwellLeft > 0f) return;
        int next = WeatherChain.Step(random, CurrentIndex, weights);
        dwellLeft = WeatherChain.Dwell(random, next);
        if (next != CurrentIndex) BeginTransition(next, TransitionSeconds);
    }

    /// <summary>途中から見始めた天気として即座に切り替える。残りの持続時間は1回分の途中から数える。</summary>
    private void StartWeather(int index)
    {
        dwellLeft = WeatherChain.Dwell(random, index) * (float)random.NextDouble();
        BeginTransition(index, 0f);
        ApplyEnvironment();
        RefreshSky(true);
        precipitation.ClearStopped();
    }

    private void BeginTransition(int index, float seconds)
    {
        fromLook = Blended();
        toLook = Look.Of(WeatherPreset.All[index]);
        CurrentIndex = index;
        blendSeconds = Mathf.Max(0.01f, seconds);
        blend = seconds > 0f ? 0f : 1f;
        PublishWeather();
    }

    private Look Blended() => Look.Lerp(fromLook, toLook, Mathf.SmoothStep(0f, 1f, blend));

    /// <summary>天気を選んで表示する。既定は撮影用の即時切り替え+固定。</summary>
    public void SelectWeather(int index, bool lockWeather = true, bool immediate = true)
    {
        if (sky == null || index < 0 || index >= WeatherPreset.All.Count) return;
        Locked = lockWeather;
        dwellLeft = WeatherChain.Dwell(random, index);
        BeginTransition(index, immediate ? 0f : TransitionSeconds);
        if (!immediate) return;
        ApplyEnvironment();
        RefreshSky(true);
        precipitation.ClearStopped();
    }

    /// <summary>固定を切り替える。解除すると今の天気から自然な移り変わりを再開する。</summary>
    public void SetLocked(bool value)
    {
        Locked = value;
        if (!value) dwellLeft = WeatherChain.Dwell(random, CurrentIndex) * (float)random.NextDouble();
        PublishWeather();
    }

    public void SetWeatherFromUi(string id)
    {
        if (string.IsNullOrEmpty(id)) { SetLocked(false); return; }
        int index = WeatherPreset.IndexOf(id);
        if (index >= 0) SelectWeather(index);
        else PublishWeather();
    }

    public void PublishWeather()
    {
        if (sky == null || IpcClient.Instance == null) return;
        var options = new object[WeatherPreset.All.Count];
        for (int i = 0; i < options.Length; i++)
        {
            var preset = WeatherPreset.All[i];
            options[i] = new { id = preset.Id, weight = weights[i], rain = preset.Rain, snow = preset.Snow };
        }
        var current = WeatherPreset.All[CurrentIndex];
        IpcClient.Instance.EmitWeatherState(current.Id, Locked, current.Rain, current.Snow, options);
    }

    private void ApplyEnvironment()
    {
        Look look = Blended();
        float elevation = Clock.Elevation, darkness = Clock.Darkness;
        // 太陽が低いほど夕焼けの色へ寄せ、暗転・薄明では夜の色へ寄せる。厚い雲の下では夕焼けの色づきが弱い。
        float golden = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f, 22f, elevation))) * (1f - 0.8f * look.Coverage);
        Color zenith = Color.Lerp(Color.Lerp(look.Zenith, LowSunZenith, golden), NightZenith, darkness);
        Color horizon = Color.Lerp(Color.Lerp(look.Horizon, LowSunHorizon, golden), NightHorizon, darkness);
        Color cloudColor = Color.Lerp(Color.Lerp(look.Cloud, LowSunCloud, golden), NightCloud, darkness);
        Color sunlight = Color.Lerp(look.Sun, LowSunLight, golden);
        // 直射光は地平線の少し下で消し、日没直後の残照は空の色だけで表す。低い太陽は大気を長く通るぶん弱い。
        float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-3f, 5f, elevation)) * (1f - darkness);
        float lowSun = Mathf.Lerp(0.6f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 30f, elevation)));
        // 方位は太陽のある方向で、Light.forward は光の進む向きなので反転する。地平線より下からは照らさない。
        sun.transform.rotation = Quaternion.Euler(Mathf.Max(elevation, 2f), Clock.Azimuth + 180f, 0f);
        sun.color = sunlight;
        sun.intensity = look.Intensity * lowSun * daylight;
        sun.shadowStrength = look.Shadow * daylight;
        // 影の濃さ=日光のうち点として届く割合。残りは雲で広がった光で、水面はこれを鋭く反射しない。
        Shader.SetGlobalFloat(SunScatterId, 1f - look.Shadow);
        sky.SetColor("_Zenith", zenith);
        sky.SetColor("_Horizon", horizon);
        sky.SetColor("_SunColor", sunlight * (1f - darkness));
        sky.SetColor("_CloudColor", cloudColor);
        sky.SetVector("_SunDirection", SunDirection(elevation, Clock.Azimuth));
        sky.SetFloat("_Coverage", look.Coverage);
        sky.SetFloat("_Thickness", look.Thickness);
        sky.SetFloat("_Seed", cloudSeed);
        RenderSettings.ambientSkyColor = Color.Lerp(zenith, horizon, 0.55f) * 0.75f;
        RenderSettings.ambientEquatorColor = horizon * 0.48f;
        RenderSettings.ambientGroundColor = horizon * 0.22f;
        // URPの動的メッシュへ渡すSHも明示する。空の再ベイクを待たず、曇天の拡散光を反映する。
        var ambient = new SphericalHarmonicsL2();
        ambient.AddAmbientLight(horizon.linear * Mathf.Lerp(0.28f, 0.48f, look.Coverage));
        ambient.AddDirectionalLight(Vector3.up, zenith.linear, 0.12f);
        RenderSettings.ambientProbe = ambient;
        RenderSettings.fogColor = horizon;
        RenderSettings.fogStartDistance = Mathf.Lerp(1000, 60, look.Haze);
        RenderSettings.fogEndDistance = Mathf.Lerp(6500, 1200, look.Haze);
        precipitation.SetLevels(look.Rain, look.Snow, 1f - darkness);
    }

    /// <summary>空の反射と雲影は作り直しが重いので、変化が溜まったときと一定間隔でだけ更新する。</summary>
    private void RefreshSky(bool force)
    {
        Look look = Blended();
        float now = Time.unscaledTime;
        bool cloudsMoved = Mathf.Abs(look.Coverage - cookieCoverage) > 0.02f || Mathf.Abs(look.Thickness - cookieThickness) > 0.04f;
        if (force || (cloudsMoved && now >= cookieAt))
        {
            BuildCookie(look.Coverage, look.Thickness);
            cookieAt = now + 0.5f;
        }
        if (force || now >= probeAt)
        {
            reflection.RenderProbe();
            probeAt = now + 3f;
        }
    }

    private void BuildCookie(float coverage, float thickness)
    {
        cookieCoverage = coverage;
        cookieThickness = thickness;
        // 周期境界をブレンドして、都市上で雲影のタイルの継ぎ目が見えないようにする。
        var pixels = new Color[128 * 128];
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float u = x / 128f, v = y / 128f;
            float Noise(float a, float b) => Mathf.PerlinNoise(a * 5 + cloudSeed, b * 5 + cloudSeed);
            float n = Mathf.Lerp(Mathf.Lerp(Noise(u,v), Noise(u-1,v), u),
                Mathf.Lerp(Noise(u,v-1), Noise(u-1,v-1), u), v);
            float shade = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1-coverage, 1.25f-coverage, n));
            float value = 1 - shade * Mathf.Min(0.5f, thickness * 0.45f);
            pixels[y * 128 + x] = new Color(value, value, value, 1);
        }
        cookie.SetPixels(pixels);
        cookie.Apply(false, false);
        sun.cookie = cookie;
    }

    private void HandleKeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.f9Key.wasPressedThisFrame) panel = !panel;
        if (!panel) return;
        int count = WeatherPreset.All.Count;
        if (keyboard.f6Key.wasPressedThisFrame) SelectWeather((CurrentIndex + count - 1) % count);
        if (keyboard.f7Key.wasPressedThisFrame) SelectWeather((CurrentIndex + 1) % count);
        if (keyboard.f10Key.wasPressedThisFrame) SetLocked(!Locked);
        if (keyboard.f11Key.wasPressedThisFrame) { Locked = false; StartWeather(WeatherChain.Sample(random, weights)); }
        if (keyboard.f3Key.wasPressedThisFrame) Clock.Step(-1f);
        if (keyboard.f4Key.wasPressedThisFrame) Clock.Step(1f);
        if (keyboard.f5Key.wasPressedThisFrame) Clock.Paused = !Clock.Paused;
    }

    private void OnGUI()
    {
        if (!panel || sky == null || Clock.FadeAlpha > 0f) return;
        if (panelStyle == null) panelStyle = new GUIStyle(GUI.skin.box)
            { font = UiFontProvider.GetFont(16), fontSize = 16, alignment = TextAnchor.UpperLeft, padding = new RectOffset(16,16,12,12) };
        float hours = Clock.Hours;
        int hour = Mathf.FloorToInt(hours), minute = Mathf.FloorToInt((hours - hour) * 60f);
        GUI.Box(new Rect(20, 70, 560, 176),
            $"天気 / Weather   {CurrentIndex + 1:00}/{WeatherPreset.All.Count}  {WeatherPreset.All[CurrentIndex].Name}   {(Locked ? "固定 / Locked" : "自動 / Auto")}\n" +
            $"気候 / Climate   {Climate}   {WeatherClimate.SeasonName(Season)}\n" +
            $"時刻 / Time   {hour:00}:{minute:00}{(Clock.Paused ? "   停止 / Paused" : "")}\n" +
            "F6/F7 天気   F10 固定   F11 再抽選\nF3/F4 時刻を1時間戻す/進める   F5 時刻の停止\nF9 パネルを閉じる（撮影前に非表示）", panelStyle);
    }

    private void CreateFadeOverlay()
    {
        // ScreenSpaceOverlayはライティングや都市側のUnlitマテリアルに依存せず、画面全体を黒く覆う。
        var root = new GameObject("Epoch fade", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        root.transform.SetParent(transform, false);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        fadeOverlay = root.GetComponent<CanvasGroup>();
        fadeOverlay.alpha = 0f;
        fadeOverlay.blocksRaycasts = false;
        var image = new GameObject("Black", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        image.transform.SetParent(root.transform, false);
        image.color = Color.black;
        image.raycastTarget = false;
        image.rectTransform.anchorMin = Vector2.zero;
        image.rectTransform.anchorMax = Vector2.one;
        image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
    }

    private void UnbindIpc()
    {
        if (ipc == null) return;
        ipc.OnSessionReload -= RequestCity;
        ipc.OnSessionForceReload -= RequestCity;
        ipc.OnSessionReset -= Suspend;
        ipc.OnGameEnd -= Suspend;
        ipc.OnGameStart -= BeginSession;
        ipc.OnEpochExecuting -= SetEpochExecuting;
        ipc.OnEpochPrepare -= PrepareEpoch;
        ipc.OnWeatherQuery -= PublishWeather;
        ipc.OnWeatherSet -= SetWeatherFromUi;
    }

    private float NewSeed() => (float)(random.NextDouble() * 100.0);

    private static Vector3 SunDirection(float elevation, float azimuth)
    {
        float e = elevation * Mathf.Deg2Rad, a = azimuth * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));
    }

    private static Color Hex(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out var c); return c; }

    private void OnDisable()
    {
        if (fadeOverlay != null) fadeOverlay.alpha = 0f;
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
        if (precipitation != null) precipitation.enabled = true;
        ApplyEnvironment();
        RefreshSky(true);
    }

    private void OnDestroy()
    {
        if (fadeOverlay != null) Destroy(fadeOverlay.gameObject);
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
        private readonly float sunScatter = Shader.GetGlobalFloat(SunScatterId);
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
            Shader.SetGlobalFloat(SunScatterId, sunScatter);
            if (light == null) return;
            light.transform.rotation = rotation; light.color = color; light.intensity = intensity;
            light.shadowStrength = strength; light.shadows = shadows; light.cookie = cookie;
            light.useColorTemperature = temperature;
            if (data != null) data.lightCookieSize = cookieSize;
        }
    }
}
