using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using CommandResult = global::Rhino.Commands.Result;

namespace RhinoRoad.Rhino.Services;

internal enum DirectionPick { Picked, Cleared, Cancelled }

/// <summary>
/// The viewport picks the Edit Road palette and its clear-area search share. Each one leaves the
/// road's control line selected with its grips on afterwards, as the edit session expects.
/// </summary>
internal static class JourneyEditPicks
{
    public static bool IsClosedEnough(RhinoDoc document, Curve curve) =>
        curve.IsClosed || curve.IsClosable(document.ModelAbsoluteTolerance);

    public static void RestoreEditSelection(RhinoDoc document, Guid sourceId)
    {
        document.Objects.UnselectAll();
        document.Objects.Select(sourceId);
        if (document.Objects.FindId(sourceId) is { } source) source.GripsOn = true;
        document.Views.Redraw();
    }

    /// <summary>
    /// Picks curves in the viewport, other than the road's own control line. That line is selected
    /// while editing, and a picker that honoured that selection took it -- an open curve -- before
    /// anything could be clicked; so preselection is ignored and the selection restored afterwards.
    /// Null when cancelled; with <paramref name="multiple"/>, Enter with nothing picked gives none.
    /// </summary>
    public static List<Guid>? PickCurves(RhinoDoc document, Guid sourceId, string prompt, bool multiple, bool closedOnly)
    {
        document.Objects.UnselectAll();
        try
        {
            using var picker = new GetObject();
            picker.SetCommandPrompt(prompt);
            picker.GeometryFilter = ObjectType.Curve;
            picker.SubObjectSelect = false;
            picker.EnablePreSelect(false, true);
            picker.SetCustomGeometryFilter((rhinoObject, geometry, _) =>
                rhinoObject?.Id != sourceId && geometry is Curve curve && (!closedOnly || IsClosedEnough(document, curve)));
            if (multiple)
            {
                picker.AcceptNothing(true);
                picker.GetMultiple(1, 0);
                if (picker.CommandResult() == CommandResult.Cancel) return null;
                if (picker.Result() == GetResult.Nothing) return [];
            }
            else if (picker.Get() != GetResult.Object) return null;
            return Enumerable.Range(0, picker.ObjectCount).Select(index => picker.Object(index).ObjectId).ToList();
        }
        finally
        {
            RestoreEditSelection(document, sourceId);
        }
    }

    /// <summary>
    /// Picks a direction from <paramref name="pivot"/>. It is a real point pick from that base point,
    /// so Rhino's ortho (F8 or Shift, at its ortho angle on the CPlane) and object snaps constrain it
    /// as they would drawing a line; an angle can also be typed, counted from the CPlane.
    /// </summary>
    public static DirectionPick PickDirection(
        RhinoDoc document, Point3d pivot, string prompt, bool allowClear,
        Action<DisplayPipeline, double>? draw, out double headingRadians)
    {
        headingRadians = 0.0;
        var reference = document.Views.ActiveView?.ActiveViewport is { } viewport ? CPlaneHeading(viewport) : 0.0;
        using var picker = new GetPoint();
        picker.SetCommandPrompt(prompt);
        picker.SetBasePoint(pivot, true);
        picker.DrawLineFromPoint(pivot, true);
        picker.PermitObjectSnap(true);
        picker.PermitOrthoSnap(true);
        picker.AcceptNumber(true, true);
        picker.AcceptNothing(allowClear);
        picker.DynamicDraw += (_, args) =>
        {
            var delta = args.CurrentPoint - pivot;
            if (draw is null || new Vector2d(delta.X, delta.Y).Length <= document.ModelAbsoluteTolerance) return;
            draw(args.Display, Math.Atan2(delta.Y, delta.X));
        };
        while (true)
        {
            var result = picker.Get();
            if (result == GetResult.Nothing && allowClear) return DirectionPick.Cleared;
            if (result == GetResult.Number)
            {
                headingRadians = Geometry2D.NormalizeAngle(reference + (picker.Number() * Math.PI / 180.0));
                return DirectionPick.Picked;
            }
            if (result != GetResult.Point) return DirectionPick.Cancelled;
            var delta = picker.Point() - pivot;
            if (new Vector2d(delta.X, delta.Y).Length > document.ModelAbsoluteTolerance)
            {
                headingRadians = Math.Atan2(delta.Y, delta.X);
                return DirectionPick.Picked;
            }
            RhinoApp.WriteLine("That point is on the control itself, so it gives no direction; pick away from it.");
        }
    }

    public static void DrawDockedTrailer(RhinoDoc document, DisplayPipeline display, VehicleDefinition vehicle, Point3 axle, double heading)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var outline = TrailerBay.DockedOutline(vehicle, axle, heading)
            .Select(point => new Point3d(point.X * scale, point.Y * scale, axle.Z * scale)).ToList();
        if (outline.Count == 0) return;
        outline.Add(outline[0]);
        display.DrawPolyline(new Polyline(outline), Color.MediumPurple, 2);
    }

    public static double Degrees(double radians)
    {
        var degrees = radians * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private static double CPlaneHeading(RhinoViewport viewport)
    {
        var axis = viewport.GetConstructionPlane().Plane.XAxis;
        return Math.Abs(axis.X) + Math.Abs(axis.Y) <= RhinoMath.ZeroTolerance ? 0.0 : Math.Atan2(axis.Y, axis.X);
    }
}
