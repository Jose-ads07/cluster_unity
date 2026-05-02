using Unity.Profiling;

public static class ClusterProfilerMarkers
{
    public static readonly ProfilerMarker Update = new ProfilerMarker("Cluster.Update");
    public static readonly ProfilerMarker DrainNetwork = new ProfilerMarker("Cluster.DrainNetwork");
    public static readonly ProfilerMarker Tick = new ProfilerMarker("Cluster.Tick");
    public static readonly ProfilerMarker SnapshotSerialize = new ProfilerMarker("Cluster.SnapshotSerialize");
    public static readonly ProfilerMarker SimulateSerialize = new ProfilerMarker("Cluster.SimulateSerialize");
    public static readonly ProfilerMarker ApplyResult = new ProfilerMarker("Cluster.ApplyResult");
    public static readonly ProfilerMarker LocalFallback = new ProfilerMarker("Cluster.LocalFallback");
    public static readonly ProfilerMarker VisualUpdate = new ProfilerMarker("Cluster.VisualUpdate");
}
