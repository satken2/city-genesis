using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Bridge spans: one `span` model from the path's part folder per span between pier positions (RoadPath.PierPercents),
/// stretched along the deck from pier to pier. Trusses above the deck and arches below it are both span models,
/// so an appearance holding a `span` part draws its bridge with that structure; piers and the deck stay as they are.
/// Model: X and Y at actual size from the deck centerline at the road surface (the bed top for tracks). Only its length (Z extent)
/// scales to the span, so author it at the master pillar_spacing to keep proportions on full spans.
/// </summary>
public static class BridgeSpanGenerator
{
    public const string SpanPart = "span";

    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, float pillarSpacing, float baseHeight)
    {
        string name = $"{PathParts.Folder(path)}/{SpanPart}";
        if (ModelManager.Instance == null || ModelManager.Instance.GetModel(name, true) == null) return;
        List<double> piers = RoadPath.PierPercents(spline, path, pillarSpacing, baseHeight, out _);
        if (piers == null) return;
        for (int i = 1; i < piers.Count; i++)
        {
            if (piers[i] - piers[i - 1] < 1e-6) continue;
            RoadPath.CreateSplineMesh(parent, spline, name, 0f, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false,
                clipFrom: piers[i - 1], clipTo: piers[i], singleSpan: true, roadSurface: false);
        }
    }
}
