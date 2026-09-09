namespace RhinoRoad.Core;

public sealed class VehicleAccessAnalyzer
{
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
                towedTracks[unit].Add(towedPoses[unit].AxleCentreMetres);
                maximumFolds[unit] = Math.Max(maximumFolds[unit], Math.Abs(towedPoses[unit].ArticulationAngleRadians));
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
