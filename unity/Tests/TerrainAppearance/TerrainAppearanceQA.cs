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
            report += CheckLoadedBoundary(camera, terrainGO, farGO, far, root);
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

    static string CheckLoadedBoundary(Camera camera, GameObject terrainGO, GameObject farGO, Material far, string root)
    {
        RenderSettings.fog = false;
        far.SetColor("_TerrainTint", Color.white);
        terrainGO.GetComponent<Terrain>().materialTemplate.SetColor("_TerrainTint", Color.white);
        terrainGO.transform.position = new Vector3(0, 20, 0);
        camera.transform.position = new Vector3(100, 400, 100);
        camera.orthographicSize = 250;
        var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
        sea.transform.position = new Vector3(100, 0, 100);
        sea.transform.localScale = Vector3.one * 100;
        var seaMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        seaMaterial.SetColor("_BaseColor", Color.blue);
        sea.GetComponent<Renderer>().sharedMaterial = seaMaterial;

        // セル幅200mに対して遠景格子80mとし、三角形がロード境界をまたぐ条件を再現する。
        const int side = 9;
        var vertices = new Vector3[side * side];
        var triangles = new int[(side - 1) * (side - 1) * 6];
        int t = 0;
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                vertices[i] = new Vector3(-220 + 80 * x, 20, -220 + 80 * z);
                if (x == side - 1 || z == side - 1) continue;
                triangles[t++] = i; triangles[t++] = i + side; triangles[t++] = i + 1;
                triangles[t++] = i + 1; triangles[t++] = i + side; triangles[t++] = i + side + 1;
            }
        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        farGO.GetComponent<MeshFilter>().sharedMesh = mesh;
        farGO.transform.position = Vector3.zero;
        farGO.transform.localScale = Vector3.one;
        farGO.SetActive(true);
        var mask = new Texture2D(3, 3, TextureFormat.R8, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        far.SetTexture("_LoadedCellMask", mask);
        far.SetVector("_LoadedCellMaskParams", new Vector4(1 / 200f, 1, 1, 0));
        far.SetVector("_LoadedCellMaskTexel", new Vector4(1 / 3f, 1 / 3f, 0, 0));
        far.SetFloat("_FarGridStep", 80);
        far.SetFloat("_FarSinkAmount", 100);
        var report = "";
        foreach (int state in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var bytes = new byte[9];
            if (state != 1) bytes[state == 3 ? 5 : state == 5 ? 3 : 4] = 255;
            mask.SetPixelData(bytes, 0); mask.Apply();
            terrainGO.SetActive(state != 1 && state != 2);
            terrainGO.transform.position = new Vector3(state == 3 ? 200 : state == 5 ? -200 : 0, 20, 0);
            far.SetFloat("_FarGridStep", state == 4 ? 1 : 80);
            var pixels = Capture(camera, root + $"/boundary-{state}.png");
            int seaPixels = pixels.Count(c => c.b > 150 && c.r < 30 && c.g < 30);
            report += $"boundary state={state}: exposed sea pixels={seaPixels}\n";
            // 状態2は内部の沈み込み確認。状態4は余白を縮め、境界の裂け目を再現する対照。
            if (state == 2 || state == 4 ? seaPixels == 0 : seaPixels != 0) throw new Exception(report);
        }
        return report;
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
