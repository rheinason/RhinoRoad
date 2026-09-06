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

[Guid("C3697BC4-5D35-46E9-B422-034B17D2C476")]
public sealed class RREditVehicleJourneyCommand : Command
{
    public override string EnglishName => "RREditRoad";

    protected override Result RunCommand(RhinoDoc document, RunMode mode) => Execute(document, mode);

    internal static Result Execute(RhinoDoc document, RunMode mode)
    {
        using var pick = new GetObject();
        pick.SetCommandPrompt("Select road to edit");
        pick.Get();
        if (pick.CommandResult() != Result.Success) return pick.CommandResult();
        var source = AccessDefinitionStore.ResolveSource(document, pick.Object(0).Object());
        if (source is null || !AccessDefinitionStore.TryRead(source, out var saved, out _) ||
            saved?.Manoeuvre is null || source.Geometry is not Curve curve)
        {
            RhinoApp.WriteLine("Select a saved click-to-drive journey. Move its control grips to edit positions.");
            return Result.Failure;
        }
        try
        {
            var journey = ManoeuvreControlReconciler.Reconcile(saved.Manoeuvre,
                AccessDefinitionStore.PolylineVertices(curve, document.ModelUnitSystem));
            using var action = new GetOption();
            action.SetCommandPrompt("Edit road (Enter for point editing)");
            action.AcceptNothing(true);
            var points = action.AddOption("Points");
            var settings = action.AddOption("Settings");
            var heading = action.AddOption("StartHeading");
            var direction = action.AddOption("StartDirection");
            action.AddOption("Control");
            var result = action.Get();
            if (result == GetResult.Nothing || (result == GetResult.Option && action.OptionIndex() == points))
            {
                if (source.IsLocked) throw new InvalidOperationException("Unlock the control line before editing its points.");
                document.Objects.UnselectAll();
                document.Objects.Select(source.Id);
                source.GripsOn = true;
                document.Views.Redraw();
                RhinoApp.WriteLine("Drag control points to preview the path and sweep. RRUpdateRoad saves the result and reruns checks.");
                return Result.Success;
            }
            if (result != GetResult.Option) return Result.Cancel;
            if (action.OptionIndex() == settings)
            {
                document.Objects.UnselectAll();
                document.Objects.Select(source.Id);
                return RRVehicleAccessCommand.Execute(document, mode);
            }
            if (action.OptionIndex() == heading)
            {
                using var aim = new GetPoint();
                aim.SetCommandPrompt("Pick the vehicle's starting forward heading");
                aim.SetBasePoint(curve.PointAtStart, true);
                aim.DrawLineFromPoint(curve.PointAtStart, true);
                if (aim.Get() != GetResult.Point) return Result.Cancel;
                var delta = aim.Point() - curve.PointAtStart;
                if (new Vector2d(delta.X, delta.Y).Length <= document.ModelAbsoluteTolerance) return Result.Failure;
                journey = journey with { StartHeadingRadians = Math.Atan2(delta.Y, delta.X) };
            }
            else if (action.OptionIndex() == direction)
            {
                using var choose = new GetOption();
                choose.SetCommandPrompt("Starting travel direction (preserves reversal pattern)");
                var forward = choose.AddOption("Forward");
                choose.AddOption("Reverse");
                if (choose.Get() != GetResult.Option) return Result.Cancel;
                journey = ManoeuvreControlReconciler.WithStartDirection(journey,
                    choose.OptionIndex() == forward ? TravelDirection.Forward : TravelDirection.Reverse);
            }
            else
            {
                var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
                using var controlPick = new GetPoint();
                controlPick.SetCommandPrompt("Pick near a numbered journey control");
                controlPick.DynamicDraw += (_, args) =>
                {
                    for (var i = 0; i < journey.Controls.Count; i++)
                    {
                        var c = journey.Controls[i];
                        args.Display.DrawDot(new Point3d(c.PositionMetres.X * scale, c.PositionMetres.Y * scale,
                            c.PositionMetres.Z * scale), $"{i + 1}: {c.Direction} / {c.Kind}");
                    }
                };
                if (controlPick.Get() != GetResult.Point) return Result.Cancel;
                var point = controlPick.Point();
                var index = Enumerable.Range(0, journey.Controls.Count).MinBy(i =>
                    new Point3d(journey.Controls[i].PositionMetres.X * scale,
                        journey.Controls[i].PositionMetres.Y * scale, journey.Controls[i].PositionMetres.Z * scale).DistanceTo(point));
                using var choice = new GetOption();
                choice.SetCommandPrompt($"Control {index + 1}: {journey.Controls[index].Direction} / {journey.Controls[index].Kind}");
                var forward = choice.AddOption("Forward");
                var reverse = choice.AddOption("Reverse");
                var aim = choice.AddOption("Aim");
                var finish = index == journey.Controls.Count - 1 ? choice.AddOption("Finish") : -1;
                if (choice.Get() != GetResult.Option) return Result.Cancel;
                var controls = journey.Controls.ToArray();
                var selected = choice.OptionIndex();
                if (selected == forward || selected == reverse)
                    controls[index] = controls[index] with { Direction = selected == forward ? TravelDirection.Forward : TravelDirection.Reverse };
                else if (selected == aim || selected == finish)
                    controls[index] = controls[index] with { Kind = selected == aim ? ManoeuvreControlKind.Aim : ManoeuvreControlKind.Finish };
                journey = journey with { Controls = controls };
            }
            var configured = saved with { Manoeuvre = journey };
            if (!VehicleAccessRunService.TryPrepareUpdate(document, source, out var run, out var error, configured) || run is null)
                throw new InvalidOperationException(error);
            VehicleAccessRunService.Commit(document, run);
            document.Views.Redraw();
            return Result.Success;
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine(error.Message);
            return Result.Failure;
        }
    }
}
