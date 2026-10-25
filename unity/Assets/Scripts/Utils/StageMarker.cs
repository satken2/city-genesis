using System;
using Unity.Profiling;

/// <summary>
/// ProfilerMarker that becomes a no-op outside the Unity runtime. Plain .NET test harnesses load game classes directly,
/// where every ProfilerMarker call (creating one, or even disposing a default scope) throws.
/// </summary>
public readonly struct StageMarker
{
    private static readonly bool Available = Probe();
    private readonly ProfilerMarker marker;

    public StageMarker(string name) => marker = Available ? new ProfilerMarker(name) : default;

    public Scope Auto() => new Scope(marker, Available);

    // Outside Unity the native call fails with an exception type that depends on the host, so any exception means unavailable.
    private static bool Probe()
    {
        try
        {
            _ = new ProfilerMarker(nameof(StageMarker));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ProfilerMarker marker;
        private readonly bool active;

        internal Scope(ProfilerMarker marker, bool active)
        {
            this.marker = marker;
            this.active = active;
            if (active) marker.Begin();
        }

        public void Dispose()
        {
            if (active) marker.End();
        }
    }
}
