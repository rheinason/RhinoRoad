using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// The reversing prompts name the body that actually backs into the bay, so a rigid lorry is never
/// asked for the rear of a trailer, and a shortfall is described by what fell short.
/// </summary>
public sealed class ReverseWordingTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static VehicleDefinition ThreeJoints
    {
        get
        {
            var pvt = Catalog.Get("PVT");
            return pvt with
            {
                Id = "PVT3",
                TowedUnits = [.. pvt.TowedUnits, pvt.TowedUnits[^1] with { Id = "SECOND", HitchOffsetMetres = -1.15 }]
            };
        }
    }

    private static readonly ManoeuvreControl Bay =
        new(new Point3(-20, 0, 0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);

    [Theory]
    [InlineData("REN", "vehicle", "rear axle")]
    [InlineData("PV", "vehicle", "rear axle")]
    [InlineData("SVT", "trailer", "trailer axle")]
    [InlineData("PVT", "trailer", "trailer axle")]
    public void The_bay_is_named_by_the_body_that_docks(string id, string body, string axle)
    {
        var vehicle = Catalog.Get(id);

        Assert.Equal(body, ReverseWording.DockingBody(vehicle));
        Assert.Equal($"the rear of the {body}", ReverseWording.BayRear(vehicle));
        Assert.Equal(axle, ReverseWording.DockingAxle(vehicle));
    }

    [Fact]
    public void A_rigid_bay_that_is_missed_says_it_cannot_back_square_rather_than_steering_limit()
    {
        var rigid = Catalog.Get("REN");

        Assert.StartsWith("Cannot back square into that bay from here", ReverseWording.ShortfallWarning(rigid, Bay));
        Assert.Contains($"rear axle stopped {1.25:0.00} m", ReverseWording.CommittedShortfall(rigid, Bay, 1.25));
        // Without a direction a rigid reverse is an ordinary aim, and its shortfall is the lock.
        var aim = Bay with { ExitHeadingRadians = null };
        Assert.StartsWith("Steering limit reached", ReverseWording.ShortfallWarning(rigid, aim));
        Assert.Null(ReverseWording.CommittedShortfall(rigid, aim, 1.25));
    }

    [Fact]
    public void A_trailer_shortfall_names_the_trailer_and_what_it_was_asked_for()
    {
        var svt = Catalog.Get("SVT");

        Assert.StartsWith("Trailer cannot reach that point", ReverseWording.ShortfallWarning(svt, Bay));
        Assert.Contains("from the bay", ReverseWording.CommittedShortfall(svt, Bay, 0.6));
        Assert.Contains("from the picked point", ReverseWording.CommittedShortfall(svt, Bay with { ExitHeadingRadians = null }, 0.6));
        Assert.StartsWith("Steering limit reached",
            ReverseWording.ShortfallWarning(svt, Bay with { Direction = TravelDirection.Forward }));
    }

    [Fact]
    public void Changing_into_reverse_says_what_the_point_now_places()
    {
        Assert.Null(ReverseWording.ReverseGuidance(Catalog.Get("REN"), TravelDirection.Forward));
        var rigid = ReverseWording.ReverseGuidance(Catalog.Get("REN"), TravelDirection.Reverse)!;
        Assert.StartsWith("Pick where the rear axle should stop", rigid);
        Assert.Contains("the rear of the vehicle at the bay", rigid);
        var svt = ReverseWording.ReverseGuidance(Catalog.Get("SVT"), TravelDirection.Reverse)!;
        Assert.StartsWith("Pick where the trailer axle should stop", svt);
        Assert.Contains("the rear of the trailer at the bay", svt);
        // A chain that cannot be squared onto a bay is not offered one.
        Assert.DoesNotContain("bay", ReverseWording.ReverseGuidance(ThreeJoints, TravelDirection.Reverse)!);
    }

    [Fact]
    public void A_proposal_whose_heading_was_not_honoured_says_so()
    {
        var vehicle = ThreeJoints;
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(state, vehicle) };
        var target = new Point3(vehicle.StraightAxleOffsetsMetres[^1] - 30.0, 0.0, 0.0);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, state, target, Math.PI);

        var summary = ReverseWording.ProposalSummary(vehicle, proposal);
        Assert.Contains("trailer axle miss", summary);
        Assert.Contains("heading not honoured", summary);
        Assert.DoesNotContain("heading miss", summary);
        Assert.Contains("not honoured", ReverseWording.ProposalCommitted(vehicle, proposal));
    }

    [Fact]
    public void A_rigid_proposal_is_reported_by_its_rear_axle_and_heading()
    {
        var vehicle = Catalog.Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(state, vehicle) };

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, state, new Point3(-15, 4, 0), Math.PI);

        Assert.True(proposal.HeadingHonoured);
        var summary = ReverseWording.ProposalSummary(vehicle, proposal);
        Assert.Contains("rear axle miss", summary);
        Assert.Contains("heading miss", summary);
        Assert.DoesNotContain("trailer", ReverseWording.ProposalCommitted(vehicle, proposal));
    }
}
