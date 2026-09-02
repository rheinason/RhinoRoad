using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class CertificationPlanTests
{
    private static CertificationPlan Shipped() =>
        CertificationPlan.Load(Path.Combine(AppContext.BaseDirectory, "Reference", CertificationPlan.FileName));

    private static CertificationPlan Sample() => new(
        [40, 100, 180],
        [
            new CertificationVehicle("PV", true,
            [
                new CertificationDrawing("A", "a/PV_A.dwg", "PV A"),
                new CertificationDrawing("B", "b/PV_B.dwg", "PV B")
            ]),
            // The published catalogue ships T and SKT in mode B only, so the matrix must not
            // assume every vehicle carries every mode.
            new CertificationVehicle("T", false, [new CertificationDrawing("B", "b/T_B.dwg", "T B")])
        ]);

    [Fact]
    public void ShippedPlanLoadsAndDescribesTheReport()
    {
        var plan = Shipped();

        Assert.NotEmpty(plan.Vehicles);
        Assert.NotEmpty(plan.AnglesGon);
        Assert.All(plan.Vehicles, vehicle => Assert.NotEmpty(vehicle.Drawings));
        Assert.All(
            plan.Vehicles.SelectMany(vehicle => vehicle.Drawings),
            drawing => Assert.EndsWith(".dwg", drawing.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CaseCountFollowsTheMatrixRatherThanAConstant()
    {
        var plan = Sample();

        // Two PV drawings plus one T drawing, across three angles.
        Assert.Equal(9, plan.ExpectedCaseCount);
        Assert.Equal(["A", "B"], plan.ModeIdsFor("PV"));
        Assert.Equal(["B"], plan.ModeIdsFor("T"));
        Assert.Empty(plan.ModeIdsFor("NOPE"));
    }

    [Fact]
    public void TrialVehiclesAreCarriedWithoutGatingRelease()
    {
        var plan = Sample();

        Assert.Equal(["PV"], plan.RequiredVehicleIds);
        Assert.NotNull(plan.Find("T"));
        Assert.False(plan.Find("T")!.Required);
    }

    [Fact]
    public void CompletenessIsJudgedAgainstEachVehiclesOwnModes()
    {
        var plan = Sample();
        var report = new ReferenceCertificationReport(
            ReferenceCertificationReport.CurrentSchemaVersion,
            "now",
            "8",
            "method",
            new ReferenceCertificationEnvironment("Meters", 0.01, 0.01),
            new ReferenceCertificationThresholds(0.10, 0.05),
            [.. Cases("T", "B"), .. Cases("PV", "A")]);

        // T is complete on its single mode; PV is not, because its B drawing produced no cases.
        Assert.True(report.IsCompleteFor(plan, "T"));
        Assert.True(report.IsValidated(plan, "T"));
        Assert.False(report.IsCompleteFor(plan, "PV"));
        Assert.False(report.IsValidated(plan, "PV"));

        static IEnumerable<ReferenceCertificationCase> Cases(string vehicleId, string modeId) =>
            new[] { 40, 100, 180 }.Select(angle => new ReferenceCertificationCase(
                vehicleId, modeId, angle, "x.dwg", new string('0', 64),
                0.01, 0.01, null, 3.26, 0.01, 3.26, 900, true, "ok"));
    }

    [Fact]
    public void MalformedPlansAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new CertificationPlan([], [.. Sample().Vehicles]).Validate());
        Assert.Throws<InvalidDataException>(() => new CertificationPlan([40], []).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new CertificationPlan([40], [new CertificationVehicle("PV", true, [])]).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new CertificationPlan([40], [new CertificationVehicle("PV", true,
            [
                new CertificationDrawing("A", "one.dwg", "one"),
                new CertificationDrawing("A", "two.dwg", "two")
            ])]).Validate());
    }
}
