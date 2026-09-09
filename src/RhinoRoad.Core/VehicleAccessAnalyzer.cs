namespace RhinoRoad.Core;

public sealed class VehicleAccessAnalyzer
{
    /// <summary>
    /// The fold past which the result stops describing a real vehicle.
    /// </summary>
    /// <remarks>
    /// No per-vehicle jackknife limit is published, and it cannot be recovered from the presets
    /// either: a semitrailer's body legitimately overlaps its tractor's in plan, because it sits on
    /// top of it, so the angle at which they would actually collide is not a plan-geometry question.
    /// A right angle is used instead, and it is a kinematic fact rather than a vehicle specification.
    /// The towed unit's speed along its own axis is <c>v·cos y - w·h·sin y</c>; at a right angle the
    /// first term is gone and the unit is no longer following the one towing it at all. Past that
    /// the fold only runs further, and the footprints being unioned into an envelope are of a
    /// configuration no driver reaches and no vehicle survives.
    ///
    /// <para>
    /// Real jackknife happens earlier than this — contact typically comes somewhere past 60 degrees
    /// — so this is a floor, not a threshold: clearing it does not mean the manoeuvre is drivable.
    /// It exists so a folded run cannot be reported as feasible, which it was.
    /// </para>
    /// </remarks>
    public const double JackknifeFoldRadians = Math.PI / 2.0;

    public VehicleAccessResult Analyze(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> samples,
        double? maximumAbsoluteGrade = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        if (samples.Count < 2) throw new ArgumentException("A route requires at least two samples.", nameof(samples));
        vehicle.Validate();

        var poses = new List<VehiclePose>(samples.Count);
        var rearTrack = new List<Point2>(samples.Count);
        var frontTrack = new List<Point2>(samples.Count);
        var violations = new List<AnalysisViolation>();
        var maximumSteering = 0.0;
        var maximumSteeringRate = 0.0;
        var maximumGrade = 0.0;
        double? previousSteering = null;

        // A towed unit is placed from route history, not from the current sample, so the walk is
        // handed to ArticulationTrace, which carries the fold forward one interval at a time.
        var towedTracks = vehicle.TowedUnits.Select(_ => new List<Point2>(samples.Count)).ToArray();
        var maximumFolds = new double[vehicle.TowedUnits.Count];

        // Reported once per joint, where the fold first crosses. Past that the run is meaningless
        // rather than progressively worse, and a marker every quarter metre of it says nothing the
        // first one did not.
        var jackknifed = new bool[vehicle.TowedUnits.Count];

        var index = -1;
        foreach (var (sample, vehicleHeading, chain) in ArticulationTrace.Follow(vehicle, samples))
        {
            index++;
            var directionSign = (double)sample.Direction;
            var steering = Math.Atan(vehicle.WheelbaseMetres * sample.SignedCurvaturePerMetre / directionSign);
            var rear = sample.PositionMetres.XY;
            var front = Geometry2D.Transform(new Point2(vehicle.WheelbaseMetres, 0.0), rear, vehicleHeading);
            var footprint = vehicle.BodyOutline.Select(point => Geometry2D.Transform(point, rear, vehicleHeading)).ToArray();

            var towedPoses = chain?.Poses(rear, vehicleHeading) ?? [];
            for (var unit = 0; unit < towedPoses.Count; unit++)
            {
                var fold = Math.Abs(towedPoses[unit].ArticulationAngleRadians);
                towedTracks[unit].Add(towedPoses[unit].AxleCentreMetres);
                maximumFolds[unit] = Math.Max(maximumFolds[unit], fold);
                if (fold > JackknifeFoldRadians && !jackknifed[unit])
                {
                    jackknifed[unit] = true;
                    violations.Add(new AnalysisViolation(
                        ViolationKind.ArticulationAngle,
                        sample.StationMetres,
                        sample.PositionMetres,
                        $"The {vehicle.TowedUnits[unit].Name} is folded {ToDegrees(fold):0.0}° against the unit " +
                        "towing it. The combination has jackknifed, and the swept area past this point " +
                        "is not a shape any driver reaches."));
                }
            }

            rearTrack.Add(rear);
            frontTrack.Add(front);
            poses.Add(new VehiclePose(
                sample.StationMetres,
                sample.PositionMetres,
                front,
                vehicleHeading,
                steering,
                sample.Direction,
                footprint)
            {
                TowedUnits = towedPoses
            });

            maximumSteering = Math.Max(maximumSteering, Math.Abs(steering));
            if (Math.Abs(steering) > mode.MaximumWheelAngleRadians + 1e-8)
            {
                violations.Add(new AnalysisViolation(
                    ViolationKind.SteeringAngle,
                    sample.StationMetres,
                    sample.PositionMetres,
                    $"Wheel angle {ToDegrees(Math.Abs(steering)):0.0}° exceeds {mode.MaximumWheelAngleDegrees:0.0}°."));
            }

            if (sample.IsTangentDiscontinuous)
            {
                violations.Add(new AnalysisViolation(
                    ViolationKind.TangentDiscontinuity,
                    sample.StationMetres,
                    sample.PositionMetres,
                    "The route contains a tangent discontinuity that a rigid vehicle cannot follow."));
            }

            if (index > 0)
            {
                var previous = samples[index - 1];
                var distance = sample.PositionMetres.XY.DistanceTo(previous.PositionMetres.XY);
                if (distance > Geometry2D.Epsilon)
                {
                    // A wheel turned at a standstill covers no distance, so measuring its movement
                    // against the distance to the next sample says the wheel moved infinitely fast.
                    // It did not: it moved while the vehicle was stopped, which the rate limit -- a
                    // rate per second, spent here per metre -- has nothing to say about.
                    if (!sample.StartsFromStandstill)
                    {
                        var elapsed = distance / mode.SpeedMetresPerSecond;
                        var steeringRate = previousSteering.HasValue
                            ? Math.Abs(Geometry2D.NormalizeAngle(steering - previousSteering.Value)) / elapsed
                            : 0.0;
                        maximumSteeringRate = Math.Max(maximumSteeringRate, steeringRate);
                        if (steeringRate > mode.MaximumSteeringRateRadiansPerSecond + 1e-8)
                        {
                            violations.Add(new AnalysisViolation(
                                ViolationKind.SteeringRate,
                                sample.StationMetres,
                                sample.PositionMetres,
                                $"Steering rate {ToDegrees(steeringRate):0.0}°/s exceeds {ToDegrees(mode.MaximumSteeringRateRadiansPerSecond):0.0}°/s."));
                        }
                    }

                    var grade = (sample.PositionMetres.Z - previous.PositionMetres.Z) / distance;
                    maximumGrade = Math.Max(maximumGrade, Math.Abs(grade));
                    if (maximumAbsoluteGrade.HasValue && Math.Abs(grade) > maximumAbsoluteGrade.Value + 1e-8)
                    {
                        violations.Add(new AnalysisViolation(
                            ViolationKind.Grade,
                            sample.StationMetres,
                            sample.PositionMetres,
                            $"Grade {Math.Abs(grade) * 100.0:0.00}% exceeds {maximumAbsoluteGrade.Value * 100.0:0.00}%."));
                    }
                }
            }

            previousSteering = steering;
        }

        return new VehicleAccessResult
        {
            Vehicle = vehicle,
            DrivingMode = mode,
            Poses = poses,
            RearAxleTrackMetres = rearTrack,
            FrontAxleTrackMetres = frontTrack,
            Violations = CoalesceViolations(violations),
            MaximumSteeringAngleRadians = maximumSteering,
            MaximumSteeringRateRadiansPerSecond = maximumSteeringRate,
            MaximumAbsoluteGrade = maximumGrade,
            TowedAxleTracksMetres = towedTracks.Select(track => (IReadOnlyList<Point2>)track).ToArray(),
            MaximumArticulationAnglesRadians = maximumFolds
        };
    }

    private static IReadOnlyList<AnalysisViolation> CoalesceViolations(IReadOnlyList<AnalysisViolation> violations)
    {
        var result = new List<AnalysisViolation>();
        AnalysisViolation? previous = null;
        foreach (var violation in violations.OrderBy(item => item.StationMetres).ThenBy(item => item.Kind))
        {
            if (previous is not null && previous.Kind == violation.Kind && violation.StationMetres - previous.StationMetres < 0.25)
            {
                continue;
            }

            result.Add(violation);
            previous = violation;
        }

        return result;
    }

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
