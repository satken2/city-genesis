using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地下パス（トンネル）の区間分類ユーティリティ。
///
/// パスのY座標は「地面からの相対高さ」であり、負の値は地下を表す。
/// 描画上は、各サンプル点での「深さ = 現在の地形標高 − 道路のワールドY」を
/// マスターデータのクリアランス高さ（height）と比較して区間を分類する。
///
/// - 深さ ≤ height: 開削（切り通し）区間。地形はバックエンドが着工時に
///   道路標高まで掘削するため、通常どおりメッシュを描画する。
/// - 深さ > height: 完全埋設（トンネル）区間。地形は変形されないため
///   メッシュ生成をスキップし、境界（坑口）に portal パーツを配置する。
///
/// この分類は「サンプル点ごとの深さ関数」として純粋に決まるため、1本のパス内で
/// 開削⇔トンネルを何度でも遷移でき、坑口は閾値を跨ぐすべての境界点に現れる。
/// バックエンド側の同じ分類は genesis-core の geo_path_depth.py にあり、
/// 「掘削済みの開削区間は深さ≈0になる」ことで両者の分類が整合する。
/// </summary>
public static class TunnelSectionUtil
{
    /// <summary>相対Yがこの値より小さい（負側）のアンカーを持つパスを地下パスとみなす。</summary>
    public const float UNDERGROUND_REL_Y_EPS = 0.01f;

    /// <summary>
    /// 「地表に張り付いている」とみなすY差の許容値(m)。
    /// 地形追従点は地形+0.1mに置かれるため、それを地表として扱えるだけの余裕を持たせる。
    /// </summary>
    public const float GROUND_SNAP_EPSILON = 0.5f;

    /// <summary>深さ判定のサブサンプリング間隔(m)。CalculatePathPointsのサンプリング間隔と同じ。</summary>
    public const float SAMPLE_INTERVAL = 5f;

    /// <summary>坑口（トンネル出入口）の配置情報。</summary>
    public struct PortalInfo
    {
        /// <summary>坑口の位置（道路中心線上、深さがクリアランスを跨ぐ点）。</summary>
        public Vector3 position;

        /// <summary>トンネル内部へ向かう水平方向（正規化済み）。</summary>
        public Vector3 direction;
    }

    /// <summary>
    /// パスが地下区間を持ち得るか（いずれかのアンカーの相対Yが負か）を返す。
    /// バックエンドの is_underground_path と同一の定義。
    /// </summary>
    public static bool IsUndergroundPath(GeneratedModels.PathSummary path)
    {
        if (path == null) return false;

        var startList = path.GetStartPointList();
        if (startList != null && startList.Count >= 3 && startList[1] < -UNDERGROUND_REL_Y_EPS)
            return true;

        var endList = path.GetEndPointList();
        if (endList != null && endList.Count >= 3 && endList[1] < -UNDERGROUND_REL_Y_EPS)
            return true;

        var waypoints = path.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints)
            {
                if (wp.position != null && wp.position.Length >= 3 && wp.position[1] < -UNDERGROUND_REL_Y_EPS)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// バリアントのクリアランス高さ(m)を返す。切り通し⇔トンネルの分類閾値として
    /// バッファなしでそのまま使う。マスター未取得時は0（地表以下はすべてトンネル扱い）。
    /// </summary>
    public static float GetClearanceHeight(int pathVariant)
    {
        var store = MasterDataStore.Instance;
        if (store == null || !store.IsInitialized) return 0f;

        var master = store.GetPathMaster(pathVariant);
        if (master != null && master.height.HasValue) return master.height.Value;
        return 0f;
    }

    /// <summary>
    /// 地下パスの点列を「描画する連続区間（開削・地表・高架）」のリストに分割する。
    /// トンネル区間（深さ > clearance）の点は除外され、境界には深さがちょうど
    /// clearance になる補間点が追加される。境界の坑口情報は portals に格納される。
    ///
    /// 点列の各ペア間は SAMPLE_INTERVAL でサブサンプリングして判定する。
    /// ただし両端点がともに地表に張り付いている（|地形−Y| ≤ GROUND_SNAP_EPSILON）
    /// 区間は地形追従区間とみなし、深さ0（＝描画対象）として扱う。
    /// これは CalculatePathPoints の followTerrain 分岐と同じ考え方で、
    /// 地表区間のY線形補間による誤ったトンネル判定を防ぐ。
    /// </summary>
    /// <param name="points">CalculatePathPoints が返したワールド座標の点列</param>
    /// <param name="clearance">クリアランス高さ(m)</param>
    /// <param name="portals">坑口の位置・向きの出力先（null可）</param>
    /// <returns>描画区間ごとの点リスト。全埋設の場合は空リスト。</returns>
    public static List<List<Vector3>> SplitDrawableRuns(
        List<Vector3> points, float clearance, List<PortalInfo> portals,
        bool pathFollowsTerrain = true)
    {
        return SplitRuns(points, clearance, keepTunnel: false, portals, pathFollowsTerrain);
    }

    /// <summary>
    /// 点列にトンネル区間（深さ &gt; clearance）が1つでも存在するかを安価に判定する。
    /// 相対Y=0でも実効非追従のパスが丘に潜るケースを検出するために使う。
    /// pathFollowsTerrain=false のときは IsOnGround ショートカットを無効化して
    /// （地表近くを通る非追従デッキが誤って追従扱いされないよう）常に深さ評価する。
    /// sampleInterval を粗くすれば Far ティア用に軽量判定できる。
    /// </summary>
    public static bool HasTunnelSection(
        List<Vector3> points, float clearance, bool pathFollowsTerrain,
        float sampleInterval = SAMPLE_INTERVAL)
    {
        if (points == null || points.Count < 2) return false;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            bool followsTerrain = pathFollowsTerrain && IsOnGround(a) && IsOnGround(b);
            if (followsTerrain) continue;

            float horizontal = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            int subdivisions = Mathf.Max(1, Mathf.CeilToInt(horizontal / sampleInterval));
            for (int s = 0; s < subdivisions; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, (float)s / subdivisions);
                if (GetDepth(p) > clearance) return true;
            }
        }
        Vector3 last = points[points.Count - 1];
        bool lastOnGround = pathFollowsTerrain && IsOnGround(last);
        return !lastOnGround && GetDepth(last) > clearance;
    }

    /// <summary>
    /// 点列からトンネル区間（深さ > clearance）の連続区間だけを取り出す。
    /// 道路建設エディタのプレビューで、地形に隠れるトンネル区間を
    /// オーバーレイ表示するために使う。区間の両端には境界の補間点を含む。
    /// </summary>
    public static List<List<Vector3>> CollectTunnelRuns(
        List<Vector3> points, float clearance, bool pathFollowsTerrain = true)
    {
        return SplitRuns(points, clearance, keepTunnel: true, portals: null, pathFollowsTerrain);
    }

    /// <summary>
    /// 点列をトンネル判定でサブサンプリングし、keepTunnel=false なら描画区間
    /// （非トンネル）、true ならトンネル区間の連続区間リストを返す。
    /// 区間の境界には深さがちょうど clearance になる補間点が追加され、
    /// portals が指定されていれば坑口情報も格納される。
    /// </summary>
    private static List<List<Vector3>> SplitRuns(
        List<Vector3> points, float clearance, bool keepTunnel, List<PortalInfo> portals,
        bool pathFollowsTerrain = true)
    {
        var runs = new List<List<Vector3>>();
        if (points == null || points.Count < 2) return runs;

        // 点列をサブサンプリングしながら、各点のトンネル判定を行う
        var samples = new List<Vector3>();
        var isTunnel = new List<bool>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];

            // pathFollowsTerrain=false（実効非追従パス）は地表張り付きショートカットを
            // 無効化し、地表近くを通るデッキが誤って追従扱いされて丘の隆起を見逃すのを防ぐ。
            bool followsTerrain = pathFollowsTerrain && IsOnGround(a) && IsOnGround(b);

            float horizontal = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            int subdivisions = Mathf.Max(1, Mathf.CeilToInt(horizontal / SAMPLE_INTERVAL));

            for (int s = 0; s < subdivisions; s++)
            {
                float t = (float)s / subdivisions;
                Vector3 p = Vector3.Lerp(a, b, t);
                samples.Add(p);
                isTunnel.Add(!followsTerrain && GetDepth(p) > clearance);
            }
        }
        // 最終点
        Vector3 last = points[points.Count - 1];
        samples.Add(last);
        bool lastOnGround = pathFollowsTerrain && IsOnGround(last);
        isTunnel.Add(!lastOnGround && GetDepth(last) > clearance);

        // 対象区分（keepTunnelに応じてトンネル側/描画側）の連続区間を切り出す
        List<Vector3> current = null;
        for (int i = 0; i < samples.Count; i++)
        {
            bool isTarget = isTunnel[i] == keepTunnel;
            if (isTarget)
            {
                if (current == null)
                {
                    current = new List<Vector3>();
                    // 区分の切り替わり: 境界の補間点（坑口）を区間の先頭に追加
                    if (i > 0)
                    {
                        Vector3 tunnelSide = keepTunnel ? samples[i] : samples[i - 1];
                        Vector3 openSide = keepTunnel ? samples[i - 1] : samples[i];
                        Vector3 boundary = InterpolateBoundary(tunnelSide, openSide, clearance);
                        current.Add(boundary);
                        if (!keepTunnel)
                        {
                            AddPortal(portals, boundary, samples[i - 1] - samples[i]);
                        }
                    }
                }
                current.Add(samples[i]);
            }
            else
            {
                if (current != null)
                {
                    // 区分の切り替わり: 境界の補間点（坑口）を区間の末尾に追加
                    Vector3 tunnelSide = keepTunnel ? samples[i - 1] : samples[i];
                    Vector3 openSide = keepTunnel ? samples[i] : samples[i - 1];
                    Vector3 boundary = InterpolateBoundary(tunnelSide, openSide, clearance);
                    current.Add(boundary);
                    if (!keepTunnel)
                    {
                        AddPortal(portals, boundary, samples[i] - samples[i - 1]);
                    }

                    if (current.Count >= 2) runs.Add(current);
                    current = null;
                }
            }
        }
        if (current != null && current.Count >= 2) runs.Add(current);

        return runs;
    }

    /// <summary>点が地表に張り付いているか（地形追従点か）を判定する。</summary>
    private static bool IsOnGround(Vector3 p)
    {
        float ground = HeightmapTextureManager.GetTerrainHeight(new Vector3(p.x, 1000f, p.z));
        return Mathf.Abs(ground - p.y) <= GROUND_SNAP_EPSILON;
    }

    /// <summary>点の深さ（地形標高 − 道路Y。正=地下）を返す。</summary>
    private static float GetDepth(Vector3 p)
    {
        float ground = HeightmapTextureManager.GetTerrainHeight(new Vector3(p.x, 1000f, p.z));
        return ground - p.y;
    }

    /// <summary>
    /// トンネル側の点(tunnelSide)と描画側の点(openSide)の間で、
    /// 深さがちょうど clearance になる境界点を線形補間で求める。
    /// </summary>
    private static Vector3 InterpolateBoundary(Vector3 tunnelSide, Vector3 openSide, float clearance)
    {
        float depthTunnel = GetDepth(tunnelSide);
        float depthOpen = GetDepth(openSide);

        float denom = depthTunnel - depthOpen;
        if (Mathf.Abs(denom) < 1e-4f) return openSide;

        // openSide → tunnelSide 方向へ、深さが clearance になる位置
        float t = (clearance - depthOpen) / denom;
        t = Mathf.Clamp01(t);
        return Vector3.Lerp(openSide, tunnelSide, t);
    }

    private static void AddPortal(List<PortalInfo> portals, Vector3 position, Vector3 towardTunnel)
    {
        if (portals == null) return;

        Vector3 dir = new Vector3(towardTunnel.x, 0f, towardTunnel.z);
        if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
        portals.Add(new PortalInfo
        {
            position = position,
            direction = dir.normalized,
        });
    }
}
