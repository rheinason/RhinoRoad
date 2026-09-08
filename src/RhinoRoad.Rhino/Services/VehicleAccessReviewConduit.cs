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

    /// <summary>
    /// Marks the station the panel is pointing at. The vehicle outline alone was not enough to answer
    /// "where on the road is this?" -- at a zoom that shows the whole route it is a thin white sliver
    /// among the footprints. A marker on the route itself, with the reading beside it, is the part
    /// that actually ties the graph to the geometry, so it is drawn whatever else is switched off.
    /// </summary>
    private void DrawHighlightedPose(DrawEventArgs e)
    {
        if (_snapshot is null) return;
        var station = SelectedEvent?.WorstStationMetres ?? HoveredStation;
        if (!station.HasValue) return;
        var review = _snapshot.Review;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits);
        var pose = _snapshot.Run.Analysis.Poses.MinBy(item => Math.Abs(item.StationMetres - station.Value))!;
        foreach (var outline in pose.OccupiedOutlinesWorldMetres)
        {
            var points = outline.Select(point => new Point3d(
                point.X * scale, point.Y * scale, pose.RearAxleCentreMetres.Z * scale)).ToList();
            points.Add(points[0]);
            e.Display.DrawPolyline(new Polyline(points), Color.White, 4);
        }

        if (review.Samples.Count == 0) return;
        var index = NearestSample(review, station.Value);
        var sample = review.Samples[index];
        var anchor = ToModel(sample.PositionMetres, scale);
        var value = VehicleAccessMetricPalette.Value(Metric, sample);
        // Offset the label across the route so the leader is readable rather than sitting on top of
        // the swept band, and lift it clear of the footprints stacked at the same elevation.
        var ahead = review.Samples[Math.Min(index + 1, review.Samples.Count - 1)];
        var behind = review.Samples[Math.Max(index - 1, 0)];
        var tangent = new Vector3d(
            (ahead.PositionMetres.X - behind.PositionMetres.X) * scale,
            (ahead.PositionMetres.Y - behind.PositionMetres.Y) * scale,
            0.0);
        if (!tangent.Unitize()) tangent = Vector3d.XAxis;
        var offset = Math.Max(_snapshot.Run.Geometry.RearAxleTrack.GetBoundingBox(true).Diagonal.Length * 0.04,
            RhinoMath.UnitScale(UnitSystem.Meters, ModelUnits) * 2.0);
        var label = anchor + new Vector3d(-tangent.Y, tangent.X, 0.0) * offset;
        e.Display.DrawLine(anchor, label, Color.White, 2);
        e.Display.DrawPoint(anchor, PointStyle.RoundControlPoint, 11, Color.White);
        e.Display.DrawDot(
            label,
            $"Sta {sample.StationMetres:0.0} m  {MetricLabel(Metric)} {value:0.##}",
            VehicleAccessMetricPalette.For(Metric, value, review),
            Color.White);
    }

    private static int NearestSample(VehicleAccessReview review, double station)
    {
        var nearest = 0;
        var best = double.MaxValue;
        for (var index = 0; index < review.Samples.Count; index++)
        {
            var distance = Math.Abs(review.Samples[index].StationMetres - station);
            if (distance >= best) continue;
            best = distance;
            nearest = index;
        }
        return nearest;
    }

    private static string MetricLabel(ReviewMetricKind kind) => kind switch
    {
        ReviewMetricKind.SteeringAngle => "wheel°",
        ReviewMetricKind.SteeringRate => "°/s",
        ReviewMetricKind.ClearanceOrRoadMargin => "margin m",
        ReviewMetricKind.Grade => "grade %",
        _ => string.Empty
    };

    private static Point3d ToModel(Point3 point, double scale) => new(point.X * scale, point.Y * scale, point.Z * scale);
}
