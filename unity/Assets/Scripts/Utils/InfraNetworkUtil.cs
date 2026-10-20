using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Utilities for node connection compatibility and snapping candidates during editing.
///
/// A node's type is a bitmask representing the connection types it accepts:
///   - Transport bits: NODE_ROAD / NODE_RAIL (shared for level crossings), NODE_MONORAIL, NODE_CONVEYOR (people movers, including ropeways), NODE_TRANSIT.
///   - Infrastructure bits: one unique bit per infrastructure good (GOODS_INFRASTRUCTURE). Its position follows
///     ascending product ID order across all infrastructure goods: 1 << (INFRA_BIT_BASE + rank).
///     Keep this consistent with backend MasterData.get_infra_node_bit.
///
/// A connection is compatible when the node type bitmask & path mask is nonzero.
/// Thus power lines (electricity mask) snap to nodes with an electricity bit, such as electrified tracks or shared terminals,
/// but not to water-pipe nodes or ordinary street nodes without that bit.
/// The same bitwise intersection also distinguishes networks such as electricity and water.
///
/// Track and monorail snapping requires the same path type. Electricity or shared level-crossing bits alone
/// cannot identify the intended track connection, so snapping is stricter than connection compatibility.
/// </summary>
public static class InfraNetworkUtil
{
    // Transport-bit range (0 .. INFRA_BIT_BASE-1). Higher bits represent infrastructure goods.
    private static int TransportBitMask => (1 << Constants.INFRA_BIT_BASE) - 1;

    // Cached infrastructure product IDs in ascending order for deterministic node-bit assignment.
    private static List<int> _infraProductIdsSorted;

    /// <summary>
    /// Return infrastructure product IDs that this path variant can carry.
    /// null for non-infrastructure variants or incomplete master data without infra_products.
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
    /// Return a cached list of all infrastructure product IDs in ascending order.
    /// Uses the same ordering as backend MasterData._infra_product_ids_sorted.
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
    /// Return the node bit for an infrastructure product ID; 0 for other goods or incomplete data.
    /// Keep equal to backend MasterData.get_infra_node_bit (electricity=32 / water=64).
    /// </summary>
    public static int GetInfraNodeBit(int productId)
    {
        int idx = InfraProductIdsSorted().IndexOf(productId);
        if (idx < 0) return 0;
        return 1 << (Constants.INFRA_BIT_BASE + idx);
    }

    /// <summary>
    /// Return whether a mask contains infrastructure-good bits, identifying infrastructure nodes/paths.
    /// </summary>
    public static bool IsInfraMask(int mask)
    {
        return (mask & ~TransportBitMask) != 0;
    }

    /// <summary>
    /// Return whether path_type is infrastructure: electricity, water, or pipelines. Keep consistent with
    /// backend consts.PATH_TYPES_INFRA. Node bits provide physical separation between networks,
    /// so use this only for shared infrastructure behavior independent of the good carried.
    /// </summary>
    public static bool IsInfraPathType(int pathType)
    {
        return pathType == Constants.PATH_ELECTRICITY
            || pathType == Constants.PATH_WATER
            || pathType == Constants.PATH_PIPELINE;
    }

    /// <summary>
    /// Return whether a path variant is infrastructure, such as a power line or water pipe.
    /// </summary>
    public static bool IsInfraPathVariant(int pathVariant)
    {
        var store = MasterDataStore.Instance;
        var master = store != null ? store.GetPathMaster(pathVariant) : null;
        return master != null && IsInfraPathType(master.type);
    }

    /// <summary>
    /// Return the node-bit mask its endpoints may connect to for a path variant.
    /// Same mapping as backend PathUtil.type_p2n: base transport bits | bits from infra_products.
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

        // OR infrastructure bits into variants that carry infrastructure goods, including infrastructure paths and electrified tracks.
        var products = GetPathInfraProducts(pathVariant);
        if (products != null)
        {
            foreach (var pid in products)
            {
                mask |= GetInfraNodeBit(pid);
            }
        }

        // If the mask is 0, such as with incomplete master data, fall back to road-node handling as before.
        return mask != 0 ? mask : Constants.NODE_ROAD;
    }

    /// <summary>
    /// Check infrastructure network connection compatibility, including electrified tracks, independently of snapping restrictions.
    /// </summary>
    public static bool IsNodeConnectable(NodeData node, int drawingVariant)
    {
        if (drawingVariant < 0 || node == null) return true;
        int requiredMask = GetRequiredNodeTypeForPath(drawingVariant);
        return (node.type & requiredMask) != 0;
    }

    /// <summary>
    /// Track nodes share a bit with roads, so restrict snapping by the types of actually connected paths.
    /// For nodes without connected paths, such as depot terminals, use only the bitwise intersection.
    /// drawingVariant < 0 means generic snapping outside road construction; apply no restriction.
    /// </summary>
    public static bool IsNodeSnappable(NodeData node, int drawingVariant, PathManager pathManager)
    {
        if (!IsNodeConnectable(node, drawingVariant)) return false;
        int? railType = GetRailSnapType(drawingVariant);
        if (!railType.HasValue || node == null) return true;
        if (pathManager == null) return false;
        var connected = pathManager.GetPathsConnectedToNode(node.node_id);
        if (connected.Count == 0) return true;
        foreach (var path in connected)
        {
            if (path.path_type == railType.Value) return true;
        }
        return false;
    }

    private static int? GetRailSnapType(int pathVariant)
    {
        if (pathVariant < 0) return null;
        var store = MasterDataStore.Instance;
        var master = store != null ? store.GetPathMaster(pathVariant) : null;
        return master != null && TrackIntersectionMeshGenerator.IsTrackType(master.type)
            ? master.type : (int?)null;
    }

    /// <summary>
    /// Return whether drawingVariant, the path being built, may snap to the edge or endpoint
    /// of existingVariant. drawingVariant < 0 means unrestricted.
    /// Track/monorail requires the same path type; other paths require shared node-mask bits.
    /// </summary>
    public static bool IsPathSnappable(int existingVariant, int drawingVariant)
    {
        if (drawingVariant < 0) return true;
        int? railType = GetRailSnapType(drawingVariant);
        if (railType.HasValue)
            return MasterDataStore.Instance.GetPathMaster(existingVariant)?.type == railType.Value;
        int drawingMask = GetRequiredNodeTypeForPath(drawingVariant);
        int existingMask = GetRequiredNodeTypeForPath(existingVariant);
        return (drawingMask & existingMask) != 0;
    }
}
