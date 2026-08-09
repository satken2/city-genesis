using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 方位磁針の針(北を指す赤い四角錐)と「N」の文字メッシュを手続き的に生成する。
/// CompassPanel から使う。
///
/// 座標系: ローカル+Zが北、+Yが上、原点が回転の中心。
/// 陰影はライトを使わず頂点色に焼き込む(CompassPanelが使うCustom/CompassUnlitは
/// 完全なアンリット)。針は固定光源で面ごとの明るさを計算するため、カメラの方位に
/// 合わせて回るときに4面の明暗が入れ替わり、立体であることが読み取れる。
/// </summary>
public static class CompassNeedleMesh
{
    /// <summary>針の先端(北)のZ座標</summary>
    public const float TipZ = 0.85f;
    /// <summary>四角錐の底面のZ座標。原点(回転中心)を挟んで先端の反対側に置く</summary>
    private const float BaseZ = -0.32f;
    /// <summary>四角錐の底面の半径(中心から頂点まで)</summary>
    private const float BaseRadius = 0.20f;

    /// <summary>「N」の文字の高さ(ワールド単位)</summary>
    private const float LetterSize = 0.30f;
    /// <summary>「N」の文字の線幅(文字ローカル座標=高さ1.0基準)</summary>
    private const float LetterStrokeWidth = 0.2f;

    private static readonly Color NeedleColor = new Color(0.88f, 0.29f, 0.24f);
    private static readonly Color LetterColor = new Color(0.95f, 0.96f, 0.98f);

    /// <summary>面の陰影に使う固定光源の向き(画面左上手前から当てる)</summary>
    private static readonly Vector3 LightDir = new Vector3(-0.45f, 0.75f, -0.5f).normalized;

    /// <summary>
    /// 「N」のストローク定義。文字ローカル座標(中心原点・高さ1.0の正方形内、
    /// +Xが文字の右・+Yが文字の上)の線分を2点ずつ並べたもの。
    /// </summary>
    private static readonly Vector2[] LetterStrokes =
    {
        new Vector2(-0.32f, -0.5f), new Vector2(-0.32f, 0.5f),
        new Vector2(-0.32f, 0.5f), new Vector2(0.32f, -0.5f),
        new Vector2(0.32f, -0.5f), new Vector2(0.32f, 0.5f),
    };

    /// <summary>
    /// 北を指す赤い四角錐を生成する。底面は45度回した正方形(頂点が上下左右)で、
    /// 側面4枚の法線が上下左右に散るため、どの角度から見ても稜線で立体が分かる。
    /// </summary>
    public static Mesh BuildNeedle()
    {
        var b = new MeshBuffer();

        Vector3 apex = new Vector3(0f, 0f, TipZ);
        Vector3 up = new Vector3(0f, BaseRadius, BaseZ);
        Vector3 down = new Vector3(0f, -BaseRadius, BaseZ);
        Vector3 left = new Vector3(-BaseRadius, 0f, BaseZ);
        Vector3 right = new Vector3(BaseRadius, 0f, BaseZ);

        b.AddShadedTri(apex, up, left, NeedleColor);
        b.AddShadedTri(apex, left, down, NeedleColor);
        b.AddShadedTri(apex, down, right, NeedleColor);
        b.AddShadedTri(apex, right, up, NeedleColor);
        // 底面(北を向いていないときに開口部が見えないよう塞ぐ)
        b.AddShadedTri(up, down, left, NeedleColor);
        b.AddShadedTri(up, right, down, NeedleColor);

        return b.ToMesh("CompassNeedle");
    }

    /// <summary>
    /// 「N」の文字を、原点中心・ローカルXY平面上の平らな帯として生成する。
    /// CompassPanel側でカメラの方を向くよう回すため、ここでは向きを持たせない。
    /// </summary>
    public static Mesh BuildNorthLabel()
    {
        var b = new MeshBuffer();
        float half = LetterStrokeWidth * 0.5f;

        for (int s = 0; s + 1 < LetterStrokes.Length; s += 2)
        {
            Vector2 dir = (LetterStrokes[s + 1] - LetterStrokes[s]).normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x) * half;
            // 線分の端を線幅の半分だけ延長し、折れ線の角に隙間ができないようにする
            Vector2 p0 = LetterStrokes[s] - dir * half;
            Vector2 p1 = LetterStrokes[s + 1] + dir * half;
            b.AddQuad(
                ToLocal(p0 + perp), ToLocal(p1 + perp),
                ToLocal(p1 - perp), ToLocal(p0 - perp), LetterColor);
        }

        return b.ToMesh("CompassNorthLabel");
    }

    private static Vector3 ToLocal(Vector2 letterSpace)
    {
        return new Vector3(letterSpace.x * LetterSize, letterSpace.y * LetterSize, 0f);
    }

    /// <summary>頂点色付きメッシュを組み立てる作業バッファ</summary>
    private class MeshBuffer
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> triangles = new List<int>();

        public void AddTri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            int baseIndex = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            colors.Add(color); colors.Add(color); colors.Add(color);
            triangles.Add(baseIndex); triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 2);
        }

        /// <summary>
        /// 面の法線と固定光源から明るさを求め、陰影を焼き込んだ三角形を追加する。
        /// 法線は外向き(Unityの巻き順規約)である前提。光が当たらない面も
        /// 真っ黒に潰れないよう環境光ぶんの下駄を履かせる。
        /// </summary>
        public void AddShadedTri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            float lambert = Mathf.Max(0f, Vector3.Dot(normal, LightDir));
            AddTri(a, b, c, Shade(color, 0.35f + 0.65f * lambert));
        }

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            AddTri(a, b, c, color);
            AddTri(a, c, d, color);
        }

        public Mesh ToMesh(string name)
        {
            Mesh mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 明るさ係数を掛けた色を返す。アルファは必ず1のままにする。
        /// レンダーテクスチャのアルファがそのままパネルの不透明度になるため、
        /// Colorの乗算(アルファも掛かる)をそのまま使うと陰の部分が透けてしまう。
        /// </summary>
        private static Color Shade(Color color, float factor)
        {
            return new Color(color.r * factor, color.g * factor, color.b * factor, 1f);
        }
    }
}
