using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>Towers use actual width; main cables follow span-dependent parabolas.</summary>
public static class SuspensionBridgeGenerator
{
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, float width)
    {
        var prefab = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/tower", null, out _);
        if (prefab == null) return;
        string truss = $"Models/Paths/p{path.path_variant}/truss";
        if (PathDrawContext.ResolvePrefab(truss, null, out _) != null)
            RoadPath.CreateSplineMesh(parent, spline, truss, width, 0, RoadPartHeights.BODY_ABOVE_SPLINE,
                stretchToWidth: false, maxTileLength: 0, normalizeBodySurface: false);
        float length = spline.CalculateLength();
        if (length < 8f) return;
        var filter = prefab.GetComponentInChildren<MeshFilter>();
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        if (filter == null || renderer == null) return;
        // Use a 12m two-lane bridge as reference; preserve tower proportions/roadside margins on wider bridges.
        float widthScale = Mathf.Max(1f, width / 12f);
        float height = Mathf.Clamp(length * .12f, 8f, 80f) * widthScale;
        float lateral = width*.5f + .8f * widthScale;
        var metal = System.Array.Find(renderer.sharedMaterials, m => m != null && m.name.StartsWith("PATH_METAL"));
        var batch = new PathStructureMesh(parent, "SuspensionCables", new[] { metal ?? renderer.sharedMaterial });
        Vector3 Deck(float t, float x)
        {
            var sample = spline.Evaluate(RoadPath.ClampSplinePercent(t));
            var right = Vector3.Cross(Vector3.up, sample.forward).normalized;
            return sample.position + right*x + Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE;
        }
        foreach (float t in new[] { .2f,.8f })
        {
            var sample = spline.Evaluate(t);
            var tower = Object.Instantiate(prefab,parent.transform);
            tower.name = "SuspensionTower";
            tower.transform.position = Deck(t,0);
            tower.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(sample.forward,Vector3.up).normalized);
            tower.transform.localScale = new Vector3(1,height/12f,1);
            foreach (var r in tower.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = r.sharedMaterials;
                for (int i=0;i<mats.Length;i++) mats[i]=RoadPath.GetOrCreateRoadMaterial(mats[i]);
                r.sharedMaterials=mats;
            }
            for (int side=-1;side<=1;side+=2)
            {
                Vector3 top=Deck(t,side*lateral);
                Vector3 ground=new Vector3(top.x,Utils.GetGroundHeight(top),top.z);
                if (top.y > ground.y) batch.Bar(ground,top,.85f * widthScale);
            }
        }
        for (int side=-1;side<=1;side+=2)
        {
            // Always include tower positions as subdivision boundaries so cables meet tower tops even on short bridges.
            float[] ends={0,.2f,.8f,1};
            for (int span=0;span<3;span++)
            {
                int count=Mathf.Clamp(Mathf.CeilToInt(length*(ends[span+1]-ends[span])/3f),4,256);
                Vector3 previous=default;
                for(int i=0;i<=count;i++)
                {
                    float u=(float)i/count;
                    float t=Mathf.Lerp(ends[span],ends[span+1],u);
                    float rise=span==1 ? height*(.25f+3f*(u-.5f)*(u-.5f))
                        : Mathf.Lerp(span==0 ? .8f : height,span==0 ? height : .8f,u)-height*.12f*4*u*(1-u);
                    Vector3 deck=Deck(t,side*lateral), cable=deck+Vector3.up*rise;
                    if(i>0) batch.Bar(previous,cable,.25f, sides: 12);
                    if(i>0 && i<count && rise>1f) batch.Bar(deck,cable,.035f);
                    previous=cable;
                }
            }
        }
        batch.Finish();
    }
}
