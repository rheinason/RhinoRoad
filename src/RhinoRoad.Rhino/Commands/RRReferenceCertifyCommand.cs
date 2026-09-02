using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("55E12960-557A-4D9B-A7F1-A6F5D2963485")]
[CommandStyle(Style.ScriptRunner)]
public sealed class RRReferenceCertifyCommand : Command
{
    public override string EnglishName => "RRReferenceCertify";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        try
        {
            var repositoryRoot = ReferenceCertificationService.FindRepositoryRoot();
            var plan = ReferenceCertificationService.LoadPlan(repositoryRoot);
            var report = ReferenceCertificationService.Run(document, repositoryRoot, plan);
            var outputPath = ReferenceCertificationService.WriteReport(repositoryRoot, report);
            var passed = report.Cases.Count(item => item.Passed);
            RhinoApp.WriteLine($"RhinoRoad reference certification: {passed}/{report.Cases.Count} cases passed.");
            RhinoApp.WriteLine($"Report: {outputPath}");
            foreach (var vehicleId in plan.RequiredVehicleIds)
            {
                RhinoApp.WriteLine($"  {vehicleId}: {(report.IsValidated(plan, vehicleId) ? "VALIDATED" : "NOT VALIDATED")}");
            }

            return report.Cases.Count == plan.ExpectedCaseCount ? Result.Success : Result.Failure;
        }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"RhinoRoad reference certification failed: {exception.Message}");
            RhinoApp.WriteLine(exception.StackTrace ?? string.Empty);
            return Result.Failure;
        }
    }
}
