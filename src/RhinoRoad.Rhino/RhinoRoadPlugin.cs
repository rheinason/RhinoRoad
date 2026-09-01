using Rhino.PlugIns;

namespace RhinoRoad.Rhino;

public sealed class RhinoRoadPlugin : PlugIn
{
    public RhinoRoadPlugin() => Instance = this;

    public static RhinoRoadPlugin? Instance { get; private set; }
}
