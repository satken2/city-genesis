using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>
/// Placement plans for utility plants (substation, water station, sewage plant, waste plant) and the forecourt of open pits.
/// All follow one rule. A gate opens on the road-facing edge, a spine road runs in from it along one side, and rows lie across
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
    const double ScatterTurn = Math.PI * 25 / 180;  // Largest turn of a loose thing off the site's axes (rad).

    /// <summary>Parts the plant plans place, for resolving them from the model folder.</summary>
    public static readonly string[] FacilityParts = { "gate", "site_asphalt", "site_gravel", "site_light", "site_pipe", "site_office",
        "site_transformer", "site_regulator", "site_switchgear", "site_gantry",
        "site_pump", "site_generator", "site_tank", "site_pctank", "site_reservoir",
        "site_blower", "site_digester", "site_clarifier", "site_basin",
        "site_plant", "site_shed", "site_bunker", "site_scale",
        "site_crusher", "site_conveyor", "site_rom", "site_workshop", "site_pond",
        "site_excavator", "site_truck", "site_loader", "site_pickup", "site_drill" };
    /// <summary>Numbered parts the plant plans place: as many as are numbered consecutively from 1.</summary>
    public static readonly string[] FacilitySeries = { "site_stockpile", "site_rock", "site_heap", "site_clutter" };

    public static bool IsFacilityModel(int modelType) => modelType == Constants.MODEL_SUBSTATION
        || modelType == Constants.MODEL_WATER_STATION || modelType == Constants.MODEL_SEWAGE_PLANT || modelType == Constants.MODEL_WASTE_PLANT
        || modelType == Constants.MODEL_OPEN_PIT || modelType == Constants.MODEL_QUARRY;

    /// <summary>Where the front of a building in a <see cref="FacilityYard.ListRow"/> points.</summary>
    enum Face { Street, Spine, Yard }

    sealed class FacilityYard
    {
        public Plan Plan; public Frame F;
        public IReadOnlyDictionary<string, Vector2> Sizes;
        public Geometry Site;
        public Geometry Field;          // Where units may stand: the site inside the edge strip, without the spine and ring roads.
        public Envelope Env;            // Extent of the site inside the edge strip.
        public bool Roads;              // False on sites too small for service roads; rows then start at the front.
        public string RoadPart = "site_asphalt";  // Material part of the service roads.
        public double Edge, GateS, RingS = double.NaN;
        IPreparedGeometry field;
        // Rectangles of the placed units and loose props, bucketed in plan so a site littered with rocks stays quick to test.
        readonly Dictionary<long, List<Geometry>> taken = new Dictionary<long, List<Geometry>>();
        const double TakenCell = 16;
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
            if (!Fits(part, s, t, fs, ft, out var box)) return false;
            Take(box);
            Add(part, s, t, fs, ft, unit);
            return true;
        }

        /// <summary>Whether <see cref="Put"/> would place the part there.</summary>
        public bool Fits(string part, double s, double t, double fs, double ft) => Fits(part, s, t, fs, ft, out _);

        bool Fits(string part, double s, double t, double fs, double ft, out Geometry box)
        {
            box = null;
            if (!Sizes.TryGetValue(part, out var size) || Plan.Props.Count >= MaxFacilityProps) return false;
            box = ParkBox(s, t, fs, ft, size.x, size.y);
            if (!field.Covers(box)) return false;
            // Neighbors in a row may touch (gantries form one line), so only a real overlap rejects the part.
            return !Taken(ParkBox(s, t, fs, ft, size.x - .04, size.y - .04));
        }

        void Take(Geometry box)
        {
            var e = box.EnvelopeInternal;
            for (long z = (long)Math.Floor(e.MinY / TakenCell); z <= (long)Math.Floor(e.MaxY / TakenCell); z++)
                for (long x = (long)Math.Floor(e.MinX / TakenCell); x <= (long)Math.Floor(e.MaxX / TakenCell); x++)
                {
                    long key = z * 1000003 + x;
                    if (!taken.TryGetValue(key, out var bucket)) taken[key] = bucket = new List<Geometry>();
                    bucket.Add(box);
                }
        }

        bool Taken(Geometry box)
        {
            var e = box.EnvelopeInternal;
            for (long z = (long)Math.Floor(e.MinY / TakenCell); z <= (long)Math.Floor(e.MaxY / TakenCell); z++)
                for (long x = (long)Math.Floor(e.MinX / TakenCell); x <= (long)Math.Floor(e.MaxX / TakenCell); x++)
                    if (taken.TryGetValue(z * 1000003 + x, out var bucket))
                        foreach (var other in bucket)
                            if (other.EnvelopeInternal.Intersects(e) && other.Intersects(box)) return true;
            return false;
        }

        /// <summary>Place a part without the field and overlap checks (parts that stand on a road or in a gap chosen by the caller).</summary>
        public void Add(string part, double s, double t, double fs, double ft, bool unit)
        {
            if (!Has(part) || Plan.Props.Count >= MaxFacilityProps) return;
            Plan.Props.Add(new Prop { Part = part, Position = F.World(s, t), Forward = F.Dir(fs, ft), Fitted = true, Unit = unit });
        }

        /// <summary>
        /// Row of one-off buildings at depth t: the listed parts once each in order, outward from the spine road, then filler parts
        /// while they fit (up to fillLimit), facing the street. Returns the depth taken.
        /// </summary>
        public double ListRow(double t, double gap, (string part, Face face)[] items, string filler = null, int fillLimit = 0)
        {
            double cursor = Roads ? GateS + FacilityRoad * .5 + 1.5 : Env.MinX, depth = 0;
            for (int i = 0; i < items.Length + fillLimit; i++)
            {
                string part = i < items.Length ? items[i].part : filler;
                Face face = i < items.Length ? items[i].face : Face.Street;
                if (part == null || !Sizes.TryGetValue(part, out var size)) continue;
                bool side = face == Face.Spine;
                double along = side ? size.y : size.x, across = side ? size.x : size.y;
                if (!Put(part, cursor + along * .5, t + across * .5, side ? -1 : 0, side ? 0 : face == Face.Yard ? 1 : -1)) continue;
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

        /// <summary>Carry the spine road in to depth t (a road that leads on past the rows).</summary>
        public void SpineTo(double t)
        {
            if (Roads) spineEnd = Math.Max(spineEnd, Math.Min(t, Env.MaxY + Edge - 1));
        }

        /// <summary>Area of the spine, cross, and ring roads laid so far. Null without roads.</summary>
        Geometry RoadArea()
        {
            if (!Roads) return null;
            double half = FacilityRoad * .5;
            var parts = new List<Geometry> { Rect(GateS - half, -.5, GateS + half, Math.Max(spineEnd, FacilityRoad)) };
            foreach (double t in crossRoads) parts.Add(Rect(Env.MinX - Edge + 1, t, Env.MaxX + Edge - 1, t + FacilityRoad));
            if (!double.IsNaN(RingS) && crossRoads.Count >= 2)
                parts.Add(Rect(RingS - half, crossRoads[0], RingS + half, crossRoads[crossRoads.Count - 1] + FacilityRoad));
            // Dilating and eroding rounds the inside corners where roads meet.
            Geometry road = OverlayNGRobust.Union(parts).Buffer(half, Round()).Buffer(-half, Round());
            return And(road, Union(Site.Buffer(-1), And(Site, Rect(GateS - half, -.5, GateS + half, 2))));
        }

        /// <summary>The numbered parts of a series present in the model folder, each with an equal part of share.</summary>
        public List<(string part, float share)> Series(string name, float share)
        {
            var parts = new List<(string, float)>();
            int count = 0;
            while (Has(name + (count + 1))) count++;
            for (int n = 1; n <= count; n++) parts.Add((name + n, share / count));
            return parts;
        }

        /// <summary>
        /// Loose things over a region (rocks, heaps, clutter, parked machines, stockpiles): one draw per cell of a grid of pitch m.
        /// parts gives each part's share of the draws; the rest leave the cell empty. The part stands at a random spot that keeps it
        /// within its cell, square to the road-facing edge or across it and turned up to ScatterTurn either way, as things parked
        /// and stacked by hand are. A draw is dropped where the part's rectangle leaves the region or touches a road, a unit, or an
        /// earlier draw, so the count follows the free ground. The seed and stream fix the draws, so a site keeps its look.
        /// </summary>
        public void Scatter(Geometry region, double pitch, uint seed, int stream, List<(string part, float share)> parts, bool unit = false)
        {
            if (region == null || region.IsEmpty || parts.Count == 0) return;
            var inside = PreparedGeometryFactory.Prepare(region);
            Geometry road = RoadArea();
            var roads = road != null && !road.IsEmpty ? PreparedGeometryFactory.Prepare(road) : null;
            var e = region.EnvelopeInternal;
            int cell = stream * 100003;
            for (double t = e.MinY; t < e.MaxY; t += pitch)
                for (double s = e.MinX; s < e.MaxX; s += pitch, cell += 4)
                {
                    float draw = Rand(seed, cell);
                    string part = null;
                    foreach (var candidate in parts)
                    {
                        if (draw < candidate.share) { part = candidate.part; break; }
                        draw -= candidate.share;
                    }
                    if (part == null || !Sizes.TryGetValue(part, out var size) || Plan.Props.Count >= MaxFacilityProps) continue;
                    double room = Math.Max(pitch - Math.Max(size.x, size.y), 0), inset = (pitch - room) * .5;
                    double ps = s + inset + Rand(seed, cell + 1) * room, pt = t + inset + Rand(seed, cell + 2) * room;
                    double turn = Rand(seed, cell + 3) * 4;
                    double angle = Math.Floor(turn) * Math.PI * .5 + (turn - Math.Floor(turn) - .5) * 2 * ScatterTurn, fs = Math.Cos(angle), ft = Math.Sin(angle);
                    var box = ParkBox(ps, pt, fs, ft, size.x, size.y);
                    if (!inside.Covers(box) || (roads != null && roads.Intersects(box)) || Taken(box)) continue;
                    Take(box);
                    Add(part, ps, pt, fs, ft, unit);
                }
        }

        /// <summary>Emit the roads, the gate, and the yard lights along the cross roads.</summary>
        public void Close()
        {
            Plan.Gate = F.World(GateS, FacilityFenceInset);
            Add("gate", GateS, FacilityFenceInset, 0, -1, true);
            if (!Roads) return;
            Emit(Plan, F, RoadArea(), RoadPart, .012f);
            foreach (double t in crossRoads)
                for (double s = GateS + FacilityRoad * .5 + 4; s < Env.MaxX; s += 26)
                    Put("site_light", s, t + FacilityRoad + .55, 0, -1, false);
        }
    }

    /// <summary>depth limits where units may stand to that far in from the road-facing edge; roads still span the whole site.</summary>
    static FacilityYard OpenYard(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes, bool ring = false, double depth = double.NaN)
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
        if (!double.IsNaN(depth)) yard.Field = And(yard.Field, Rect(env.MinX - 1, env.MinY - 1, env.MaxX + 1, depth));
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
        double t = y.Env.MinY, d = y.ListRow(t, 2, new[] { ("site_office", Face.Spine) });
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
        double t = y.Env.MinY, d = y.ListRow(t, 2.5, new[] { ("site_pump", Face.Street), ("site_generator", Face.Street), ("site_tank", Face.Street), ("site_tank", Face.Street) });
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
        double t = y.Env.MinY, d = y.ListRow(t, 2.5, new[] { ("site_office", Face.Street), ("site_blower", Face.Street) }, "site_digester", 4);
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
        y.ListRow(t, 3, new[] { ("site_office", Face.Street), ("site_shed", Face.Street), ("site_bunker", Face.Street), ("site_bunker", Face.Street) });
        y.Close();
    }

    const double PitOutlet = 8.0;        // From a crusher's center to its screen tower, where the yard conveyor starts (m), as site_crusher is authored.
    const int PitPilesPerCrusher = 4;    // Most stockpiles one crusher feeds before the line starts over with another crusher.
    const double QuarryPlantSpacing = 22; // Least clear run between a quarry's plants along the frontage (m), left to stockpiles and machines. Each run draws up to 2.5 times this.

    /// <summary>
    /// Open pit: only the forecourt (OPEN_PIT_YARD_DEPTH in from the road-facing edge, which the backend leaves undug) takes units.
    /// The rock reads from the pit to the gate. A haul road on the undug strip along the pit's rim reaches the crushers' hoppers; each
    /// crusher feeds a line of stockpiles; a yard road in front of the piles leads to the gate, past the office and the workshop
    /// (doors to the yard road) on the street side. Around these, machines stand parked off any line, and rocks, spoil heaps, and
    /// site clutter lie over the free ground, the margins of the site, and the pit. Plan.Pit is the area the backend digs.
    /// </summary>
    static void OpenPit(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        double yardDepth = Constants.OPEN_PIT_YARD_DEPTH, rim = Constants.OPEN_PIT_RIM_MARGIN;
        var y = OpenYard(p, seed, sizes, false, yardDepth);
        if (y == null) return;
        y.RoadPart = "site_gravel";
        double t = y.Env.MinY, d = y.ListRow(t, 2, new[] { ("site_office", Face.Street), ("site_workshop", Face.Yard) });
        if (d > 0) t += d + 2;
        double road = y.CrossRoad(t);
        if (!PitLines(y, t + (road > 0 ? road + 2 : 0), seed) && road > 0) y.DropCrossRoad();
        y.CrossRoad(yardDepth + (rim - FacilityRoad) * .5);
        var pit = Largest(And(y.Site.Buffer(-rim), Rect(y.Env.MinX - rim, yardDepth + rim, y.Env.MaxX + rim, y.Env.MaxY + rim)));
        if (pit != null) p.Pit = ToWorld(y.F, pit.ExteriorRing.Coordinates);
        Litter(y, seed, pit, new[] { "site_truck", "site_loader", "site_pickup", "site_truck", "site_drill" });
        y.Close();
    }

    /// <summary>
    /// What lies around an excavation's units: parked machines and clutter over the free forecourt, rocks and spoil heaps there and
    /// along the margins between the site edge and the excavation, and fallen rocks in the excavation itself (heaps would sit askew
    /// on its faces). The machines take a few draws each so the commoner ones come up more often.
    /// </summary>
    static void Litter(FacilityYard y, uint seed, Polygon pit, string[] machines)
    {
        var yard = new List<(string part, float share)>();
        foreach (string machine in machines) yard.Add((machine, .3f / machines.Length));
        yard.AddRange(y.Series("site_clutter", .26f));
        yard.AddRange(y.Series("site_rock", .16f));
        yard.AddRange(y.Series("site_heap", .06f));
        y.Scatter(y.Field, 8, seed, 1, yard);
        var margins = new List<(string part, float share)>(y.Series("site_rock", .3f));
        margins.AddRange(y.Series("site_heap", .2f));
        Geometry ground = y.Site.Buffer(-1.5);
        y.Scatter(pit != null ? Minus(Minus(ground, y.Field), pit) : Minus(ground, y.Field), 9, seed, 2, margins);
        if (pit != null) y.Scatter(pit, 16, seed, 3, y.Series("site_rock", .4f));
    }

    /// <summary>
    /// Quarry: the backend cuts the hill behind the forecourt (QUARRY_YARD_DEPTH in from the road-facing edge) down to the forecourt's
    /// level, so the floor runs on from the forecourt and the spine road leads straight onto it. The forecourt is one band: the office
    /// by the gate, and crushing-and-screening plants (each with its own sorted piles) against the cut with their hoppers toward it,
    /// spaced along the frontage. A settling pond and further stockpiles take turns in the free ground between them, with the same
    /// loose machines, rocks, heaps, and clutter as an open pit. Plan.Pit is the area the backend cuts and Plan.PitFloor where its level is read.
    /// </summary>
    static void Quarry(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        double yardDepth = Constants.QUARRY_YARD_DEPTH, rim = Constants.QUARRY_RIM_MARGIN;
        var y = OpenYard(p, seed, sizes, false, yardDepth);
        if (y == null) return;
        y.RoadPart = "site_gravel";
        y.ListRow(y.Env.MinY, 2, new[] { ("site_office", Face.Street) });
        if (sizes.TryGetValue("site_plant", out var plant))
        {
            double at = yardDepth - plant.y * .5;
            foreach (var piece in Polygons(And(y.Field, Rect(y.Env.MinX - 1, yardDepth - plant.y, y.Env.MaxX + 1, yardDepth))))
            {
                var e = piece.EnvelopeInternal;
                int placed = 0;
                for (double s = e.MinX + plant.x * .5; s + plant.x * .5 <= e.MaxX + 1e-6;)
                    s += y.Put("site_plant", s, at, 0, -1) ? plant.x + QuarryPlantSpacing * (1 + 1.5 * Rand(seed, 40 + placed++)) : 2;
            }
        }
        // What the plants leave of the strip against the cut: a pond here and there, then stockpiles in a loose row.
        Geometry rear = And(y.Field, Rect(y.Env.MinX - 1, yardDepth - 12.5, y.Env.MaxX + 1, yardDepth));
        y.Scatter(rear, 30, seed, 4, new List<(string part, float share)> { ("site_pond", .5f) }, true);
        y.Scatter(rear, 11, seed, 5, y.Series("site_stockpile", .8f), true);
        y.SpineTo(yardDepth + FacilityRoad);
        var pit = Largest(And(y.Site.Buffer(-rim), Rect(y.Env.MinX - rim, yardDepth, y.Env.MaxX + rim, y.Env.MaxY + rim)));
        if (pit != null)
        {
            p.Pit = ToWorld(y.F, pit.ExteriorRing.Coordinates);
            p.PitFloor = y.F.World(0, 0);
        }
        Litter(y, seed, pit, new[] { "site_truck", "site_loader", "site_truck", "site_pickup", "site_excavator" });
        y.Close();
    }

    /// <summary>
    /// Production lines across the band at depth t: a run-of-mine pad, a crusher with its hopper to the pit, then stockpiles
    /// (each line draws how many, from 2 to PitPilesPerCrusher) one conveyor span apart on the side the crusher's screen tower is on,
    /// each joined to the one before by a span of the overhead conveyor; the numbered pile parts take turns away from the crusher.
    /// The line repeats while crushers fit.
    /// Relies on how the parts are authored: site_crusher has its hopper on the back and its screen tower PitOutlet from the origin
    /// toward model -X, on the centerline; site_conveyor is symmetric along X about its one trestle, so trestles stand between piles;
    /// a pile's peak is over its origin, below the conveyor.
    /// </summary>
    static bool PitLines(FacilityYard y, double t, uint seed)
    {
        if (!y.Sizes.TryGetValue("site_crusher", out var crusher)) return false;
        const double gap = 3;
        // The hopper (model back) faces the pit, so the tower (model -X, Unity local +X) lies to the right of the unit's front.
        Vector2 front = y.F.Dir(0, -1);
        int dir = Vector2.Dot(new Vector2(front.y, -front.x), y.F.U) >= 0 ? 1 : -1;
        double pitch = y.Sizes.TryGetValue("site_conveyor", out var conveyor) ? conveyor.x : 0;
        int kinds = 0;
        while (y.Has("site_stockpile" + (kinds + 1))) kinds++;
        bool hasRom = y.Sizes.TryGetValue("site_rom", out var rom), feeds = kinds > 0 && pitch > 0;
        double at = t + crusher.y * .5;
        bool any = false;
        int lines = 0;
        foreach (var piece in Polygons(And(y.Field, Rect(y.Env.MinX - 1, t, y.Env.MaxX + 1, t + crusher.y))))
        {
            var e = piece.EnvelopeInternal;
            double cursor = dir > 0 ? e.MinX : e.MaxX, end = dir > 0 ? e.MaxX : e.MinX;
            while ((end - cursor) * dir >= crusher.x)
            {
                // A crusher goes only where its first pile fits too. Prefer room for the pad before it; where it fits neither way
                // (a slanted site edge, the end of the band), slide along.
                double center = 0;
                bool placed = false;
                foreach (double lead in hasRom ? new[] { rom.x + gap, 0 } : new[] { 0.0 })
                {
                    center = cursor + dir * (lead + crusher.x * .5);
                    if (feeds && !y.Fits("site_stockpile1", center + dir * (PitOutlet + pitch), at, 0, -1)) continue;
                    if (!y.Put("site_crusher", center, at, 0, -1)) continue;
                    if (lead > 0) y.Put("site_rom", cursor + dir * rom.x * .5, at, 0, -1);
                    placed = true;
                    break;
                }
                if (!placed) { cursor += dir * 2; continue; }
                any = true;
                cursor = center + dir * crusher.x * .5;
                int piles = 2 + (int)(Rand(seed, 60 + lines++) * (PitPilesPerCrusher - 1));
                for (int k = 1; k <= piles && feeds; k++)
                {
                    string pile = "site_stockpile" + ((k - 1) % kinds + 1);
                    double s = center + dir * (PitOutlet + pitch * k);
                    if (!y.Put(pile, s, at, 0, -1)) break;
                    y.Add("site_conveyor", s - dir * pitch * .5, at, 0, -1, true);
                    cursor = s + dir * y.Sizes[pile].x * .5;
                }
                cursor += dir * gap;
            }
        }
        return any;
    }
}
