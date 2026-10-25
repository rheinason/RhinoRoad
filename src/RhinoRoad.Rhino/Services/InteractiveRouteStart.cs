using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>Where the vehicle stands, and which way it faces, before the first leg is driven.</summary>
internal static class InteractiveRouteStart
{
    /// <summary>
    /// The start state, picked as a point and a heading, or taken tangent to an existing curve.
    /// Null when the pick was abandoned; <paramref name="result"/> then says how.
    /// </summary>
    public static VehicleState? Pick(RhinoDoc document, TravelDirection initialDirection, out Result result)
    {
        var metresPerModelUnit = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
        while (true)
        {
            using var startGetter = new GetPoint();
            startGetter.SetCommandPrompt("Place vehicle (rear axle centre)");
            var tangentOption = startGetter.AddOption("TangentToCurve");
            var startResult = startGetter.Get();
            if (startResult == GetResult.Option && startGetter.OptionIndex() == tangentOption)
            {
                // Abandoning the curve pick returns here rather than leaving the command, since the
                // designer may simply have changed their mind about how to start.
                if (FromCurve(document, initialDirection) is { } tangent)
                {
                    result = Result.Success;
                    return tangent;
                }

                continue;
            }

            if (startResult != GetResult.Point)
            {
                result = Result.Cancel;
                return null;
            }

            var startModel = startGetter.Point();
            using var headingGetter = new GetPoint();
            headingGetter.SetCommandPrompt("Vehicle forward heading");
            headingGetter.SetBasePoint(startModel, true);
            headingGetter.DrawLineFromPoint(startModel, true);
            if (headingGetter.Get() != GetResult.Point)
            {
                result = Result.Cancel;
                return null;
            }

            var headingVector = headingGetter.Point() - startModel;
            if (Math.Sqrt((headingVector.X * headingVector.X) + (headingVector.Y * headingVector.Y)) <= document.ModelAbsoluteTolerance)
            {
                RhinoApp.WriteLine("Heading point is too close to the start point.");
                result = Result.Failure;
                return null;
            }

            result = Result.Success;
            return new VehicleState(
                new Point3(startModel.X * metresPerModelUnit, startModel.Y * metresPerModelUnit, startModel.Z * metresPerModelUnit),
                Math.Atan2(headingVector.Y, headingVector.X),
                0.0,
                initialDirection,
                0.0);
        }
    }

    /// <summary>
    /// A start continuing an existing curve, usually a road centreline: at the end nearer the pick,
    /// travelling on past it. The travel direction follows the curve, so a journey that starts in
    /// reverse faces back along it. No heading is picked afterwards; the curve supplies it.
    /// </summary>
    private static VehicleState? FromCurve(RhinoDoc document, TravelDirection initialDirection)
    {
        var metresPerModelUnit = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
        var atEnd = new OptionToggle(true, "PickedPoint", "CurveEnd");
        using var getter = new GetObject();
        getter.SetCommandPrompt("Select the curve to continue, near the end to start from");
        getter.GeometryFilter = ObjectType.Curve;
        getter.SubObjectSelect = false;
        // The pick point says which end is meant, and a preselected curve has none.
        getter.DisablePreSelect();
        getter.EnableUnselectObjectsOnExit(true);
        getter.AddOptionToggle("StartAt", ref atEnd);
        while (true)
        {
            var picked = getter.Get();
            if (picked == GetResult.Option) continue;
            if (picked != GetResult.Object) return null;
            break;
        }

        var reference = getter.Object(0);
        var curve = reference.Curve();
        if (curve is null) return null;
        if (reference.CurveParameter(out var pickedParameter) is null
            && !curve.ClosestPoint(reference.SelectionPoint(), out pickedParameter))
        {
            pickedParameter = curve.Domain.T1;
        }

        var lengthModel = curve.GetLength();
        if (!(lengthModel > document.ModelAbsoluteTolerance))
        {
            RhinoApp.WriteLine("That curve has no length to continue from.");
            return null;
        }

        var placement = TangentStart.Place(
            curve.GetLength(new Interval(curve.Domain.T0, pickedParameter)) * metresPerModelUnit,
            lengthModel * metresPerModelUnit,
            curve.IsClosed,
            atEnd.CurrentValue);
        if (!curve.LengthParameter(placement.LengthFromStartMetres / metresPerModelUnit, out var startParameter))
        {
            startParameter = placement.AlongCurve ? curve.Domain.T1 : curve.Domain.T0;
        }

        var position = curve.PointAt(startParameter);
        var tangent = curve.TangentAt(startParameter);
        if (!placement.AlongCurve) tangent.Reverse();
        if (Math.Abs(tangent.X) + Math.Abs(tangent.Y) <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("The curve is vertical there, so it gives no plan direction to start along.");
            return null;
        }

        var travelHeading = Math.Atan2(tangent.Y, tangent.X);
        var degrees = Geometry2D.NormalizeAngle(travelHeading) * 180.0 / Math.PI;
        RhinoApp.WriteLine(
            $"Starting tangent to the curve{(placement.AlongCurve ? string.Empty : " (against its direction)")}, "
            + $"travelling {initialDirection.ToString().ToLowerInvariant()} at {(degrees < 0.0 ? degrees + 360.0 : degrees):0.#} deg.");
        return new VehicleState(
            new Point3(position.X * metresPerModelUnit, position.Y * metresPerModelUnit, position.Z * metresPerModelUnit),
            TangentStart.VehicleHeading(travelHeading, initialDirection),
            0.0,
            initialDirection,
            0.0);
    }
}
