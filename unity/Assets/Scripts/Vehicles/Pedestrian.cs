using UnityEngine;

/// <summary>
/// State of one pedestrian without a rendering object. Retain lateral position relative to path geometry (start -> end),
/// staying on the same sidewalk even when direction reverses. Connector polylines represent intersections and building entry/exit.
/// </summary>
public sealed class Pedestrian
{
    public int pathId, variant;
    public int previousPath = -1, previousPath2 = -1;   // Previous one and two paths traversed, passed to citizen sampling for a clicked entity.
    public bool forward;
    public float s, speed, age;
    public int side;                 // Side relative to path geometry: +1=right, -1=left; 0 for pedestrian-only roads using a central band.
    public float lateral;            // Signed lateral offset from centerline (m).
    public float surface;            // Height above the polyline's spline reference surface.
    public float walked;             // Accumulated walking distance for pose phase (m), initialized randomly per entity so footsteps do not synchronize.
    public bool moving;              // Whether the latest movement step advanced. Use the feet-together pose while stopped.
    public VehiclePolyline road, connector;
    public float connectorS;
    public bool arrivalPending, fading;
    public float fadeElapsed;
    public float arrivalDistance = -1;
    public int destinationParcel, destinationBuilding;
    public int searchedPath = -1, accessGeneration = -1;
    public Vector3 destinationPoint;
    public bool Entering { get; private set; }
    public bool InConnector => connector != null;
    public bool ConnectorComplete => InConnector && connectorS >= connector.Length - .001f;
    public float TotalLength => road.Length;
    public float Remaining => forward ? TotalLength - s : s;

    public void SetPath(int id, VehiclePolyline points, bool travelForward, float startS = -1)
    {
        pathId = id; road = points; forward = travelForward;
        s = startS < 0 ? (forward ? 0 : TotalLength) : Mathf.Clamp(startS, 0, TotalLength);
    }

    public void Sample(out Vector3 position, out Vector3 tangent)
    {
        if (InConnector) { connector.Sample(connectorS, true, out position, out tangent); return; }
        road.Sample(s, true, out position, out Vector3 geometry);
        position += Vector3.Cross(Vector3.up, geometry).normalized * lateral + Vector3.up * surface;
        tangent = forward ? geometry : -geometry;
    }

    /// <summary>Advance to the end of the state and return unused time, preserving travel time even on short paths.</summary>
    public float Advance(float seconds)
    {
        float velocity = Mathf.Max(0, speed);
        if (velocity <= .00001f) { age += seconds; return 0; }
        float remaining = InConnector ? connector.Length - connectorS : Remaining;
        if (!InConnector && arrivalDistance >= 0) remaining = Mathf.Min(remaining, arrivalDistance);
        float used = Mathf.Min(seconds, Mathf.Max(0, remaining) / velocity);
        float distance = used * velocity;
        age += used;
        walked += distance;
        if (InConnector) connectorS += distance;
        else
        {
            s = Mathf.Clamp(s + (forward ? distance : -distance), 0, TotalLength);
            if (arrivalDistance >= 0) arrivalDistance = Mathf.Max(0, arrivalDistance - distance);
        }
        return Mathf.Max(0, seconds - used);
    }

    /// <summary>Move straight from the current position into a site point, then shrink and disappear on arrival.</summary>
    public void BeginEntering(Vector3 destination)
    {
        Sample(out Vector3 start, out _);
        connector = new VehiclePolyline(new[] { start, destination });
        connectorS = 0; Entering = true; arrivalDistance = -1;
    }

    /// <summary>Move straight from a site point to the current sidewalk position, then resume normal walking.</summary>
    public void BeginLeaving(Vector3 origin)
    {
        Sample(out Vector3 target, out _);
        if (Vector3.Distance(origin, target) < .01f) return;
        connector = new VehiclePolyline(new[] { origin, target });
        connectorS = 0;
    }

    public void EndConnector() { connector = null; }
}
