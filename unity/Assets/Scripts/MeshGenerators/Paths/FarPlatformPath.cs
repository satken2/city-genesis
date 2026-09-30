using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 遠景の乗り場。通常パーツの材質で大きな断面だけを押し出し、小物や8mタイル反復を省く。
/// 軌道・ホームの断面は1軌道ぶんの寸法で持ち、軌道数と軌道の横位置（TrackPath.GetTrackCenterOffset）から並べる。
/// ホームはその側で最も外側の軌道の中心線から外へ組み立てる（通常パーツの「原点＝軌道の中心線・右側用」と同じ）。
/// 島式ホームの駅は左右の軌道の中心線から中央へ向けて同じ断面を組み立てる。
/// </summary>
public static class FarPlatformPath
{
    // 貨物PF(貨物列車専用のホーム)。463=コンテナ、464=鉱石、465=石油。
    static bool IsFreight(int variant) => variant == 463 || variant == 464 || variant == 465;

    // 471(複線鉄道PF・相対式)・472(同・島式)は 461 と同じ部品の見た目で、軌道数2の並びは BuildInto が軌道の横位置から組み立てる。
    public static bool Handles(PathSummary path) => path != null &&
        (path.path_variant == 451 || path.path_variant == 461 || path.path_variant == 462 || path.path_variant == 471
         || path.path_variant == 472 || IsFreight(path.path_variant) || path.path_variant == 502
         || path.path_variant == Constants.PATH_VARIANT_ROPEWAY_STATION);

    public static void GenerateInto(GameObject root, PathSummary path, List<Vector3> points)
    {
        float width = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.platform_width ?? 3f;
        TrackPath.ResolvePlatformExtents(path, width, out float left, out float right);
        BuildInto(root, path.path_variant, points, left > 0, right > 0);
    }

    /// <summary>
    /// 軌道数・全幅・中央の間隔はマスターから引く。マスターが無い(検証用の独立プロジェクト等)ときは単線の既定幅
    /// (モノレール502は3m、それ以外は5m)で組み立てる。島式ホームの駅(TrackPath.IsIslandPlatform)は left/right によらず
    /// 軌道の間にホームを組み立てる。
    /// </summary>
    public static void BuildInto(GameObject root, int variant, List<Vector3> points, bool left, bool right)
    {
        if (points == null || points.Count < 2) return;
        var builder = new Builder(root, variant, points);
        if (variant == Constants.PATH_VARIANT_ROPEWAY_STATION) builder.Ropeway();
        else
        {
            var master = MasterDataStore.Instance?.GetPathMaster(variant);
            int lanes = Mathf.Max(1, master?.lanes ?? 1);
            float width = master != null && master.width > 0f ? master.width : lanes * (variant == 502 ? 3f : 5f);
            float centerWidth = master?.center_width ?? 0f;
            float trackWidth = (width - (lanes % 2 == 0 ? centerWidth : 0f)) / lanes;
            if (variant != 502) builder.Box(variant == 462 ? "PATH_CONCRETE" : "PATH_ROADBED", -width * .5f, width * .5f, -.20f, .12f);
            for (int track = 0; track < lanes; track++)
            {
                float center = TrackPath.GetTrackCenterOffset(width, lanes, track, centerWidth);
                if (variant == 502) builder.Box("PATH_CONCRETE", center - .4f, center + .4f, -1.08f, .12f);
                else foreach(int sign in new[]{-1,1}) builder.Box("PATH_RAIL",center+sign*.7175f-.04f,center+sign*.7175f+.04f,.35f,.475f);
            }
            if (TrackPath.IsIslandPlatform(master))
            {
                builder.IslandPlatform(variant, Mathf.Abs(TrackPath.GetTrackCenterOffset(width, lanes, lanes / 2, centerWidth)));
            }
            else
            {
                if (left) builder.Platform(-1, variant, trackWidth, Mathf.Abs(TrackPath.PlatformTrackOffset(width, lanes, true, centerWidth)));
                if (right) builder.Platform(1, variant, trackWidth, Mathf.Abs(TrackPath.PlatformTrackOffset(width, lanes, false, centerWidth)));
            }
        }
        builder.Finish();
    }

    sealed class Builder
    {
        readonly GameObject root;
        readonly int variant;
        readonly List<Vector3> points;
        readonly Vector3[] right;
        readonly Dictionary<string, List<Vector3>> vertices = new Dictionary<string, List<Vector3>>();
        readonly Dictionary<string, List<int>> triangles = new Dictionary<string, List<int>>();
        readonly Dictionary<string, Material> sourceMaterials = new Dictionary<string, Material>();
        // ホームを組み立てている間だけ、断面の横位置を軌道の中心線から外向きにずらす量。
        float shift;
        public Builder(GameObject root, int variant, List<Vector3> points)
        {
            this.root = root; this.variant = variant; this.points = points;
            right = new Vector3[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 tangent = points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(0, i - 1)];
                tangent.y = 0;
                right[i] = tangent.sqrMagnitude > .0001f ? Vector3.Cross(Vector3.up, tangent.normalized) : Vector3.right;
            }
            // 通常表示と同じ名前解決を使い、Resources側の材質差し替えにも追従する。
            foreach (string part in new[] { "platform", "platform_wall", "platform_panel", "station_shell", "body", "track" })
            {
                var model = ModelManager.Instance?.GetModel($"Models/Paths/p{variant}/{part}", true);
                if (model == null) continue;
                foreach (var renderer in model.GetComponentsInChildren<Renderer>())
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null) sourceMaterials[material.name] = material;
            }
        }

        void Face(string mat, params Vector3[] face)
        {
            if (!vertices.ContainsKey(mat)) { vertices[mat] = new List<Vector3>(); triangles[mat] = new List<int>(); }
            int start = vertices[mat].Count;
            vertices[mat].AddRange(face);
            for (int i = 1; i + 1 < face.Length; i++) triangles[mat].AddRange(new[] { start, start + i, start + i + 1 });
        }

        // 断面はX/Y平面の反時計回り。両側の法線を保つため左右反転後も順序を揃える。
        void Prism(string mat, Vector2[] profile, int side = 1) => Extrude(mat, profile, side, points, right);

        void Extrude(string mat, Vector2[] profile, int side, IList<Vector3> pts, IList<Vector3> rights)
        {
            var p = (Vector2[])profile.Clone();
            for (int i = 0; i < p.Length; i++) p[i].x += shift;
            if (side < 0) { for (int i = 0; i < p.Length; i++) p[i].x = -p[i].x; System.Array.Reverse(p); }
            Vector3 Pt(int j, Vector2 q) => pts[j] + rights[j] * q.x + Vector3.up * q.y;
            for (int j = 0; j + 1 < pts.Count; j++)
                for (int i = 0; i < p.Length; i++)
                {
                    int n = (i + 1) % p.Length;
                    Face(mat, Pt(j, p[i]), Pt(j, p[n]), Pt(j + 1, p[n]), Pt(j + 1, p[i]));
                }
            var first = new Vector3[p.Length]; var last = new Vector3[p.Length];
            for (int i = 0; i < p.Length; i++) { first[i] = Pt(0, p[p.Length - i - 1]); last[i] = Pt(pts.Count - 1, p[i]); }
            Face(mat, first); Face(mat, last);
        }
        public void Box(string mat, float x0, float x1, float y0, float y1, int side = 1)
            => Prism(mat, new[] { new Vector2(x0,y0), new Vector2(x1,y0), new Vector2(x1,y1), new Vector2(x0,y1) }, side);

        /// <summary>
        /// 片側のホーム。断面は単線の駅の寸法(ホームが面する軌道の中心線から外向きの距離)で持ち、
        /// trackShift(その軌道の中心の横距離)だけ外へずらす。trackWidth は1軌道ぶんの幅(width/lanes)。
        /// </summary>
        public void Platform(int side, int type, float trackWidth, float trackShift)
        {
            shift = trackShift;
            if (IsFreight(type)) Freight(side, type, trackWidth);
            else Passenger(side, type, false);
            shift = 0f;
        }

        /// <summary>
        /// 島式ホーム。左右の軌道(中心から ±trackOffset)から中央へ向けて単線の駅と同じ断面を組み立てる。
        /// 断面の外縁が中央に来るので、外縁の柵は置かない。
        /// </summary>
        public void IslandPlatform(int type, float trackOffset)
        {
            shift = -trackOffset;
            Passenger(1, type, true);
            Passenger(-1, type, true);
            shift = 0f;
        }

        void Passenger(int side, int type, bool island)
        {
            float outer = type == 502 ? 4.5f : 5.5f;
            Box("PATH_CONCRETE",type == 502 ? .3f : 2.3f,outer,type == 502 ? -1.08f : -.3f,type == 502 ? -.83f : .10f,side);
            Posts(side,outer-.2f,type == 502 ? -.83f : .10f,1.2f,"PATH_CONCRETE");
            Box("PATH_CONCRETE", 1.5f, outer, 1.2f, 1.48f, side);
            Box("PATH_ASPHALT", 1.8f, outer, 1.48f, 1.49f, side);
            Box("PATH_YELLOW_BLOCK", type == 502 ? 1.79f : 2.1f, type == 502 ? 1.99f : 2.4f, 1.49f, 1.53f, side);
            if (type == 502)
            {
                Prism("BD_WHITE_WALL", new[] { new Vector2(1.5f,4.92f), new Vector2(3.55f,5.08f), new Vector2(4.5f,4.82f), new Vector2(4.5f,4.96f), new Vector2(3.55f,5.22f), new Vector2(1.5f,5.06f) }, side);
                Box("BD_WHITE_WALL",4.3f,4.5f,1.48f,1.78f,side);
                Box("BD_WINDOW",4.40f,4.45f,1.78f,4.82f,side);
                Box("BD_WHITE_WALL",1.54f,1.78f,1.50f,1.94f,side);
                Box("BD_WINDOW",1.62f,1.68f,1.94f,2.75f,side);
                Posts(side,3.55f,1.48f,4.90f,"PATH_METAL");
            }
            else if (type == 462)
            {
                Prism("BD_CONCRETE",new[] { new Vector2(1.35f,5.62f),new Vector2(5.6f,5.43f),new Vector2(5.6f,5.64f),new Vector2(1.35f,5.83f) },side);
                Box("BD_WHITE_WALL",5.36f,5.60f,0,2.8f,side);
                Box("BD_WINDOW_LIGHT",5.43f,5.51f,2.8f,4.35f,side);
                Box("BD_WHITE_WALL",5.36f,5.60f,4.35f,5.48f,side);
            }
            else
            {
                Prism("BD_CONCRETE",new[] { new Vector2(1.55f,4.28f),new Vector2(3.6f,4.43f),new Vector2(5.6f,4.28f),new Vector2(5.6f,4.36f),new Vector2(3.6f,4.51f),new Vector2(1.55f,4.36f) },side);
                if (!island) Box("PATH_METAL",5.44f,5.52f,2.60f,2.70f,side);
                Posts(side,3.6f,1.48f,4.4f,"PATH_METAL");
            }
        }

        // 貨物PFは荷役床に品目ごとの設備を載せる。位置は通常パーツ(16mタイル)の造形に合わせ、
        // 床の外縁だけをマスターの platform_width から取る(Unityの動的デッキと同じ淵1.5m〜外縁、上面1.48m)。
        const float FreightTile = 16f;

        void Freight(int side, int type, float trackWidth)
        {
            float outer = trackWidth * .5f + (MasterDataStore.Instance?.GetPathMaster(type)?.platform_width ?? (type == 463 ? 14f : type == 464 ? 12f : 10f));
            Box("PATH_CONCRETE", 1.5f, outer, -.4f, 1.46f, side);
            Box(type == 463 ? "PATH_ASPHALT" : type == 464 ? "BD_IRON_DUST" : "PATH_CONCRETE", 1.5f, outer, 1.46f, 1.48f, side);
            if (type == 463)
            {
                Dashes(side, 8.55f, 11.0f, 1.48f, 4.07f, ("PROP_ContainerRed", .97f, 7.03f), ("PROP_ContainerGreen", 8.14f, 11.86f));
                Dashes(side, 11.65f, 14.1f, 1.48f, 4.07f, ("PROP_ContainerBlue", .14f, 3.86f), ("PROP_ContainerRed", 4.14f, 7.86f), ("BD_WHITE_WALL", 8.97f, 15.03f));
            }
            else if (type == 464)
            {
                Box("BD_METAL_PANEL", 2.45f, 4.35f, 4.95f, 5.75f, side);
                Posts(side, 3.4f, 1.48f, 4.95f, "BD_DARK_METAL", 8f);
                Box("BD_CONCRETE", outer - .95f, outer - .1f, 1.48f, 4.3f, side);
                Prism("BD_IRON_ORE", new[] { new Vector2(7.1f, 1.47f), new Vector2(outer - .95f, 1.47f), new Vector2(outer - .95f, 3.7f), new Vector2(10.9f, 3.7f) }, side);
            }
            else
            {
                Box("PATH_METAL", 2.35f, 3.65f, 4.45f, 4.55f, side);
                Box("BD_YELLOW", 2.35f, 2.41f, 5.5f, 5.6f, side);
                Box("BD_YELLOW", 3.59f, 3.65f, 5.5f, 5.6f, side);
                Posts(side, 3.0f, 1.48f, 4.45f, "PATH_METAL", 8f);
                Box("PATH_PIPELINE", 6.3f, 9.5f, 1.8f, 2.4f, side);
            }
        }

        // タイル内の区間 [a, b] ごとに箱を並べる。通常パーツと同じく全長に最も近い整数タイルへ伸縮した周期を使う。
        void Dashes(int side, float x0, float x1, float y0, float y1, params (string mat, float a, float b)[] spans)
        {
            var cum = new float[points.Count];
            for (int i = 1; i < points.Count; i++) cum[i] = cum[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            float total = cum[points.Count - 1];
            if (total < .01f) return;
            int tiles = Mathf.Max(1, Mathf.RoundToInt(total / FreightTile));
            float tile = total / tiles;
            var profile = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };
            for (int t = 0; t < tiles; t++)
                foreach (var span in spans)
                {
                    float s0 = (t + span.a / FreightTile) * tile, s1 = (t + span.b / FreightTile) * tile;
                    var pts = new List<Vector3>(); var rights = new List<Vector3>();
                    for (int i = 1; i < points.Count; i++)
                    {
                        if (cum[i] <= s0) continue;
                        if (pts.Count == 0) AddAt(i, s0, cum, pts, rights);
                        if (cum[i] >= s1) { AddAt(i, s1, cum, pts, rights); break; }
                        pts.Add(points[i]); rights.Add(right[i]);
                    }
                    if (pts.Count >= 2) Extrude(span.mat, profile, side, pts, rights);
                }
        }

        void AddAt(int i, float s, float[] cum, List<Vector3> pts, List<Vector3> rights)
        {
            float t = Mathf.InverseLerp(cum[i - 1], cum[i], s);
            pts.Add(Vector3.Lerp(points[i - 1], points[i], t));
            rights.Add(Vector3.Slerp(right[i - 1], right[i], t).normalized);
        }

        void Posts(int side, float x, float bottom, float top, string material, float spacing = 16f)
        {
            float total = 0; for (int i=1;i<points.Count;i++) total += Vector3.Distance(points[i-1],points[i]);
            int count = Mathf.Max(1,Mathf.CeilToInt(total/spacing));
            for (int k=0;k<count;k++)
            {
                float target=(k+.5f)*total/count, walked=0;
                for(int i=1;i<points.Count;i++)
                {
                    float length=Vector3.Distance(points[i-1],points[i]);
                    if(walked+length>=target)
                    {
                        Vector3 c=Vector3.Lerp(points[i-1],points[i],length>.0001f?(target-walked)/length:0)+Vector3.Lerp(right[i-1],right[i],.5f).normalized*(x+shift)*side;
                        Vector3 a=c+Vector3.up*bottom,b=c+Vector3.up*top;
                        // 支柱は幅のある交差板2枚。両面を持たせてどの方向からも消えない。
                        foreach(Vector3 axis in new[]{Vector3.right*.1f,Vector3.forward*.1f})
                        { Face(material,a-axis,a+axis,b+axis,b-axis); Face(material,b-axis,b+axis,a+axis,a-axis); }
                        break;
                    }
                    walked+=length;
                }
            }
        }

        public void Ropeway()
        {
            Box("BD_WHITE_WALL",-4,4,-3.60f,-3.30f);
            Box("PATH_RAIL_ASPHALT",-3.8f,3.8f,-3.30f,-3.27f);
            foreach(int side in new[]{-1,1})
            {
                Box("BD_WHITE_WALL",3.72f,3.98f,-3.28f,-2.46f,side);
                Box("BD_WINDOW",3.81f,3.89f,-2.46f,.68f,side);
                Box("BD_WHITE_WALL",3.7f,4f,.60f,.78f,side);
                Box("PATH_YELLOW_BLOCK",1.12f,1.24f,-3.27f,-3.25f,side);
            }
            for(int i=0;i<6;i++)
            {
                float a=Mathf.PI*i/6,b=Mathf.PI*(i+1)/6;
                float x0=4.2f*Mathf.Cos(a),x1=4.2f*Mathf.Cos(b),y0=.75f+1.65f*Mathf.Sin(a),y1=.75f+1.65f*Mathf.Sin(b);
                Prism(i>=2&&i<4?"BD_WINDOW":"BD_DARK_METAL",new[]{new Vector2(x1,y1-.14f),new Vector2(x0,y0-.14f),new Vector2(x0,y0),new Vector2(x1,y1)});
            }
        }

        public void Finish()
        {
            foreach(var pair in vertices)
            {
                var mesh=new Mesh { name=$"FarPlatform_{variant}_{pair.Key}",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(pair.Value);mesh.SetTriangles(triangles[pair.Key],0);mesh.RecalculateNormals();mesh.RecalculateBounds();
                var uv=new List<Vector2>();foreach(var v in pair.Value)uv.Add(new Vector2(v.x,v.z)*.25f);mesh.SetUVs(0,uv);
                var go=new GameObject(mesh.name);go.transform.SetParent(root.transform,false);
                go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<IntersectionMeshOwner>().OwnedMesh=mesh;
                Material material=RoadPath.GetOrCreateRoadMaterialByName(pair.Key);
                if(material==null&&sourceMaterials.TryGetValue(pair.Key,out var source))material=RoadPath.GetOrCreateRoadMaterial(source);
                go.AddComponent<MeshRenderer>().sharedMaterial=material??RoadPath.GetOrCreateRoadMaterialByName("BuildingMaterial");
            }
        }
    }
}
