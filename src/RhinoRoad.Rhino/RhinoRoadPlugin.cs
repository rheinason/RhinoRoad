using Rhino.PlugIns;

namespace RhinoRoad.Rhino;

public sealed class RhinoRoadPlugin : PlugIn
{
    public RhinoRoadPlugin()
    {
        Instance = this;
        Services.AccessDerivedService.Initialize();
        Services.LiveJourneyEditConduit.Initialize();
    }

    public static RhinoRoadPlugin? Instance { get; private set; }
}
