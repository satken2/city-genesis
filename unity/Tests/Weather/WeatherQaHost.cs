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
        File.WriteAllText(root + "/result.txt", $"PASS {checks} checks; 10 weather captures");
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
        int[] counts = new int[10];
        int current = 0;
        for (int i=0; i<9000; i++)
        {
            int next = WeatherEpochTracker.Next(random, current, 10);
            Check(next != current && next >= 0 && next < 10, "nonrepeating valid choice");
            counts[next]++; current = next;
        }
        foreach (int count in counts) Check(count > 600 && count < 1200, "all presets reachable");
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
        ApiClient.Epoch = 1; ipc.Reload();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex != initial, "epoch advances weather");
        controller.SelectWeather(8);
        ApiClient.Epoch = 2; ipc.Reload();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex == 8 && controller.Locked, "photo lock survives epoch");
        ipc.ResetSession(); ApiClient.Epoch = 30; ipc.Begin();
        yield return null; yield return null; yield return null;
        Check(controller.CurrentIndex == 8, "new session only establishes baseline");
        controller.enabled = false;
        Check(RenderSettings.skybox == originalSky && Mathf.Approximately(sun.intensity,1.1f), "environment restored on disable");
        controller.enabled = true;
        Check(RenderSettings.skybox != originalSky, "environment restored on enable");

        var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(220,95,-230); camera.transform.LookAt(new Vector3(0,28,70));
        camera.clearFlags = CameraClearFlags.Skybox; camera.farClipPlane = 8000;
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
        for (int i=0; i<10; i++)
        {
            controller.SelectWeather(i, true, true);
            Vector3 expectedSun = new Vector3(Mathf.Sin(WeatherPreset.All[i].Azimuth * Mathf.Deg2Rad), 0,
                Mathf.Cos(WeatherPreset.All[i].Azimuth * Mathf.Deg2Rad));
            Vector3 actualSun = Vector3.ProjectOnPlane(-sun.transform.forward, Vector3.up).normalized;
            Check(Vector3.Dot(expectedSun, actualSun) > 0.999f, "sun position matches preset compass bearing");
            for (int frame = 0; frame < 16; frame++) yield return null;
            Check(!UnityEditor.ShaderUtil.ShaderHasError(RenderSettings.skybox.shader), "sky shader compilation");
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
