using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>Render power-line track/facility connections as receiving gantries with four lattice posts.</summary>
public static class PowerLineRailGantry
{
    public static Func<PathSummary, List<Vector3>> TrackPolyline;
    public static Func<PathSummary, List<TrackNeighborInfo>> NeighborProvider;
    const float HalfDepth = 2.5f;
    const float HeightScale = .6f;
    const float TrussHeight = 1.2f * HeightScale;
    const float InsulatorLength = 1.4f * HeightScale;
    const float JumperSag = .65f * HeightScale;
    static float BeamHeight(PowerLinePath.Support s) => Mathf.Max((s.variant == Constants.PATH_VARIANT_GRID ? 14f : 11f) * HeightScale,s.minimumBeamHeight);
    static int Circuits(PowerLinePath.Support s) => s.variant == Constants.PATH_VARIANT_GRID ? 6 : 3;
    public static float MinimumHalfWidth(int variant) => variant == Constants.PATH_VARIANT_GRID ? 5f : 4f;
    static Vector3 World(PowerLinePath.Support s, Vector3 p) => s.position + s.rotation * p;

    public static bool CanConnect(PathSummary power, PathSummary rail)
    {
        if (rail == null || (rail.path_type != Constants.PATH_TRACK && rail.path_type != Constants.PATH_MONORAIL)) return false;
        var products = InfraNetworkUtil.GetPathInfraProducts(power.path_variant);
        var railProducts = InfraNetworkUtil.GetPathInfraProducts(rail.path_variant);
        return products != null && railProducts != null && products.Overlaps(railProducts);
    }

    public static bool TryPlan(PathSummary rail, int node, int variant, Vector3 incoming, out PowerLinePath.Support support)
    {
        support = default;
        var points = TrackPolyline?.Invoke(rail);
        if (points == null || points.Count < 2) return false;
        bool start = rail.start_node_id == node;
        Vector3 position = points[start ? 0 : points.Count - 1];
        Vector3 forward = start ? points[1] - position : position - points[points.Count - 2];
        forward.y = 0;
        if (forward.sqrMagnitude < .0001f) return false;
        forward.Normalize();
        bool reverse = forward.z < -.0001f || Mathf.Abs(forward.z) < .0001f && forward.x < 0;
        if (reverse) forward = -forward;

        TrackPath.GetTrackMasterParams(rail, out float width, out _, out float wall, out _, out _, out _, out _, out float platform);
        TrackPath.ResolvePlatformExtents(rail, platform, out float leftPlatform, out float rightPlatform);
        if (reverse) (leftPlatform,rightPlatform) = (rightPlatform,leftPlatform);
        float left = width * .5f + leftPlatform, right = width * .5f + rightPlatform;
        var neighbors = (NeighborProvider ?? TrackAdjacency.NeighborProvider)?.Invoke(rail);
        // Apply joining rules before/after gantries to keep posts out of adjacent beds at branches/curves.
        var probes = new List<Vector3> { position - forward * HalfDepth, position, position + forward * HalfDepth };
        var corridor = TrackAdjacency.ComputeWithShifts(rail.path_id,probes,width,wall,leftPlatform,rightPlatform,neighbors,0,null,TrackAdjacency.IsProvisional(rail));
        if (corridor != null)
            for (int i = 0; i < probes.Count; i++)
            {
                left = Mathf.Max(left,corridor.LeftExtent[i]);
                right = Mathf.Max(right,corridor.RightExtent[i]);
            }
        float minimum = MinimumHalfWidth(variant);
        // Keep jumper low points 0.5m above railway portal-beam lower chord (6.27m).
        support = new PowerLinePath.Support
        {
            position = position, rotation = Quaternion.LookRotation(forward), variant = variant, node = node,
            railLeft = Mathf.Max(minimum,left + 1.2f), railRight = Mathf.Max(minimum,right + 1.2f),
            feedSide = Vector3.Dot(incoming,forward) >= 0 ? 1f : -1f,
            minimumBeamHeight = 6.27f + .5f + InsulatorLength + JumperSag
        };
        return true;
    }

    static float TerminalX(PowerLinePath.Support s, int circuit)
    {
        int count = Circuits(s);
        int index = count == 6 ? (circuit % 2 == 0 ? circuit / 2 : 5 - circuit / 2) : (circuit == 0 ? 0 : circuit == 1 ? 2 : 1);
        return Mathf.Lerp(-s.railLeft + .9f,s.railRight - .9f,(float)index / (count - 1));
    }

    public static Vector3 Attachment(PowerLinePath.Support s, int circuit, int bundle)
    {
        float offset = s.variant == Constants.PATH_VARIANT_GRID ? (bundle == 0 ? -.22f : .22f) : 0;
        return World(s,new Vector3(TerminalX(s,circuit) + offset,BeamHeight(s) - InsulatorLength,s.feedSide * HalfDepth));
    }

    public static Vector3 ShieldAttachment(PowerLinePath.Support s) =>
        World(s,new Vector3((s.railRight - s.railLeft) * .5f,BeamHeight(s) + TrussHeight + .7f * HeightScale,s.feedSide * HalfDepth));

    public static void Generate(GameObject parent, PowerLinePath.Support s, Material metal, DrawLod lod)
    {
        bool close = lod == DrawLod.Close;
        var mesh = new PathStructureMesh(parent,"RailPowerGantry",new[] { metal });
        float height = BeamHeight(s);
        int sides = close ? 6 : 4;
        Vector3 P(float x,float y,float z) => World(s,new Vector3(x,y,z));
        void Bar(Vector3 a,Vector3 b,float radius) => mesh.Bar(a,b,radius,sides:sides);
        foreach (float x in new[] { -s.railLeft,s.railRight })
            foreach (float z in new[] { -HalfDepth,HalfDepth })
            {
                Vector3 center = P(x,0,z);
                float bottom = Mathf.Min(0,Utils.GetGroundHeight(center) - s.position.y);
                float top = height + TrussHeight;
                const float half = .32f;
                for (int a = -1; a <= 1; a += 2) for (int b = -1; b <= 1; b += 2)
                    Bar(P(x+a*half,bottom,z+b*half),P(x+a*half,top,z+b*half),.075f);
                int levels = Mathf.Max(1,Mathf.CeilToInt((top-bottom)/2f));
                for (int i = 0; i < levels; i++)
                {
                    float y0 = Mathf.Lerp(bottom,top,(float)i/levels), y1 = Mathf.Lerp(bottom,top,(float)(i+1)/levels);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Bar(P(x-half,y0,z+side*half),P(x+half,y1,z+side*half),.035f);
                        Bar(P(x+side*half,y0,z-half),P(x+side*half,y1,z+half),.035f);
                        Bar(P(x-half,y1,z+side*half),P(x+half,y1,z+side*half),.045f);
                        Bar(P(x+side*half,y1,z-half),P(x+side*half,y1,z+half),.045f);
                    }
                }
            }
        void Truss(Vector3 a,Vector3 b)
        {
            Vector3 across = Vector3.Cross(Vector3.up,(b-a).normalized) * .24f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 lowA = a + across * side, lowB = b + across * side;
                Vector3 highA = lowA + Vector3.up * TrussHeight, highB = lowB + Vector3.up * TrussHeight;
                Bar(lowA,lowB,.08f); Bar(highA,highB,.08f);
                int bays = Mathf.Max(1,Mathf.CeilToInt(Vector3.Distance(a,b)/1.4f));
                for (int i = 0; i < bays; i++)
                {
                    float t0 = (float)i/bays, t1 = (float)(i+1)/bays;
                    Bar(Vector3.Lerp(lowA,lowB,t0),Vector3.Lerp(highA,highB,t1),.045f);
                    Bar(Vector3.Lerp(highA,highB,t0),Vector3.Lerp(lowA,lowB,t1),.045f);
                    Bar(Vector3.Lerp(lowA,lowB,t0),Vector3.Lerp(highA,highB,t0),.045f);
                }
            }
        }
        foreach (float z in new[] { -HalfDepth,HalfDepth }) Truss(P(-s.railLeft,height,z),P(s.railRight,height,z));
        foreach (float x in new[] { -s.railLeft,s.railRight }) Truss(P(x,height,-HalfDepth),P(x,height,HalfDepth));
        for (int circuit = 0; circuit < Circuits(s); circuit++)
        {
            float x = TerminalX(s,circuit);
            foreach (float z in new[] { -HalfDepth,HalfDepth })
            {
                Bar(P(x,height,z),P(x,height-InsulatorLength,z),.065f);
                int discs = close ? 9 : 5;
                for (int i = 0; i < discs; i++)
                {
                    float y = height - (.2f + i * 1.05f/(discs-1)) * HeightScale;
                    Bar(P(x,y-.035f*HeightScale,z),P(x,y+.035f*HeightScale,z),.16f);
                }
                if (s.variant == Constants.PATH_VARIANT_GRID)
                    Bar(P(x-.22f,height-InsulatorLength,z),P(x+.22f,height-InsulatorLength,z),.045f);
            }
            // Jumpers between front/rear beams stay above regular railway overhead wires.
            int steps = 12;
            Vector3 previous = P(x,height-InsulatorLength,-HalfDepth);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i/steps;
                Vector3 next = P(x,height-InsulatorLength-JumperSag*4*t*(1-t),Mathf.Lerp(-HalfDepth,HalfDepth,t));
                Bar(previous,next,.032f); previous = next;
            }
        }
        float centerX = (s.railRight-s.railLeft)*.5f;
        Bar(P(centerX,height+TrussHeight,s.feedSide*HalfDepth),ShieldAttachment(s),.06f);
        mesh.Finish();
    }
}
