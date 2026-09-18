using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class WaterFogQA
{
    public static void Run()
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        string report = "";
        bool passed = true;
        try
        {
            GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            RenderSettings.reflectionIntensity = 0;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 1000;
            RenderSettings.fogEndDistance = 6500;
            RenderSettings.fogColor = new Color(.65f, .75f, .8f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            // フォグ比較では光沢ピーク付近の浮動小数点誤差を除外し、光沢自体は別に検証する。
            sun.intensity = 0;
            sun.transform.rotation = Quaternion.Euler(45, 180, 0);
            RenderSettings.sun = sun;
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture = true;
            camera.GetUniversalAdditionalCameraData().requiresColorTexture = true;
            camera.fieldOfView = 30;
            camera.farClipPlane = 50000;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGBHalf);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = Vector3.down * 30;
            floor.transform.localScale = Vector3.one * 4000;
            floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var sea = new GameObject("Sea", typeof(MeshFilter), typeof(MeshRenderer));
            var material = new Material(Shader.Find("Custom/WaterLit"));
            material.SetFloat("_RippleSpeed", 0);
            sea.GetComponent<Renderer>().sharedMaterial = material;
            var coarse = WaterGrid(1);
            var dense = WaterGrid(128);
            foreach (int distance in new[] { 300, 3000 })
            foreach (int yaw in new[] { 0, 90, 180, 270 })
            {
                camera.transform.rotation = Quaternion.Euler(45, yaw, 0);
                camera.transform.position = -camera.transform.forward * distance;
                sea.GetComponent<MeshFilter>().sharedMesh = coarse;
                RenderSettings.fog = false;
                var clear = Capture(camera, root + $"/clear-{distance}-{yaw}.png");
                RenderSettings.fog = true;
                var fog = Capture(camera, root + $"/fog-{distance}-{yaw}.png");
                sea.GetComponent<MeshFilter>().sharedMesh = dense;
                var reference = Capture(camera, root + $"/dense-{distance}-{yaw}.png");
                double meshError = Difference(fog, reference);
                double fogChange = Difference(fog, clear);
                report += $"distance={distance} yaw={yaw}: meshError={meshError:F6} fogChange={fogChange:F6}\n";
                // 近景は霧の開始距離より手前。遠景には霧が残り、どちらも分割数に依存しない。
                passed &= meshError < .005 && (distance == 300 ? fogChange < .002 : fogChange > .01);
            }
            sun.intensity = 2;
            RenderSettings.fog = false;
            camera.transform.rotation = Quaternion.Euler(45, 0, 0);
            camera.transform.position = -camera.transform.forward * 300;
            sea.GetComponent<MeshFilter>().sharedMesh = coarse;
            var sparkle = Capture(camera, root + "/sparkle.png");
            float sparklePeak = sparkle.Max(p => Mathf.Max(p.r, Mathf.Max(p.g, p.b)));
            report += $"sparklePeak={sparklePeak:F3}\n";
            passed &= sparklePeak > 1;
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity.ToString() == "Error") throw new Exception(message.message);
            File.WriteAllText(root + "/result.txt", (passed ? "PASS\n" : "FAIL\n") + report);
            EditorApplication.Exit(passed ? 0 : 1);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/result.txt", "FAIL\n" + report + e);
            EditorApplication.Exit(1);
        }
    }

    static Mesh WaterGrid(int divisions)
    {
        var vertices = new Vector3[(divisions + 1) * (divisions + 1)];
        var triangles = new int[divisions * divisions * 6];
        int t = 0;
        for (int z = 0; z <= divisions; z++)
        for (int x = 0; x <= divisions; x++)
        {
            int i = z * (divisions + 1) + x;
            vertices[i] = new Vector3(-10000 + 20000f * x / divisions, 0, -6000 + 20000f * z / divisions);
            if (x == divisions || z == divisions) continue;
            triangles[t++] = i; triangles[t++] = i + divisions + 1; triangles[t++] = i + 1;
            triangles[t++] = i + 1; triangles[t++] = i + divisions + 1; triangles[t++] = i + divisions + 2;
        }
        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateBounds();
        return mesh;
    }

    static Color[] Capture(Camera camera, string path)
    {
        camera.Render();
        RenderTexture.active = camera.targetTexture;
        var texture = new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true);
        texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); texture.Apply();
        var pixels = texture.GetPixels();
        var png = new Texture2D(256, 256, TextureFormat.RGB24, false);
        png.SetPixels(pixels.Select(p => p.gamma).ToArray()); png.Apply();
        File.WriteAllBytes(path, png.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        UnityEngine.Object.DestroyImmediate(png);
        RenderTexture.active = null;
        return pixels;
    }

    static double Difference(Color[] a, Color[] b)
    {
        // 海面の中央領域で比較し、画面端のクリップや屈折UV境界を除外する。
        double sum = 0;
        for (int y = 96; y < 160; y++)
        for (int x = 96; x < 160; x++)
        {
            int i = y * 256 + x;
            sum += (Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b)) / 3;
        }
        return sum / (64 * 64);
    }
}
