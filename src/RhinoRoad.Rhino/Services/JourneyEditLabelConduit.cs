using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Numbers the controls in the viewport so a row in the Edit Road palette names something visible,
/// and shows what the clear-area search is working with: the picked site, the bay and a suggestion.
/// </summary>
internal sealed class JourneyEditLabelConduit : DisplayConduit
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    public Guid SourceId { get; set; }
    public int? Highlighted { get; set; }
    public ManoeuvringAreaSearch.Candidate? SearchCandidate { get; set; }
    public Guid? YardId { get; set; }
    public Guid[] ObstacleIds { get; set; } = [];

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (e.RhinoDoc is not { } document || document.Objects.FindId(SourceId) is not { } source) return;
        if (!AccessDefinitionStore.TryReadCached(source, out var definition, out _) || definition?.Manoeuvre is null) return;
        if (source.Geometry is not Curve curve) return;
        var vertices = AccessDefinitionStore.PolylineVertices(curve, document.ModelUnitSystem);
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);

        // The site the search will use, so what was picked is visible rather than remembered.
        if (YardId is Guid yardId && document.Objects.FindId(yardId)?.Geometry is Curve yard)
            e.Display.DrawCurve(yard, Color.SeaGreen, 4);
        foreach (var obstacleId in ObstacleIds)
            if (document.Objects.FindId(obstacleId)?.Geometry is Curve obstacle)
                e.Display.DrawCurve(obstacle, Color.IndianRed, 3);

        // The trailer as it stands in the bay at the control line's current end, rear marked.
        if (definition.Manoeuvre.Controls.Count > 0 && vertices.Count > 1 &&
            definition.Manoeuvre.Controls[^1] is { Direction: TravelDirection.Reverse, ExitHeadingRadians: double bayHeading } &&
            Catalog.Get(definition.VehicleId) is { IsArticulated: true } bayVehicle)
        {
            var axle = vertices[^1];
            var docked = TrailerBay.DockedOutline(bayVehicle, axle, bayHeading)
                .Select(point => new Point3d(point.X * scale, point.Y * scale, axle.Z * scale)).ToList();
            if (docked.Count > 0)
            {
                docked.Add(docked[0]);
                e.Display.DrawDottedPolyline(docked, Color.MediumPurple, true);
                var rear = TrailerBay.RearFromAxle(bayVehicle, axle, bayHeading);
                e.Display.DrawDot(new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale),
                    "rear", Color.MediumPurple, Color.White);
            }
        }
        if (SearchCandidate is { } suggestion)
        {
            var height = suggestion.Route[0].PositionMetres.Z * scale;
            e.Display.DrawPolyline(suggestion.Route.Select(sample =>
                new Point3d(sample.PositionMetres.X * scale,
                    sample.PositionMetres.Y * scale, sample.PositionMetres.Z * scale)),
                Color.MediumVioletRed, 3);
            foreach (var loop in new[] { suggestion.Clearance.OuterBoundary }
                .Concat(suggestion.Clearance.Holes))
                e.Display.DrawPolyline(loop.Append(loop[0]).Select(point =>
                    new Point3d(point.X * scale, point.Y * scale, height)),
                    Color.SeaGreen, 2);
            e.Display.DrawDot(new Point3d(suggestion.LimitingPointMetres.X * scale,
                suggestion.LimitingPointMetres.Y * scale, height),
                "tightest", Color.DarkOrange, Color.White);
        }
        var controls = definition.Manoeuvre.Controls;
        // Grips move the vertices but never add or remove one, and the palette's insert and delete
        // rewrite the line and the controls together, so the vertex list still lines up with the
        // stored controls; a mismatch means the curve was edited another way.
        var count = Math.Min(controls.Count, Math.Max(0, vertices.Count - 1));
        for (var index = 0; index < count; index++)
        {
            var control = controls[index];
            var vertex = vertices[index + 1];
            var point = new Point3d(vertex.X * scale, vertex.Y * scale, vertex.Z * scale);
            var highlighted = Highlighted == index;
            var colour = highlighted ? Color.Gold
                : control.Kind == ManoeuvreControlKind.Finish ? Color.DarkOrange
                : control.Direction == TravelDirection.Forward ? Color.RoyalBlue : Color.MediumPurple;
            e.Display.DrawDot(point, $"{index + 1}", colour, Color.White);
        }
    }
}
