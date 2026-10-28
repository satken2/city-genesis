using System.Collections.Generic;
using UnityEngine;

/// <summary>Fixed parking candidates on clipped roads. Vehicle types and placements are not resampled when counts or the camera change.</summary>
public static class ParkedVehicleLayout
{
    public const float EndMargin = 5f;
    public const float Gap = 1.2f;
    public const float CurbMargin = .02f;
    private const float SingleLaneCurbMargin = .2f;

    public sealed class Model
    {
        public int type, variant;
        public int weight; // Appearance-selection weights within the same vehicle type (ModelWeights).
        public Vector3 size;
    }

    public sealed class Placement
    {
        public int model, side;
        public float distance;
        public Vector3 position;
        public Quaternion rotation;
        public uint priority;
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352dU;
        value ^= value >> 15; value *= 0x846ca68bU;
        return value ^ (value >> 16);
    }

    public static int VehicleType(uint seed)
    {
        uint choice = Hash(seed) % 10;
        return choice < 6 ? Constants.VEHICLE_STANDARD
            : choice < 9 ? Constants.VEHICLE_COMMERCIAL : Constants.VEHICLE_TRUCK;
    }

    /// <summary>Round fractional counts probabilistically with a road-ID-fixed seed, avoiding rounding small parking counts up on every road.</summary>
    public static int Count(float cars, int capacity, int pathId)
    {
        if (float.IsNaN(cars) || cars <= 0 || capacity <= 0) return 0;
        if (cars >= capacity) return capacity;
        int count = Mathf.FloorToInt(cars);
        float fraction = (Hash((uint)pathId) & 0xffffff) / 16777216f;
        return count + (fraction < cars - count ? 1 : 0);
    }

    public static List<Placement> Build(VehiclePolyline line, int pathId, float width, float sidewalk,
        float divider, int lanes, int direction, int trafficHand, IReadOnlyList<Model> models)
    {
        var result = new List<Placement>();
        if (line.Length <= EndMargin * 2 || models.Count == 0) return result;
        float outer = width * .5f - Mathf.Max(0, sidewalk);
        float inner = Mathf.Max(0, divider) * .5f;
        float curbMargin = lanes >= 2 ? CurbMargin : SingleLaneCurbMargin;
        var eligible = new Dictionary<int, List<int>>();
        var eligibleWeights = new Dictionary<int, List<int>>();
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i].size.x + curbMargin > outer - inner) continue;
            if (!eligible.TryGetValue(models[i].type, out var choices))
            {
                eligible[models[i].type] = choices = new List<int>();
                eligibleWeights[models[i].type] = new List<int>();
            }
            choices.Add(i);
            eligibleWeights[models[i].type].Add(models[i].weight);
        }
        if (eligible.Count == 0) return result;
        var nearby = new SpatialHash<Placement>(16);
        float maxLength = 0;
        foreach (var model in models) maxLength = Mathf.Max(maxLength, model.size.magnitude);
        // 中央線のない1車線道路は、順方向の通行側だけに駐車する。一方通行では許可された向きに合わせる。
        int parkingSide = (trafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1)
            * (direction == Constants.DIR_BACKWARD ? -1 : 1);
        for (int side = -1; side <= 1; side += 2)
        {
            if (lanes <= 1 && side != parkingSide) continue;
            float cursor = EndMargin;
            int slot = 0;
            while (cursor < line.Length - EndMargin)
            {
                uint seed = Hash(unchecked((uint)pathId * 397U + (uint)(slot++ * 2 + (side > 0 ? 1 : 0))));
                int type = VehicleType(seed);
                if (!eligible.TryGetValue(type, out var choices)) { cursor += Gap + 4; continue; }
                int index = choices[ModelWeights.IndexBySeed(eligibleWeights[type], Hash(seed + 1))];
                var model = models[index];
                float length = Mathf.Max(1, model.size.z);
                float s = cursor + length * .5f;
                cursor += length + Gap;
                if (s + length * .5f > line.Length - EndMargin) break;
                bool forward = direction == Constants.DIR_FORWARD || (direction == Constants.DIR_BOTH &&
                    side == (trafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1));
                float offset = side * (outer - curbMargin - model.size.x * .5f);
                line.Sample(s, true, out var center, out var tangent);
                Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
                var p = new Placement { model = index, side = side, distance = s, priority = Hash(seed + 2),
                    position = center + right * offset + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE,
                    rotation = Quaternion.LookRotation(forward ? tangent : -tangent, Vector3.up) };
                if (!FitsRoad(line, p, model.size, outer, inner)) continue;
                bool overlaps = false;
                foreach (var other in nearby.Query(p.position, maxLength))
                    if (Overlaps(p, model.size, other, models[other.model].size)) { overlaps = true; break; }
                if (overlaps) continue;
                nearby.Add(p, p.position);
                result.Add(p);
            }
        }
        result.Sort((a, b) => { int c = a.priority.CompareTo(b.priority); return c != 0 ? c : a.distance.CompareTo(b.distance); });
        return result;
    }

    private static bool FitsRoad(VehiclePolyline line, Placement p, Vector3 size, float outer, float inner)
    {
        // Exclude candidates where long bodies protrude into sidewalks or medians on curves.
        for (int x = -1; x <= 1; x += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 corner = p.position + p.rotation * new Vector3(x * size.x * .5f, 0, z * size.z * .5f);
            float reach = size.z + outer;
            float s = line.ProjectRange(corner, p.distance - reach, p.distance + reach);
            if (s < EndMargin - .01f || s > line.Length - EndMargin + .01f) return false;
            line.Sample(s, true, out var center, out var tangent);
            float lateral = Vector3.Dot(corner - center, Vector3.Cross(Vector3.up, tangent).normalized) * p.side;
            if (lateral < inner - .01f || lateral > outer + .01f) return false;
        }
        return true;
    }

    public static bool Overlaps(Placement a, Vector3 aSize, Placement b, Vector3 bSize)
    {
        Vector3 ar = a.rotation * Vector3.right, af = a.rotation * Vector3.forward;
        Vector3 br = b.rotation * Vector3.right, bf = b.rotation * Vector3.forward;
        foreach (var axis in new[] { ar, af, br, bf })
        {
            float reachA = (Mathf.Abs(Vector3.Dot(axis, ar)) * aSize.x + Mathf.Abs(Vector3.Dot(axis, af)) * aSize.z) * .5f;
            float reachB = (Mathf.Abs(Vector3.Dot(axis, br)) * bSize.x + Mathf.Abs(Vector3.Dot(axis, bf)) * bSize.z) * .5f;
            if (Mathf.Abs(Vector3.Dot(b.position - a.position, axis)) >= reachA + reachB + .1f) return false;
        }
        return true;
    }
}
