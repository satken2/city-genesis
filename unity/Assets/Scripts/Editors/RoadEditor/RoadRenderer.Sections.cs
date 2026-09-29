using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

public partial class RoadRenderer
{
    private readonly RoadRenderer[] sections = new RoadRenderer[3];
    private readonly List<Mesh> ownedMeshes = new List<Mesh>();
    private readonly List<Vector3> stripVertices = new List<Vector3>();
    private readonly List<Vector3> stripNormals = new List<Vector3>();
    private readonly List<int> stripTriangles = new List<int>();
    private bool isSection;

    /// <summary>
    /// 詳細な前後区間と、操作中の帯を独立した寿命で保持する。
    /// カーソル位置は詳細区間の点列に含めず、追従中の詳細生成を防ぐ。
    /// </summary>
    public void UpdateSections(List<Vector3> allPoints, List<Vector3> before,
        List<Vector3> moving, List<Vector3> after, int pathType, int variant, bool curved,
        int platformSide, float[] offsets, int[] directions, string houseEnds, List<List<Vector3>> profiles = null, string passageMode = "elevated")
    {
        if (previewMeshFilter == null) return;
        lastPathType = pathType;
        if (lastPreviewPoints == null) lastPreviewPoints = new List<Vector3>();
        lastPreviewPoints.Clear();
        lastPreviewPoints.AddRange(allPoints);
        bool station = pathType == Constants.PATH_STATION_HOUSE;
        UpdateSection(0, station ? allPoints : before, false);
        UpdateSection(1, station ? null : moving, true);
        UpdateSection(2, station ? null : after, false);

        void UpdateSection(int index, List<Vector3> points, bool simple)
        {
            var section = sections[index];
            if (points == null || points.Count < 2)
            {
                if (section != null) section.ResetPreview();
                return;
            }
            if (section == null)
            {
                var go = new GameObject($"RoadPreviewSection_{index}");
                go.transform.SetParent(previewMeshFilter.transform, false);
                section = go.AddComponent<RoadRenderer>();
                section.isSection = true;
                section.previewMeshFilter = go.AddComponent<MeshFilter>();
                section.previewMeshRenderer = go.AddComponent<MeshRenderer>();
                section.previewMaterial = previewMaterial;
                sections[index] = section;
            }
            if (simple)
                section.UpdateStrip(points, variant, platformSide, offsets, directions);
            else
            {
                bool startHouse = index == 0 && (houseEnds == "both" || houseEnds == "start");
                bool endHouse = (station || index == 2 || (moving == null && after == null))
                    && (houseEnds == "both" || houseEnds == "end");
                string sectionEnds = startHouse ? (endHouse ? "both" : "start") : (endHouse ? "end" : "none");
                section.UpdateRoadPreview(points, pathType, variant, curved, true,
                    platformSide, offsets, directions,
                    pathType == Constants.PATH_STATION_HOUSE ? sectionEnds : houseEnds, profiles, passageMode);
            }
        }
    }

    private void UpdateStrip(List<Vector3> points, int variant, int platformSide,
        float[] offsets, int[] directions)
    {
        if (previewMesh == null)
        {
            previewMesh = new Mesh { name = "MovingRoadPreview" };
            previewMesh.MarkDynamic();
            ownedMeshes.Add(previewMesh);
            previewMeshFilter.sharedMesh = previewMesh;
            previewMeshRenderer.sharedMaterial = previewMaterial;
            previewMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            previewMeshRenderer.receiveShadows = false;
        }
        stripVertices.Clear();
        stripNormals.Clear();
        stripTriangles.Clear();
        var master = MasterDataStore.Instance?.GetPathMaster(variant);
        float width = (master?.width ?? 0f);
        float platformEdge = TrackPath.PlatformEdgeDistance(width, Mathf.Max(1, master?.lanes ?? 1));
        int count = offsets != null && offsets.Length > 0 ? offsets.Length : 1;
        for (int line = 0; line < count; line++)
        {
            float offset = offsets != null && offsets.Length > 0 ? offsets[line] : 0f;
            var path = new PathSummary
            {
                path_variant = variant,
                platform_side = platformSide,
                travel_direction = directions != null && line < directions.Length
                    ? directions[line] : Constants.DIR_BOTH
            };
            TrackPath.ResolvePlatformExtents(path, master?.platform_width ?? 0f,
                out float left, out float right);
            AddStrip(offset - width * 0.5f, offset + width * 0.5f, RoadPartHeights.BODY_ABOVE_SPLINE);
            if (left > 0f)
                AddStrip(offset - width * 0.5f - left, offset - platformEdge, RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE);
            if (right > 0f)
                AddStrip(offset + platformEdge, offset + width * 0.5f + right, RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE);
        }
        previewMesh.Clear();
        previewMesh.indexFormat = stripVertices.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        previewMesh.SetVertices(stripVertices);
        previewMesh.SetNormals(stripNormals);
        previewMesh.SetTriangles(stripTriangles, 0);
        previewMesh.RecalculateBounds();

        void AddStrip(float left, float right, float height)
        {
            var leftPoints = OffsetPolylineLateral(points, left);
            var rightPoints = OffsetPolylineLateral(points, right);
            int start = stripVertices.Count;
            for (int i = 0; i < points.Count; i++)
            {
                stripVertices.Add(rightPoints[i] + Vector3.up * height);
                stripVertices.Add(leftPoints[i] + Vector3.up * height);
                stripNormals.Add(Vector3.up);
                stripNormals.Add(Vector3.up);
                if (i == points.Count - 1) continue;
                int v = start + i * 2;
                stripTriangles.Add(v); stripTriangles.Add(v + 1); stripTriangles.Add(v + 2);
                stripTriangles.Add(v + 2); stripTriangles.Add(v + 1); stripTriangles.Add(v + 3);
            }
        }
    }

    private void ReleaseOwnedMeshes()
    {
        foreach (var mesh in ownedMeshes)
            if (mesh != null) Destroy(mesh);
        ownedMeshes.Clear();
        previewMesh = null;
    }

    private void OnDestroy()
    {
        ResetPreview();
        foreach (var section in sections)
            if (section != null) Destroy(section.gameObject);
    }
}
