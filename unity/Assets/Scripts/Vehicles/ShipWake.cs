using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 船の航跡。船首波と船体沿いの泡・ケルビン波の腕(船首から両側へ19.5°で開く線)・船尾の乱流を、1枚の水平な帯メッシュで描く。
/// 各成分の濃さは前進の速さで決まり、港内の微速では船尾の薄い白濁だけになり、横寄せと停泊では何も出ない。
/// 泡の形は Shaders/ShipWake が水に固定した座標のノイズで決める。座標は FloatingOrigin のずれを含まない保存座標で持ち、
/// 描画時にオフセットを足す。
/// </summary>
public sealed class ShipWake
{
    /// <summary>喫水線の平面形。モデルのメッシュを原点の高さ(喫水線)で切って求める。</summary>
    public sealed class Hull
    {
        public float bow, stern;    // 喫水線の船首・船尾の前後座標(モデル座標の Z) [m]
        public float halfBeam;      // 喫水線の最大の半幅 [m]
        private float z0, step;
        private float[] halfWidths;

        public float Length => bow - stern;

        public float HalfWidthAt(float z)
        {
            if (z > bow || z < stern) return 0f;
            float k = Mathf.Clamp((z - z0) / step, 0f, halfWidths.Length - 1);
            int i = Mathf.Min((int)k, halfWidths.Length - 2);
            return Mathf.Lerp(halfWidths[i], halfWidths[i + 1], k - i);
        }

        /// <summary>
        /// 接写・遠景ではなく無印のメッシュの、喫水線を横切る辺の交点から前後の区間ごとの最大の半幅を取る。
        /// 中央平行部は列が粗く交点の無い区間があるので、前後の区間から補間する。喫水線を横切らないモデルは外接箱で代用する。
        /// </summary>
        public static Hull Measure(PropRenderModel model)
        {
            const int Stations = 64;
            var hull = new Hull { z0 = model.bounds.min.z, step = Mathf.Max(model.bounds.size.z / (Stations - 1), 1e-3f), halfWidths = new float[Stations] };
            var found = new bool[Stations];
            hull.bow = float.NegativeInfinity;
            hull.stern = float.PositiveInfinity;
            var vertices = new Dictionary<Mesh, Vector3[]>();
            foreach (var part in model.normal)
            {
                if (part.mesh == null || !part.mesh.isReadable || part.mesh.GetTopology(part.submesh) != MeshTopology.Triangles) continue;
                if (!vertices.TryGetValue(part.mesh, out var source)) vertices.Add(part.mesh, source = part.mesh.vertices);
                int[] triangles = part.mesh.GetTriangles(part.submesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 a = part.local.MultiplyPoint3x4(source[triangles[i + e]]);
                        Vector3 b = part.local.MultiplyPoint3x4(source[triangles[i + (e + 1) % 3]]);
                        if ((a.y <= 0f) == (b.y <= 0f)) continue;
                        Vector3 p = Vector3.Lerp(a, b, a.y / (a.y - b.y));
                        int k = Mathf.Clamp(Mathf.RoundToInt((p.z - hull.z0) / hull.step), 0, Stations - 1);
                        hull.halfWidths[k] = Mathf.Max(hull.halfWidths[k], Mathf.Abs(p.x));
                        found[k] = true;
                        hull.bow = Mathf.Max(hull.bow, p.z);
                        hull.stern = Mathf.Min(hull.stern, p.z);
                    }
            }
            if (hull.bow - hull.stern < 1f)
            {
                hull.bow = model.bounds.max.z;
                hull.stern = model.bounds.min.z;
                for (int k = 0; k < Stations; k++) { hull.halfWidths[k] = model.bounds.extents.x; found[k] = true; }
            }
            int previous = -1;
            for (int k = 0; k < Stations; k++)
            {
                if (!found[k]) continue;
                for (int j = previous + 1; previous >= 0 && j < k; j++)
                    hull.halfWidths[j] = Mathf.Lerp(hull.halfWidths[previous], hull.halfWidths[k], (j - previous) / (float)(k - previous));
                previous = k;
            }
            foreach (float w in hull.halfWidths) hull.halfBeam = Mathf.Max(hull.halfBeam, w);
            return hull;
        }
    }

    private struct Sample
    {
        public Vector3 position;    // 船尾の喫水線(保存座標)
        public Vector3 forward;     // 記録したときの船首の向き
        public float time, along, speed, foam, wash, arm;
    }

    private const float Height = .1f;                // 海面板(Y=0)から浮かせる高さ [m]
    private const float KelvinTan = .354f;           // ケルビン角19.47°の正接。腕は船首から船の進んだ距離×これだけ横へ開く
    private const float ArmLife = .4f;               // 腕の波頭が消えるまでの寿命の割合(白濁は寿命まで残る)
    private const float SpeedSmoothing = 1.5f;       // 前進の速さの平滑化の時定数 [s]
    private const float FoamStartSpeed = .4f;        // 乱流の泡が出始める前進の速さ [m/s]
    private const float FullFoamSpeed = 5f;          // 乱流の泡が最も濃くなる前進の速さ [m/s]
    private const float FullWashSpeed = 2.5f;        // 白濁が最も濃くなる前進の速さ [m/s]
    private const float GrainHalfBeam = 13.8f;       // ノイズの粒を等倍にする半幅(大型船)。小さな船ほど泡の粒を細かくする
    private const int HullStations = 18;

    private readonly Hull hull;
    private readonly float life, grain;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly Mesh mesh = new Mesh { name = "ShipWake" };
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector4> wake = new List<Vector4>();
    private readonly List<Vector4> widths = new List<Vector4>();
    private readonly List<Vector4> foams = new List<Vector4>();
    private readonly List<Vector2> tints = new List<Vector2>();
    private readonly List<int> indices = new List<int>();
    private Vector3 lastStern;
    private bool hasLast;
    private float speed;
    private float along;    // 船尾が前進した累積距離 [m]。泡のノイズを水に固定する前後座標

    /// <param name="life">航跡が消えるまでの時間 [s]</param>
    /// <param name="speed">発生したときの前進の速さ [m/s]</param>
    public ShipWake(Hull hull, float life, float speed)
    {
        this.hull = hull;
        this.life = life;
        this.speed = speed;
        grain = Mathf.Clamp(hull.halfBeam / GrainHalfBeam, .25f, 1f);
        along = Random.Range(0f, 500f);
        mesh.MarkDynamic();
    }

    public void Dispose() => Object.Destroy(mesh);

    /// <summary>
    /// 船尾の通過点を記録する。前進の速さは船尾の移動を船首の向きへ射影して求めるので、横寄せの移動は航跡にならない。
    /// 通過点の間隔は速さに比例させ、旋回の曲がりを同じ角度の細かさで追う。
    /// </summary>
    public void Record(Vector3 position, Vector3 forward, float time, float dt)
    {
        Vector3 stern = position + forward * hull.stern;
        if (hasLast && dt > 0f)
        {
            float advance = Mathf.Max(0f, Vector3.Dot(stern - lastStern, forward));
            along += advance;
            speed = Mathf.Lerp(speed, advance / dt, 1f - Mathf.Exp(-dt / SpeedSmoothing));
        }
        lastStern = stern;
        hasLast = true;
        float spacing = Mathf.Clamp(speed * 1.2f, 1f, 10f);
        if (speed > .05f && (samples.Count == 0 || (samples[samples.Count - 1].position - stern).sqrMagnitude >= spacing * spacing))
            samples.Add(new Sample { position = stern, forward = forward, time = time, along = along, speed = speed,
                                     foam = FoamStrength(speed), wash = WashStrength(speed), arm = ArmStrength(speed) });
        while (samples.Count > 0 && time - samples[0].time > life) samples.RemoveAt(0);
    }

    public void Draw(Vector3 position, Vector3 forward, float time, Vector3 offset, Material material)
    {
        if (material == null) return;
        vertices.Clear(); wake.Clear(); widths.Clear(); foams.Clear(); tints.Clear(); indices.Clear();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        float bow = BowStrength(speed), arm = ArmStrength(speed);
        if (bow > .01f || arm > .01f) AddHull(position + offset, forward, right, bow, arm);
        AddSection(position + forward * hull.stern + offset, forward, 0f, speed, FoamStrength(speed), WashStrength(speed), arm, along, false);
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            Sample s = samples[i];
            AddSection(s.position + offset, s.forward, time - s.time, s.speed, s.foam, s.wash, s.arm, s.along, true);
        }
        if (indices.Count == 0) return;
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, wake);
        mesh.SetUVs(1, widths);
        mesh.SetUVs(2, foams);
        mesh.SetUVs(3, tints);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
        var rp = new RenderParams(material)
        {
            worldBounds = mesh.bounds,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = true,
        };
        Graphics.RenderMesh(rp, mesh, 0, Matrix4x4.identity);
    }

    /// <summary>
    /// 船体に固定した、船首の少し前から船尾までの区間。船首波は船首で最も広く、船体沿いに細くなり、船尾の手前で船尾波として少し広がる。
    /// 腕は船首から開く線として描き、船体の内側にある間は船体に隠れる。
    /// </summary>
    private void AddHull(Vector3 center, Vector3 forward, Vector3 right, float bow, float arm)
    {
        float waveMax = Mathf.Clamp(hull.Length * .06f, 2f, 9f);
        float ahead = waveMax * .5f;
        for (int i = 0; i < HullStations; i++)
        {
            float z = hull.bow + ahead - (hull.Length + ahead) * Mathf.Pow(i / (HullStations - 1f), 1.5f);   // 船首側ほど細かく刻む
            float fromBow = hull.bow - z;
            float u = Mathf.Clamp01(fromBow / hull.Length);
            float profile = fromBow < 0f ? 1f + fromBow / ahead : Mathf.Max(Mathf.Exp(-u / .12f), .3f + .35f * Smooth(.8f, 1f, u));
            float halfWidth = Mathf.Max(0f, hull.HalfWidthAt(z) - .3f);   // 泡の内縁を喫水線より少し内側に置き、船体との間に水を見せない
            float waveWidth = waveMax * (.4f + .6f * bow) * profile;
            float armOffset = KelvinTan * Mathf.Max(0f, fromBow);
            float extent = Mathf.Max(halfWidth + waveWidth * 1.2f + 1f, arm > .01f ? ArmExtent(armOffset) : 0f);
            AddVertices(center + forward * z, right, extent, along + (z - hull.stern), 0f,
                        new Vector4(0f, armOffset, halfWidth, waveWidth), new Vector4(0f, arm, bow, 0f), new Vector2(0f, arm), i > 0);
        }
    }

    /// <summary>
    /// 船尾から後ろの横断面。乱流の帯はプロペラの後流ほどの幅から、船長の4割ほど進む間に船幅近くまで開き、その後は時間の平方根で広がる。
    /// 白い泡は船長÷速さに比例する時間で減って網目の筋になり、寿命の終わりに消える。白濁は寿命まで残る。腕は船首から船の進んだ距離に比例して
    /// 外へ開くので、記録したときの速さ×経過時間だけ船尾の位置から開いていく(減速しても、速かったときの腕はそのまま広がる)。
    /// </summary>
    private void AddSection(Vector3 stern, Vector3 forward, float age, float sectionSpeed, float foam, float wash, float arm, float sectionAlong, bool connect)
    {
        float passage = hull.Length / Mathf.Max(sectionSpeed, .5f);   // 船長ぶん進む時間 [s]
        float flare = 1f - Mathf.Exp(-age / (.4f * passage));
        float turbHalf = hull.halfBeam * (.3f + .5f * wash * flare + .12f * Mathf.Sqrt(age) * (.4f + .6f * wash));
        float a = Mathf.Clamp01(age / life);
        float tail = Mathf.Clamp01((1f - a) / .3f);
        float turbFoam = foam * Mathf.Exp(-age / (passage + 4f)) * tail;
        float armFoam = arm * Mathf.Exp(-age / (.5f * passage + 3f)) * tail;
        float armSheen = arm * Mathf.Clamp01(1f - age / (life * ArmLife));
        float armOffset = KelvinTan * (hull.Length + sectionSpeed * age);
        float extent = Mathf.Max(turbHalf * 1.25f + 1f, armSheen > .01f ? ArmExtent(armOffset) : 0f);
        AddVertices(stern, Vector3.Cross(Vector3.up, forward), extent, sectionAlong, a,
                    new Vector4(turbHalf, armOffset, 0f, 0f), new Vector4(turbFoam, armFoam, 0f, 0f), new Vector2(wash, armSheen), connect);
    }

    private void AddVertices(Vector3 center, Vector3 right, float extent, float sectionAlong, float age, Vector4 width, Vector4 foam, Vector2 tint, bool connect)
    {
        center.y += Height;
        int b = vertices.Count;
        vertices.Add(center - right * extent);
        vertices.Add(center + right * extent);
        wake.Add(new Vector4(-extent, sectionAlong, age, grain));
        wake.Add(new Vector4(extent, sectionAlong, age, grain));
        widths.Add(width); widths.Add(width);
        foams.Add(foam); foams.Add(foam);
        tints.Add(tint); tints.Add(tint);
        if (!connect) return;
        indices.Add(b - 2); indices.Add(b - 1); indices.Add(b);
        indices.Add(b - 1); indices.Add(b + 1); indices.Add(b);
    }

    /// <summary>腕の線(シェーダーで線幅 0.8m+横位置×0.035)を収める横の範囲。</summary>
    private static float ArmExtent(float armOffset) => armOffset * 1.15f + 4f;

    private static float FoamStrength(float speed) => Mathf.Clamp01((speed - FoamStartSpeed) / (FullFoamSpeed - FoamStartSpeed));

    private static float WashStrength(float speed) => Mathf.Clamp01(speed / FullWashSpeed);

    /// <summary>ケルビン波は船の長さに対する速さ(フルード数)で立つ。大型船の巡航では淡く、小さな漁船ではくっきり出る。</summary>
    private float ArmStrength(float speed) => Smooth(.07f, .3f, Froude(speed));

    private float BowStrength(float speed) => Smooth(.04f, .2f, Froude(speed));

    private float Froude(float speed) => speed / Mathf.Sqrt(9.81f * Mathf.Max(hull.Length, 1f));

    private static float Smooth(float from, float to, float x)
    {
        float t = Mathf.Clamp01((x - from) / (to - from));
        return t * t * (3f - 2f * t);
    }
}
