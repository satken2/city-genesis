using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ノードの溶接互換（吸着可否）判定ユーティリティ。バックエンドの溶接ルールをフロントで再現する。
///
/// ノードの type はビットフラグで、「そのノードが受け入れる接続種別の集合」を表す:
///   - 交通ビット: NODE_ROAD / NODE_RAIL（道路・線路は共有＝踏切）/ NODE_MONORAIL / NODE_CONVEYOR（人員搬送。ロープウェイ等） / NODE_TRANSIT
///   - インフラビット: インフラ財（GOODS_INFRASTRUCTURE）ごとの固有ビット。ビット位置は
///     全インフラ財の製品IDを昇順に並べた順位で決まる（1 << (INFRA_BIT_BASE + 順位)）。
///     バックエンドの MasterData.get_infra_node_bit と一致させること。
///
/// 溶接（吸着）は「ノードの type ビットマスク & そのパスのマスク が非0」で判定する。
/// これにより、送電線（電力ビットのマスク）は電力ビットを持つノード（電化線路・共用端子）へ
/// 吸着でき、導水管ノード（水道ビット）や普通の街路ノード（電力ビット無し）へは吸着しない。
/// 網互換（電力 vs 水道）の判定もこのビット積に畳み込まれている。
///
/// 道路建設時のスナップ候補からインフラノードを除外し、インフラ建設時は同一網の相手にのみ
/// 吸着させるために使う。
/// </summary>
public static class InfraNetworkUtil
{
    // 交通ビットの範囲（0 .. INFRA_BIT_BASE-1）。これより上位のビットがインフラ財ビット。
    private static int TransportBitMask => (1 << Constants.INFRA_BIT_BASE) - 1;

    // 全インフラ財の製品IDを昇順に並べたキャッシュ（ノードビットの決定的割り当て用）。
    private static List<int> _infraProductIdsSorted;

    /// <summary>
    /// パスバリアントが流せるインフラ財の製品ID集合を返す。
    /// インフラ以外のバリアント、およびマスター未整備（infra_products 無し）の場合は null。
    /// </summary>
    public static HashSet<int> GetPathInfraProducts(int pathVariant)
    {
        var master = MasterDataStore.Instance != null
            ? MasterDataStore.Instance.GetPathMaster(pathVariant)
            : null;
        if (master == null || master.infra_products == null || master.infra_products.Count == 0)
        {
            return null;
        }
        return new HashSet<int>(master.infra_products);
    }

    private static bool IsInfraProduct(MasterDataStore store, int productId)
    {
        var product = store.GetProductMaster(productId);
        return product != null && product.type == Constants.GOODS_INFRASTRUCTURE;
    }

    /// <summary>
    /// 全インフラ財の製品IDを昇順に並べたリストを返す（キャッシュ）。
    /// バックエンド MasterData._infra_product_ids_sorted と同じ順序基盤。
    /// </summary>
    private static List<int> InfraProductIdsSorted()
    {
        if (_infraProductIdsSorted != null) return _infraProductIdsSorted;
        var result = new List<int>();
        var store = MasterDataStore.Instance;
        if (store != null)
        {
            foreach (var kv in store.GetProductMasterDictionary())
            {
                if (int.TryParse(kv.Key, out var pid)
                    && kv.Value != null
                    && kv.Value.type == Constants.GOODS_INFRASTRUCTURE)
                {
                    result.Add(pid);
                }
            }
        }
        result.Sort();
        _infraProductIdsSorted = result;
        return result;
    }

    /// <summary>
    /// インフラ財の製品IDに対応するノードビットを返す（非インフラ財・未整備は0）。
    /// バックエンド MasterData.get_infra_node_bit と一致させること（電力→32 / 水道→64）。
    /// </summary>
    public static int GetInfraNodeBit(int productId)
    {
        int idx = InfraProductIdsSorted().IndexOf(productId);
        if (idx < 0) return 0;
        return 1 << (Constants.INFRA_BIT_BASE + idx);
    }

    /// <summary>
    /// マスクがインフラ財ビットを含むか（＝インフラノード/インフラパスか）を返す。
    /// </summary>
    public static bool IsInfraMask(int mask)
    {
        return (mask & ~TransportBitMask) != 0;
    }

    /// <summary>
    /// パス種別（path_type）がインフラ（電気・水）かを返す。バックエンド consts.PATH_TYPES_INFRA
    /// と一致させること。電気網/水網の物理的分離はノードビット側で行われるため、ここは
    /// 「財を区別しない共通のインフラ挙動」の判定にのみ使う。
    /// </summary>
    public static bool IsInfraPathType(int pathType)
    {
        return pathType == Constants.PATH_ELECTRICITY || pathType == Constants.PATH_WATER;
    }

    /// <summary>
    /// パスバリアントが（送電線・導水管などの）インフラパスかを返す。
    /// </summary>
    public static bool IsInfraPathVariant(int pathVariant)
    {
        var store = MasterDataStore.Instance;
        var master = store != null ? store.GetPathMaster(pathVariant) : null;
        return master != null && IsInfraPathType(master.type);
    }

    /// <summary>
    /// パスバリアントから、その端点が接続してよいノードのビットマスクを返す。
    /// バックエンド PathUtil.type_p2n と同じ写像（交通基本ビット | infra_products 由来ビット）。
    /// </summary>
    public static int GetRequiredNodeTypeForPath(int pathVariant)
    {
        var store = MasterDataStore.Instance;
        if (store == null) return Constants.NODE_ROAD;

        var master = store.GetPathMaster(pathVariant);
        if (master == null) return Constants.NODE_ROAD;

        int mask = 0;
        switch (master.type)
        {
            case Constants.PATH_MOTORWAY:
            case Constants.PATH_STREET:
            case Constants.PATH_WALKWAY:
            case Constants.PATH_TRACK:
                mask |= Constants.NODE_ROAD | Constants.NODE_RAIL;
                break;
            case Constants.PATH_MONORAIL:
                mask |= Constants.NODE_MONORAIL;
                break;
            case Constants.PATH_CONVEYOR:
                mask |= Constants.NODE_CONVEYOR;
                break;
            case Constants.PATH_TRANSPORT:
                mask |= Constants.NODE_TRANSIT;
                break;
        }

        // インフラ財を流せるバリアント（インフラパス本体・電化線路など）にはインフラビットを OR する。
        var products = GetPathInfraProducts(pathVariant);
        if (products != null)
        {
            foreach (var pid in products)
            {
                mask |= GetInfraNodeBit(pid);
            }
        }

        // マスクが0（マスター未整備等）の場合は従来どおり道路ノード扱いにフォールバックする。
        return mask != 0 ? mask : Constants.NODE_ROAD;
    }

    /// <summary>
    /// 建設中のパスバリアント drawingVariant がノード node に吸着してよいかを返す。
    /// drawingVariant < 0 は「制限なし」（道路建設以外の汎用スナップ）。
    /// 溶接互換は node.type ビットマスクと drawingVariant のマスクのビット積で判定する
    /// （網互換もこのビット積に畳み込まれているため、製品集合の別途照合は不要）。
    /// pathManager は将来の拡張用に残す（現行の判定には不要）。
    /// </summary>
    public static bool IsNodeSnappable(NodeData node, int drawingVariant, PathManager pathManager)
    {
        if (drawingVariant < 0 || node == null) return true;
        int requiredMask = GetRequiredNodeTypeForPath(drawingVariant);
        return (node.type & requiredMask) != 0;
    }

    /// <summary>
    /// 建設中のパスバリアント drawingVariant が既存パス existingVariant（の辺・端点）に
    /// 吸着してよいかを返す。drawingVariant < 0 は「制限なし」。
    /// 両パスのノードマスクのビット積が非0なら同系統（吸着可）とみなす。
    /// </summary>
    public static bool IsPathSnappable(int existingVariant, int drawingVariant)
    {
        if (drawingVariant < 0) return true;
        int drawingMask = GetRequiredNodeTypeForPath(drawingVariant);
        int existingMask = GetRequiredNodeTypeForPath(existingVariant);
        return (drawingMask & existingMask) != 0;
    }
}
