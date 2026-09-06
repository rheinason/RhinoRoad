using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

[Flags]
internal enum VehicleAccessOverlayParts
{
    None = 0,
    MetricRoute = 1,
    Envelopes = 2,
    Footprints = 4,
    RoadReferences = 8,
    Events = 16,
    ControlIntent = 32,
    All = MetricRoute | Envelopes | Footprints | RoadReferences | Events | ControlIntent
}

internal sealed class VehicleAccessReviewConduit : DisplayConduit
{
    private VehicleAccessReviewSnapshot? _snapshot;
    public ReviewMetricKind Metric { get; set; } = ReviewMetricKind.SteeringAngle;
    public VehicleAccessOverlayParts Parts { get; set; } = VehicleAccessOverlayParts.All;
    public UnitSystem ModelUnits { get; set; } = UnitSystem.Meters;
    public double? HoveredStation { get; set; }
    public VehicleAccessProblemEvent? SelectedEvent { get; set; }

    public void SetSnapshot(VehicleAccessReviewSnapshot? snapshot) => _snapshot = snapshot;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (_snapshot is null) return;
        var box = _snapshot.Run.Geometry.RearAxleTrack.GetBoundingBox(true);
        if (_snapshot.Run.Geometry.ClearanceEnvelope is not null)
            box.Union(_snapshot.Run.Geometry.ClearanceEnvelope.GetBoundingBox(true));
        foreach (var curve in _snapshot.Run.Obstacles.Concat(_snapshot.Run.AllowedBoundaries))
            box.Union(curve.GetBoundingBox(true));
        if (box.IsValid) e.IncludeBoundingBox(box);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (_snapshot is null) return;
        if (LiveJourneyEditConduit.IsPreviewing(e.RhinoDoc, _snapshot.Run.Definition.StableSourceId)) return;
        var run = _snapshot.Run;
        var review = _snapshot.Review;
        e.Display.PushDepthTesting(false);
        try
        {
            if (Parts.HasFlag(VehicleAccessOverlayParts.MetricRoute))
            {
                var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
                for (var index = 0; index < review.Samples.Count - 1; index++)
                {
                    var a = review.Samples[index];
                    var b = review.Samples[index + 1];
                    e.Display.DrawLine(
                        ToModel(a.PositionMetres, scale),
                        ToModel(b.PositionMetres, scale),
                        VehicleAccessMetricPalette.For(Metric, VehicleAccessMetricPalette.Value(Metric, a), review),
                        6);
                }
            }
            if (Parts.HasFlag(VehicleAccessOverlayParts.Envelopes))
            {
                if (run.Geometry.BodyEnvelope is not null) e.Display.DrawCurve(run.Geometry.BodyEnvelope, Color.DarkOrange, 3);
                if (run.Geometry.ClearanceEnvelope is not null) e.Display.DrawCurve(run.Geometry.ClearanceEnvelope, Color.OrangeRed, 3);
            }
            if (Parts.HasFlag(VehicleAccessOverlayParts.Footprints))
                foreach (var footprint in run.Geometry.Footprints) e.Display.DrawCurve(footprint, Color.SlateGray, 1);
            if (Parts.HasFlag(VehicleAccessOverlayParts.RoadReferences))
            {
                foreach (var edge in run.Geometry.FixedRoadEdges) e.Display.DrawCurve(edge, Color.ForestGreen, 3);
                foreach (var curve in run.Obstacles) e.Display.DrawCurve(curve, Color.Firebrick, 2);
                foreach (var curve in run.AllowedBoundaries) e.Display.DrawCurve(curve, Color.MediumSeaGreen, 2);
            }
            if (Parts.HasFlag(VehicleAccessOverlayParts.Events))
            {
                var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
                foreach (var item in review.ProblemEvents)
                    e.Display.DrawPoint(ToModel(item.WorldPointMetres, scale), PointStyle.RoundActivePoint, 8, Color.OrangeRed);
            }
            if (Parts.HasFlag(VehicleAccessOverlayParts.ControlIntent)) DrawControlIntent(e);
            DrawSelectedSection(e);
            DrawHighlightedPose(e);
        }
        finally { e.Display.PopDepthTesting(); }
    }

    private void DrawControlIntent(DrawEventArgs e)
    {
        var manoeuvre = _snapshot?.Run.Definition.Manoeuvre;
        if (manoeuvre is null) return;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
        var previousDirection = manoeuvre.StartDirection;
        for (var index = 0; index < manoeuvre.Controls.Count; index++)
        {
            var control = manoeuvre.Controls[index];
            var point = ToModel(control.PositionMetres, scale);
            var colour = control.Kind == ManoeuvreControlKind.Finish
                ? Color.DarkOrange
                : control.Direction == TravelDirection.Forward ? Color.RoyalBlue : Color.MediumPurple;
            e.Display.DrawPoint(point, PointStyle.RoundControlPoint, 7, colour);
            if (index == 0 || control.Direction != previousDirection || control.Kind == ManoeuvreControlKind.Finish)
            {
                var label = control.Kind == ManoeuvreControlKind.Finish
                    ? $"Finish · {DirectionName(control.Direction)}"
                    : DirectionName(control.Direction);
                e.Display.DrawDot(point, label, colour, Color.White);
            }
            previousDirection = control.Direction;
        }
    }

    private static string DirectionName(TravelDirection direction) =>
        direction == TravelDirection.Forward ? "Forward" : "Reverse";

    private void DrawSelectedSection(DrawEventArgs e)
    {
        if (_snapshot is null || SelectedEvent is null) return;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
        var samples = _snapshot.Review.Samples.Where(sample =>
            sample.StationMetres >= SelectedEvent.StartStationMetres - 0.1 &&
            sample.StationMetres <= SelectedEvent.EndStationMetres + 0.1).ToArray();
        if (samples.Length < 2) return;
        for (var index = 0; index < samples.Length - 1; index++)
            e.Display.DrawLine(ToModel(samples[index].PositionMetres, scale), ToModel(samples[index + 1].PositionMetres, scale), Color.White, 10);
    }

    private void DrawHighlightedPose(DrawEventArgs e)
    {
        if (_snapshot is null) return;
        var station = SelectedEvent?.WorstStationMetres ?? HoveredStation;
        if (!station.HasValue) return;
        var pose = _snapshot.Run.Analysis.Poses.MinBy(item => Math.Abs(item.StationMetres - station.Value))!;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
        var points = pose.BodyOutlineWorldMetres.Select(point => new Point3d(
            point.X * scale, point.Y * scale, pose.RearAxleCentreMetres.Z * scale)).ToList();
        points.Add(points[0]);
        e.Display.DrawPolyline(new Polyline(points), Color.White, 4);
    }

    private static Point3d ToModel(Point3 point, double scale) => new(point.X * scale, point.Y * scale, point.Z * scale);
}
