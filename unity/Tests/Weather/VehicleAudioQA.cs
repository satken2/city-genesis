using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

// 音声だけの隔離プロジェクトで使う。本編の通信・都市データには接続しない。
public sealed class IpcClient : MonoBehaviour
{
    public static IpcClient Instance => null;
    public event Action<float> OnAmbienceVolume;
}
public sealed class GameStateManager : MonoBehaviour
{
    public static GameStateManager Instance { get; private set; }
    private void Awake() => Instance = this;
    public bool IsGameStarted() => true;
}

[InitializeOnLoad]
public static class VehicleAudioQA
{
    static VehicleAudioQA()
    {
        if (SessionState.GetBool("VehicleAudioQA", false)) EditorApplication.delayCall += Start;
    }
    public static void Run()
    {
        SessionState.SetBool("VehicleAudioQA", true);
        EditorApplication.EnterPlaymode();
    }
    private static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("VehicleAudioQA", false);
        new GameObject("Audio QA").AddComponent<VehicleAudioQaHost>();
    }
}

public sealed class VehicleAudioQaHost : MonoBehaviour
{
    private int checks;
    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            File.WriteAllText("result.txt", "FAIL: " + message);
            EditorApplication.Exit(1);
            throw new Exception(message);
        }
        checks++;
    }

    private void CheckDistanceResponse(AudioSource source)
    {
        var lowPass = source.GetComponent<AudioLowPassFilter>();
        Check(source.rolloffMode == AudioRolloffMode.Custom && lowPass != null && lowPass.enabled,
            "every vehicle layer has distance attenuation and a low-pass filter: " + source.name);
        Check(!source.bypassEffects && lowPass.lowpassResonanceQ == 1f, "distance filter is audible without resonance boost");
        var gain = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
        var cutoff = lowPass.customCutoffCurve;
        float previousGain = 1f, previousCutoff = 22000f;
        for (int distance = 0; distance <= (int)source.maxDistance; distance++)
        {
            float t = distance / source.maxDistance;
            float g = gain.Evaluate(t), hz = cutoff.Evaluate(t);
            Check(g >= 0f && g <= previousGain + 0.0001f, "moving away never increases volume");
            Check(hz >= 2000f && hz <= previousCutoff + 1f, "moving away softens highs without excessive muffling");
            previousGain = g;
            previousCutoff = hz;
        }
        Check(gain.Evaluate(source.minDistance / source.maxDistance) > 0.99f && cutoff.Evaluate(0f) > 20000f,
            "nearby vehicles retain volume and clarity");
        Check(gain.Evaluate(1f) == 0f && gain.Evaluate(1.1f) == 0f, "audible range ends in silence");
        if (source.maxDistance == VehicleAmbience.TrainMaxDistance)
        {
            float distantGain = gain.Evaluate(100f / source.maxDistance);
            Check(distantGain >= 0.35f && distantGain <= 0.5f, "100m train remains audible with clear distance attenuation");
            Check(gain.Evaluate(300f / source.maxDistance) >= 0.03f, "300m train retains a distant rumble");
            float hz = cutoff.Evaluate(100f / source.maxDistance);
            Check(hz >= 4000f && hz <= 8000f, "100m train loses sharp highs but retains body");
        }
        else Check(gain.Evaluate(100f / source.maxDistance) < 0.15f, "cars retain strong distance attenuation");
    }

    private IEnumerator Start()
    {
        new GameObject("Game state").AddComponent<GameStateManager>();
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.gameObject.AddComponent<AudioListener>();
        var ambience = new GameObject("Ambience").AddComponent<VehicleAmbience>();
        ambience.SetVolume(0.5f);
        foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) CheckDistanceResponse(source);
        foreach (string name in new[] { "vehicle-train", "vehicle-train-slow", "vehicle-train-fast", "vehicle-train-motor" })
        {
            var clip = Resources.Load<AudioClip>("Audio/Vehicles/" + name);
            Check(clip != null && clip.length > 2f && clip.channels == 1, "mono clip import: " + name);
            Check(clip.loadType == AudioClipLoadType.CompressedInMemory, "compressed clip: " + name);
        }
        var train = new VehicleAmbience.TrainSound { wheel = "vehicle-train", motor = "vehicle-train-motor" };
        var motorOnly = new VehicleAmbience.TrainSound { wheel = "", motor = "vehicle-train-motor" };
        AudioSource rolling = null, motor = null;
        var rollingLayers = new System.Collections.Generic.List<AudioSource>();
        for (int phase = 0; phase < 3; phase++)
        {
            float speed = phase == 1 ? 30f : 0f;
            float until = Time.unscaledTime + 2f;
            while (Time.unscaledTime < until)
            {
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 1, new Vector3(0, 0, 30), speed, train);
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 2, new Vector3(0, 0, 900), speed, train);
                yield return null;
            }
            int playing = 0;
            rollingLayers.Clear();
            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            {
                if (!source.isPlaying) continue;
                playing++;
                if (source.clip.name == "vehicle-train") rolling = source;
                if (source.clip.name == "vehicle-train-motor") motor = source;
                else rollingLayers.Add(source);
                Check(source.transform.position == new Vector3(0, 0, 30), "only nearby train plays");
                Check(source.spatialBlend == 1 && source.loop && source.dopplerLevel == 0, "3D loop setup");
            }
            Check(playing == 4 && rolling != null && motor != null, "three tempo loops and motor playing");
            foreach (var layer in rollingLayers) Check(layer.pitch == 1f, "rolling pitch remains unchanged at every speed");
            Check(Mathf.Abs(motor.pitch - (phase == 1 ? 2f : 0.8f)) < 0.01f, "motor playback rate follows acceleration and stop");
            float rollingVolume = 0;
            foreach (var layer in rollingLayers)
            {
                rollingVolume += layer.volume;
                if (phase == 1 && layer.clip.name == "vehicle-train-fast")
                    Check(Mathf.Abs(layer.volume - 0.375f) < 0.01f, "fast tempo selected at speed");
            }
            Check(Mathf.Abs(rollingVolume - (phase == 1 ? 0.375f : 0)) < 0.01f, "rolling mix stops at rest");
            Check(Mathf.Abs(motor.volume - (phase == 1 ? 0.275f : 0.09f)) < 0.01f, "motor idles at rest");
        }
        ambience.SetVolume(0);
        foreach (var layer in rollingLayers) Check(layer.volume == 0, "all rolling loops mute immediately");
        Check(motor.volume == 0, "motor mutes immediately");
        ambience.SetVolume(0.5f);
        yield return new WaitForSecondsRealtime(1.7f);
        foreach (var layer in rollingLayers) Check(!layer.isPlaying, "stale train stops every rolling loop");
        Check(!motor.isPlaying, "stale train stops motor");
        // 車輪音が空の音の組(モノレール)はモーター音だけを鳴らし、同じ編成の音の組を変えると鳴る音も入れ替わる。
        foreach (var sound in new[] { motorOnly, train })
        {
            float soundUntil = Time.unscaledTime + 1f;
            while (Time.unscaledTime < soundUntil)
            {
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 4, new Vector3(0, 0, 30), 30f, sound);
                yield return null;
            }
            int layers = 0;
            bool motorPlaying = false;
            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            {
                if (!source.isPlaying) continue;
                layers++;
                motorPlaying |= source.clip.name == "vehicle-train-motor";
            }
            Check(motorPlaying && layers == (sound == motorOnly ? 1 : 4), "train layers follow the reported sound set");
            Check(ambience.PlayingCount == 1, "a motor-only train counts as playing");
        }
        yield return new WaitForSecondsRealtime(1.7f);
        // 音源プールを遠景で再使用し、リスナーの移動で範囲外・近景へ切り替える。
        foreach (float cameraZ in new[] { -100f, -VehicleAmbience.TrainMaxDistance, 190f })
        {
            camera.transform.position = new Vector3(0f, 0f, cameraZ);
            float until = Time.unscaledTime + 0.9f;
            while (Time.unscaledTime < until)
            {
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 3, new Vector3(0f, 0f, 200f), 15f, train);
                yield return null;
            }
            Check(ambience.PlayingCount == (cameraZ == -VehicleAmbience.TrainMaxDistance ? 0 : 1), "listener movement updates audible train selection");
            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                if (source.isPlaying) CheckDistanceResponse(source);
        }
        camera.transform.position = Vector3.zero;
        VehicleAmbience.Report(VehicleAmbience.Kind.Train, 3, Vector3.zero, 10f, train);
        yield return new WaitForSecondsRealtime(0.4f);
        ambience.enabled = false;
        foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            Check(!source.isPlaying, "disable stops every source");
        // 主音源を先に破棄する終了順序を再現し、残った鉄道レイヤーも停止することを確認する。
        ambience.enabled = true;
        var construction = new GameObject("Construction ambience").AddComponent<ConstructionAmbience>();
        construction.SetVolume(0.5f);
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Building, 1, new[] { Vector3.zero });
        float shutdownTestUntil = Time.unscaledTime + 0.7f;
        while (Time.unscaledTime < shutdownTestUntil)
        {
            VehicleAmbience.Report(VehicleAmbience.Kind.Train, 3, Vector3.zero, 10f, train);
            yield return null;
        }
        Check(ambience.PlayingCount > 0 && construction.PlayingCount > 0, "shutdown starts with active vehicle and construction sounds");
        var shutdownExceptions = new System.Collections.Generic.List<string>();
        Application.LogCallback captureException = (message, trace, type) =>
        {
            if (type == LogType.Exception) shutdownExceptions.Add(message);
        };
        Application.logMessageReceived += captureException;
        try
        {
            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                if (source.name.StartsWith("Vehicle voice ") || source.name.StartsWith("Construction voice "))
                    DestroyImmediate(source.gameObject);
            construction.enabled = false;
            ambience.enabled = false;
            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                Check(!source.isPlaying && source.volume == 0f, "surviving train layers stop after primary sources are destroyed");
            DestroyImmediate(construction.gameObject);
            DestroyImmediate(ambience.gameObject);
            Check(shutdownExceptions.Count == 0, "disable and destroy tolerate already destroyed AudioSources");
        }
        finally
        {
            Application.logMessageReceived -= captureException;
            ConstructionAmbience.Clear(ConstructionAmbience.Kind.Building);
            VehicleAmbience.Clear(VehicleAmbience.Kind.Train);
        }
        File.WriteAllText("result.txt", "PASS: " + checks + " checks");
        EditorApplication.Exit(0);
    }
}
