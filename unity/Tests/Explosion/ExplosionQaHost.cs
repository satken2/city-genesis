using System;
using System.Collections;
using System.IO;
using UnityEngine;

// 隔離プロジェクト専用。本編のIPCや都市の破壊操作へ接続しない。
public class IpcClient : MonoBehaviour
{
    public static IpcClient Instance => null;
    public event Action<float> OnAmbienceVolume;
}

public class ExplosionQaHost : MonoBehaviour
{
    private int checks;
    private string root;
    private Camera camera;
    private bool runtimeError;

    private void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }

    private IEnumerator Start()
    {
        root = Path.GetDirectoryName(Application.dataPath);
        Application.logMessageReceived += (message, trace, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception) runtimeError = true;
        };
        var suite = Suite();
        while (true)
        {
            object next;
            try { if (!suite.MoveNext()) break; next = suite.Current; }
            catch (Exception e)
            {
                File.WriteAllText(root + "/result.txt", "FAIL " + e);
                UnityEditor.EditorApplication.Exit(1);
                yield break;
            }
            yield return next;
        }
        File.WriteAllText(root + "/result.txt", $"PASS {checks} checks; captures in Captures");
        UnityEditor.EditorApplication.Exit(0);
    }

    private IEnumerator Suite()
    {
        camera = new GameObject("Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(34, 30, -40);
        camera.transform.LookAt(new Vector3(0, 4, 0));
        camera.backgroundColor = new Color(0.35f, 0.45f, 0.56f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.gameObject.AddComponent<AudioListener>();
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.transform.rotation = Quaternion.Euler(45, -30, 0);
        sun.intensity = 1.5f;
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.localScale = Vector3.one * 10;
        var surface = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        surface.color = new Color(0.26f, 0.3f, 0.23f);
        floor.GetComponent<Renderer>().sharedMaterial = surface;
        var rubble = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rubble.transform.position = new Vector3(0, 0.6f, 0);
        rubble.transform.localScale = new Vector3(12, 1.2f, 12);
        var concrete = new Material(surface); concrete.color = new Color(0.38f, 0.36f, 0.34f);
        rubble.GetComponent<Renderer>().sharedMaterial = concrete;

        var manager = new GameObject("Effects").AddComponent<VisualEffectManager>();
        manager.SetVolume(0.5f);
        var clip = Resources.Load<AudioClip>("Audio/Effects/building-explosion");
        Check(clip != null && clip.channels == 1 && Mathf.Abs(clip.length - 4.054694f) < 0.01f, "mono audio duration");
        Check(clip.preloadAudioData && clip.loadType == AudioClipLoadType.DecompressOnLoad, "preloaded single shot");
        // 初回シェーダーコンパイルの停止時間を、短い炎の寿命計測へ混ぜない。
        var warmup = FireEffectFactory.CreateExplosion(8f, manager.transform);
        foreach (var ps in warmup.GetComponentsInChildren<ParticleSystem>()) ps.Simulate(0.12f, false, true);
        Capture("00-warmup");
        manager.ClearTransientEffects();
        yield return new WaitForSecondsRealtime(1f);
        // エポック待機などでtimeScaleが0でも、単発演出は消滅まで進む。
        Time.timeScale = 0f;
        manager.PlayExplosion(new Vector3(0, 0.5f, 0), 8f);
        yield return new WaitForSecondsRealtime(0.12f);
        var particles = manager.GetComponentsInChildren<ParticleSystem>();
        Check(particles.Length == 2, "flames and smoke");
        foreach (var ps in particles)
        {
            Check(!ps.main.loop && !ps.main.prewarm && ps.main.useUnscaledTime, "finite unscaled burst");
            Check(ps.particleCount > 0 && ps.particleCount <= 45, $"bounded burst count: {ps.name} count={ps.particleCount} time={ps.time}");
            Check(ps.GetComponent<Renderer>().sharedMaterial.shader.name == "Custom/FireParticle", "shared production shader");
        }
        Capture("01-flames");
        var source = manager.GetComponentInChildren<AudioSource>();
        Check(source != null && !source.loop && source.spatialBlend == 1f, "single 3D source");
        Check(Mathf.Abs(source.volume - 0.4f) < 0.001f, "master times sfx gain");
        manager.SetVolume(0f);
        Check(source.volume == 0f, "mute applies during playback");
        manager.SetVolume(0.5f);
        yield return new WaitForSecondsRealtime(0.85f);
        Check(particles[0].particleCount == 0 && particles[1].particleCount > 0, "flames end before smoke");
        Capture("02-smoke");
        yield return new WaitForSecondsRealtime(2.5f);
        Capture("03-clear");
        yield return new WaitForSecondsRealtime(1f);
        Check(manager.transform.childCount == 0, "particles and audio cleaned up while paused");
        manager.PlayExplosion(Vector3.zero, 35f);
        manager.ClearTransientEffects();
        yield return null;
        Check(manager.transform.childCount == 0, "session reset removes transient effects");
        Check(!runtimeError, "no Unity runtime errors");
        Time.timeScale = 1f;
    }

    private void Capture(string name)
    {
        var target = RenderTexture.GetTemporary(960, 640, 24);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        var image = new Texture2D(960, 640, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
        image.Apply();
        File.WriteAllBytes(root + "/Captures/" + name + ".png", image.EncodeToPNG());
        RenderTexture.active = null;
        camera.targetTexture = null;
        RenderTexture.ReleaseTemporary(target);
        Destroy(image);
    }
}
