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
            // Exclude floating-point error near the specular peak from the fog comparison, and verify the specular itself separately.
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
                // The close-up is before the fog start distance. Fog remains in the far view, and neither depends on the number of subdivisions.
                passed &= meshError < .005 && (distance == 300 ? fogChange < .002 : fogChange > .01);
            }
            sun.intensity = 4;
            RenderSettings.fog = false;
            camera.transform.rotation = Quaternion.Euler(45, 0, 0);
            camera.transform.position = -camera.transform.forward * 300;
            sea.GetComponent<MeshFilter>().sharedMesh = coarse;
            var sparkle = Capture(camera, root + "/sparkle.png");
            float sparklePeak = sparkle.Max(p => Mathf.Max(p.r, Mathf.Max(p.g, p.b)));
            report += $"sparklePeak={sparklePeak:F3}\n";
            passed &= sparklePeak > 1;
            // Sunlight spread by clouds comes from a wide area of the sky, so it does not produce a sharp specular highlight even at the same intensity.
            Shader.SetGlobalFloat("_SunScatterFraction", 1);
            var scattered = Capture(camera, root + "/sparkle-scattered.png");
            float scatteredPeak = scattered.Max(p => Mathf.Max(p.r, Mathf.Max(p.g, p.b)));
            Shader.SetGlobalFloat("_SunScatterFraction", 0);
            report += $"scatteredPeak={scatteredPeak:F3}\n";
            passed &= scatteredPeak < 1;

            // The top-down mode uses orthographic projection. Verify it has the same water depth as perspective projection and that the surface does not become transparent and disappear.
            sun.intensity = 0;
            material.SetFloat("_RippleStrength", 0);
            material.SetFloat("_RefractionStrength", 0);
            material.SetFloat("_FoamDistance", .01f);
            material.SetFloat("_EdgeFadeDistance", .01f);
            camera.transform.position = Vector3.up * 60;
            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            camera.orthographicSize = 60 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2);
            foreach (float depth in new[] { .2f, 3f, 30f })
            {
                floor.transform.position = Vector3.down * depth;
                camera.orthographic = false;
                var perspective = Capture(camera, root + $"/depth-{depth}-perspective.png");
                camera.orthographic = true;
                var orthographic = Capture(camera, root + $"/depth-{depth}-orthographic.png");
                sea.SetActive(false);
                var bareFloor = Capture(camera, root + $"/depth-{depth}-bare.png");
                sea.SetActive(true);
                double projectionError = Difference(perspective, orthographic);
                double waterChange = Difference(orthographic, bareFloor);
                passed &= projectionError < .004 && waterChange > .01;
                report += $"depth={depth}: projectionError={projectionError:F6} waterChange={waterChange:F6}\n";
            }

            // The sampled water-bottom color includes fog. Set the body color to black to isolate the water-bottom contribution,
            // and verify it is composed only once using the attenuation rate for each wavelength, without applying the fog to the surface a second time.
            floor.transform.position = Vector3.down * .2f;
            material.SetColor("_DeepColor", Color.black);
            RenderSettings.fog = true;
            RenderSettings.fogStartDistance = 0;
            RenderSettings.fogEndDistance = 120;
            Vector4 attenuation = material.GetVector("_Attenuation");
            material.SetVector("_Attenuation", Vector4.one * 1000);
            var opaqueFog = Capture(camera, root + "/transmission-opaque-fog.png");
            sea.SetActive(false);
            var bottomFog = Capture(camera, root + "/transmission-bottom-fog.png");
            sea.SetActive(true);
            material.SetVector("_Attenuation", attenuation);
            var transmittedFog = Capture(camera, root + "/transmission-fog.png");
            // In top-down orthographic projection, both the downward and upward distances equal the water depth, and Fresnel reflectance is 2% at normal incidence.
            Vector3 share = new Vector3(Mathf.Exp(-attenuation.x * .4f), Mathf.Exp(-attenuation.y * .4f), Mathf.Exp(-attenuation.z * .4f)) * .98f;
            var expectedFog = opaqueFog.Zip(bottomFog, (a, b) => new Color(
                Mathf.Lerp(a.r, b.r, share.x), Mathf.Lerp(a.g, b.g, share.y), Mathf.Lerp(a.b, b.b, share.z))).ToArray();
            double transmissionFogError = Difference(transmittedFog, expectedFog);
            passed &= transmissionFogError < .004;
            report += $"transmissionFogError={transmissionFogError:F6}\n";

            // The far-distance sea has no water-bottom depth. Like deep water, it is rendered with an invisible bottom,
            // and its color is verified to remain unchanged with or without a seabed tile in both perspective and orthographic projections.
            RenderSettings.fog = false;
            material.SetColor("_DeepColor", new Color(.02f, .09f, .16f));
            foreach (bool orthographic in new[] { false, true })
            {
                camera.orthographic = orthographic;
                floor.SetActive(false);
                var unloaded = Capture(camera, root + $"/unloaded-{orthographic}.png");
                floor.SetActive(true);
                floor.transform.position = Vector3.down * 30;
                var deep = Capture(camera, root + $"/deep-{orthographic}.png");
                double tileError = Difference(deep, unloaded);
                passed &= tileError < .004;
                report += $"orthographic={orthographic}: tileError={tileError:F6}\n";
            }
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
        // Compare the central area of the sea surface, excluding clipping at the screen edge and refraction UV boundaries.
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
