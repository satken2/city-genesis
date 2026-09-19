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
    public int Publishes;
    public string WeatherId;
    public int Rain;
    public bool Locked;
    public object[] Presets;
    public event Action<float> OnAmbienceVolume;
    public void Volume(float value) => OnAmbienceVolume?.Invoke(value);
    public void EmitWeatherState(string id, bool locked, int rain, object[] presets)
    { Publishes++; WeatherId = id; Locked = locked; Rain = rain; Presets = presets; }
    public void QueryWeather() => OnWeatherQuery?.Invoke();
    public void SetWeather(string id) => OnWeatherSet?.Invoke(id);
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
    public static int Epoch, Reads;
    public static bool Fail;
    public static IEnumerator Get<T>(string path, Action<T> success, Action<string> error)
    {
        Reads++;
        yield return null;
        if (Fail) { error("test failure"); yield break; }
        success(Newtonsoft.Json.JsonConvert.DeserializeObject<T>("{\"city\":{\"epoch\":" + Epoch + "}}"));
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
    private IEnumerator Guard(IEnumerator suite)
    {
        while (true)
        {
            object value;
            try { if (!suite.MoveNext()) break; value = suite.Current; }
            catch (Exception e)
            {
                File.WriteAllText(root + "/result.txt", "FAIL " + e);
                Debug.LogException(e); UnityEditor.EditorApplication.Exit(1); yield break;
            }
            yield return value;
        }
        File.WriteAllText(root + "/result.txt", $"PASS {checks} checks; {WeatherPreset.All.Count} weather captures");
        UnityEditor.EditorApplication.Exit(0);
    }
    private IEnumerator Suite()
    {
        var tracker = new WeatherEpochTracker();
        Check(tracker.Observe(5) == 0, "initial epoch is baseline");
        Check(tracker.Observe(5) == 0, "duplicate reload");
        Check(tracker.Observe(8) == 3, "batch epochs");
        Check(tracker.Observe(2) == 0, "older save baseline");
        tracker.Reset(); Check(tracker.Observe(20) == 0, "session reset");
        var random = new System.Random(412);
        int[] counts = new int[WeatherPreset.All.Count];
        for (int i=0; i<100000; i++)
        {
            int next = WeatherEpochTracker.Next(random);
            Check(next >= 0 && next < counts.Length, "valid choice");
            counts[next]++;
        }
        for (int i=0; i<counts.Length; i++)
            Check(Mathf.Abs(counts[i] / 1000f - WeatherPreset.All[i].Weight) < 0.7f, "observed frequency matches weight");
        var ipc = new GameObject("IPC").AddComponent<IpcClient>();
        new GameObject("State").AddComponent<GameStateManager>();
        var sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        var originalSky = RenderSettings.skybox;
        var controller = new GameObject("Weather").AddComponent<WeatherController>();
        controller.TransitionSeconds = 0.1f;
        yield return null; yield return null; yield return null; yield return null;
        int initial = controller.CurrentIndex;
        ipc.Reload();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex == initial, "editing reload preserves weather");
        int before = ipc.Publishes;
        ApiClient.Epoch = 1; ipc.Reload();
        yield return null; yield return null; yield return null;
        Check(ipc.Publishes > before, "epoch draws and publishes weather, including consecutive same weather");
        controller.SelectWeather(8);
        ApiClient.Epoch = 2; ipc.Reload();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex == 8 && controller.Locked, "photo lock survives epoch");
        ipc.ResetSession(); ApiClient.Epoch = 30; ipc.Begin();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex == 8, "new session only establishes baseline");
        ipc.SetWeather("heavy_rain");
        Check(ipc.WeatherId == "heavy_rain" && ipc.Rain == 3 && ipc.Locked, "manual weather acknowledged with rain level");
        ipc.SetWeather("not_a_weather");
        Check(ipc.WeatherId == "heavy_rain", "unknown weather rejected");
        ipc.SetWeather(null);
        Check(!controller.Locked && !ipc.Locked, "automatic selection releases lock");
        ipc.QueryWeather();
        Check(ipc.Presets.Length == WeatherPreset.All.Count, "complete authoritative options");
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
        Check(CameraAmbience.TargetGain(1, 0, 1) == 0.4f && CameraAmbience.TargetGain(2, 0, 1) == 0, "rain channels exclusive");
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
            Check(Mathf.Approximately(voice.volume, 0.5f), "construction voice faded in to settings volume");
            if (voice.clip.name == "construction-path") { roadHeard = true; Check(voice.transform.position == new Vector3(0, 0, 30), "road voice sits at nearest point"); }
        }
        Check(roadHeard, "road site plays with path clip");
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Path, 1, road);
        yield return null;
        foreach (var voice in voices) Check(voice.isPlaying && Mathf.Approximately(voice.volume, 0.5f), "re-registering a site keeps its voice playing");
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
        for (int i=0; i<WeatherPreset.All.Count; i++)
        {
            controller.SelectWeather(i, true, true);
            Vector3 expectedSun = new Vector3(Mathf.Sin(WeatherPreset.All[i].Azimuth * Mathf.Deg2Rad), 0,
                Mathf.Cos(WeatherPreset.All[i].Azimuth * Mathf.Deg2Rad));
            Vector3 actualSun = Vector3.ProjectOnPlane(-sun.transform.forward, Vector3.up).normalized;
            Check(Vector3.Dot(expectedSun, actualSun) > 0.999f, "sun position matches preset compass bearing");
            for (int frame = 0; frame < 16; frame++) yield return null;
            if (WeatherPreset.All[i].Rain > 0)
            {
                var particles = controller.GetComponentInChildren<ParticleSystem>();
                particles.Simulate(3f, true, true);
                Check(particles.particleCount > 0, "rain particles emitted");
                yield return null;
            }
            Check(!UnityEditor.ShaderUtil.ShaderHasError(RenderSettings.skybox.shader), "sky shader compilation");
            Check(!UnityEditor.ShaderUtil.ShaderHasError(Resources.Load<Shader>("Weather/RainStreak")), "rain shader compilation");
            RenderTexture.active = camera.targetTexture;
            var pixels = new Texture2D(1280,720,TextureFormat.RGB24,false);
            pixels.ReadPixels(new Rect(0,0,1280,720),0,0); pixels.Apply();
            File.WriteAllBytes(root + $"/Captures/{i:00}.png", pixels.EncodeToPNG());
            Destroy(pixels); RenderTexture.active = null;
        }
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
