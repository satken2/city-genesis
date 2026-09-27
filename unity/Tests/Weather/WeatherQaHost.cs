using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// 隔離した検証用Unityだけで使う通信の代役。本編には含めない。
public class IpcClient : MonoBehaviour
{
    public static IpcClient Instance { get; private set; }
    public event Action OnSessionReload, OnSessionForceReload, OnSessionReset, OnGameEnd, OnGameStart;
    public event Action OnWeatherQuery;
    public event Action<string> OnWeatherSet;
    public event Action<bool> OnEpochExecuting;
    public event Action<string> OnEpochPrepare;
    public string DarkenedId;
    public int DarkenedCount;
    public void PrepareEpoch(string id) => OnEpochPrepare?.Invoke(id);
    public void EmitEpochDarkened(string id) { DarkenedId = id; DarkenedCount++; }
    public int Publishes;
    public string WeatherId;
    public int Rain, Snow;
    public bool Locked;
    public object[] Presets;
    public event Action<float> OnAmbienceVolume;
    public void Volume(float value) => OnAmbienceVolume?.Invoke(value);
    public void EmitWeatherState(string id, bool locked, int rain, int snow, object[] presets)
    { Publishes++; WeatherId = id; Locked = locked; Rain = rain; Snow = snow; Presets = presets; }
    public void QueryWeather() => OnWeatherQuery?.Invoke();
    public void SetWeather(string id) => OnWeatherSet?.Invoke(id);
    public void Executing(bool value) => OnEpochExecuting?.Invoke(value);
    private void Awake() => Instance = this;
    public void Reload() => OnSessionReload?.Invoke();
    public void ResetSession() => OnSessionReset?.Invoke();
    public void Begin() => OnGameStart?.Invoke();
    public void End() => OnGameEnd?.Invoke();
    public void Force() => OnSessionForceReload?.Invoke();
}
public class GameStateManager : MonoBehaviour
{
    public enum GameState { Init, Menu, Game }
    public static GameStateManager Instance { get; private set; }
    public event Action<GameState> OnStateChanged;
    private void Awake() => Instance = this;
    public bool IsGameStarted() => true;
    public void Notify(GameState state) => OnStateChanged?.Invoke(state);
}
public static class ApiClient
{
    public static int Epoch, Reads, Climate = Constants.CLIMATE_TEMPERATE, Date = 82530;
    public static bool Fail;
    public static IEnumerator Get<T>(string path, Action<T> success, Action<string> error)
    {
        Reads++;
        yield return null;
        if (Fail) { error("test failure"); yield break; }
        success(Newtonsoft.Json.JsonConvert.DeserializeObject<T>(
            "{\"city\":{\"epoch\":" + Epoch + ",\"climate\":" + Climate + ",\"current_date\":" + Date + "}}"));
    }
}
public static class Utils
{
    public static float GroundHeight;
    public static float GetGroundHeight(Vector3 position) => GroundHeight;
}
public sealed class WeatherQaHost : MonoBehaviour
{
    private int checks;
    private string root;
    public void Run() { root = Path.GetDirectoryName(Application.dataPath); StartCoroutine(Guard(Suite())); }
    private void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    // 入れ子の待機(IEnumerator)も自前で回し、どの階層の例外も FAIL として書き出す(Unityに任せると親が止まったままになる)。
    private IEnumerator Guard(IEnumerator suite)
    {
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        stack.Push(suite);
        while (stack.Count > 0)
        {
            object value;
            try
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                value = stack.Peek().Current;
            }
            catch (Exception e)
            {
                File.WriteAllText(root + "/result.txt", "FAIL " + e);
                Debug.LogException(e); UnityEditor.EditorApplication.Exit(1); yield break;
            }
            if (value is IEnumerator nested) { stack.Push(nested); continue; }
            yield return value;
        }
        File.WriteAllText(root + "/result.txt", $"PASS {checks} checks; {WeatherPreset.All.Count} weather captures");
        UnityEditor.EditorApplication.Exit(0);
    }
    private static int Index(string id) => WeatherPreset.IndexOf(id);
    private static int Day(int month, int day) => 82440 + (month - 1) * 30 + (day - 1);
    private IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }
    private IEnumerator Until(Func<bool> condition, float seconds, string message)
    {
        float limit = Time.realtimeSinceStartup + seconds;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > limit) throw new Exception("timeout: " + message);
            yield return null;
        }
    }
    private IEnumerator Suite()
    {
        var tracker = new WeatherEpochTracker();
        Check(tracker.Observe(5) == 0, "initial epoch is baseline");
        Check(tracker.Observe(5) == 0, "duplicate reload");
        Check(tracker.Observe(8) == 3, "batch epochs");
        Check(tracker.Observe(2) == 0, "older save baseline");
        tracker.Reset(); Check(tracker.Observe(20) == 0, "session reset");

        // 気候と季節の表
        int presetCount = WeatherPreset.All.Count;
        Check(WeatherClimate.ClimateCount == Constants.CLIMATE_TYPES.Length, "one row block per climate");
        foreach (int climate in Constants.CLIMATE_TYPES)
            for (int season = 0; season < 4; season++)
            {
                var weights = WeatherClimate.Weights(climate, season);
                Check(weights.Count == presetCount, "weights cover every preset");
                int total = 0;
                foreach (int weight in weights) { Check(weight >= 0, "no negative weight"); total += weight; }
                Check(total == 100, $"climate {climate} season {season} sums to 100");
                Check(WeatherChain.Connected(weights), $"climate {climate} season {season} reachable through neighbours");
            }
        for (int season = 0; season < 4; season++)
        {
            Check(WeatherClimate.Weights(Constants.CLIMATE_DESERT, season)[Index("heavy_rain")] == 0, "no heavy rain in the desert");
            foreach (string snowy in new[] { "light_snow", "snow", "blizzard" })
            {
                Check(WeatherClimate.Weights(Constants.CLIMATE_SUBTROPICAL, season)[Index(snowy)] == 0, "no snow in the subtropics");
                Check(WeatherClimate.Weights(Constants.CLIMATE_DESERT, season)[Index(snowy)] == 0, "no snow in the desert");
                if (season != WeatherClimate.Winter)
                    Check(WeatherClimate.Weights(Constants.CLIMATE_CONTINENTAL, season)[Index(snowy)] == 0, "continental snow only in winter");
            }
        }
        Check(WeatherClimate.Weights(Constants.CLIMATE_CONTINENTAL, WeatherClimate.Winter)[Index("snow")] > 0, "continental winter snows");
        Check(WeatherClimate.Weights(Constants.CLIMATE_SUBARCTIC, WeatherClimate.Winter)[Index("rain")] == 0, "subarctic winter precipitation is snow");
        Check(WeatherClimate.Weights(Constants.CLIMATE_DESERT, WeatherClimate.Summer)[Index("rain")] == 0, "desert summer is dry");
        Check(WeatherClimate.SeasonOf(Day(4, 1)) == WeatherClimate.Spring && WeatherClimate.SeasonOf(Day(3, 1)) == WeatherClimate.Spring, "march to may is spring");
        Check(WeatherClimate.SeasonOf(Day(7, 15)) == WeatherClimate.Summer && WeatherClimate.SeasonOf(Day(10, 1)) == WeatherClimate.Autumn, "summer and autumn");
        Check(WeatherClimate.SeasonOf(Day(12, 30)) == WeatherClimate.Winter && WeatherClimate.SeasonOf(Day(2, 30)) == WeatherClimate.Winter, "december to february is winter");
        Check(WeatherClimate.Normalize(99) == Constants.CLIMATE_TEMPERATE && WeatherClimate.Normalize(Constants.CLIMATE_DESERT) == Constants.CLIMATE_DESERT, "unknown climate is temperate");

        // 天気の移り変わり
        for (int i = 0; i < presetCount; i++)
        {
            Check(WeatherChain.Neighbors[i].Count > 0, "every weather has a neighbour");
            foreach (int next in WeatherChain.Neighbors[i]) Check(System.Linq.Enumerable.Contains(WeatherChain.Neighbors[next], i), "neighbours are mutual");
        }
        var random = new System.Random(412);
        foreach (var (climate, season) in new[] { (Constants.CLIMATE_TEMPERATE, WeatherClimate.Summer), (Constants.CLIMATE_CONTINENTAL, WeatherClimate.Winter), (Constants.CLIMATE_DESERT, WeatherClimate.Summer) })
        {
            var weights = WeatherClimate.Weights(climate, season);
            double[] time = new double[presetCount];
            double total = 0;
            int current = WeatherChain.Sample(random, weights);
            bool impossible = false;
            for (int step = 0; step < 400000; step++)
            {
                double dwell = WeatherChain.Dwell(random, current);
                impossible |= weights[current] <= 0;
                time[current] += dwell; total += dwell;
                current = WeatherChain.Step(random, current, weights);
            }
            Check(!impossible, "chain never enters an impossible weather");
            for (int i = 0; i < presetCount; i++)
                Check(Math.Abs(time[i] / total - weights[i] / 100.0) < 0.02, $"long-run share of {WeatherPreset.All[i].Id} matches climate {climate}");
        }
        var desertSummer = WeatherClimate.Weights(Constants.CLIMATE_DESERT, WeatherClimate.Summer);
        Check(desertSummer[WeatherChain.Step(random, Index("blizzard"), desertSummer)] > 0, "impossible weather leaves at once");
        int[] draws = new int[presetCount];
        var springWeights = WeatherClimate.Weights(Constants.CLIMATE_TEMPERATE, WeatherClimate.Spring);
        for (int i = 0; i < 100000; i++) draws[WeatherChain.Sample(random, springWeights)]++;
        for (int i = 0; i < presetCount; i++) Check(Mathf.Abs(draws[i] / 1000f - springWeights[i]) < 0.7f, "fresh draw frequency matches weight");

        // 時刻
        var clock = new WeatherClock();
        Check(clock.Current == WeatherClock.Stage.Day && Mathf.Approximately(clock.Phase, WeatherClock.MorningPhase), "clock starts in the morning");
        Check(Mathf.Approximately(WeatherClock.ElevationAt(0.5f), WeatherClock.MaxElevation) && WeatherClock.ElevationAt(0f) == 0f, "noon is highest, sunrise on the horizon");
        clock.Advance(WeatherClock.DaySeconds * (0.5f - WeatherClock.MorningPhase));
        Check(Mathf.Abs(clock.Azimuth - 180f) < 0.1f, "noon sun is due south");
        clock.Paused = true; clock.Advance(100f);
        Check(Mathf.Abs(clock.Phase - 0.5f) < 1e-4f, "paused clock holds the time");
        clock.Step(3f); Check(Mathf.Abs(clock.Hours - 15f) < 0.01f, "stepping moves by hours");
        clock.Paused = false;
        clock.Advance(WeatherClock.DaySeconds * 0.26f);
        Check(clock.Current == WeatherClock.Stage.Twilight, "sunset leads into twilight");
        float peak = 0;
        for (int i = 0; i < 40; i++) { clock.Advance(WeatherClock.TwilightSeconds / 40f); peak = Mathf.Max(peak, clock.Darkness); }
        clock.Advance(0.01f);
        Check(clock.Current == WeatherClock.Stage.Day && clock.Phase < 0.01f && Mathf.Abs(clock.Azimuth - 90f) < 1f, "twilight hands over to sunrise in the east");
        Check(peak > 0.5f && peak < 0.6f, "twilight dims without going dark");
        clock.BeginNight();
        Check(clock.Current == WeatherClock.Stage.Dusk, "epoch fast-forwards to dusk");
        clock.Advance(WeatherClock.SunsetSeconds);
        Check(Mathf.Abs(clock.Elevation - WeatherClock.LowestElevation) < 0.001f && clock.FadeAlpha == 0f, "sunset completes after 1.5 seconds before the screen fade starts");
        clock.Advance(WeatherClock.FadeSeconds * 0.5f);
        Check(clock.FadeAlpha > 0f && clock.FadeAlpha < 1f, "screen fades only after sunset");
        clock.Advance(WeatherClock.FadeSeconds * 0.5f);
        Check(clock.Current == WeatherClock.Stage.Night && clock.Darkness == 1f && clock.FadeAlpha == 1f, "full black after exactly two seconds");
        Check(!clock.Advance(30f) && clock.Current == WeatherClock.Stage.Night, "dark until the epoch completes");
        clock.RequestDawn();
        Check(clock.Advance(0.01f) && clock.Current == WeatherClock.Stage.Dawn, "dawn starts once and reports it");
        clock.Advance(WeatherClock.FadeSeconds);
        Check(clock.FadeAlpha == 0f && clock.Elevation == WeatherClock.LowestElevation, "return fades in for half a second before sunrise");
        clock.Advance(WeatherClock.SunsetSeconds);
        Check(clock.Current == WeatherClock.Stage.Day && Mathf.Approximately(clock.Phase, WeatherClock.MorningPhase) && clock.Darkness == 0f, "dawn ends in the morning");
        clock.BeginNight(); clock.RequestDawn();
        clock.Advance(WeatherClock.DuskSeconds + 0.1f);
        Check(clock.Current == WeatherClock.Stage.Night && clock.Advance(0.01f), "quick epoch waits for full darkness before dawn");
        clock.RequestDawn(); clock.StartMorning();
        Check(clock.Current == WeatherClock.Stage.Day && !clock.Advance(0.01f), "morning reset drops pending dawn");

        // WeatherController とIPC
        var ipc = new GameObject("IPC").AddComponent<IpcClient>();
        new GameObject("State").AddComponent<GameStateManager>();
        var sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        var originalSky = RenderSettings.skybox;
        var controller = new GameObject("Weather").AddComponent<WeatherController>();
        controller.TransitionSeconds = 0.3f;
        yield return Frames(4);
        Check(ipc.Publishes > 0 && ipc.Presets.Length == presetCount, "complete authoritative options");
        ApiClient.Epoch = 5; ApiClient.Climate = Constants.CLIMATE_DESERT; ApiClient.Date = Day(7, 15);
        ipc.Begin();
        yield return Until(() => controller.Climate == Constants.CLIMATE_DESERT, 5f, "session start reads the climate");
        Check(controller.Season == WeatherClimate.Summer, "season from the city date");
        Check(desertSummer[controller.CurrentIndex] > 0, "session starts with weather possible in the climate");
        Check(controller.Clock.Current == WeatherClock.Stage.Day && controller.Clock.Phase < WeatherClock.MorningPhase + 0.01f, "session starts in the morning");
        int reads = ApiClient.Reads;
        ipc.Reload();
        yield return Frames(4);
        Check(ApiClient.Reads > reads && !controller.Clock.IsDarkening, "editing reload reads the city without passing a night");

        ipc.PrepareEpoch("result-hold");
        ApiClient.Epoch = 6; ApiClient.Date = Day(1, 10);
        ipc.Reload();
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Night, 5f, "fast epoch still finishes sunset");
        yield return Frames(10);
        Check(controller.Clock.Current == WeatherClock.Stage.Night && controller.Clock.FadeAlpha == 1f, "backend completion does not reveal the city before closing the result");
        Check(ipc.DarkenedId == "result-hold" && ipc.DarkenedCount == 1, "black screen acknowledgement is sent once after applying the overlay");
        ipc.PrepareEpoch("result-hold");
        yield return Frames(4);
        Check(ipc.DarkenedCount == 1 && controller.Clock.Current == WeatherClock.Stage.Night, "duplicate prepare does not replay sunset");
        ipc.Executing(false);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "epoch end brings the morning");
        var desertWinter = WeatherClimate.Weights(Constants.CLIMATE_DESERT, WeatherClimate.Winter);
        Check(controller.Season == WeatherClimate.Winter && desertWinter[controller.CurrentIndex] > 0, "new day uses the new season");

        ipc.Executing(true);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Night, 5f, "rejected epoch darkens");
        ipc.Executing(false);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "rejected epoch returns to day");

        ApiClient.Epoch = 7; ipc.Reload();
        yield return Until(() => controller.Clock.IsDarkening, 5f, "epoch advance without the button still passes a night");
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "unannounced epoch ends in the morning");

        ApiClient.Fail = true;
        ipc.Executing(true);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Night, 5f, "offline epoch darkens");
        ipc.Executing(false);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "unreachable backend does not keep it dark");
        ApiClient.Fail = false;
        yield return Frames(4);

        ipc.SetWeather("blizzard");
        Check(ipc.WeatherId == "blizzard" && ipc.Snow == 3 && ipc.Rain == 0 && ipc.Locked, "manual weather acknowledged with snow level");
        ipc.Executing(true);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Night, 5f, "locked weather still passes the night");
        ApiClient.Epoch = 8; ipc.Reload(); ipc.Executing(false);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "locked weather sees the morning");
        Check(controller.CurrentIndex == Index("blizzard"), "photo lock survives epoch");
        ipc.SetWeather("not_a_weather");
        Check(ipc.WeatherId == "blizzard", "unknown weather rejected");
        ipc.SetWeather(null);
        Check(!controller.Locked && !ipc.Locked, "automatic selection releases lock");

        controller.TransitionSeconds = 2f;
        controller.SelectWeather(Index("clear"));
        controller.SelectWeather(Index("heavy_rain"), true, false);
        yield return null;
        Check(controller.RainAmount > 0f && controller.RainAmount < 3f, "weather changes gradually");
        yield return new WaitForSecondsRealtime(2.5f);
        Check(Mathf.Approximately(controller.RainAmount, 3f), "transition completes");

        ipc.ResetSession();
        ipc.Executing(true);
        yield return null;
        Check(!controller.Clock.IsDarkening, "no night effect outside a session");
        ApiClient.Epoch = 30; ApiClient.Climate = Constants.CLIMATE_TEMPERATE; ApiClient.Date = Day(4, 1);
        ipc.Begin();
        yield return Until(() => controller.Climate == Constants.CLIMATE_TEMPERATE, 5f, "next session reads its climate");
        controller.enabled = false;
        Check(RenderSettings.skybox == originalSky && Mathf.Approximately(sun.intensity,1.1f), "environment restored on disable");
        controller.enabled = true;
        Check(RenderSettings.skybox != originalSky, "environment restored on enable");

        var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(220,95,-230); camera.transform.LookAt(new Vector3(0,28,70));
        camera.clearFlags = CameraClearFlags.Skybox; camera.farClipPlane = 8000;
        camera.gameObject.AddComponent<AudioListener>();
        var ambience = new GameObject("Camera ambience").AddComponent<CameraAmbience>();
        Utils.GroundHeight = 85;
        yield return null;
        ipc.Volume(0.5f);
        yield return new WaitForSecondsRealtime(1.6f);
        var sounds = ambience.GetComponentsInChildren<AudioSource>();
        Check(sounds.Length == 5, "five Unity ambience channels");
        foreach (var sound in sounds)
        {
            Check(sound.clip != null && sound.clip.loadType == AudioClipLoadType.Streaming, "streaming clip imports");
            Check(sound.loop && sound.spatialBlend == 0f, "ambient bed is looping 2D audio");
        }
        Check(Mathf.Approximately(sounds[0].volume, 0.1f) && Mathf.Approximately(sounds[4].volume, 0.35f), "ten metres mixes near over base");
        Utils.GroundHeight = -5;
        yield return new WaitForSecondsRealtime(1.6f);
        Check(sounds[4].volume == 0 && Mathf.Approximately(sounds[0].volume, 0.25f), "one hundred metres is base only");
        Check(CameraAmbience.Proximity(55) == 0.5f, "fifty-five metres is halfway");
        Check(CameraAmbience.TargetGain(1, 0, 1f) == 0.4f && CameraAmbience.TargetGain(2, 0, 1f) == 0, "rain channels exclusive at whole levels");
        Check(Mathf.Approximately(CameraAmbience.TargetGain(1, 0, 1.5f), 0.2f) && Mathf.Approximately(CameraAmbience.TargetGain(2, 0, 1.5f), 0.3f), "rain channels crossfade between levels");
        Check(CameraAmbience.TargetGain(1, 0, 0f) == 0 && CameraAmbience.TargetGain(3, 0, 0f) == 0, "snow and dry weather are silent");
        Check(CameraAmbience.Proximity(float.NaN) == 0, "invalid height uses base");
        ipc.Volume(0);
        foreach (var sound in sounds) Check(sound.volume == 0, "mute immediate");
        ambience.enabled = false;
        foreach (var sound in sounds) Check(!sound.isPlaying, "disable stops sound");

        var construction = new GameObject("Construction ambience").AddComponent<ConstructionAmbience>();
        camera.transform.position = new Vector3(0, 60, 0);
        var road = new[] { new Vector3(-400, 0, 30), new Vector3(400, 0, 30) };
        Check(ConstructionAmbience.NearestPoint(road, new Vector3(0, 60, 0)) == new Vector3(0, 0, 30), "nearest point on polyline");
        Check(ConstructionAmbience.NearestPoint(road, new Vector3(900, 0, 0)) == new Vector3(400, 0, 30), "nearest point clamps to segment end");
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Path, 1, road);
        for (int i = 0; i < 10; i++) ConstructionAmbience.Register(ConstructionAmbience.Kind.Building, 100 + i, new[] { new Vector3(20 * i, 0, -20) });
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Building, 999, new[] { new Vector3(3000, 0, 0) });
        Check(ConstructionAmbience.SiteCount == 12, "construction sites registered");
        yield return null; // 初回Updateで音量IPCを購読してから音量を送る(本編は2秒ごとの再送で追いつく)
        ipc.Volume(0.5f);
        yield return new WaitForSecondsRealtime(1f);
        var voices = new System.Collections.Generic.List<AudioSource>();
        foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            if (source.gameObject.name.StartsWith("Construction voice")) voices.Add(source);
        Check(voices.Count == ConstructionAmbience.MaxVoices, "eight construction voices");
        Check(construction.PlayingCount == ConstructionAmbience.MaxVoices, "eight nearest sites play");
        bool roadHeard = false;
        foreach (var voice in voices)
        {
            Check(voice.clip != null && voice.clip.loadType == AudioClipLoadType.CompressedInMemory, "construction clip imports in memory");
            Check(voice.loop && voice.spatialBlend == 1f && voice.dopplerLevel == 0f, "construction voice is looping 3D audio without doppler");
            Check(voice.transform.position.x < 1000f, "far site not selected");
            Check(Mathf.Approximately(voice.volume, 0.5f * ConstructionAmbience.Gain), "construction voice faded in to settings volume times base gain");
            if (voice.clip.name == "construction-path") { roadHeard = true; Check(voice.transform.position == new Vector3(0, 0, 30), "road voice sits at nearest point"); }
        }
        Check(roadHeard, "road site plays with path clip");
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Path, 1, road);
        yield return null;
        foreach (var voice in voices) Check(voice.isPlaying && Mathf.Approximately(voice.volume, 0.5f * ConstructionAmbience.Gain), "re-registering a site keeps its voice playing");
        Check(ConstructionAmbience.SiteCount == 12, "re-registration does not duplicate sites");
        ipc.Volume(0);
        foreach (var voice in voices) Check(voice.volume == 0, "construction mute immediate");
        ipc.Volume(0.5f);
        ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Path, 1);
        yield return new WaitForSecondsRealtime(1f);
        foreach (var voice in voices) Check(!(voice.isPlaying && voice.clip.name == "construction-path"), "unregistered site stops after fade");
        Check(construction.PlayingCount == ConstructionAmbience.MaxVoices, "next nearest site fills the freed voice");
        ConstructionAmbience.Clear(ConstructionAmbience.Kind.Building);
        yield return new WaitForSecondsRealtime(1f);
        Check(ConstructionAmbience.SiteCount == 0 && construction.PlayingCount == 0, "cleared sites stop");
        construction.enabled = false;

        var vehicles = new GameObject("Vehicle ambience").AddComponent<VehicleAmbience>();
        Check(Mathf.Approximately(VehicleAmbience.CarPitch(0), 0.9f) && Mathf.Approximately(VehicleAmbience.CarPitch(30), 1.4f), "car pitch follows speed");
        Check(VehicleAmbience.TrainGain(0) == 0 && Mathf.Approximately(VehicleAmbience.TrainGain(10), 0.75f), "rolling sound is silent when stopped");
        yield return null;
        ipc.Volume(0.5f);
        float reportUntil = Time.unscaledTime + 1f;
        while (Time.unscaledTime < reportUntil)
        {
            for (int i = 0; i < 9; i++) VehicleAmbience.Report(VehicleAmbience.Kind.Car, i, new Vector3(10 * i, 0, 0), 20f);
            VehicleAmbience.Report(VehicleAmbience.Kind.Bus, 1, new Vector3(0, 0, 10), 0f);
            VehicleAmbience.Report(VehicleAmbience.Kind.Train, 1, new Vector3(0, 0, 100), 0f);
            VehicleAmbience.Report(VehicleAmbience.Kind.Train, 2, new Vector3(0, 0, 900), 10f);
            yield return null;
        }
        Check(VehicleAmbience.EmitterCount == 12, "vehicle emitters registered");
        var carVoices = new System.Collections.Generic.List<AudioSource>();
        var trainVoices = new System.Collections.Generic.List<AudioSource>();
        foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
        {
            if (source.gameObject.name.StartsWith("Vehicle voice car")) carVoices.Add(source);
            if (source.gameObject.name.StartsWith("Vehicle voice train")) trainVoices.Add(source);
        }
        Check(carVoices.Count == VehicleAmbience.CarVoices && trainVoices.Count == VehicleAmbience.TrainVoices, "vehicle voice pools");
        Check(vehicles.PlayingCount == VehicleAmbience.CarVoices + 1, "six nearest cars and the near train play");
        bool busHeard = false;
        foreach (var voice in carVoices)
        {
            Check(voice.isPlaying && voice.clip.name == "vehicle-car" && voice.clip.loadType == AudioClipLoadType.CompressedInMemory, "car clip imports in memory");
            Check(voice.loop && voice.spatialBlend == 1f && voice.dopplerLevel == 0f, "car voice is looping 3D audio without doppler");
            Check(voice.transform.position.x <= 50f, "farthest cars not selected");
            Check(Mathf.Approximately(voice.volume, 0.5f), "car voice faded in to settings volume");
            if (voice.transform.position == new Vector3(0, 0, 10)) { busHeard = true; Check(Mathf.Approximately(voice.pitch, 0.9f), "stopped bus idles at low pitch"); }
            else Check(Mathf.Approximately(voice.pitch, 1.4f), "moving car pitch");
        }
        Check(busHeard, "bus reports share the car clip");
        int trainsPlaying = 0;
        foreach (var voice in trainVoices)
        {
            if (!voice.isPlaying) continue;
            trainsPlaying++;
            Check(voice.clip.name == "vehicle-train" && voice.transform.position == new Vector3(0, 0, 100), "near train plays with train clip");
            Check(voice.volume == 0, "stopped train has no rolling sound");
        }
        Check(trainsPlaying == 1, "far train not selected");
        ipc.Volume(0);
        foreach (var voice in carVoices) Check(voice.volume == 0, "vehicle mute immediate");
        ipc.Volume(0.5f);
        yield return new WaitForSecondsRealtime(1.6f);
        Check(VehicleAmbience.EmitterCount == 0 && vehicles.PlayingCount == 0, "unreported vehicles drop after a second");
        VehicleAmbience.Report(VehicleAmbience.Kind.Train, 3, new Vector3(0, 0, 50), 10f);
        VehicleAmbience.Clear(VehicleAmbience.Kind.Train);
        Check(VehicleAmbience.EmitterCount == 0, "clear removes kind");
        vehicles.enabled = false;

        // 同じ視点・正午で全天気、快晴で日の出・夕方・薄明・暗転を撮る
        camera.transform.position = new Vector3(220,95,-230); camera.transform.LookAt(new Vector3(0,28,70));
        Utils.GroundHeight = 0;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        camera.targetTexture = new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
        MakeBox(new Vector3(0,-3,0), new Vector3(2400,6,2400), new Color(0.32f,0.38f,0.28f));
        MakeBox(new Vector3(0,0.03f,0), new Vector3(18,0.1f,650), new Color(0.2f,0.22f,0.23f));
        MakeBox(new Vector3(0,0.05f,0), new Vector3(650,0.1f,18), new Color(0.2f,0.22f,0.23f));
        var rng = new System.Random(47);
        for (int z=-3; z<7; z++) for (int x=-5; x<5; x++)
        {
            if (x == 0 || z == 0) continue;
            float h = 10 + (float)rng.NextDouble()*60;
            MakeBox(new Vector3(x*38,h/2,z*38), new Vector3(22,h,24), Color.Lerp(new Color(0.45f,0.48f,0.51f), new Color(0.88f,0.84f,0.75f),(float)rng.NextDouble()));
        }
        controller.Clock.StartMorning();
        controller.Clock.Step(4.5f);
        controller.Clock.Paused = true;
        Check(!UnityEditor.ShaderUtil.ShaderHasError(Resources.Load<Shader>("Weather/RainStreak")), "rain shader compilation");
        Check(!UnityEditor.ShaderUtil.ShaderHasError(Resources.Load<Shader>("Weather/Snowflake")), "snow shader compilation");
        for (int i=0; i<presetCount; i++)
        {
            controller.SelectWeather(i, true, true);
            Vector3 expectedSun = new Vector3(Mathf.Sin(controller.Clock.Azimuth * Mathf.Deg2Rad), 0, Mathf.Cos(controller.Clock.Azimuth * Mathf.Deg2Rad));
            Vector3 actualSun = Vector3.ProjectOnPlane(-sun.transform.forward, Vector3.up).normalized;
            Check(Vector3.Dot(expectedSun, actualSun) > 0.999f, "sun position matches the time of day");
            yield return Frames(16);
            var preset = WeatherPreset.All[i];
            if (preset.Rain > 0 || preset.Snow > 0)
            {
                var particles = controller.transform.Find(preset.Snow > 0 ? "Camera snow" : "Camera rain").GetComponent<ParticleSystem>();
                particles.Simulate(3f, true, true);
                Check(particles.particleCount > 0, "precipitation particles emitted");
                yield return null;
            }
            Check(!UnityEditor.ShaderUtil.ShaderHasError(RenderSettings.skybox.shader), "sky shader compilation");
            Capture(camera, $"{i:00}");
        }
        controller.SelectWeather(Index("clear"), true, true);
        controller.Clock.StartMorning();
        controller.Clock.Step(-1.4f);
        yield return Frames(8);
        Capture(camera, "t_sunrise");
        controller.Clock.Step(11f);
        yield return Frames(8);
        Check(sun.intensity > 0f && sun.intensity < WeatherPreset.All[Index("clear")].Intensity, "low evening sun is weaker");
        Capture(camera, "t_evening");
        controller.Clock.Paused = false;
        controller.Clock.Step(3f);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Twilight && controller.Clock.Darkness > 0.45f, 30f, "twilight reached");
        Check(sun.intensity < 0.05f, "sun below the horizon casts no light");
        Capture(camera, "t_twilight");
        ipc.Executing(true);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Night, 5f, "night for the capture");
        yield return Frames(4);
        Check(sun.intensity == 0f, "epoch night is dark");
        Capture(camera, "t_night");
        // オフスクリーン撮影にも黒幕を含め、照明の暗さではなく全画素が黒いことを検査する。
        var fadeCanvas = controller.GetComponentInChildren<Canvas>();
        Check(fadeCanvas.renderMode == RenderMode.ScreenSpaceOverlay && fadeCanvas.sortingOrder == short.MaxValue, "black overlay covers the entire game view");
        fadeCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        fadeCanvas.worldCamera = camera;
        fadeCanvas.planeDistance = 1f;
        Canvas.ForceUpdateCanvases();
        yield return Frames(4);
        Capture(camera, "t_blackout", true);
        fadeCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        ipc.Executing(false);
        yield return Until(() => controller.Clock.Current == WeatherClock.Stage.Day, 10f, "morning after the capture");
    }
    private void Capture(Camera camera, string name, bool requireBlack = false)
    {
        RenderTexture.active = camera.targetTexture;
        var pixels = new Texture2D(1280,720,TextureFormat.RGB24,false);
        pixels.ReadPixels(new Rect(0,0,1280,720),0,0); pixels.Apply();
        if (requireBlack)
        {
            bool black = true;
            foreach (var pixel in pixels.GetPixels32())
                if (pixel.r > 1 || pixel.g > 1 || pixel.b > 1) { black = false; break; }
            Check(black, "blackout covers every pixel, including sky and unlit surfaces");
        }
        File.WriteAllBytes(root + $"/Captures/{name}.png", pixels.EncodeToPNG());
        Destroy(pixels); RenderTexture.active = null;
    }
    private void MakeBox(Vector3 position, Vector3 scale, Color color)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.transform.position = position; box.transform.localScale = scale;
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.color = color; material.SetFloat("_Smoothness",0.25f);
        box.GetComponent<Renderer>().sharedMaterial = material;
    }
}
