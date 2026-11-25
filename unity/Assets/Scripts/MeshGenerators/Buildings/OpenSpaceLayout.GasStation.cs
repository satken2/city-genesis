using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>
/// Placement plan for gas stations. The whole lot is the paved forecourt, concrete slabs with joints square to the road-facing edge,
/// laid out as a parking lot (OpenSpaceLayout.Lot.cs): a planted
/// strip with a curb around its edge, driveways on the road-facing edge, parking stalls before the shop, in rows across open ground,
/// and along the free edges, and the price sign in the strip. The shop stands in the rear corner, and fueling bays (one part each: a
/// roof span over an island with two pumps) join edge to edge into one canopy between the shop and the road, or beside the shop where
/// that holds more. The number of bays follows the lot's area, as the station's capacity does. Along the side away from the shop stand
/// the underground tanks' fill covers and vents, an air station, and a dumpster.
/// Built in local coordinates relative to the road-facing edge (s along the edge, t inward), mirrored left/right by the seed.
/// Parts missing from the model folder are skipped.
/// </summary>
public static partial class OpenSpaceLayout
{
    const double GasBayArea = 300;        // Lot area per fueling bay (m²).
    const double GasFuelingLane = 1.95;   // From an island's center line to the center of a car being fueled (m), as site_canopy is authored.
    const double GasSlab = 5.0;           // Spacing of the joints between the forecourt's concrete slabs (m).
    const float GasCarAtPump = .5f, GasTanker = .35f;  // Chance that a car stands at a pump, that a tanker is unloading.

    /// <summary>Whether a model type's plan lays out the building's whole parcel rather than its footprint, which does not fill the parcel.</summary>
    public static bool CoversParcel(int modelType) => modelType == Constants.MODEL_GAS_STATION || modelType == Constants.MODEL_HUTS;

    static void GasStation(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        Frame f = FacilityFrame(p, seed);
        Geometry site = LocalSite(p, f);
        if (site == null || site.Area < 12) return;
        double edge = FacilityEdge(site), halfFront = Vector2.Distance(p.Polygon[0], p.Polygon[1]) * .5;
        Geometry planter = LotEdge(p, f, site, edge, halfFront, out Polygon lot, out var driveways);
        if (lot == null) return;
        // The forecourt is concrete slabs: joints square to the road-facing edge over the paved ground.
        Geometry paved = Minus(site, planter);
        var extent = lot.EnvelopeInternal;
        for (double s = Math.Ceiling(extent.MinX / GasSlab) * GasSlab; s < extent.MaxX; s += GasSlab)
            Emit(p, f, And(Rect(s - .04, -1, s + .04, extent.MaxY), paved), "site_joint", .016f);
        for (double t = GasSlab; t < extent.MaxY; t += GasSlab)
            Emit(p, f, And(Rect(extent.MinX, t - .04, extent.MaxX, t + .04), paved), "site_joint", .016f);
        if (sizes == null || sizes.Count == 0) return;

        // The fullest arrangement that still leaves room for a fueling bay: the shop with parking before it, the shop alone,
        // the small office, then pumps only.
        int bays = Math.Max(1, (int)Math.Round(site.Area / GasBayArea));
        FacilityYard y = null;
        foreach (var (building, parking) in new[] { ("site_store", true), ("site_store", false), ("site_office", false), ((string)null, false) })
        {
            int props = p.Props.Count, surfaces = p.Surfaces.Count;
            y = new FacilityYard { Plan = p, F = f, Sizes = sizes, Site = site, Env = lot.EnvelopeInternal, Edge = edge, Field = lot };
            y.Prepare();
            if (GasForecourt(y, seed, building, parking, bays)) break;
            p.Props.RemoveRange(props, p.Props.Count - props);
            p.Surfaces.RemoveRange(surfaces, p.Surfaces.Count - surfaces);
            y = null;
        }
        if (y == null) return;
        // Nothing stands or parks in the mouth of a driveway.
        foreach (var driveway in driveways) y.Take(Rect(driveway.EnvelopeInternal.MinX, edge - .5, driveway.EnvelopeInternal.MaxX, edge + LotAisle));
        GasService(y, seed);
        LotRows(y, seed);
        LotEdgeStalls(y, seed, lot);
        LotPlanting(y, seed, planter, halfFront);
    }

    /// <summary>
    /// The shop in the rear corner and the canopy. The canopy takes the rectangle that holds more of the wanted bays: the one between
    /// the road and the shop over the full width, or the one beside the shop over the full depth. Its bays fill rows along the frontage
    /// first, and it stands toward the road, leaving a drive aisle before it where there is room. The islands lie along the canopy's
    /// longer side, so cars drive through it lengthwise. Each bay's roof has plain sides; the fascia goes on the edges with no
    /// neighboring bay. False when no bay fits.
    /// </summary>
    static bool GasForecourt(FacilityYard y, uint seed, string building, bool parking, int wanted)
    {
        if (!y.Sizes.TryGetValue("site_canopy", out var bay)) return false;
        var env = y.Env;
        double frontEnd = env.MaxY, sideEnd = env.MaxX, shopS = 0, shopFront = 0;
        Vector2 shop = default;
        if (building != null)
        {
            if (!y.Sizes.TryGetValue(building, out shop)) return false;
            // The rear corner, or on a lot without one there, the nearest place along the rear and then toward the road.
            bool placed = false;
            for (double t = env.MaxY - shop.y * .5; !placed && t >= env.MaxY - shop.y * .5 - 12; t -= 2)
                for (double s = env.MaxX - shop.x * .5; !placed && s >= env.MinX + shop.x * .5 - 1e-6; s -= 2)
                    if (placed = y.Put(building, s, t, 0, -1)) { shopS = s; shopFront = t - shop.y * .5; }
            if (!placed) return false;
            frontEnd = shopFront - LotWalk - (parking ? LotStallDepth + LotAisle : .4);
            sideEnd = shopS - shop.x * .5 - LotWalk;
        }
        int frontCols = (int)Math.Floor((env.Width + 1e-6) / bay.x), frontRows = (int)Math.Floor((frontEnd - env.MinY + 1e-6) / bay.y);
        int sideCols = (int)Math.Floor((sideEnd - env.MinX + 1e-6) / bay.x), sideRows = (int)Math.Floor((env.Height + 1e-6) / bay.y);
        int frontRoom = Math.Min(wanted, Math.Max(frontCols, 0) * Math.Max(frontRows, 0)), sideRoom = Math.Min(wanted, Math.Max(sideCols, 0) * Math.Max(sideRows, 0));
        bool beside = sideRoom > frontRoom;
        int room = beside ? sideRoom : frontRoom, maxCols = beside ? sideCols : frontCols;
        if (room < 1) return false;
        int rows = (int)Math.Ceiling(room / (double)maxCols), cols = (int)Math.Ceiling(room / (double)rows);
        double left = (env.MinX + (beside ? sideEnd : env.MaxX) - cols * bay.x) * .5;
        double top = env.MinY + Math.Min(LotAisle, (beside ? env.MaxY : frontEnd) - env.MinY - rows * bay.y);
        var roofed = new List<Geometry>();
        var stands = new bool[rows, cols];
        bool turned = rows > cols;
        int draw = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double s = left + (c + .5) * bay.x, t = top + (r + .5) * bay.y;
                if (!y.Put("site_canopy", s, t, turned ? 1 : 0, turned ? 0 : -1)) continue;
                stands[r, c] = true;
                roofed.Add(Rect(s - bay.x * .5, t - bay.y * .5, s + bay.x * .5, t + bay.y * .5));
                // Cars being fueled stand along the island on either side, nose either way.
                foreach (int lane in new[] { -1, 1 })
                {
                    draw += 3;
                    if (Rand(seed, 100 + draw) >= GasCarAtPump) continue;
                    double along = (Rand(seed, 102 + draw) - .5) * 1.6, across = lane * GasFuelingLane;
                    y.Add(LotPick(y, "site_car", seed, 101 + draw), s + (turned ? across : along), t + (turned ? along : across), turned ? 0 : lane, turned ? lane : 0, false);
                }
            }
        if (roofed.Count == 0) return false;
        if (y.Sizes.TryGetValue("site_fascia", out var fascia))
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    foreach (var (ds, dt) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        if (!stands[r, c] || (r + dt >= 0 && r + dt < rows && c + ds >= 0 && c + ds < cols && stands[r + dt, c + ds])) continue;
                        double s = left + (c + .5) * bay.x, t = top + (r + .5) * bay.y;
                        y.Add("site_fascia", s + ds * (bay.x + fascia.y) * .5, t + dt * (bay.y + fascia.y) * .5, ds, dt, true, (s, t));
                    }
        // The fueling pad: newer concrete under the canopy and a little beyond its drip line.
        Emit(y.Plan, y.F, And(OverlayNGRobust.Union(roofed).Buffer(.6, new BufferParameters { JoinStyle = JoinStyle.Mitre }), y.Field), "site_pad", .012f);
        if (building == "site_store") LotFront(y, seed, shopS, shopFront, shop.x);
        return true;
    }

    /// <summary>
    /// Along the side away from the shop, from the road inward: the underground tanks' fill covers (now and then with a tanker
    /// unloading beside them), their vent pipes, and the air station; the dumpster takes the rear corner. Each goes in the first
    /// free place along the edge, and is left out where there is none.
    /// </summary>
    static void GasService(FacilityYard y, uint seed)
    {
        var env = y.Env;
        // Long side along the edge, front toward the lot. Returns how far along the edge the part reaches, or the start when it does not fit.
        double Along(string part, double from, double inset)
        {
            if (!y.Sizes.TryGetValue(part, out var size)) return from;
            for (double t = from + size.x * .5; t <= Math.Min(from + 18, env.MaxY - size.x * .5); t += 1.5)
                if (y.Put(part, env.MinX + inset + size.y * .5, t, 1, 0, false)) return t + size.x * .5;
            return from;
        }
        if (y.Sizes.TryGetValue("site_dumpster", out var dumpster))
            for (double t = env.MaxY - dumpster.y * .5, tries = 0; tries < 6 && !y.Put("site_dumpster", env.MinX + dumpster.x * .5, t, 0, -1, false); t -= 2, tries++) { }
        double start = env.MinY + LotAisle, end = Along("site_fill", start, 1.2);
        if (end > start && y.Sizes.TryGetValue("site_tanker", out var tanker) && Rand(seed, 500) < GasTanker)
            y.Put("site_tanker", env.MinX + 1.2 + y.Sizes["site_fill"].y + .6 + tanker.x * .5, (start + end) * .5 + 1, 0, -1, false);
        end = Along("site_vent", end + .5, .05);
        Along("site_air", end + 2, .2);
    }
}
