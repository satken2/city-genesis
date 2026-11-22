using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>
/// Placement plans for utility plants: substation, water station, sewage plant, and waste plant.
/// All four follow one rule. A gate opens on the road-facing edge, a spine road runs in from it along one side, and rows lie across
/// the site one behind another. A row repeats its parts at their authored size wherever they fit, so the number of units follows the
/// site size and an irregular site simply loses the units that do not fit. Each plant only lists its rows from the front, most
/// essential first: a shallow site keeps the essential rows and a deep one repeats the cycle behind a cross road.
/// Built in local coordinates relative to the road-facing edge (s along the edge, t inward), mirrored left/right by the seed.
/// Parts missing from the model folder are skipped.
/// </summary>
public static partial class OpenSpaceLayout
{
    public const float FacilityGateWidth = 6f;  // Fence opening at the gate (m). The gate part spans it.
    const double FacilityRoad = 6.0;            // Service road width (m).
    const double FacilityFenceInset = .3;       // Fence line inside the site edge (m), as the shared site decorations place it.
    const int MaxFacilityProps = 4000;

    /// <summary>Parts the plant plans place, for resolving them from the model folder.</summary>
    public static readonly string[] FacilityParts = { "gate", "site_asphalt", "site_light", "site_pipe", "site_office",
        "site_transformer", "site_regulator", "site_switchgear", "site_gantry",
        "site_pump", "site_generator", "site_tank", "site_pctank", "site_reservoir",
        "site_blower", "site_digester", "site_clarifier", "site_basin",
        "site_plant", "site_shed", "site_bunker", "site_scale" };

    public static bool IsFacilityModel(int modelType) => modelType == Constants.MODEL_SUBSTATION
        || modelType == Constants.MODEL_WATER_STATION || modelType == Constants.MODEL_SEWAGE_PLANT || modelType == Constants.MODEL_WASTE_PLANT;

    sealed class FacilityYard
    {
        public Plan Plan; public Frame F;
        public IReadOnlyDictionary<string, Vector2> Sizes;
        public Geometry Site;
        public Geometry Field;          // Where units may stand: the site inside the edge strip, without the spine and ring roads.
        public Envelope Env;            // Extent of the site inside the edge strip.
        public bool Roads;              // False on sites too small for service roads; rows then start at the front.
        public double Edge, GateS, RingS = double.NaN;
        IPreparedGeometry field;
        readonly List<Geometry> taken = new List<Geometry>();
        readonly List<double> crossRoads = new List<double>();
        double spineEnd, spineBefore;   // How far in the spine road reaches, and its value before the last cross road.

        // Rows start exactly on the field's edges, so allow for rounding when testing whether a part lies in it.
        public void Prepare() => field = PreparedGeometryFactory.Prepare(Field.Buffer(.01));
        public bool Has(string part) => Sizes.ContainsKey(part);

        /// <summary>
        /// Place a part's authored rectangle centered at (s,t), front toward (fs,ft), if it lies in the field clear of other units.
        /// Units are combined into the building mesh; decorations (unit=false) are drawn as instanced props.
        /// </summary>
        public bool Put(string part, double s, double t, double fs, double ft, bool unit = true)
        {
            if (!Sizes.TryGetValue(part, out var size) || Plan.Props.Count >= MaxFacilityProps) return false;
            var box = ParkBox(s, t, fs, ft, size.x, size.y);
            if (!field.Covers(box)) return false;
            // Neighbors in a row may touch (gantries form one line), so only a real overlap rejects the part.
            var core = ParkBox(s, t, fs, ft, size.x - .04, size.y - .04);
            var envelope = core.EnvelopeInternal;
            foreach (var other in taken)
                if (other.EnvelopeInternal.Intersects(envelope) && other.Intersects(core)) return false;
            taken.Add(box);
            Add(part, s, t, fs, ft, unit);
            return true;
        }

        /// <summary>Place a part without the field and overlap checks (parts that stand on a road or in a gap chosen by the caller).</summary>
        public void Add(string part, double s, double t, double fs, double ft, bool unit)
        {
            if (!Has(part) || Plan.Props.Count >= MaxFacilityProps) return;
            Plan.Props.Add(new Prop { Part = part, Position = F.World(s, t), Forward = F.Dir(fs, ft), Fitted = true, Unit = unit });
        }

        /// <summary>
        /// Row of one-off buildings at depth t: the listed parts once each in order, outward from the spine road, then filler parts
        /// while they fit (up to fillLimit). side=true turns a part's front to the spine road instead of the street. Returns the depth taken.
        /// </summary>
        public double ListRow(double t, double gap, (string part, bool side)[] items, string filler = null, int fillLimit = 0)
        {
            double cursor = Roads ? GateS + FacilityRoad * .5 + 1.5 : Env.MinX, depth = 0;
            for (int i = 0; i < items.Length + fillLimit; i++)
            {
                string part = i < items.Length ? items[i].part : filler;
                bool side = i < items.Length && items[i].side;
                if (part == null || !Sizes.TryGetValue(part, out var size)) continue;
                double along = side ? size.y : size.x, across = side ? size.x : size.y;
                if (!Put(part, cursor + along * .5, t + across * .5, side ? -1 : 0, side ? 0 : -1)) continue;
                cursor += along + gap;
                depth = Math.Max(depth, across);
            }
            if (depth > 0) spineEnd = Math.Max(spineEnd, t + depth);
            return depth;
        }

        /// <summary>
        /// Row across the site at depth t: repeat the part, centered in each free stretch of the band, with the given gap between
        /// neighbors or, with pitch, one per pitch-wide slot (rows sharing a pitch line up behind each other).
        /// Returns the depth taken (0 when nothing fit); centers go to placed.
        /// </summary>
        public double Row(double t, string part, double gap, double pitch = 0, List<Coordinate> placed = null)
        {
            if (!Sizes.TryGetValue(part, out var size) || t + size.y > Env.MaxY + 1e-6) return 0;
            double depth = size.y, slot = pitch > 0 ? pitch : size.x + gap, spare = pitch > 0 ? 0 : gap;
            var pieces = new List<Polygon>(Polygons(And(Field, Rect(Env.MinX - 1, t, Env.MaxX + 1, t + depth))));
            pieces.Sort((a, b) => a.EnvelopeInternal.MinX.CompareTo(b.EnvelopeInternal.MinX));
            int count = 0;
            foreach (var piece in pieces)
            {
                var e = piece.EnvelopeInternal;
                // A stretch narrower than one slot still takes one part if the part itself fits.
                int n = Math.Max((int)Math.Floor((e.Width + spare + 1e-6) / slot), e.Width + 1e-6 >= size.x ? 1 : 0);
                double first = e.MinX + (e.Width - (n * slot - spare)) * .5 + (slot - spare) * .5;
                for (int i = 0; i < n; i++)
                {
                    double s = first + i * slot, at = t + depth * .5;
                    if (Put(part, s, at, 0, -1)) { count++; placed?.Add(new Coordinate(s, at)); }
                }
            }
            return count > 0 ? depth : 0;
        }

        /// <summary>Cross road at depth t over the full width. Returns the depth taken (0 without roads or when it does not fit).</summary>
        public double CrossRoad(double t)
        {
            if (!Roads || t + FacilityRoad > Env.MaxY + Edge - 1) return 0;
            crossRoads.Add(t);
            spineBefore = spineEnd;
            spineEnd = Math.Max(spineEnd, t + FacilityRoad);
            return FacilityRoad;
        }

        /// <summary>Take back the last cross road when nothing was placed behind it.</summary>
        public void DropCrossRoad()
        {
            crossRoads.RemoveAt(crossRoads.Count - 1);
            spineEnd = spineBefore;
        }

        /// <summary>Tile the pipe part (long along its X) between two points, centered on the run. Pieces off the field are left out.</summary>
        public void PipeRun(double s0, double t0, double s1, double t1)
        {
            if (!Sizes.TryGetValue("site_pipe", out var size)) return;
            double ds = s1 - s0, dt = t1 - t0, length = Math.Sqrt(ds * ds + dt * dt);
            if (length < size.x * .5) return;
            ds /= length; dt /= length;
            int n = Math.Max(1, (int)Math.Round(length / size.x));
            for (int i = 0; i < n; i++)
            {
                double along = length * .5 + (i - (n - 1) * .5) * size.x, s = s0 + ds * along, t = t0 + dt * along;
                if (field.Covers(ParkBox(s, t, -dt, ds, size.x, size.y))) Add("site_pipe", s, t, -dt, ds, false);
            }
        }

        /// <summary>Emit the roads, the gate, and the yard lights along the cross roads.</summary>
        public void Close()
        {
            Plan.Gate = F.World(GateS, FacilityFenceInset);
            Add("gate", GateS, FacilityFenceInset, 0, -1, true);
            if (!Roads) return;
            double half = FacilityRoad * .5;
            var parts = new List<Geometry> { Rect(GateS - half, -.5, GateS + half, Math.Max(spineEnd, FacilityRoad)) };
            foreach (double t in crossRoads) parts.Add(Rect(Env.MinX - Edge + 1, t, Env.MaxX + Edge - 1, t + FacilityRoad));
            if (!double.IsNaN(RingS) && crossRoads.Count >= 2)
                parts.Add(Rect(RingS - half, crossRoads[0], RingS + half, crossRoads[crossRoads.Count - 1] + FacilityRoad));
            // Dilating and eroding rounds the inside corners where roads meet.
            Geometry road = OverlayNGRobust.Union(parts).Buffer(half, Round()).Buffer(-half, Round());
            road = And(road, Union(Site.Buffer(-1), And(Site, Rect(GateS - half, -.5, GateS + half, 2))));
            Emit(Plan, F, road, "site_asphalt", .012f);
            foreach (double t in crossRoads)
                for (double s = GateS + half + 4; s < Env.MaxX; s += 26)
                    Put("site_light", s, t + FacilityRoad + .55, 0, -1, false);
        }
    }

    static FacilityYard OpenYard(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes, bool ring = false)
    {
        if (sizes == null || sizes.Count == 0) return null;
        Vector2 u = (p.Polygon[1] - p.Polygon[0]).normalized;
        if (u.sqrMagnitude < .5f) u = Vector2.right;
        var f = new Frame { O = (p.Polygon[0] + p.Polygon[1]) * .5f, U = Rand(seed, 1) < .5f ? -u : u, V = new Vector2(-u.y, u.x) };
        Geometry site = LocalSite(p, f);
        if (site == null || site.Area < 12) return null;
        double edge = Math.Max(.6, Math.Min(2.0, Math.Sqrt(site.Area) * .03));
        var inner = Largest(site.Buffer(-edge, new BufferParameters { JoinStyle = JoinStyle.Mitre }));
        if (inner == null) return null;
        var env = inner.EnvelopeInternal;
        double halfFront = Vector2.Distance(p.Polygon[0], p.Polygon[1]) * .5;
        var yard = new FacilityYard { Plan = p, F = f, Sizes = sizes, Site = site, Env = env, Edge = edge, Field = inner,
            Roads = env.Width >= 24 && env.Height >= 24 && halfFront * 2 >= FacilityRoad + 2 * edge + 2 };
        if (yard.Roads)
        {
            double keep = FacilityRoad * .5 + .5;
            yard.GateS = -halfFront + edge + keep;
            yard.Field = Minus(yard.Field, Rect(yard.GateS - keep, env.MinY - 1, yard.GateS + keep, env.MaxY + 1));
            if (ring)
            {
                yard.RingS = env.MaxX - FacilityRoad * .5;
                yard.Field = Minus(yard.Field, Rect(yard.RingS - keep, env.MinY - 1, env.MaxX + 1, env.MaxY + 1));
            }
        }
        yard.Prepare();
        return yard;
    }

    /// <summary>
    /// Substation: control building at the gate, then banks of bays one gantry span wide. Each bay lines up a transformer, a regulator,
    /// and switchgear in front of its line gantry.
    /// </summary>
    static void Substation(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        var y = OpenYard(p, seed, sizes);
        if (y == null) return;
        double t = y.Env.MinY, d = y.ListRow(t, 2, new[] { ("site_office", true) });
        if (d > 0) t += d + 2;
        double bay = sizes.TryGetValue("site_gantry", out var gantry) ? gantry.x : 0;
        var rows = new (string part, double behind)[] { ("site_transformer", 2), ("site_regulator", 1.5), ("site_switchgear", 1.5), ("site_gantry", 2) };
        while (true)
        {
            double road = y.CrossRoad(t), at = t + (road > 0 ? road + 1.5 : 0), start = at;
            foreach (var row in rows)
            {
                if ((d = y.Row(at, row.part, 1.8, bay)) <= 0) break;
                at += d + row.behind;
            }
            if (at == start) { if (road > 0) y.DropCrossRoad(); break; }
            t = at;
        }
        y.Close();
    }

    /// <summary>
    /// Water station: pump house, generator, and surge tanks at the gate; behind a cross road, a field of buried reservoirs with
    /// header pipes in the gaps between rows, joined by a trunk pipe, and one elevated tank in the far rear corner.
    /// </summary>
    static void WaterStation(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        var y = OpenYard(p, seed, sizes);
        if (y == null) return;
        const double gap = 4;
        double t = y.Env.MinY, d = y.ListRow(t, 2.5, new[] { ("site_pump", false), ("site_generator", false), ("site_tank", false), ("site_tank", false) });
        if (d > 0) t += d + 2;
        double road = y.CrossRoad(t);
        t += road > 0 ? road + gap : 0;
        if (sizes.TryGetValue("site_pctank", out var tank))
            y.Put("site_pctank", y.Env.MaxX - tank.x * .5, y.Env.MaxY - tank.y * .5, 0, -1);
        var headers = new List<(double s0, double s1, double t)>();
        var placed = new List<Coordinate>();
        while ((d = y.Row(t, "site_reservoir", gap, 0, placed)) > 0)
        {
            double s0 = double.MaxValue, s1 = double.MinValue;
            foreach (var c in placed) { s0 = Math.Min(s0, c.X); s1 = Math.Max(s1, c.X); }
            headers.Add((s0 - d * .5, s1 + d * .5, t - gap * .5));
            placed.Clear();
            t += d + gap;
        }
        if (road > 0 && headers.Count == 0) y.DropCrossRoad();
        // The trunk runs beside the first column; headers reach it so every row is connected.
        double trunk = double.MaxValue;
        foreach (var h in headers) trunk = Math.Min(trunk, h.s0 - gap * .5);
        foreach (var h in headers) y.PipeRun(headers.Count > 1 ? trunk : h.s0, h.t, h.s1, h.t);
        if (headers.Count > 1) y.PipeRun(trunk, headers[0].t, trunk, headers[headers.Count - 1].t);
        y.Close();
    }

    /// <summary>
    /// Sewage plant: office, blower house, and digesters at the gate; behind each cross road, a row of clarifiers and a row of
    /// reaction basins of the same width, each basin piped to the clarifier in front of it.
    /// </summary>
    static void SewagePlant(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        var y = OpenYard(p, seed, sizes);
        if (y == null) return;
        const double gap = 4;
        double t = y.Env.MinY, d = y.ListRow(t, 2.5, new[] { ("site_office", false), ("site_blower", false) }, "site_digester", 4);
        if (d > 0) t += d + 2;
        var clarifiers = new List<Coordinate>();
        var basins = new List<Coordinate>();
        while (true)
        {
            double road = y.CrossRoad(t), at = t + (road > 0 ? road + 2 : 0);
            clarifiers.Clear(); basins.Clear();
            double front = y.Row(at, "site_clarifier", gap, 0, clarifiers);
            if (front > 0) at += front + gap;
            double rear = front > 0 ? y.Row(at, "site_basin", gap, 0, basins) : 0;
            if (front <= 0) { if (road > 0) y.DropCrossRoad(); break; }
            if (rear > 0)
            {
                foreach (var basin in basins)
                    foreach (var clarifier in clarifiers)
                        if (Math.Abs(basin.X - clarifier.X) < .5) y.PipeRun(basin.X, at - gap, basin.X, at);
                at += rear + 2;
            }
            t = at;
        }
        y.Close();
    }

    /// <summary>
    /// Waste plant: incinerator plants behind a cross road with a weighbridge on it, then the office, a shed, and bunkers behind them.
    /// The road behind the plants and one along the far side close the loop the collection trucks drive.
    /// </summary>
    static void WastePlant(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        var y = OpenYard(p, seed, sizes, true);
        if (y == null) return;
        double t = y.Env.MinY, d;
        bool any = false;
        while (true)
        {
            double road = y.CrossRoad(t), at = t + (road > 0 ? road + 2 : 0);
            if ((d = y.Row(at, "site_plant", 12)) <= 0) { if (road > 0) y.DropCrossRoad(); break; }
            if (!any && road > 0 && sizes.TryGetValue("site_scale", out var scale))
                y.Add("site_scale", y.GateS + FacilityRoad + scale.x * .5, t + scale.y * .5 + .2, 0, -1, true);
            t = at + d + 2;
            any = true;
        }
        if (any && (d = y.CrossRoad(t)) > 0) t += d + 2;
        y.ListRow(t, 3, new[] { ("site_office", false), ("site_shed", false), ("site_bunker", false), ("site_bunker", false) });
        y.Close();
    }
}
