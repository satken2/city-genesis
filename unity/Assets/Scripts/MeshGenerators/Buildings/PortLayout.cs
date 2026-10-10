using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using UnityEngine;

/// <summary>
/// 港（MODEL_PORT）の陸側の配置計画。水際・ヤード・区画の辺0（道路側）から、施設モジュール（port_building）・
/// 荷役クレーン（port_crane）・ヤードの荷（段積みするコンテナ port_container、または地面に1個ずつ置く荷 port_cargo）の
/// 置き場を決める純データ。
///
/// 座標はすべて center_point 基準のローカルXZ。配置は通常モデル（無印）の寸法と入力の形だけで決まり、
/// 描画LODで変わらない（接写へ切り替えても建物や荷の位置がずれない）。
/// 施設モジュールの置き方は区画と通常モデルだけから決め、海岸線・埋立地に依存させない。
/// 間口の索引（<see cref="BuildingRoadAccess"/>）が建物データの登録時に同じ置き方を求めるため。
/// 仕様は city-genesis/docs/docs/geometries/F4_港湾岸壁のメッシュ生成.md。
/// </summary>
public static class PortLayout
{
    /// <summary>荷役クレーンの設置間隔（パーツ実寸幅に対する倍率）。1.0 だと隣同士が接するため余白を持たせる。</summary>
    public const float CRANE_SPACING_RATIO = 4.5f;

    /// <summary>1つの港に並べる荷役クレーンの上限。巨大な岸壁でドローコールが膨らむのを防ぐ。</summary>
    public const int MAX_CRANE_COUNT = 8;

    /// <summary>クレーンの陸側の端の後ろに空ける荷役用の車路の幅(m)。</summary>
    public const float APRON_LANE = 6f;

    /// <summary>施設モジュール・塔屋の周りに荷を置かない幅(m)。</summary>
    public const float FACILITY_CLEARANCE = 3f;

    /// <summary>ゲート（区画の辺0の中点）から水際の車路へ抜ける構内道路の幅(m)。荷を置かない。</summary>
    public const float DRIVE_WIDTH = 12f;

    /// <summary>ヤードの縁から荷までの距離(m)。</summary>
    public const float YARD_MARGIN = 1.5f;

    /// <summary>コンテナ（20ft ISO）の長さ・幅・高さ(m)。パーツ port_container はこの寸法で著作する。</summary>
    public const float CONTAINER_LENGTH = 6.06f;
    public const float CONTAINER_WIDTH = 2.44f;
    public const float CONTAINER_HEIGHT = 2.59f;

    /// <summary>段積みの1段の高さ(m)。隅金具の隙間ぶん実寸より少し高い。</summary>
    public const float CONTAINER_TIER_PITCH = CONTAINER_HEIGHT + 0.02f;

    private const float CONTAINER_GAP = 0.35f;          // 隣り合うコンテナの隙間(m)
    private const int BLOCK_BAYS = 8;                    // コンテナの1ブロックの、水際に沿う方向の個数
    private const int BLOCK_ROWS = 6;                    // コンテナの1ブロックの、奥行き方向の列数
    private const float BLOCK_AISLE = 8f;                // ブロックの間の通路(m)
    private const int MAX_TIERS = 4;                     // コンテナの段数の上限
    private const float FILL_RATIO = 0.8f;               // 置き場のうち実際に置く割合
    private const int MAX_STACKS = 800;                  // 1つの港に置く山の上限（巨大な区画で頂点が膨らむのを防ぐ）
    private const float FOOTPRINT_TOLERANCE = 0.05f;     // 施設モジュールが区画に収まるかの判定の余裕(m)
    private const float CARGO_GAP = 1f;                  // 地面に置く荷の隣との隙間(m)
    private const float MIN_CARGO_SIZE = 0.5f;           // 地面に置く荷の置き場の辺の下限(m)。寸法の無いパーツで格子が潰れないように

    /// <summary>
    /// 地面に置く荷のブロックの目安の大きさ(m)。コンテナの1ブロックと同じ大きさにそろえ、荷の種類が違っても通路の間隔を揃える。
    /// </summary>
    private const float CARGO_BLOCK_LENGTH = BLOCK_BAYS * (CONTAINER_LENGTH + CONTAINER_GAP);
    private const float CARGO_BLOCK_DEPTH = BLOCK_ROWS * (CONTAINER_WIDTH + CONTAINER_GAP);

    /// <summary>
    /// ヤードの荷の置き方。置き場（格子の1区画）の大きさ・隙間と、ブロックの個数・列数、段数の上限。
    /// Length は主な水際に沿う長さ、Width は奥行き(m)。
    /// </summary>
    public struct CargoSpec
    {
        public float Length;
        public float Width;
        public float Gap;
        public int Bays;
        public int Rows;
        public int MaxTiers;
    }

    /// <summary>20ft コンテナを段積みする置き方（port_container）。</summary>
    public static readonly CargoSpec ContainerSpec = new CargoSpec
    {
        Length = CONTAINER_LENGTH, Width = CONTAINER_WIDTH, Gap = CONTAINER_GAP, Bays = BLOCK_BAYS, Rows = BLOCK_ROWS, MaxTiers = MAX_TIERS,
    };

    /// <summary>
    /// 地面に1個ずつ置く荷（port_cargo。野積みの山・油槽・魚箱など）の置き方。bounds はパーツの外接箱の和（ローカル+Z が水際に沿う向き）。
    /// 置き場はそれを原点について対称に広げた矩形（原点が底面の中心から外れていても隣と重ならないように）で、荷は積まない。
    /// </summary>
    public static CargoSpec GroundCargoSpec(Bounds bounds)
    {
        float length = Mathf.Max(MIN_CARGO_SIZE, 2f * Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z)));
        float width = Mathf.Max(MIN_CARGO_SIZE, 2f * Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)));
        return new CargoSpec
        {
            Length = length,
            Width = width,
            Gap = CARGO_GAP,
            Bays = Mathf.Max(1, Mathf.RoundToInt(CARGO_BLOCK_LENGTH / length)),
            Rows = Mathf.Max(1, Mathf.RoundToInt(CARGO_BLOCK_DEPTH / width)),
            MaxTiers = 1,
        };
    }

    /// <summary>
    /// クレーンを並べる水際1本。P→Q は水際の線分、Normal は海を向く単位法線、Y はローカルの上面高さ。
    /// </summary>
    public struct Waterline
    {
        public Vector2 P;
        public Vector2 Q;
        public Vector2 Normal;
        public float Y;
    }

    /// <summary>施設モジュールの置き方。Rotation はモデルのローカル+X（Blender −X＝道路側）を区画の辺0の外向き法線へ向ける。</summary>
    public sealed class Module
    {
        public Vector2 Position;    // モデル原点
        public Quaternion Rotation;
        public Vector2[] Corners;   // 占有する矩形（CCW）
        public Vector2 Gate;        // 区画の辺0の中点。モジュールの接地高さと構内道路の起点
        public Vector2 Inward;      // 区画の辺0の内向き単位法線
    }

    /// <summary>荷役クレーン1基。原点は水際の線上、ローカル+Zを Normal（海）へ向ける。</summary>
    public struct Crane
    {
        public Vector2 Position;
        public Vector2 Normal;
        public float Y;
    }

    /// <summary>
    /// 荷の山1つ（コンテナは段積み、地面に置く荷は1個）。Center は底面の中心、Along は荷の長手の向き（主な水際に沿う）。
    /// Hash は段ごとの色を引く乱数の元、RowHash はブロックの1列で共通の乱数の元で、どちらも同じ入力からは同じ値になる。
    /// </summary>
    public struct Stack
    {
        public Vector2 Center;
        public Vector2 Along;
        public int Tiers;
        public uint Hash;
        public uint RowHash;
    }

    public sealed class Plan
    {
        public Module Module;
        public readonly List<Crane> Cranes = new List<Crane>();
        public readonly List<Stack> Stacks = new List<Stack>();
        public CargoSpec Cargo;     // Stacks の置き方（Stacks が空なら意味を持たない）
    }

    /// <summary>
    /// 配置計画を作る。
    /// footprint は区画（SHAPE_FULL の底面。CCW・辺0が道路側）、yard は荷を置ける地面（岸壁フットプリント、
    /// または埋立地の上の港では区画の陸の部分）、waterlines はクレーンを並べる水際。
    /// moduleBounds・craneBounds は通常モデルのローカルの外接箱（パーツが無ければ null）。
    /// obstacle は塔屋など荷を避ける追加の多角形（無ければ null）。
    /// 荷はクレーンが1基以上ある港で cargo が与えられたときだけ置く（クレーンと建物の間の荷役の空間を埋めるもの）。
    /// </summary>
    public static Plan Create(Vector2[] footprint, Vector2[] yard, IReadOnlyList<Waterline> waterlines,
        Bounds? moduleBounds, Bounds? craneBounds, Vector2[] obstacle, CargoSpec? cargo, float visualVariant2)
    {
        var plan = new Plan();
        if (footprint == null || footprint.Length < 3)
        {
            return plan;
        }
        if (moduleBounds.HasValue)
        {
            plan.Module = PlaceModule(footprint, moduleBounds.Value);
        }
        if (craneBounds.HasValue && waterlines != null)
        {
            PlaceCranes(plan, waterlines, craneBounds.Value);
        }
        if (cargo.HasValue && plan.Cranes.Count > 0 && yard != null && yard.Length >= 3)
        {
            uint seed = (uint)Mathf.RoundToInt(Mathf.Clamp01(visualVariant2) * 1000000f);
            float craneDepth = craneBounds.HasValue ? Mathf.Max(0f, -craneBounds.Value.min.z) : 0f;
            plan.Cargo = cargo.Value;
            PlaceCargo(plan, cargo.Value, yard, waterlines, craneDepth, obstacle, seed);
        }
        return plan;
    }

    /// <summary>
    /// 施設モジュールを区画の辺0（道路側）の中点に、道路側の面（ローカル+Xの端）を辺0に揃えて置く。
    /// 向きは既成建物と同じ <see cref="BuildingRoadAccess.PrebuiltRotation"/>（ローカル+Z が辺0の向き）。
    /// 占有矩形が区画に収まらなければ null（置かない）。
    /// </summary>
    public static Module PlaceModule(Vector2[] footprint, Bounds bounds)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }
        Vector2 a = footprint[0];
        Vector2 b = footprint[1];
        float length = (b - a).magnitude;
        if (length < 1e-3f)
        {
            return null;
        }
        Vector2 along = (b - a) / length;
        var outward = new Vector2(along.y, -along.x);
        Vector2 gate = (a + b) * 0.5f;
        float centerZ = (bounds.min.z + bounds.max.z) * 0.5f;
        Quaternion rotation = BuildingRoadAccess.PrebuiltRotation(footprint);
        Vector2 position = gate - outward * bounds.max.x - along * centerZ;
        Vector2[] corners = RotatedRect(position, rotation, bounds);

        try
        {
            var gf = new GeometryFactory();
            Geometry parcel = ToPolygon(gf, footprint);
            if (!parcel.IsValid)
            {
                parcel = parcel.Buffer(0);
            }
            if (!parcel.Buffer(FOOTPRINT_TOLERANCE).Covers(ToPolygon(gf, corners)))
            {
                return null;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortLayout] 施設モジュールの収まりの判定に失敗しました: {ex.Message}。置かないものとして扱います。");
            return null;
        }

        return new Module { Position = position, Rotation = rotation, Corners = corners, Gate = gate, Inward = -outward };
    }

    /// <summary>
    /// 水際に沿って荷役クレーンを等間隔に並べる。1本の水際の基数は長さ ÷（実寸幅 × <see cref="CRANE_SPACING_RATIO"/>）で、
    /// 水際全体に等間隔・両端の余白が等しくなるよう置く。実寸幅に満たない水際には置かず、水際の順に置いて
    /// 合計が <see cref="MAX_CRANE_COUNT"/> に達したら打ち切る。施設モジュールに掛かるクレーンは置かない
    /// （浅い区画でモジュールと水際が近いとき、脚が建物を貫かないため。モジュールは間口と一体なので動かさない）。
    /// </summary>
    private static void PlaceCranes(Plan plan, IReadOnlyList<Waterline> waterlines, Bounds craneBounds)
    {
        float width = Mathf.Max(craneBounds.size.x, 0.5f);
        float spacing = width * CRANE_SPACING_RATIO;
        Geometry module = null;
        var gf = new GeometryFactory();
        if (plan.Module != null)
        {
            module = ToPolygon(gf, plan.Module.Corners);
        }

        foreach (Waterline waterline in waterlines)
        {
            if (plan.Cranes.Count >= MAX_CRANE_COUNT)
            {
                break;
            }
            float length = (waterline.Q - waterline.P).magnitude;
            if (length < width)
            {
                continue;
            }
            Vector2 normal = waterline.Normal.normalized;
            Quaternion rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y), Vector3.up);
            int count = Mathf.Clamp(Mathf.FloorToInt(length / spacing), 1, MAX_CRANE_COUNT - plan.Cranes.Count);
            Vector2 dir = (waterline.Q - waterline.P) / length;
            for (int c = 0; c < count; c++)
            {
                Vector2 at = waterline.P + dir * (length * (c + 0.5f) / count);
                if (module != null && module.Intersects(ToPolygon(gf, RotatedRect(at, rotation, craneBounds))))
                {
                    continue;
                }
                plan.Cranes.Add(new Crane { Position = at, Normal = normal, Y = waterline.Y });
            }
        }
    }

    /// <summary>
    /// ヤードの空きに荷を置く。主な水際（最長）の向きに長手を揃えた格子を、水際の車路の陸側の端から
    /// 奥へ spec.Rows 列×spec.Bays 個のブロックと通路で区切り、ヤードの縁から
    /// <see cref="YARD_MARGIN"/> 内側に収まり、次を避ける置き場だけに置く。
    /// - 各水際のクレーン帯（水際から クレーンの陸側の端 + <see cref="APRON_LANE"/>）
    /// - 施設モジュールと追加の障害物の周り <see cref="FACILITY_CLEARANCE"/>
    /// - ゲートから内向きに伸びる幅 <see cref="DRIVE_WIDTH"/> の構内道路
    /// 置くかどうか・段数は置き場の格子番号と種から引くので、他の置き場の有無や列挙の順に影響されない。
    /// </summary>
    private static void PlaceCargo(Plan plan, CargoSpec spec, Vector2[] yard, IReadOnlyList<Waterline> waterlines,
        float craneDepth, Vector2[] obstacle, uint seed)
    {
        try
        {
            var gf = new GeometryFactory();
            Geometry yardGeom = ToPolygon(gf, yard);
            if (!yardGeom.IsValid)
            {
                yardGeom = yardGeom.Buffer(0);
            }
            Geometry inner = yardGeom.Buffer(-YARD_MARGIN);
            if (inner.IsEmpty)
            {
                return;
            }
            IPreparedGeometry allowed = PreparedGeometryFactory.Prepare(inner);

            // 主な水際と、荷の格子の向き。u=水際に沿う向き、v=陸へ向かう向き。
            Waterline main = waterlines[0];
            float mainLength = -1f;
            foreach (Waterline w in waterlines)
            {
                float l = (w.Q - w.P).magnitude;
                if (l > mainLength)
                {
                    mainLength = l;
                    main = w;
                }
            }
            if (mainLength < 1e-3f)
            {
                return;
            }
            Vector2 u = (main.Q - main.P) / mainLength;
            Vector2 v = -main.Normal.normalized;
            float zoneDepth = craneDepth + APRON_LANE;

            var blocked = new List<Geometry>();
            foreach (Waterline w in waterlines)
            {
                Vector2 inward = -w.Normal.normalized * zoneDepth;
                blocked.Add(ToPolygon(gf, new[] { w.P, w.Q, w.Q + inward, w.P + inward }));
            }
            if (plan.Module != null)
            {
                blocked.Add(ToPolygon(gf, plan.Module.Corners).Buffer(FACILITY_CLEARANCE));
                Envelope env = yardGeom.EnvelopeInternal;
                float reach = (float)(env.Width + env.Height) + DRIVE_WIDTH;
                Vector2 side = new Vector2(-plan.Module.Inward.y, plan.Module.Inward.x) * (DRIVE_WIDTH * 0.5f);
                Vector2 end = plan.Module.Gate + plan.Module.Inward * reach;
                blocked.Add(ToPolygon(gf, new[] { plan.Module.Gate - side, end - side, end + side, plan.Module.Gate + side }));
            }
            if (obstacle != null && obstacle.Length >= 3)
            {
                blocked.Add(ToPolygon(gf, obstacle).Buffer(FACILITY_CLEARANCE));
            }
            IPreparedGeometry blockedAll = PreparedGeometryFactory.Prepare(gf.BuildGeometry(blocked).Union());

            // 格子の原点は主な水際の始点から、水際に沿ってヤードの縁の余白だけ内側、クレーン帯の奥行きだけ陸側。
            // 始点はヤードの縁にあるので、余白をずらさないと端の置き場が縁の余白に掛かって必ず欠ける（小さな港で荷が無くなる）。
            // ヤードの範囲を (u, v) で測って走査する。
            Vector2 origin = main.P + u * YARD_MARGIN + v * zoneDepth;
            float uMin = float.PositiveInfinity, uMax = float.NegativeInfinity;
            float vMin = float.PositiveInfinity, vMax = float.NegativeInfinity;
            foreach (Vector2 p in yard)
            {
                float pu = Vector2.Dot(p - origin, u);
                float pv = Vector2.Dot(p - origin, v);
                uMin = Mathf.Min(uMin, pu); uMax = Mathf.Max(uMax, pu);
                vMin = Mathf.Min(vMin, pv); vMax = Mathf.Max(vMax, pv);
            }

            float slotU = spec.Length + spec.Gap;
            float slotV = spec.Width + spec.Gap;
            float pitchU = spec.Bays * slotU + BLOCK_AISLE;
            float pitchV = spec.Rows * slotV + BLOCK_AISLE;
            int blockU0 = Mathf.FloorToInt(uMin / pitchU), blockU1 = Mathf.FloorToInt(uMax / pitchU);
            int blockV0 = Mathf.FloorToInt(vMin / pitchV), blockV1 = Mathf.FloorToInt(vMax / pitchV);
            Vector2 halfU = u * (spec.Length * 0.5f);
            Vector2 halfV = v * (spec.Width * 0.5f);

            for (int bv = blockV0; bv <= blockV1; bv++)
            {
                for (int row = 0; row < spec.Rows; row++)
                {
                    float cv = bv * pitchV + row * slotV + slotV * 0.5f;
                    for (int bu = blockU0; bu <= blockU1; bu++)
                    {
                        uint rowHash = Hash(Hash(Hash(seed, bu), bv), -1 - row);
                        for (int bay = 0; bay < spec.Bays; bay++)
                        {
                            if (plan.Stacks.Count >= MAX_STACKS)
                            {
                                return;
                            }
                            float cu = bu * pitchU + bay * slotU + slotU * 0.5f;
                            uint hash = Hash(Hash(Hash(Hash(seed, bu), bay), bv), row);
                            if (Unit(hash) >= FILL_RATIO)
                            {
                                continue;
                            }
                            Vector2 center = origin + u * cu + v * cv;
                            Geometry slot = ToPolygon(gf, new[]
                            {
                                center - halfU - halfV, center + halfU - halfV, center + halfU + halfV, center - halfU + halfV,
                            });
                            if (!allowed.Contains(slot) || blockedAll.Intersects(slot))
                            {
                                continue;
                            }
                            int maxTiers = Mathf.Max(1, spec.MaxTiers);
                            int tiers = Mathf.Min(maxTiers, 1 + Mathf.FloorToInt(Unit(Hash(hash, 1)) * maxTiers));
                            plan.Stacks.Add(new Stack { Center = center, Along = u, Tiers = tiers, Hash = hash, RowHash = rowHash });
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortLayout] ヤードの荷の配置に失敗しました: {ex.Message}。荷を置きません。");
            plan.Stacks.Clear();
        }
    }

    /// <summary>山の t 段目（0が最下段）の色の番号（0〜count−1）。</summary>
    public static int TierColor(Stack stack, int tier, int count)
    {
        return count <= 0 ? 0 : (int)(Hash(stack.Hash, 2 + tier) % (uint)count);
    }

    /// <summary>
    /// 地面に置く荷の種類の番号（0〜count−1）。ブロックの1列で同じ種類にする。
    /// 野積みの山や油槽は品目ごとにまとめて並べるのが普通で、置き場ごとに変えると品目がまだらに混ざるため。
    /// </summary>
    public static int CargoKind(Stack stack, int count)
    {
        return count <= 0 ? 0 : (int)(Hash(stack.RowHash, 2) % (uint)count);
    }

    /// <summary>
    /// プレハブ配下の全メッシュの、プレハブルート基準の外接箱。<see cref="BuildingMeshUtils.CombineModuleMeshes"/> と同じ変換で、
    /// メッシュを作らずに求める。メッシュが無ければ null。
    /// </summary>
    public static Bounds? GetModelBounds(GameObject prefab)
    {
        if (prefab == null)
        {
            return null;
        }
        Matrix4x4 rootInverse = prefab.transform.localToWorldMatrix.inverse;
        Bounds? result = null;
        foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
            {
                continue;
            }
            Matrix4x4 toRoot = rootInverse * mf.transform.localToWorldMatrix;
            Bounds b = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = toRoot.MultiplyPoint3x4(corner);
                if (result.HasValue)
                {
                    Bounds grown = result.Value;
                    grown.Encapsulate(p);
                    result = grown;
                }
                else
                {
                    result = new Bounds(p, Vector3.zero);
                }
            }
        }
        return result;
    }

    /// <summary>外接箱の水平の矩形を、回転 rotation・原点 at に置いたときの4隅（ローカルXZ）。</summary>
    public static Vector2[] RotatedRect(Vector2 at, Quaternion rotation, Bounds bounds)
    {
        var corners = new Vector2[4];
        float[] xs = { bounds.min.x, bounds.max.x, bounds.max.x, bounds.min.x };
        float[] zs = { bounds.min.z, bounds.min.z, bounds.max.z, bounds.max.z };
        for (int i = 0; i < 4; i++)
        {
            Vector3 r = rotation * new Vector3(xs[i], 0f, zs[i]);
            corners[i] = at + new Vector2(r.x, r.z);
        }
        if (PolygonUtils.SignedArea(corners) < 0f)
        {
            System.Array.Reverse(corners);
        }
        return corners;
    }

    internal static Polygon ToPolygon(GeometryFactory gf, IReadOnlyList<Vector2> ring)
    {
        var coords = new Coordinate[ring.Count + 1];
        for (int i = 0; i < ring.Count; i++)
        {
            coords[i] = new Coordinate(ring[i].x, ring[i].y);
        }
        coords[ring.Count] = new Coordinate(ring[0].x, ring[0].y);
        return gf.CreatePolygon(coords);
    }

    private static uint Hash(uint a, int b)
    {
        uint h = a ^ ((uint)b * 0x9E3779B1u);
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static float Unit(uint h)
    {
        return (h & 0xFFFFFF) / 16777216f;
    }
}
