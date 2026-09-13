using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class OccupancyQA
{
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-occupancyRoot") + 1];
        string output = root + "/Temp/OccupancyQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { "/Temp/bin/Debug/", "/Library/ScriptAssemblies/" })
                    if (File.Exists(root + dir + name)) return Assembly.LoadFrom(root + dir + name);
                return null;
            };
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var add = assembly.GetType("OccupancyVolumeUtil").GetMethod("AddSegment");
            var triangles = new List<Vector3>();
            add.Invoke(null, new object[] { triangles, new Vector3(-14, 0, 0), new Vector3(14, 0, 0), 5f, 5f });
            if (triangles.Count != 36 || triangles.Max(p => p.y) != 5f || triangles.Min(p => p.z) != -2.5f)
                throw new Exception("Occupancy dimensions differ from the road clearance");
            add.Invoke(null, new object[] { triangles, new Vector3(0, 2, -14), new Vector3(0, 2, 14), 4f, 5f });
            add.Invoke(null, new object[] { triangles, new Vector3(9, 0, 6), new Vector3(9, 0, 12), 6f, 9f });
            if (triangles.Count != 108) throw new Exception("Missing box faces");
            var mesh = new Mesh { vertices = triangles.ToArray(), triangles = Enumerable.Range(0, triangles.Count).ToArray() };
            var material = new Material(Shader.Find("Hidden/Internal-Colored"));
            material.SetColor("_Color", new Color(1, .12f, .12f, .22f));
            material.SetInt("_SrcBlend", 5); material.SetInt("_DstBlend", 10);
            material.SetInt("_Cull", 0); material.SetInt("_ZWrite", 0); material.renderQueue = 3000;
            var go = new GameObject("Occupancy");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            Slab(new Vector3(0, -.1f, 0), new Vector3(28, .2f, 5));
            Slab(new Vector3(0, 1.9f, 0), new Vector3(4, .2f, 28));
            Slab(new Vector3(9, 3, 9), new Vector3(5, 6, 5));
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(35, 28, -38); camera.transform.LookAt(new Vector3(0, 2, 0));
            camera.orthographic = true; camera.orthographicSize = 23;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.14f, .18f, .2f);
            var target = new RenderTexture(1000, 800, 24); camera.targetTexture = target; camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(1000, 800, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0); image.Apply();
            File.WriteAllBytes(output + "/preview.png", image.EncodeToPNG());
            File.WriteAllText(output + "/result.txt", "PASS: road width, clearance height, complete prism faces; rendered preview.png");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(output + "/result.txt", e.ToString()); EditorApplication.Exit(1); }
    }

    private static void Slab(Vector3 position, Vector3 scale)
    {
        var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.transform.position = position; slab.transform.localScale = scale;
        var material = new Material(Shader.Find("Unlit/Color")); material.color = new Color(.38f, .4f, .42f);
        slab.GetComponent<Renderer>().sharedMaterial = material;
    }
}
