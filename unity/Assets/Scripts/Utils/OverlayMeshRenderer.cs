using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 3D空間にメッシュを直接構築して描画する共通オーバーレイレンダラー。
/// ZTest Always / ZWrite Off / Queue Transparent+100 の専用マテリアルを使い、
/// 地形や建物より手前に常に表示される補助線・ハンドル・枠線などを描画する。
///
/// 入力はすべてワールド座標であり、Canvas 上のスクリーン座標変換を行わない。
/// これにより毎フレームの WorldToScreenPoint + RectTransform 更新による
/// CPU負荷とGC負荷を根絶する。
///
/// 線幅や円・四角のサイズは「スクリーン上のピクセル数」で指定し、
/// 内部でカメラ距離を考慮したワールド空間の厚みに自動換算する。
/// </summary>
public class OverlayMeshRenderer : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;

    private List<Vector3> verts = new List<Vector3>(4096);
    private List<Color> colors = new List<Color>(4096);
    private List<int> indices = new List<int>(8192);

    private struct LineEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float pixelThickness;
    }

    private struct DashedEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float pixelThickness;
        public float dashLen;
        public float gapLen;
    }

    /// <summary>
    /// ワールド空間での固定の太さと間隔を持つ点線の描画エントリー。
    /// スクリーン空間のピクセルサイズではなく、メートル単位で指定される。
    /// </summary>
    private struct WorldDashedEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float worldThickness;
        public float worldDashLen;
        public float worldGapLen;
        public Vector3 upVector;
    }


    private struct CircleEntry
    {
        public Vector3 pos;
        public Color color;
        public float pixelRadius;
    }

    private struct SquareEntry
    {
        public Vector3 pos;
        public Color color;
        public float pixelHalfSize;
    }

    private struct PolygonEntry
    {
        public List<Vector3> points;
        public Color color;
    }

    private struct PreTriangulatedEntry
    {
        public List<Vector3> verts;
        public Color color;
        /// <summary>頂点ごとの色。null のときは color を全頂点に適用する。</summary>
        public List<Color> vertexColors;
    }

    private List<LineEntry> frameLines = new List<LineEntry>(512);
    private List<DashedEntry> frameDashed = new List<DashedEntry>(128);
    private List<WorldDashedEntry> frameWorldDashed = new List<WorldDashedEntry>(128);
    private List<CircleEntry> frameCircles = new List<CircleEntry>(128);
    private List<SquareEntry> frameSquares = new List<SquareEntry>(128);
    private List<PolygonEntry> framePolygons = new List<PolygonEntry>(16);
    private List<PreTriangulatedEntry> framePreTriangulated = new List<PreTriangulatedEntry>(32);

    /// <summary>OverlayLine シェーダーの参照。Shader.Find は名前検索なので一度だけ引いて使い回す。</summary>
    private static Shader overlayLineShader;

    /// <summary>
    /// オーバーレイ描画用の GameObject を新規生成し、専用の OverlayLine マテリアルで初期化して返す。
    /// 頂点はワールド座標で与えるため、親を指定してもオブジェクト自体はワールド原点に置く。
    /// alwaysOnTop=true は深度テストを無効化(_ZTest=Always)し、起伏地形や建物に埋もれず常に手前へ描く
    /// (丘の裏や手前の建物を透かすX線状の見え方になるが、ギズモ・強調表示用途では許容する)。
    /// false はシェーダー既定の LEqual+ポリゴンオフセットのまま(塗りが建物に自然に隠れる用途)。
    /// persistent=true はシーン遷移をまたいで残す(DontDestroyOnLoad)。
    /// 描画キューや加算合成など引数にない設定は、戻り値の <see cref="Material"/> に対して呼び出し側が行う。
    /// </summary>
    public static OverlayMeshRenderer Create(string name, bool alwaysOnTop, Transform parent = null, bool persistent = false)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, true);
        if (persistent) DontDestroyOnLoad(go);
        var renderer = go.AddComponent<OverlayMeshRenderer>();
        renderer.Initialize(CreateOverlayLineMaterial(alwaysOnTop));
        return renderer;
    }

    /// <summary>OverlayLine シェーダーのマテリアルを新規生成する。深度テストの扱いは <see cref="Create"/> と同じ。</summary>
    public static Material CreateOverlayLineMaterial(bool alwaysOnTop)
    {
        if (overlayLineShader == null) overlayLineShader = Shader.Find("Custom/OverlayLine");
        var mat = new Material(overlayLineShader);
        if (alwaysOnTop) mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        return mat;
    }

    /// <summary>
    /// このレンダラー専用のマテリアル。各レンダラーは Initialize で渡された自分だけのマテリアルを持つので、
    /// 複製せずそのまま返す(プロパティ変更は即座に描画へ反映される)。
    /// </summary>
    public Material Material => meshRenderer != null ? meshRenderer.sharedMaterial : null;

    /// <summary>
    /// 指定のマテリアルで初期化する。MeshFilter/MeshRenderer を自動生成する。
    /// マテリアルの所有権はこのレンダラーに移り、破棄時にメッシュと一緒に解放する。
    /// </summary>
    public void Initialize(Material material)
    {
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.material = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        mesh = new Mesh();
        // 効果範囲フィルのような広域のセル敷き詰めでは頂点数が16bitインデックスの
        // 上限(65535)を超え得るため、32bitインデックスで頂点数の制限を外す。
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();
        meshFilter.mesh = mesh;
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (meshRenderer != null && meshRenderer.sharedMaterial != null) Destroy(meshRenderer.sharedMaterial);
    }

    /// <summary>
    /// 頂点色に乗算される全体色（_Tint）を設定する。
    /// メッシュを作り直さずに明滅アニメーションを行うために使う。
    /// </summary>
    public void SetTint(Color tint)
    {
        if (meshRenderer == null) return;
        meshRenderer.sharedMaterial.SetColor("_Tint", tint);
    }

    /// <summary>
    /// フレームの描画リストをクリアする。毎フレーム描画前に呼ぶ。
    /// </summary>
    public void BeginFrame()
    {
        frameLines.Clear();
        frameDashed.Clear();
        frameWorldDashed.Clear();
        frameCircles.Clear();
        frameSquares.Clear();
        framePolygons.Clear();
        framePreTriangulated.Clear();
    }

    /// <summary>
    /// ワールド座標の2点間に実線を追加する。
    /// </summary>
    public void AddSolidLine(Vector3 worldA, Vector3 worldB, Color color, float pixelThickness)
    {
        frameLines.Add(new LineEntry { a = worldA, b = worldB, color = color, pixelThickness = pixelThickness });
    }

    /// <summary>
    /// ワールド座標の2点間に点線を追加する。
    /// dashLen / gapLen はピクセル単位。
    /// </summary>
    public void AddDashedLine(Vector3 worldA, Vector3 worldB, Color color, float pixelThickness, float dashLen = 8f, float gapLen = 6f)
    {
        frameDashed.Add(new DashedEntry
        {
            a = worldA,
            b = worldB,
            color = color,
            pixelThickness = pixelThickness,
            dashLen = dashLen,
            gapLen = gapLen
        });
    }

    /// <summary>
    /// ワールド座標の2点間に、ワールド空間での固定幅（メートル）の点線を追加する。
    /// 幅方向は上方向ベクトル（upVector）に基づいて決定され、ビルボード（カメラ対向）表示は行われない。
    /// </summary>
    public void AddDashedLineWorld(Vector3 worldA, Vector3 worldB, Color color, float worldThickness, float worldDashLen = 4f, float worldGapLen = 3f, Vector3? upVector = null)
    {
        frameWorldDashed.Add(new WorldDashedEntry
        {
            a = worldA,
            b = worldB,
            color = color,
            worldThickness = worldThickness,
            worldDashLen = worldDashLen,
            worldGapLen = worldGapLen,
            upVector = upVector ?? Vector3.up
        });
    }

    /// <summary>
    /// ワールド座標の位置に円を追加する。カメラに対してビルボード表示される。
    /// </summary>
    public void AddCircle(Vector3 worldPos, float pixelRadius, Color color)
    {
        frameCircles.Add(new CircleEntry { pos = worldPos, color = color, pixelRadius = pixelRadius });
    }

    /// <summary>
    /// ワールド座標の位置に四角を追加する。カメラに対してビルボード表示される。
    /// </summary>
    public void AddSquare(Vector3 worldPos, float pixelHalfSize, Color color)
    {
        frameSquares.Add(new SquareEntry { pos = worldPos, color = color, pixelHalfSize = pixelHalfSize });
    }

    /// <summary>
    /// ワールド座標で囲まれたポリゴンを追加する。
    /// ハイライト塗りつぶしなどに使用する。
    /// </summary>
    public void AddPolygon(List<Vector3> worldPoints, Color color)
    {
        framePolygons.Add(new PolygonEntry { points = new List<Vector3>(worldPoints), color = color });
    }

    /// <summary>
    /// フレームの描画リストをメッシュに反映する。毎フレーム描画後に呼ぶ。
    /// </summary>
    public void EndFrame(Camera cam)
    {
        verts.Clear();
        colors.Clear();
        indices.Clear();

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        cachedTanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        cachedHalfScreenHeight = Screen.height * 0.5f;

        foreach (var line in frameLines)
        {
            AddLineQuads(cam, line.a, line.b, line.color, line.pixelThickness);
        }

        foreach (var dash in frameDashed)
        {
            AddDashedLineQuads(cam, dash.a, dash.b, dash.color, dash.pixelThickness, dash.dashLen, dash.gapLen);
        }

        foreach (var dash in frameWorldDashed)
        {
            AddWorldDashedLineQuads(dash.a, dash.b, dash.color, dash.worldThickness, dash.worldDashLen, dash.worldGapLen, dash.upVector);
        }

        foreach (var circle in frameCircles)
        {
            AddCircleMesh(cam, circle.pos, circle.color, circle.pixelRadius);
        }

        foreach (var sq in frameSquares)
        {
            AddSquareMesh(cam, sq.pos, sq.color, sq.pixelHalfSize);
        }

        foreach (var poly in framePolygons)
        {
            AddPolygonMesh(poly.points, poly.color);
        }

        foreach (var pt in framePreTriangulated)
        {
            AddPreTriangulatedMesh(pt.verts, pt.color, pt.vertexColors);
        }

        mesh.Clear();
        if (verts.Count > 0)
        {
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
        }
    }

    /// <summary>
    /// メッシュをクリアして何も表示しない状態にする。
    /// </summary>
    public void ClearMesh()
    {
        BeginFrame();
        mesh.Clear();
    }

    /// <summary>
    /// EndFrame 内で計算済みのカメラパラメータ。
    /// FOV と画面サイズはフレーム内で不変なため、毎頂点ごとの再計算を回避する。
    /// </summary>
    private float cachedTanHalfFov;
    private float cachedHalfScreenHeight;

    /// <summary>
    /// 指定ピクセル厚みを、指定ワールド位置におけるワールド空間の厚みに換算する。
    /// </summary>
    private float GetWorldThickness(Camera cam, Vector3 worldPos, float pixelThickness)
    {
        float distance = Vector3.Distance(cam.transform.position, worldPos);
        float worldPerPixel = distance * cachedTanHalfFov / cachedHalfScreenHeight;
        return pixelThickness * worldPerPixel;
    }

    /// <summary>
    /// 線分をクワッド（4頂点・2三角形）で追加する。
    /// 線分中点でのカメラ方向を基準にビルボード法線を求め、
    /// 両端点でのカメラ距離に応じた厚みを適用する。
    /// </summary>
    private void AddLineQuads(Camera cam, Vector3 a, Vector3 b, Color color, float pixelThickness)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 0.0001f) return;
        dir /= len;

        Vector3 mid = (a + b) * 0.5f;
        Vector3 viewDirMid = (mid - cam.transform.position).normalized;
        Vector3 side = Vector3.Cross(viewDirMid, dir).normalized;
        if (side.sqrMagnitude < 0.0001f)
        {
            side = cam.transform.up;
        }

        float thickA = GetWorldThickness(cam, a, pixelThickness);
        float thickB = GetWorldThickness(cam, b, pixelThickness);

        int baseIdx = verts.Count;
        verts.Add(a + side * thickA * 0.5f); colors.Add(color);
        verts.Add(a - side * thickA * 0.5f); colors.Add(color);
        verts.Add(b - side * thickB * 0.5f); colors.Add(color);
        verts.Add(b + side * thickB * 0.5f); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    /// <summary>
    /// 点線を3D空間上で細切れにして描画する。
    /// dashLen / gapLen はピクセル単位であり、ワールド空間に換算して配置する。
    /// </summary>
    private void AddDashedLineQuads(Camera cam, Vector3 a, Vector3 b, Color color, float pixelThickness, float dashLen, float gapLen)
    {
        Vector3 dir = b - a;
        float totalLen = dir.magnitude;
        if (totalLen < 0.0001f) return;
        dir /= totalLen;

        Vector3 mid = (a + b) * 0.5f;
        float worldPerPixel = GetWorldThickness(cam, mid, 1f);
        float worldDashLen = dashLen * worldPerPixel;
        float worldGapLen = gapLen * worldPerPixel;

        float pos = 0f;
        bool draw = true;
        while (pos < totalLen)
        {
            float segLen = draw ? worldDashLen : worldGapLen;
            float segEnd = Mathf.Min(pos + segLen, totalLen);

            if (draw)
            {
                Vector3 segA = a + dir * pos;
                Vector3 segB = a + dir * segEnd;
                AddLineQuads(cam, segA, segB, color, pixelThickness);
            }

            pos = segEnd;
            draw = !draw;
        }
    }

    /// <summary>
    /// 円をトライアングルファンで追加する。カメラに対してビルボード表示される。
    /// </summary>
    private void AddCircleMesh(Camera cam, Vector3 pos, Color color, float pixelRadius)
    {
        const int segments = 16;
        float worldRadius = GetWorldThickness(cam, pos, pixelRadius);

        Vector3 toCam = cam.transform.position - pos;
        Vector3 forward = toCam.normalized;
        Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(forward, Vector3.right).normalized;
        Vector3 up = Vector3.Cross(right, forward).normalized;

        int centerIdx = verts.Count;
        verts.Add(pos);
        colors.Add(color);

        for (int s = 0; s < segments; s++)
        {
            float angle = (float)s / segments * Mathf.PI * 2f;
            Vector3 offset = right * Mathf.Cos(angle) * worldRadius + up * Mathf.Sin(angle) * worldRadius;
            verts.Add(pos + offset);
            colors.Add(color);
        }

        for (int s = 0; s < segments; s++)
        {
            int next = (s + 1) % segments;
            indices.Add(centerIdx);
            indices.Add(centerIdx + 1 + s);
            indices.Add(centerIdx + 1 + next);
        }
    }

    /// <summary>
    /// 四角をクワッドで追加する。カメラに対してビルボード表示される。
    /// </summary>
    private void AddSquareMesh(Camera cam, Vector3 pos, Color color, float pixelHalfSize)
    {
        float worldHalfSize = GetWorldThickness(cam, pos, pixelHalfSize);

        Vector3 toCam = cam.transform.position - pos;
        Vector3 forward = toCam.normalized;
        Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(forward, Vector3.right).normalized;
        Vector3 up = Vector3.Cross(right, forward).normalized;

        int baseIdx = verts.Count;
        verts.Add(pos - right * worldHalfSize - up * worldHalfSize); colors.Add(color);
        verts.Add(pos + right * worldHalfSize - up * worldHalfSize); colors.Add(color);
        verts.Add(pos + right * worldHalfSize + up * worldHalfSize); colors.Add(color);
        verts.Add(pos - right * worldHalfSize + up * worldHalfSize); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    /// <summary>
    /// 事前に三角形分割済みの頂点リスト（3頂点×N三角形）をフレームキューに追加する。
    /// EndFrame で他のエントリと合わせてメッシュに反映される。
    /// </summary>
    public void AddPreTriangulated(List<Vector3> triangleVerts, Color color)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        framePreTriangulated.Add(new PreTriangulatedEntry { verts = triangleVerts, color = color });
    }

    /// <summary>
    /// 事前に三角形分割済みの頂点リストを、頂点ごとの色付きでフレームキューに追加する。
    /// 光のグラデーション（地面側は濃く、上端で透明）のように面内で色が変わる表現に使う。
    /// vertexColors は triangleVerts と同数でなければ無視される。
    /// </summary>
    public void AddPreTriangulated(List<Vector3> triangleVerts, List<Color> vertexColors)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        if (vertexColors == null || vertexColors.Count != triangleVerts.Count) return;
        framePreTriangulated.Add(new PreTriangulatedEntry
        {
            verts = triangleVerts,
            color = Color.white,
            vertexColors = vertexColors
        });
    }

    /// <summary>
    /// ポリゴンをトライアングルファンで追加する。
    /// 頂点はワールド座標のまま使用する。
    /// </summary>
    private void AddPolygonMesh(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 3) return;

        int startIdx = verts.Count;
        for (int i = 0; i < points.Count; i++)
        {
            verts.Add(points[i]);
            colors.Add(color);
        }

        for (int i = 1; i < points.Count - 1; i++)
        {
            indices.Add(startIdx);
            indices.Add(startIdx + i);
            indices.Add(startIdx + i + 1);
        }
    }

    /// <summary>
    /// 事前三角形分割済みの頂点リスト（3頂点×N三角形）をメッシュに追加する。
    /// vertexColors が与えられている場合は頂点ごとの色を使う。
    /// </summary>
    private void AddPreTriangulatedMesh(List<Vector3> triangleVerts, Color color, List<Color> vertexColors = null)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        int startIdx = verts.Count;
        for (int i = 0; i < triangleVerts.Count; i++)
        {
            verts.Add(triangleVerts[i]);
            colors.Add(vertexColors != null ? vertexColors[i] : color);
        }
        for (int i = 0; i + 2 < triangleVerts.Count; i += 3)
        {
            indices.Add(startIdx + i);
            indices.Add(startIdx + i + 1);
            indices.Add(startIdx + i + 2);
        }
    }

    /// <summary>
    /// ワールド空間で定義された固定の長さと間隔を持つ点線を、複数のクワッドとしてメッシュに構築する。
    /// スクリーン空間のピクセルサイズに依存せず、遠近感に応じて描画される。
    /// </summary>
    private void AddWorldDashedLineQuads(Vector3 a, Vector3 b, Color color, float worldThickness, float worldDashLen, float worldGapLen, Vector3 upVector)
    {
        Vector3 dir = b - a;
        float totalLen = dir.magnitude;
        if (totalLen < 0.0001f) return;
        dir /= totalLen;

        float pos = 0f;
        bool draw = true;
        while (pos < totalLen)
        {
            float segLen = draw ? worldDashLen : worldGapLen;
            float segEnd = Mathf.Min(pos + segLen, totalLen);

            if (draw)
            {
                Vector3 segA = a + dir * pos;
                Vector3 segB = a + dir * segEnd;
                AddWorldLineQuads(segA, segB, color, worldThickness, upVector);
            }

            pos = segEnd;
            draw = !draw;
        }
    }

    /// <summary>
    /// ワールド空間での固定の厚みを持つ線分を、上方向ベクトルに基づいてクワッドとして構築する。
    /// 幅方向は上方向ベクトルと線の進行方向の外積で求めるため、常に地面に沿ったフラットな形状となる。
    /// </summary>
    private void AddWorldLineQuads(Vector3 a, Vector3 b, Color color, float worldThickness, Vector3 upVector)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 0.0001f) return;
        dir /= len;

        // 線の進行方向と上方向ベクトルから、横幅方向のベクトルを算出する。
        Vector3 side = Vector3.Cross(upVector, dir).normalized;
        if (side.sqrMagnitude < 0.0001f)
        {
            // 外積が極端に小さい（線と上方向が平行である）場合は、別の直交ベクトルで代替する。
            side = Vector3.Cross(Vector3.forward, dir).normalized;
            if (side.sqrMagnitude < 0.0001f)
            {
                side = Vector3.right;
            }
        }

        int baseIdx = verts.Count;
        verts.Add(a + side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(a - side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(b - side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(b + side * worldThickness * 0.5f); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }
}

