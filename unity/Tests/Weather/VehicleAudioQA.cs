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
    private IEnumerator Start()
    {
        new GameObject("Game state").AddComponent<GameStateManager>();
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.gameObject.AddComponent<AudioListener>();
        var ambience = new GameObject("Ambience").AddComponent<VehicleAmbience>();
        ambience.SetVolume(0.5f);
        foreach (string name in new[] { "vehicle-train", "vehicle-train-slow", "vehicle-train-fast", "vehicle-train-motor" })
        {
            var clip = Resources.Load<AudioClip>("Audio/Vehicles/" + name);
            Check(clip != null && clip.length > 2f && clip.channels == 1, "mono clip import: " + name);
            Check(clip.loadType == AudioClipLoadType.CompressedInMemory, "compressed clip: " + name);
        }
        AudioSource rolling = null, motor = null;
        var rollingLayers = new System.Collections.Generic.List<AudioSource>();
        for (int phase = 0; phase < 3; phase++)
        {
            float speed = phase == 1 ? 30f : 0f;
            float until = Time.unscaledTime + 2f;
            while (Time.unscaledTime < until)
            {
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 1, new Vector3(0, 0, 30), speed);
                VehicleAmbience.Report(VehicleAmbience.Kind.Train, 2, new Vector3(0, 0, 900), speed);
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
        VehicleAmbience.Report(VehicleAmbience.Kind.Train, 3, Vector3.zero, 10f);
        yield return new WaitForSecondsRealtime(0.4f);
        ambience.enabled = false;
        foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            Check(!source.isPlaying, "disable stops every source");
        File.WriteAllText("result.txt", "PASS: " + checks + " checks");
        EditorApplication.Exit(0);
    }
}
