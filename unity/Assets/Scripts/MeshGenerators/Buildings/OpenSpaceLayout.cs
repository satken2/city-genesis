using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>地面用途の配置計画。ロード順・描画LOD・確定時のIDから独立した平面幾何。</summary>
public static class OpenSpaceLayout
{
    public const float CellSize = 2.5f;
    const int MaxAxis = 128;
    public const float BOUNDARY_TOLERANCE = 1e-3f; // 敷地の辺上に置いた点を内側と数えるための余裕(m)。QAテストも参照する
    public sealed class Surface
    {
        public string Material;
        public Vector2[] Polygon;
        public float Lift;
    }
    public sealed class Prop
    {
        public string Part;
        public Vector2 Position;
        public Vector2 Forward;
    }
    public sealed class Plan
    {
        public Vector2[] Polygon;
        public readonly List<Surface> Surfaces = new List<Surface>();
        public readonly List<Prop> Props = new List<Prop>();
        public readonly List<Vector2[]> ParkingBays = new List<Vector2[]>();
        public readonly List<Vector2> AccessCells = new List<Vector2>();
        public bool HasLoop;
    }
    sealed class Grid
    {
        public Vector2 U, V, Origin;
        public Vector2[] Polygon;
        public int Width, Height, Entry;
        public bool[] Inside;
        public Vector2 Point(float x, float y) => Origin + U * (x * CellSize) + V * (y * CellSize);
        public Vector2 Center(int i) => Point(i % Width + .5f, i / Width + .5f);
        public Vector2[] Box(float x, float y, float w, float h) => new[] {Point(x,y),Point(x+w,y),Point(x+w,y+h),Point(x,y+h)};
        public IEnumerable<int> Neighbors(int i)
        {
            int x=i%Width, y=i/Width;
            if (x>0) yield return i-1;
            if (x+1<Width) yield return i+1;
            if (y>0) yield return i-Width;
            if (y+1<Height) yield return i+Width;
        }
    }
    public static Plan Create(Vector2[] polygon, int modelType, float visualVariant)
    {
        var plan = new Plan { Polygon = polygon };
        if (polygon == null || polygon.Length < 3) return plan;
        var grid = MakeGrid(polygon);
        if (grid.Entry < 0) return plan;
        uint seed = (uint)Mathf.RoundToInt(Mathf.Clamp01(visualVariant)*1000000f);
        if (modelType == Constants.MODEL_PARKING) Parking(plan, grid);
        else if (modelType == Constants.MODEL_ROTARY) Rotary(plan, grid);
        else if (modelType == Constants.MODEL_PARK) Park(plan, grid, seed);
        return plan;
    }
    static Grid MakeGrid(Vector2[] polygon)
    {
        Vector2 u=(polygon[1]-polygon[0]).normalized;
        if (u.sqrMagnitude < .5f) u=Vector2.right;
        Vector2 v=new Vector2(-u.y,u.x), front=(polygon[0]+polygon[1])*.5f;
        float minX=float.MaxValue,minY=float.MaxValue,maxX=float.MinValue,maxY=float.MinValue;
        foreach(var p in polygon)
        {
            float x=Vector2.Dot(p-front,u),y=Vector2.Dot(p-front,v);
            minX=Mathf.Min(minX,x); maxX=Mathf.Max(maxX,x); minY=Mathf.Min(minY,y); maxY=Mathf.Max(maxY,y);
        }
        // 巨大区画では入口側320m四方だけを装飾し、全面の地面描画は維持する。
        minX=Mathf.Max(minX,-MaxAxis*CellSize*.5f);
        maxX=Mathf.Min(maxX,minX+MaxAxis*CellSize);
        maxY=Mathf.Min(maxY,minY+MaxAxis*CellSize);
        var g=new Grid {U=u,V=v,Origin=front+u*minX+v*minY,Polygon=polygon,
            Width=Mathf.Max(1,Mathf.FloorToInt((maxX-minX)/CellSize+.0001f)),Height=Mathf.Max(1,Mathf.FloorToInt((maxY-minY)/CellSize+.0001f)),Entry=-1};
        g.Inside=new bool[g.Width*g.Height];
        float nearest=float.MaxValue;
        for(int i=0;i<g.Inside.Length;i++)
        {
            g.Inside[i]=ContainsPolygon(polygon,g.Box(i%g.Width,i/g.Width,1,1));
            float d=(g.Center(i)-front).sqrMagnitude;
            if(g.Inside[i] && d<nearest-.001f) {nearest=d;g.Entry=i;}
        }
        return g;
    }
    static int[] Flood(Grid g, bool[] allowed, int start)
    {
        int[] parent=new int[allowed.Length];
        for(int i=0;i<parent.Length;i++) parent[i]=-1;
        if(start<0 || !allowed[start]) return parent;
        var queue=new Queue<int>();queue.Enqueue(start);parent[start]=start;
        while(queue.Count>0)
        {
            int i=queue.Dequeue();
            foreach(int n in g.Neighbors(i)) if(allowed[n] && parent[n]<0) {parent[n]=i;queue.Enqueue(n);}
        }
        return parent;
    }
    static void Add(Plan p,string material,Vector2[] polygon,float lift=.012f)
    {
        p.Surfaces.Add(new Surface {Material=material,Polygon=polygon,Lift=lift});
    }
    static void Line(Plan p,Vector2 a,Vector2 b,float width,string material="site_marking",float lift=.025f)
    {
        Vector2 n=new Vector2(-(b-a).y,(b-a).x).normalized*width*.5f;
        var q=new[]{a-n,b-n,b+n,a+n};
        if(ContainsPolygon(p.Polygon,q)) Add(p,material,q,lift);
    }
    static void Parking(Plan p,Grid g)
    {
        int spine=Mathf.Clamp(g.Entry%g.Width-1,0,Mathf.Max(0,g.Width-2));
        var allowed=new bool[g.Inside.Length];
        for(int i=0;i<allowed.Length;i++) allowed[i]=g.Inside[i] && (i%g.Width==spine || i%g.Width==spine+1 || (i/g.Width)%6<2);
        int[] reachable=Flood(g,allowed,g.Entry);
        for(int i=0;i<reachable.Length;i++) if(reachable[i]>=0) p.AccessCells.Add(g.Center(i));
        for(int y=2;y+1<g.Height;y+=2)
        {
            if(y%6==0) continue;
            int aisle=y%6==2?y-1:y+2;
            if(aisle>=g.Height) continue;
            for(int x=0;x<g.Width;x++)
            {
                if(p.ParkingBays.Count>=512)return;
                if(x==spine || x==spine+1 || reachable[aisle*g.Width+x]<0) continue;
                if(!g.Inside[y*g.Width+x] || !g.Inside[(y+1)*g.Width+x]) continue;
                var q=g.Box(x+.06f,y+.06f,.88f,1.88f);
                if(!ContainsPolygon(p.Polygon,q)) continue;
                p.ParkingBays.Add(q);
                Line(p,q[0],q[3],.10f); Line(p,q[1],q[2],.10f);
                bool forward=y%6==2;
                Line(p,forward?q[3]:q[0],forward?q[2]:q[1],.10f);
                p.Props.Add(new Prop {Part="site_stop",Position=g.Point(x+.5f,forward?y+1.7f:y+.3f),Forward=forward?g.V:-g.V});
            }
        }
    }
    static void Park(Plan p,Grid g,uint seed)
    {
        int hub=g.Entry;float hubDistance=float.MaxValue;
        var fromEntry=Flood(g,g.Inside,g.Entry);
        Vector2 middle=g.Point(g.Width*.5f,g.Height*.5f);
        for(int i=0;i<fromEntry.Length;i++) if(fromEntry[i]>=0 && (g.Center(i)-middle).sqrMagnitude<hubDistance)
        {hub=i;hubDistance=(g.Center(i)-middle).sqrMagnitude;}
        int[] parent=Flood(g,g.Inside,hub);
        var paths=new bool[parent.Length];
        // 幅を持つセルの探索木を辿るので、凹区画の外側を園路が横切らない。
        for(int direction=0;direction<5;direction++)
        {
            int best=g.Entry;float score=float.MinValue;
            for(int i=0;i<parent.Length;i++) if(parent[i]>=0)
            {
                float value=direction==0?i%g.Width:direction==1?-(i%g.Width):direction==2?i/g.Width:-(i/g.Width);
                value-=.001f*(g.Center(i)-middle).sqrMagnitude;
                if(value>score) {score=value;best=i;}
            }
            if(direction==4)best=g.Entry;
            while(!paths[best]) {paths[best]=true;if(parent[best]==best)break;best=parent[best];}
        }
        // 広い長方形には主園路と接続した周回園路を加える。凹区画では無理に閉じない。
        if(g.Width>=12 && g.Height>=12)
        {
            var ring=new List<int>();bool valid=true;
            for(int y=2;y<g.Height-2;y++)for(int x=2;x<g.Width-2;x++)
                if(x==2 || y==2 || x==g.Width-3 || y==g.Height-3)
                {int i=y*g.Width+x;ring.Add(i);valid&=parent[i]>=0;}
            if(valid){foreach(int i in ring)paths[i]=true;p.HasLoop=true;}
        }
        for(int i=0;i<paths.Length;i++)
        {
            if(paths[i]) {Add(p,"site_paving",g.Box(i%g.Width,i/g.Width,1,1));p.AccessCells.Add(g.Center(i));continue;}
            if(parent[i]<0) continue;
            int x=i%g.Width,y=i/g.Width;
            uint h=Hash((uint)i ^ seed);
            bool nearPath=false;Vector2 facing=g.V;
            foreach(int n in g.Neighbors(i))if(paths[n]){nearPath=true;facing=(g.Center(n)-g.Center(i)).normalized;break;}
            if(p.Props.Count>=512)continue;
            if(nearPath && h%13==0)
                p.Props.Add(new Prop {Part="site_bench",Position=g.Center(i),Forward=facing});
            else if(!nearPath && x%2==1 && y%2==1 && h%5!=0)
            {
                var crown=g.Box(x-.15f,y-.15f,1.3f,1.3f);
                if(ContainsPolygon(p.Polygon,crown)) p.Props.Add(new Prop {Part="site_tree",Position=g.Center(i),Forward=g.V});
            }
        }
    }
    static uint Hash(uint x) {unchecked {x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16);}}
    static Vector2[] Capsule(Grid g,Vector2 center,float halfStraight,float radius)
    {
        var points=new Vector2[48];
        for(int i=0;i<points.Length;i++)
        {
            float angle=2*Mathf.PI*i/points.Length;
            float x=Mathf.Cos(angle),y=Mathf.Sin(angle);
            points[i]=center+g.U*(x*radius+(x>=0?halfStraight:-halfStraight))+g.V*y*radius;
        }
        return points;
    }
    static void Rotary(Plan p,Grid g)
    {
        int[] parent=Flood(g,g.Inside,g.Entry);
        Vector2[] outer=null,inner=null; float bestArea=0;
        // 探索候補数を固定上限にし、面積・走査順で同点を解決する。
        int step=Mathf.Max(1,Mathf.CeilToInt(Mathf.Sqrt(g.Inside.Length/64f)));
        for(int y=0;y<g.Height;y+=step) for(int x=0;x<g.Width;x+=step)
        {
            int index=y*g.Width+x;if(parent[index]<0)continue;
            for(float r=7.5f;r<=Mathf.Min(25f,g.Height*CellSize*.5f);r+=2.5f)
            for(float s=0;s<=Mathf.Min(30f,g.Width*CellSize*.25f);s+=5f)
            {
                float area=Mathf.PI*r*r+4*s*r;if(area<=bestArea)continue;
                var candidate=Capsule(g,g.Center(index),s,r);
                if(!ContainsPolygon(p.Polygon,candidate))continue;
                outer=candidate;inner=Capsule(g,g.Center(index),s,r-5f);bestArea=area;
            }
        }
        if(outer==null)
        {
            // 車路幅が取れない場合は舗装された車寄せを残す。
            return;
        }
        var lane=new bool[g.Inside.Length];int entry=-1;float entryDistance=float.MaxValue;
        for(int i=0;i<lane.Length;i++)
        {
            int x=i%g.Width,y=i/g.Width;
            var box=g.Box(x,y,2,2);
            lane[i]=x+1<g.Width && y+1<g.Height && ContainsPolygon(p.Polygon,box) && !PolygonsOverlap(inner,box);
            float d=(g.Point(x+1,y+1)-g.Center(g.Entry)).sqrMagnitude;
            if(lane[i] && d<entryDistance){entry=i;entryDistance=d;}
        }
        parent=Flood(g,lane,entry);
        int target=-1;float nearest=float.MaxValue;
        for(int i=0;i<parent.Length;i++)if(parent[i]>=0 && PolygonsOverlap(outer,g.Box(i%g.Width,i/g.Width,2,2)))
        {float d=(g.Center(i)-g.Center(g.Entry)).sqrMagnitude;if(d<nearest){nearest=d;target=i;}}
        if(target<0)return;
        p.HasLoop=true;
        var entranceCells=new bool[lane.Length];
        while(target>=0)
        {
            entranceCells[target]=entranceCells[target+1]=entranceCells[target+g.Width]=entranceCells[target+g.Width+1]=true;
            if(parent[target]==target)break;target=parent[target];
        }
        for(int i=0;i<entranceCells.Length;i++)if(entranceCells[i])
        {Add(p,"site_asphalt",g.Box(i%g.Width,i/g.Width,1,1),.014f);p.AccessCells.Add(g.Center(i));}
        for(int i=0;i<outer.Length;i++)
        {
            int n=(i+1)%outer.Length;
            Add(p,"site_asphalt",new[]{outer[i],outer[n],inner[n],inner[i]},.018f);
            Line(p,inner[i],inner[n],.2f,"site_paving",.04f);
        }
        Add(p,"site_grass",inner,.025f);
        for(int i=0;i<parent.Length;i++) if(parent[i]>=0 && i%3==0)
        {
            var q=g.Box(i%g.Width-.7f,i/g.Width-.3f,2.4f,1.6f);
            if(ContainsPolygon(p.Polygon,q) && !PolygonsOverlap(outer,q))
            {
                p.Props.Add(new Prop {Part="site_shelter",Position=g.Center(i),Forward=g.V});break;
            }
        }
    }
    static float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
    static bool Intersects(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
    {
        float ab1=Cross(b-a,c-a),ab2=Cross(b-a,d-a),cd1=Cross(d-c,a-c),cd2=Cross(d-c,b-c);
        float abTolerance=.001f*(b-a).magnitude,cdTolerance=.001f*(d-c).magnitude;
        return ((ab1>abTolerance && ab2<-abTolerance)||(ab2>abTolerance && ab1<-abTolerance))
            && ((cd1>cdTolerance && cd2<-cdTolerance)||(cd2>cdTolerance && cd1<-cdTolerance));
    }
    public static bool ContainsPolygon(Vector2[] polygon,Vector2[] candidate)
    {
        for(int i=0;i<candidate.Length;i++)
        {
            Vector2 a=candidate[i],b=candidate[(i+1)%candidate.Length];
            if(!PolygonUtils.Contains(polygon,a,BOUNDARY_TOLERANCE) || !PolygonUtils.Contains(polygon,(a+b)*.5f,BOUNDARY_TOLERANCE))return false;
            for(int j=0;j<polygon.Length;j++) if(Intersects(a,b,polygon[j],polygon[(j+1)%polygon.Length]))return false;
        }
        return true;
    }
    static bool PolygonsOverlap(Vector2[] a,Vector2[] b)
    {
        foreach(var p in a)if(PolygonUtils.Contains(b,p,BOUNDARY_TOLERANCE))return true;
        foreach(var p in b)if(PolygonUtils.Contains(a,p,BOUNDARY_TOLERANCE))return true;
        for(int i=0;i<a.Length;i++)for(int j=0;j<b.Length;j++)if(Intersects(a[i],a[(i+1)%a.Length],b[j],b[(j+1)%b.Length]))return true;
        return false;
    }
}
