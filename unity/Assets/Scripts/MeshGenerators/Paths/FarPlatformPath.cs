using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>遠景の乗り場。通常パーツの材質で大きな断面だけを押し出し、小物や8mタイル反復を省く。</summary>
public static class FarPlatformPath
{
    public static bool Handles(PathSummary path) => path != null &&
        (path.path_variant == 451 || path.path_variant == 461 || path.path_variant == 462 || path.path_variant == 502 || path.path_variant == Constants.PATH_VARIANT_ROPEWAY_STATION);

    public static void GenerateInto(GameObject root, PathSummary path, List<Vector3> points)
    {
        float width = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.platform_width ?? 3f;
        TrackPath.ResolvePlatformExtents(path, width, out float left, out float right);
        BuildInto(root, path.path_variant, points, left > 0, right > 0);
    }

    public static void BuildInto(GameObject root, int variant, List<Vector3> points, bool left, bool right)
    {
        if (points == null || points.Count < 2) return;
        var builder = new Builder(root, variant, points);
        if (variant == Constants.PATH_VARIANT_ROPEWAY_STATION) builder.Ropeway();
        else
        {
            if (variant == 502) builder.Box("PATH_CONCRETE", -.4f, .4f, -1.08f, .12f);
            else
            {
                builder.Box(variant == 462 ? "PATH_CONCRETE" : "PATH_ROADBED", -2.5f, 2.5f, -.20f, .12f);
                foreach(int sign in new[]{-1,1}) builder.Box("PATH_RAIL",sign*.7175f-.04f,sign*.7175f+.04f,.35f,.475f);
            }
            if (left) builder.Platform(-1, variant);
            if (right) builder.Platform(1, variant);
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

        Vector3 At(int i, Vector2 p) => points[i] + right[i] * p.x + Vector3.up * p.y;
        void Face(string mat, params Vector3[] face)
        {
            if (!vertices.ContainsKey(mat)) { vertices[mat] = new List<Vector3>(); triangles[mat] = new List<int>(); }
            int start = vertices[mat].Count;
            vertices[mat].AddRange(face);
            for (int i = 1; i + 1 < face.Length; i++) triangles[mat].AddRange(new[] { start, start + i, start + i + 1 });
        }

        // 断面はX/Y平面の反時計回り。両側の法線を保つため左右反転後も順序を揃える。
        void Prism(string mat, Vector2[] profile, int side = 1)
        {
            var p = (Vector2[])profile.Clone();
            if (side < 0) { for (int i = 0; i < p.Length; i++) p[i].x = -p[i].x; System.Array.Reverse(p); }
            for (int j = 0; j + 1 < points.Count; j++)
                for (int i = 0; i < p.Length; i++)
                {
                    int n = (i + 1) % p.Length;
                    Face(mat, At(j, p[i]), At(j, p[n]), At(j + 1, p[n]), At(j + 1, p[i]));
                }
            var first = new Vector3[p.Length]; var last = new Vector3[p.Length];
            for (int i = 0; i < p.Length; i++) { first[i] = At(0, p[p.Length - i - 1]); last[i] = At(points.Count - 1, p[i]); }
            Face(mat, first); Face(mat, last);
        }
        public void Box(string mat, float x0, float x1, float y0, float y1, int side = 1)
            => Prism(mat, new[] { new Vector2(x0,y0), new Vector2(x1,y0), new Vector2(x1,y1), new Vector2(x0,y1) }, side);

        public void Platform(int side, int type)
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
                Box("PATH_METAL",5.44f,5.52f,2.60f,2.70f,side);
                Posts(side,3.6f,1.48f,4.4f,"PATH_METAL");
            }
        }

        void Posts(int side, float x, float bottom, float top, string material)
        {
            float total = 0; for (int i=1;i<points.Count;i++) total += Vector3.Distance(points[i-1],points[i]);
            int count = Mathf.Max(1,Mathf.CeilToInt(total/16f));
            for (int k=0;k<count;k++)
            {
                float target=(k+.5f)*total/count, walked=0;
                for(int i=1;i<points.Count;i++)
                {
                    float length=Vector3.Distance(points[i-1],points[i]);
                    if(walked+length>=target)
                    {
                        Vector3 c=Vector3.Lerp(points[i-1],points[i],length>.0001f?(target-walked)/length:0)+Vector3.Lerp(right[i-1],right[i],.5f).normalized*x*side;
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
