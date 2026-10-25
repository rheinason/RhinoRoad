using System.Drawing;
using Grasshopper.Kernel;

namespace RhinoRoad.GrasshopperPlugin;

public sealed class RhinoRoadInfo : GH_AssemblyInfo
{
    public override string Name => "RhinoRoad";
    public override Bitmap? Icon => null;
    public override string Description => "Vehicle swept-path and access screening over the RhinoRoad Core.";
    public override Guid Id => new("5d3a0a62-8f0e-4f6c-9a53-7b0c6c4f1a01");
    public override string AuthorName => "rheinason";
    public override string AuthorContact => "https://github.com/rheinason/RhinoRoad";
    public override string Version => typeof(RhinoRoadInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

public sealed class RhinoRoadCategoryIcon : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        Grasshopper.Instances.ComponentServer.AddCategorySymbolName("RhinoRoad", 'R');
        return GH_LoadingInstruction.Proceed;
    }
}
