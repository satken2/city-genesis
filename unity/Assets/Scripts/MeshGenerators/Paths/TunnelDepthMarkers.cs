using Dreamteck.Splines;
using UnityEngine;

/// <summary>Depth guide attached to ceiling parts, sharing source updates/LOD/retirement/destruction.</summary>
public static class TunnelDepthMarkers
{
    private const float Spacing = 30f;
    private static readonly Color LineColor = new Color(.75f, .85f, .9f, .65f);
    private static readonly Color CeilingColor = new Color(.4f, .85f, 1f, .95f);
    private static readonly Color GroundColor = new Color(1f, .9f, .6f, .95f);

    public static void Create(GameObject part, SplineComputer spline, float topHeight, float lateral,
        PartWarp warp, double from, double to)
    {
        if (!UndergroundView.IsActive || !PathDrawContext.IntersectDetailRange(from, to, out from, out to)) return;
        float length = spline.CalculateLength();
        float start = spline.CalculateLength(0, from), end = spline.CalculateLength(0, to);
        if (end <= start) return;
        // Measure fixed spacing from the entire spline to avoid duplicate/shifted placements at LOD seams.
        float distance = Mathf.Ceil(start / Spacing - .5f) * Spacing + Spacing * .5f;
        if (length <= Spacing) distance = length * .5f;
        if (distance < start) return;
        OverlayMeshRenderer overlay = null;
        double percent = spline.Travel(0, distance, Spline.Direction.Forward);
        while (distance < end && percent < to)
        {
            var sample = spline.Evaluate(percent);
            Vector3 top = sample.position + sample.up * topHeight + sample.right *
                (lateral + (warp?.Shift != null ? warp.Shift(percent) : 0f));
            Vector3 ground = new Vector3(top.x, Utils.GetGroundHeight(top), top.z);
            if (ground.y > top.y)
            {
                if (overlay == null) overlay = OverlayMeshRenderer.CreateStatic("TunnelDepthMarkers", false, part.transform);
                overlay.AddSolidLine(top, ground, LineColor, 3f);
                overlay.AddCircle(top, 3f, CeilingColor);
                overlay.AddCircle(ground, 3f, GroundColor);
            }
            double next = spline.Travel(percent, Spacing, Spline.Direction.Forward);
            if (next <= percent) break;
            percent = next;
            distance += Spacing;
        }
        if (overlay != null) overlay.EndStaticFrame();
    }
}
