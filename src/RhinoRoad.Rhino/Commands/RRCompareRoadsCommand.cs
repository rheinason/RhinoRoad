using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

/// <summary>
/// Compares the clearance footprints of two saved journeys (or two combined footprints): what designing
/// for B instead of A costs in extra space, and what it frees. The output is a static snapshot.
/// </summary>
[Guid("5B0E2C4A-93D1-4F6E-A7B8-2D6C41E90F13")]
public sealed class RRCompareRoadsCommand : Command
{
    private static readonly Color OnlyAColour = Color.FromArgb(220, 90, 40);
    private static readonly Color OnlyBColour = Color.FromArgb(40, 120, 220);

    public override string EnglishName => "RRCompareRoads";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var first = Pick(document, "Select the first saved journey or combined footprint (A)", out var resultA);
        if (first is null) return resultA;
        var second = Pick(document, "Select the second saved journey or combined footprint (B)", out var resultB);
        if (second is null) return resultB;
        try
        {
            var a = Footprint(document, first);
            var b = Footprint(document, second);
            var comparison = EnvelopeComparison.Compare(a.Regions, b.Regions);
            Bake(document, comparison);
            Report(comparison, a, b);
            document.Views.Redraw();
            return Result.Success;
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine($"Comparison stopped: {error.Message}");
            return Result.Failure;
        }
    }

    private static RhinoObject[]? Pick(RhinoDoc document, string prompt, out Result result)
    {
        using var pick = new GetObject();
        pick.SetCommandPrompt(prompt);
        pick.GeometryFilter = ObjectType.AnyObject;
        pick.SubObjectSelect = false;
        pick.Get();
        result = pick.CommandResult();
        if (result != Result.Success) return null;
        return new[] { pick.Object(0).Object() };
    }

    private sealed record Side(IReadOnlyList<SweptRegion> Regions, string Description);

    /// <summary>Same per-journey path as RRCombineRoads: current clearance region of each source, then the union.</summary>
    private static Side Footprint(RhinoDoc document, RhinoObject[] selected)
    {
        var ids = AccessDerivedService.SourceIds(document, selected);
        var regions = new List<SweptRegion>();
        var descriptions = new List<string>();
        foreach (var id in ids)
        {
            var (_, saved, run) = AccessDerivedService.PrepareJourney(document, id, "Update that journey first.");
            regions.Add(run.Geometry.ClearanceRegion!);
            descriptions.Add($"{run.Analysis.Vehicle.Name} ({saved.VehicleId}), mode {saved.ModeId}");
        }
        return new Side(AccessFootprint.Combine(regions), string.Join(" + ", descriptions));
    }

    private static void Bake(RhinoDoc document, EnvelopeComparisonResult comparison)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var layer = AccessDefinitionStore.EnsureChildLayer(document, "Comparison", Color.Gray);
        var created = new List<Guid>();
        try
        {
            AddRegions(comparison.OnlyA, "Only in A", OnlyAColour);
            AddRegions(comparison.OnlyB, "Only in B", OnlyBColour);
            if (created.Count > 0)
            {
                var group = document.Groups.Add($"RhinoRoad comparison {Guid.NewGuid():D}");
                if (group < 0 || !document.Groups.AddToGroup(group, created))
                    throw new InvalidOperationException("Rhino could not group the comparison output.");
            }
        }
        catch
        {
            foreach (var id in created) DocumentObjects.Delete(document, id);
            throw;
        }

        void AddRegions(IReadOnlyList<SweptRegion> regions, string label, Color colour)
        {
            foreach (var region in regions)
            {
                var name = $"{label} {region.AreaSquareMetres.ToString("0.00", CultureInfo.InvariantCulture)} m2";
                var attributes = new ObjectAttributes
                {
                    LayerIndex = layer, Name = name, ObjectColor = colour, ColorSource = ObjectColorSource.ColorFromObject
                };
                var curves = new List<Curve>();
                foreach (var loop in new[] { region.OuterBoundary }.Concat(region.Holes))
                {
                    var curve = new PolylineCurve(loop.Append(loop[0]).Select(p => new Point3d(p.X * scale, p.Y * scale, 0)));
                    curves.Add(curve);
                    Add(curve, attributes);
                }
                try
                {
                    var pattern = document.HatchPatterns.FindName("Solid")?.Index ?? 0;
                    var hatches = Hatch.Create(curves, pattern, 0, 1, document.ModelAbsoluteTolerance);
                    if (hatches is not null)
                        foreach (var hatch in hatches) Add(hatch, attributes);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    RhinoApp.WriteLine($"Hatch skipped for {name}: {error.Message}");
                }
            }
        }

        void Add(GeometryBase geometry, ObjectAttributes attributes)
        {
            var id = document.Objects.Add(geometry, attributes);
            if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not create the comparison output.");
            created.Add(id);
        }
    }

    private static void Report(EnvelopeComparisonResult c, Side a, Side b)
    {
        static string Area(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + " m2";
        RhinoApp.WriteLine($"A: {a.Description}");
        RhinoApp.WriteLine($"B: {b.Description}");
        RhinoApp.WriteLine($"Area A {Area(c.AreaA)}, area B {Area(c.AreaB)}");
        RhinoApp.WriteLine($"Only A {Area(c.OnlyAArea)}, only B {Area(c.OnlyBArea)}, overlap {Area(c.BothArea)}");
        RhinoApp.WriteLine($"Net difference (B - A) {(c.NetDifference >= 0 ? "+" : "-")}{Area(Math.Abs(c.NetDifference))}: " +
            (c.NetDifference > 0 ? "B needs more space than A." : c.NetDifference < 0 ? "B needs less space than A." : "equal space."));
        RhinoApp.WriteLine("Comparison outputs on layer RhinoRoad::Comparison are static (not tracked or updated); " +
            "rerun RRCompareRoads after editing a journey.");
    }
}
