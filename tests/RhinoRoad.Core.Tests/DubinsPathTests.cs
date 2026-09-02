using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the bounded-curvature path solver.
/// </summary>
/// <remarks>
/// Nothing in the plugin drives these paths — a rate-limited wheel cannot execute one, because the
/// curvature steps instantly where an arc meets a tangent, and for these vehicles reaching full
/// lock costs metres of travel. It is kept because it is the exact geometric answer to "what is the
/// shortest bounded-curvature path between two poses", which is the seed a future steering-profile
/// solver needs, and because a wrong word here would be silent: every candidate returns a plausible
/// length whether or not it solves the problem posed.
/// </remarks>
public sealed class DubinsPathTests
{
    private const double Radius = 4.821;

    /// <summary>
    /// The words are transcribed trigonometric identities. Integrating each solution back and
    /// checking where it lands is the only test that can tell a right one from a wrong one.
    /// </summary>
    [Fact]
    public void EverySolutionLandsOnTheGoalPose()
    {
        var random = new Random(20260902);
        var solved = 0;

        for (var attempt = 0; attempt < 20000; attempt++)
        {
            var start = new Pose2(new Point2(0, 0), ((random.NextDouble() * 2) - 1) * Math.PI);
            var goal = new Pose2(
                new Point2(((random.NextDouble() * 2) - 1) * 40, ((random.NextDouble() * 2) - 1) * 40),
                ((random.NextDouble() * 2) - 1) * Math.PI);

            var solution = DubinsPath.Solve(start, goal, Radius);
            Assert.NotNull(solution);
            solved++;

            var end = DubinsPath.PoseAt(start, solution, solution.LengthMetres);
            Assert.True(
                end.PositionMetres.DistanceTo(goal.PositionMetres) < 1e-9,
                $"{solution.Word} landed {end.PositionMetres.DistanceTo(goal.PositionMetres):E3} m from the goal");
            Assert.True(
                Math.Abs(Geometry2D.NormalizeAngle(end.HeadingRadians - goal.HeadingRadians)) < 1e-9,
                $"{solution.Word} arrived on the wrong heading");
        }

        Assert.Equal(20000, solved);
    }

    [Fact]
    public void NoSolutionIsShorterThanTheStraightLineBetweenThePoses()
    {
        var random = new Random(7);

        for (var attempt = 0; attempt < 5000; attempt++)
        {
            var start = new Pose2(new Point2(0, 0), ((random.NextDouble() * 2) - 1) * Math.PI);
            var goal = new Pose2(
                new Point2(((random.NextDouble() * 2) - 1) * 30, ((random.NextDouble() * 2) - 1) * 30),
                ((random.NextDouble() * 2) - 1) * Math.PI);

            var solution = DubinsPath.Solve(start, goal, Radius);

            Assert.NotNull(solution);
            Assert.True(solution.LengthMetres >= start.PositionMetres.DistanceTo(goal.PositionMetres) - 1e-9);
        }
    }

    [Fact]
    public void DrivingStraightAheadIsASingleTangent()
    {
        var solution = DubinsPath.Solve(
            new Pose2(new Point2(0, 0), 0.0),
            new Pose2(new Point2(20, 0), 0.0),
            Radius);

        Assert.NotNull(solution);
        Assert.Equal(20.0, solution.LengthMetres, 9);
        Assert.Equal(20.0, solution.Segments.Single(segment => segment.Direction == ArcDirection.Straight).LengthMetres, 9);
    }

    /// <summary>A U-turn too tight for a tangent between the circles falls to a three-arc word.</summary>
    [Fact]
    public void ATurnTighterThanTheTangentSolutionUsesThreeArcs()
    {
        var solution = DubinsPath.Solve(
            new Pose2(new Point2(0, 0), 0.0),
            new Pose2(new Point2(0, Radius), Math.PI),
            Radius);

        Assert.NotNull(solution);
        Assert.Contains(solution.Word, new[] { "RLR", "LRL" });
        Assert.DoesNotContain(solution.Segments, segment => segment.Direction == ArcDirection.Straight);
    }

    [Fact]
    public void CurvatureIsTheTurningRadiusOnArcsAndZeroOnTangents()
    {
        var start = new Pose2(new Point2(0, 0), 0.0);
        var solution = DubinsPath.Solve(start, new Pose2(new Point2(25, 25), Math.PI / 2.0), Radius);
        Assert.NotNull(solution);

        for (var distance = 0.0; distance < solution.LengthMetres; distance += 0.25)
        {
            var curvature = Math.Abs(DubinsPath.CurvatureAt(solution, distance));
            Assert.True(curvature < 1e-12 || Math.Abs(curvature - (1.0 / Radius)) < 1e-12);
        }
    }

    [Fact]
    public void ARadiusThatIsNotPositiveIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DubinsPath.Solve(new Pose2(new Point2(0, 0), 0.0), new Pose2(new Point2(10, 0), 0.0), 0.0));
    }
}
