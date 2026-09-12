using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class TerrainAppearanceQA
{
    public static void Run()
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        try
        {
            GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50, 25, 0);
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 220, 0);
            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            camera.orthographic = true;
            camera.orthographicSize = 90;
            camera.farClipPlane = 1000;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.magenta;
            camera.targetTexture = new RenderTexture(256, 256, 24);

            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(200, 100, 200) };
            var layer = new TerrainLayer { diffuseTexture = Texture2D.whiteTexture, tileSize = Vector2.one, smoothness = .15f };
            data.terrainLayers = new[] { layer };
            var terrainGO = Terrain.CreateTerrainGameObject(data);
            terrainGO.transform.position = new Vector3(-100, 20, -100);
            var terrain = terrainGO.GetComponent<Terrain>();
            terrain.basemapDistance = 10000;
            var near = new Material(Shader.Find("Custom/DepthOffsetTerrainLit"));
            terrain.materialTemplate = near;
            var farGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
            farGO.transform.position = new Vector3(0, 20, 0);
            farGO.transform.localScale = new Vector3(20, 1, 20);
            var far = new Material(Shader.Find("Custom/DepthOffsetSimpleLitFar"));
            farGO.GetComponent<Renderer>().sharedMaterial = far;
            far.SetFloat("_Smoothness", .15f);
            foreach (var material in new[] { near, far })
            {
                material.SetFloat("_ProceduralGroundBlend", .85f);
                material.SetFloat("_ProceduralGroundScale", 40);
                material.SetColor("_TerrainGroundColorA", new Color(.38f, .47f, .26f));
                material.SetColor("_TerrainGroundColorB", new Color(.55f, .48f, .34f));
                material.SetColor("_TerrainGroundColorC", new Color(.62f, .66f, .45f));
                material.SetFloat("_TerrainDetailNormalStrength", .25f);
                material.SetFloat("_TerrainDetailNormalScale", 1 / 3.5f);
            }
            var report = "";
            foreach (bool dark in new[] { false, true })
            {
                RenderSettings.fog = dark;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = .002f;
                RenderSettings.fogColor = new Color(.6f, .5f, .4f);
                foreach (var material in new[] { near, far })
                    material.SetColor("_TerrainTint", dark ? new Color(.5f, .5f, .5f) : Color.white);
                farGO.SetActive(false); terrainGO.SetActive(true);
                var a = Capture(camera, root + (dark ? "/near-dark.png" : "/near.png"));
                farGO.SetActive(true); terrainGO.SetActive(false);
                var b = Capture(camera, root + (dark ? "/far-dark.png" : "/far.png"));
                double error = a.Zip(b, (x, y) => (Math.Abs(x.r - y.r) + Math.Abs(x.g - y.g) + Math.Abs(x.b - y.b)) / 3.0).Average();
                report += $"dark={dark}: mean RGB error={error:F3}/255\n";
                if (error > 3) throw new Exception(report);
                if (b.All(x => x.r > 240 && x.b > 240 && x.g < 10)) throw new Exception("Error shader rendered");
            }
            foreach (var shader in new[] { near.shader, far.shader })
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity.ToString() == "Error") throw new Exception(shader.name + ": " + message.message);
            File.WriteAllText(root + "/result.txt", "PASS\n" + report);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/result.txt", "FAIL " + e);
            EditorApplication.Exit(1);
        }
    }

    static Color32[] Capture(Camera camera, string path)
    {
        camera.Render();
        RenderTexture.active = camera.targetTexture;
        var texture = new Texture2D(256, 256, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        var pixels = texture.GetPixels32();
        UnityEngine.Object.DestroyImmediate(texture);
        RenderTexture.active = null;
        return pixels;
    }
}
