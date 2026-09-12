using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>造成予定の体積だけを表示する。Terrainや確定済みパスは変更しない。</summary>
public sealed class RoadEarthworkPreview : MonoBehaviour
{
    public sealed class Result
    {
        public List<List<List<float>>> cells;
        public float cut_volume, fill_volume;
        public string status;
    }
    string desired, sent;
    object payload;
    float changedAt;
    bool busy;
    Mesh mesh;
    Material material;
    GameObject display;
    Result result;
    GUIStyle legend;

    public void Request(object value)
    {
        string key = JsonConvert.SerializeObject(value);
        if (key == desired) return;
        desired = key;
        payload = value;
        changedAt = Time.unscaledTime;
        ClearMesh();
    }

    public void Clear()
    {
        desired = sent = null;
        payload = null;
        ClearMesh();
    }

    void Update()
    {
        if (!busy && desired != null && desired != sent && Time.unscaledTime-changedAt >= .25f)
            StartCoroutine(Fetch(desired, payload));
    }

    IEnumerator Fetch(string key, object value)
    {
        busy = true;
        sent = key;
        try
        {
            yield return ApiClient.Post<Result>("/paths/earthwork-preview", value, response =>
            {
                // 操作変更や編集終了の前に出した結果は表示しない。
                if (desired == key && isActiveAndEnabled) Draw(response);
            });
        }
        finally { busy = false; }
    }

    void Draw(Result response)
    {
        ClearMesh();
        result = response;
        if (response.cells == null || response.cells.Count == 0) return;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();
        foreach (var cell in response.cells)
        {
            float delta = 0;
            foreach (var p in cell) delta += p[3]-p[2];
            Color color = delta < 0 ? new Color(1f,.48f,.12f,.24f) : new Color(.12f,.7f,1f,.24f);
            int start = vertices.Count;
            foreach (var p in cell)
            {
                vertices.Add(new Vector3(p[0], p[2]+.04f, p[1]));
                colors.Add(color);
            }
            foreach (var p in cell)
            {
                vertices.Add(new Vector3(p[0], p[3]+.04f, p[1]));
                colors.Add(color);
            }
            // 元地形と造成面を結ぶ半透明の柱。切土の底は地形越しにも確認できる。
            int[] faces = {0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
            foreach (int index in faces) triangles.Add(start+index);
        }
        mesh = new Mesh { name = "EarthworkPreview", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles,0); mesh.RecalculateBounds();
        if (material == null) material = new Material(Resources.Load<Shader>("EarthworkPreview"));
        if (display == null)
        {
            display = new GameObject("EarthworkPreview");
            display.AddComponent<MeshFilter>();
            display.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
        display.GetComponent<MeshFilter>().sharedMesh = mesh;
        display.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
    }

    void OnGUI()
    {
        if (result == null) return;
        if (legend == null) legend = new GUIStyle(GUI.skin.box) { font = UiFontProvider.GetFont(14), fontSize = 14, richText = true };
        string text = result.status == "too_large" ? "造成プレビュー：範囲が広すぎます" :
            $"造成概算  <color=#ff9a42>切土 {result.cut_volume:N0} m³</color>  <color=#40baff>盛土 {result.fill_volume:N0} m³</color>";
        GUI.Box(new Rect(16,Screen.height-72,430,32), text, legend);
    }

    void ClearMesh()
    {
        result = null;
        if (display != null) display.GetComponent<MeshFilter>().sharedMesh = null;
        if (mesh != null) Destroy(mesh);
        mesh = null;
    }
    void OnDisable() { Clear(); }
    void OnDestroy()
    {
        Clear();
        if (display != null) Destroy(display);
        if (material != null) Destroy(material);
    }
}
