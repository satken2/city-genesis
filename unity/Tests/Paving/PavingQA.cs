using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Verify by rendering that the pattern does not shift when the same floor is rotated and that the normal map affects the lighting.
public static class PavingQA
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
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(40, 25, 0);
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 10, 0);
            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            camera.orthographic = true;
            camera.orthographicSize = 2;
            camera.targetTexture = new RenderTexture(512, 512, 24);
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/PATH_PAVING_STONE.mat");
            if (material == null || material.GetTexture("_BaseMap") == null || material.GetTexture("_BumpMap") == null)
                throw new Exception("Missing paving material or texture references");
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity.ToString() == "Error") throw new Exception(message.message + " line=" + message.line);
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.GetComponent<Renderer>().sharedMaterial = material;
            Capture(camera);
            foreach (string pass in new[] { "ForwardLit", "DepthNormals", "ShadowCaster", "DepthOnly" })
                if (material.FindPass(pass) < 0) throw new Exception("Missing pass: " + pass);
            var original = Capture(camera);
            File.WriteAllBytes(root + "/paving.png", original.EncodeToPNG());
            plane.transform.rotation = Quaternion.Euler(0, 90, 0);
            var rotated = Capture(camera);
            double rotationError = Difference(original, rotated);
            if (rotationError > 1) throw new Exception("World projection changed with road rotation: " + rotationError);
            material.SetFloat("_BumpScale", 0);
            var flat = Capture(camera);
            double normalDifference = Difference(original, flat);
            if (normalDifference < .05) throw new Exception("Normal map has no visible effect");
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                if (message.severity.ToString() == "Error") throw new Exception(message.message);
            var pixels = original.GetPixels32();
            if (pixels.Count(p => p.r > 230 && p.b > 230 && p.g < 30) > pixels.Length / 10)
                throw new Exception("Error shader rendered");
            foreach (string name in new[] { "body", "body_close" })
            {
                string path = "Assets/Models/Paths/p301/" + name + ".fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var mesh = asset.GetComponentInChildren<MeshFilter>().sharedMesh;
                if (mesh.triangles.Length != 36) throw new Exception(name + " must contain only 12 triangles");
            }
            File.WriteAllText(root + "/result.txt", $"PASS: rotation error={rotationError:F4}, normal effect={normalDifference:F4}; both FBX=12 triangles; four passes valid");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/result.txt", "FAIL " + e);
            EditorApplication.Exit(1);
        }
    }

    static Texture2D Capture(Camera camera)
    {
        camera.Render();
        RenderTexture.active = camera.targetTexture;
        var image = new Texture2D(512, 512, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
        image.Apply();
        return image;
    }

    static double Difference(Texture2D a, Texture2D b) => a.GetPixels32().Zip(b.GetPixels32(),
        (x, y) => (Math.Abs(x.r-y.r) + Math.Abs(x.g-y.g) + Math.Abs(x.b-y.b)) / 3.0).Average();
}
