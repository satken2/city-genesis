using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    // ポイントやホバー位置の変更時に無効化する。
    private List<Vector3> cachedRenderPoints;

    /// <summary>
    /// 詳細メッシュ用の点列。アンカーの添字を固定し、操作中も前後のメッシュを保持できるようにする。
    /// 軽量プレビューと検証は同じ区間形状を、用途に必要な密度でサンプリングする。
    /// </summary>
    private List<Vector3> GetRenderPoints()
    {
        if (cachedRenderPoints != null) return cachedRenderPoints;
        var result = new List<Vector3>();
        for (int i = 0; i < points.Count; i++)
        {
            result.Add(points[i].position + Vector3.up * points[i].embankmentHeight);
            if (!Curved || i == points.Count - 1) continue;
            var span = GetShapeSpan(i, false);
            for (int j = 1; j <= bezierSamplesPerSegment; j++)
                result.Add(span.Sample((float)j / (bezierSamplesPerSegment + 1), surfaceOffset: RoadPartHeights.ROAD_SPLINE_OFFSET));
        }
        if (phase == RoadEditorPhase.Placing && points.Count > 0)
        {
            var hover = GetHoverPoint();
            result.Add(hover.position + Vector3.up * hover.embankmentHeight);
        }
        return cachedRenderPoints = result;
    }

    private PathBuildPoint GetHoverPoint()
    {
        Vector3 position = hoverWorldPosition;
        position.y = Utils.GetGroundHeight(position) + (hoverSnapWasElevated ? hoverSnapRelHeight : currentHeight);
        return new PathBuildPoint(position, PathPointRole.End) { embankmentHeight = currentEmbankmentHeight };
    }

    /// <summary>配置中の仮終点と直前の接線も含め、描画・検証が使う区間を組み立てる。</summary>
    private RoadShapeGeometry.Span GetShapeSpan(int index, bool placing)
    {
        var a = points[index];
        bool hover = placing && index == points.Count - 1;
        var b = hover ? GetHoverPoint() : points[index + 1];
        if (Curved && hover)
        {
            Vector3 previous = index > 0 ? points[index - 1].position : a.position;
            if (!a.control_out_manual || !a.control_out.HasValue)
                a.control_out = a.position + (b.position - previous) / 6f;
            b.control_in = b.position - (b.position - a.position) / 6f;
        }
        else if (Curved && placing && index + 1 == points.Count - 1 && !b.control_in_manual)
            b.control_in = b.position - (GetHoverPoint().position - a.position) / 6f;
        return RoadShapeGeometry.CreateSpan(a, b, Curved, EffectiveFollowTerrain(), PathManager.GetPathBaseHeight(PathSubtype));
    }

    /// <summary>
    /// 3Dプレビューを更新する。
    /// 各セグメントを三次ベジエとしてサンプリングし、細分化済みの点群を生成する。
    /// セグメント構成: P0[i].control_out → P1[i+1].control_in → P1[i+1].position
    /// 制御点がない場合は直線で補間する。
    /// </summary>
    private void Update3DPreview()
    {
        UpdateEarthworkPreview();
        // 非同期に届いた完成予定の縦断でも、警告を同じ形状から再計算する。
        cachedAlignmentViolations = null;
        if (roadRenderer == null) return;

        // 操作停止後は全線の造成後標高を使う。取得中・ドラッグ中は軽量なローカル表示を続ける。
        var profiles = earthworkPreview?.PathProfiles;
        if (phase == RoadEditorPhase.Editing && !isDragging && profiles != null
            && profiles.Count > 0 && profiles.TrueForAll(line => line.Count >= 2))
        {
            RegisterPreviewTracks(profiles[0], profiles);
            roadRenderer.UpdateSections(profiles[0], profiles[0], null, null, PathType,
                PathSubtype, Curved, PlatformSide, LateralOffsets, TravelDirections, HouseEnds, profiles, PassageMode);
            return;
        }
        List<Vector3> renderPoints = GetRenderPoints();
        UpdatePreviewSections(renderPoints);
    }

    private void UpdatePreviewSections(List<Vector3> renderPoints)
    {
        if (renderPoints.Count >= 2)
        {
            int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
            int firstMoving = points.Count - 1;
            int endMoving = points.Count - 1;
            bool placing = phase == RoadEditorPhase.Placing;
            // 仮終点を追加すると最後のアンカーの入口接線も変わるため、手前の1区間も帯で追従する。
            if (placing && Curved) firstMoving = Mathf.Max(0, points.Count - 2);
            if (isDragging && dragPointIndex >= 0)
            {
                // アンカー移動は隣接アンカーの自動接線にも影響する。
                // 制御点移動は反対側ハンドルの共線補正を含めた両隣を扱う。
                int reach = Curved && dragTarget == DragTarget.Point ? 2 : 1;
                firstMoving = Mathf.Max(0, dragPointIndex - reach);
                endMoving = Mathf.Min(points.Count - 1, dragPointIndex + reach);
            }
            int firstIndex = firstMoving * stride;
            int endIndex = endMoving * stride;
            List<Vector3> before = firstIndex > 0 ? renderPoints.GetRange(0, firstIndex + 1) : null;
            List<Vector3> after = endIndex < (points.Count - 1) * stride
                ? renderPoints.GetRange(endIndex, (points.Count - 1) * stride - endIndex + 1) : null;
            List<Vector3> moving = placing || isDragging
                ? GetMovingPreviewPoints(renderPoints, firstMoving, endMoving, placing) : null;
            RegisterPreviewTracks(renderPoints, null);
            roadRenderer.UpdateSections(renderPoints, before, moving, after, PathType,
                PathSubtype, Curved, PlatformSide, LateralOffsets, TravelDirections, HouseEnds, passageMode: PassageMode);
        }
        else
        {
            roadRenderer.ResetPreview();
        }
    }

    /// <summary>
    /// 確定済みの形を、完成済み線路の描画(つなぎ目のS字・並走の縫い合わせ・車止め)に参加する仮の相手として登録する
    /// (RoadRenderer.RegisterPreviewTracks)。配置中は仮終点を除いた確定済みの点まで、ドラッグ中は前の登録を保ち、
    /// 離したときに更新する(既存の線路の作り直しを、操作の区切りだけに抑える)。形状を編集中の計画は相手から外す。
    /// プレビューの線どうしも互いを相手にするので、プレビューのメッシュを作る前に呼ぶ。
    /// </summary>
    private void RegisterPreviewTracks(List<Vector3> renderPoints, List<List<Vector3>> profiles)
    {
        if (isDragging || roadRenderer == null) return;
        List<Vector3> committed = renderPoints;
        if (phase == RoadEditorPhase.Placing)
        {
            int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
            int end = (points.Count - 1) * stride;
            committed = points.Count >= 2 && end < renderPoints.Count ? renderPoints.GetRange(0, end + 1) : null;
            profiles = null;
        }
        roadRenderer.RegisterPreviewTracks(committed, PathType, PathSubtype, Curved, PlatformSide, LateralOffsets,
            TravelDirections, HouseEnds, PassageMode, pathId, profiles);
    }

    /// <summary>
    /// 帯にも地形追従・盛土・高架の高さを与える。保存形状は変更しない。
    /// 曲線と仮終点の接線は線形評価と同じサンプラーで生成する。
    /// </summary>
    private List<Vector3> GetMovingPreviewPoints(List<Vector3> renderPoints, int first, int end, bool placing)
    {
        return SamplePreviewSpans(renderPoints, first, end, placing, true);
    }

    /// <summary>描画と線形評価で仮終点の接線を共有し、評価には描画用の浮かせ量を含めない。</summary>
    private List<Vector3> SamplePreviewSpans(List<Vector3> renderPoints, int first, int end, bool placing, bool surfaceOffset, Vector3 origin = default, List<bool> followSamples = null)
    {
        var result = new List<Vector3>();
        for (int index = first; index < end + (placing ? 1 : 0); index++)
        {
            var span = GetShapeSpan(index, placing);
            int steps = span.DenseSteps;
            // 相対座標で補間し、大きなワールド座標の丸めが曲率へ増幅されるのを防ぐ。
            for (int i = 0; i < steps; i++)
            {
                result.Add(span.Sample((float)i / steps, origin, surfaceOffset ? RoadPartHeights.ROAD_SPLINE_OFFSET : 0f));
                followSamples?.Add(span.follows);
            }
        }
        int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
        result.Add((placing ? renderPoints[renderPoints.Count - 1] : renderPoints[end * stride]) - origin);
        if (followSamples != null) followSamples.Add(followSamples.Count > 0 && followSamples[followSamples.Count - 1]);
        return result;
    }
}
