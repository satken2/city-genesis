using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 旅客機が後ろへ残す飛行機雲。船の航跡は ShipWake。
/// 一定距離ごとに通過点を記録し、古い点ほど広がりながら薄くなる水平な帯メッシュを毎フレーム組み直す。
/// 座標は FloatingOrigin のずれを含まない保存座標で持ち、描画時にオフセットを足す。
/// </summary>
public sealed class AmbientTrail
{
    private struct Sample { public Vector3 position; public float time; }

    private readonly List<Sample> samples = new List<Sample>();
    private readonly Mesh mesh = new Mesh { name = "AmbientTrail" };
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Color32> colors = new List<Color32>();
    private readonly List<int> indices = new List<int>();
    private readonly float spacing, life, startWidth, endWidth, alpha;

    /// <param name="spacing">通過点を記録する間隔 [m]</param>
    /// <param name="life">通過点が消えるまでの時間 [s]</param>
    /// <param name="startWidth">乗り物直後の幅 [m]</param>
    /// <param name="endWidth">消える直前の幅 [m]</param>
    /// <param name="alpha">乗り物直後の不透明度</param>
    public AmbientTrail(float spacing, float life, float startWidth, float endWidth, float alpha)
    {
        this.spacing = spacing; this.life = life; this.startWidth = startWidth; this.endWidth = endWidth; this.alpha = alpha;
        mesh.MarkDynamic();
    }

    public void Record(Vector3 position, float time)
    {
        if (samples.Count == 0 || (samples[samples.Count - 1].position - position).sqrMagnitude >= spacing * spacing)
            samples.Add(new Sample { position = position, time = time });
        while (samples.Count > 0 && time - samples[0].time > life) samples.RemoveAt(0);
    }

    public void Clear() => samples.Clear();

    public void Dispose() => Object.Destroy(mesh);

    /// <summary>帯を組み直して描画する。head は乗り物の現在位置(帯の先端をここへ伸ばす)。</summary>
    public void Draw(Vector3 head, float time, Vector3 offset, Material material)
    {
        if (samples.Count < 1 || material == null) return;
        vertices.Clear(); colors.Clear(); indices.Clear();
        int count = samples.Count + 1;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = i < samples.Count ? samples[i].position : head;
            float age = i < samples.Count ? Mathf.Clamp01((time - samples[i].time) / life) : 0f;
            Vector3 prev = i > 0 ? (i - 1 < samples.Count ? samples[i - 1].position : head) : p;
            Vector3 next = i + 1 < count ? (i + 1 < samples.Count ? samples[i + 1].position : head) : p;
            Vector3 dir = next - prev; dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, dir.normalized);
            float halfWidth = Mathf.Lerp(startWidth, endWidth, age) * 0.5f;
            // 先端で立ち上がり、寿命の終わりで消える。
            float a = alpha * Mathf.Min(1f, (1f - age) * 1.5f) * (i == count - 1 ? 0.3f : 1f);
            var color = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255));
            vertices.Add(p - side * halfWidth + offset);
            vertices.Add(p + side * halfWidth + offset);
            colors.Add(color); colors.Add(color);
            if (i > 0)
            {
                int b = i * 2;
                indices.Add(b - 2); indices.Add(b - 1); indices.Add(b);
                indices.Add(b - 1); indices.Add(b + 1); indices.Add(b);
            }
        }
        if (indices.Count == 0) return;
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
        var rp = new RenderParams(material)
        {
            worldBounds = mesh.bounds,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };
        Graphics.RenderMesh(rp, mesh, 0, Matrix4x4.identity);
    }
}
