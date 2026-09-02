namespace RhinoRoad.Core;

public enum WheelTrackReconstructionStatus
{
    Success,
    InvalidVehicleGeometry,
    InsufficientVertices,
    PairingFailed,
    SeparationOutOfTolerance,
    InsufficientSamples
}

/// <summary>Tuning for <see cref="WheelTrackRouteReconstructor"/>.</summary>
public sealed record WheelTrackReconstructionOptions
{
    public static readonly WheelTrackReconstructionOptions Default = new();

    /// <summary>Spacing used to resample each source polyline segment.</summary>
    public double SampleSpacingMetres { get; init; } = 0.025;

    /// <summary>How far the measured front/rear chord may drift from the value the preset predicts.</summary>
    public double MaximumSeparationErrorMetres { get; init; } = 0.15;

    /// <summary>Reference cases below this sample count are treated as failed extractions.</summary>
    public int MinimumSampleCount { get; init; } = 500;
}

public sealed record WheelTrackReconstruction(
    WheelTrackReconstructionStatus Status,
    IReadOnlyList<RouteSample> Samples,
    double MeasuredSeparationErrorMetres,
    string Message)
{
    /// <summary>
    /// Mean chord actually measured between the two traces. A rigid vehicle holds this constant, so
    /// comparing it against the chord the preset predicts is an independent check on whether the
    /// drawing depicts the vehicle the preset describes.
    /// </summary>
    public double MeasuredChordMetres { get; init; }

    /// <summary>Spread of that chord along the run. Non-zero means the source traces disagree with themselves.</summary>
    public double MeasuredChordSpreadMetres { get; init; }

    public bool IsSuccess => Status == WheelTrackReconstructionStatus.Success;
}

/// <summary>
/// Rebuilds a rear-axle midpoint route from the paired outer wheel-edge traces published in the
/// official reference drawings.
/// </summary>
/// <remarks>
/// The two traces are diagonally opposed. With the vehicle heading <c>h</c>, the body normal
/// <c>n = (-sin h, cos h)</c>, the rear-axle midpoint <c>C</c>, wheelbase <c>L</c> and outer
/// wheel-edge offset <c>e</c>:
/// <code>
/// rear  = C - e * n
/// front = C + L * (cos h, sin h) + e * n
/// </code>
/// so the chord <c>front - rear</c> is <c>(L, 2e)</c> in the vehicle frame. Its length is therefore
/// a constant <c>sqrt(L^2 + 4e^2)</c> — used as a consistency check against the preset — and its
/// bearing leads the vehicle heading by a constant <c>atan2(2e, L)</c>. Both quantities invert to
/// give the heading and rear-axle midpoint at every paired vertex.
/// The traces are treated as planar; reconstructed sample elevations are zero.
/// </remarks>
public static class WheelTrackRouteReconstructor
{
    public static WheelTrackReconstruction Reconstruct(
        IReadOnlyList<Point2> outerFrontWheelEdge,
        IReadOnlyList<Point2> outerRearWheelEdge,
        double wheelbaseMetres,
        double wheelOuterEdgeOffsetMetres,
        WheelTrackReconstructionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outerFrontWheelEdge);
        ArgumentNullException.ThrowIfNull(outerRearWheelEdge);
        options ??= WheelTrackReconstructionOptions.Default;

        if (wheelbaseMetres <= 0.0 || wheelOuterEdgeOffsetMetres <= 0.0)
        {
            return Failed(
                WheelTrackReconstructionStatus.InvalidVehicleGeometry,
                $"Wheelbase ({wheelbaseMetres:0.000} m) and outer wheel-edge offset " +
                $"({wheelOuterEdgeOffsetMetres:0.000} m) must both be positive.");
        }

        if (outerFrontWheelEdge.Count < 2 || outerRearWheelEdge.Count < 2)
        {
            return Failed(
                WheelTrackReconstructionStatus.InsufficientVertices,
                $"Each wheel-edge trace needs at least two vertices; got {outerFrontWheelEdge.Count} front and {outerRearWheelEdge.Count} rear.");
        }

        var expectedSeparation = Math.Sqrt(
            (wheelbaseMetres * wheelbaseMetres) +
            (4.0 * wheelOuterEdgeOffsetMetres * wheelOuterEdgeOffsetMetres));
        var headingCorrection = Math.Atan2(2.0 * wheelOuterEdgeOffsetMetres, wheelbaseMetres);

        // Vertex-paired traces are used as drawn, which keeps the measured chord available as an
        // independent check. Unequal tessellation is resolved against the chord instead.
        var pairedFront = outerFrontWheelEdge;
        var pairingNote = string.Empty;
        if (outerFrontWheelEdge.Count != outerRearWheelEdge.Count)
        {
            var pairing = WheelTrackPairing.Resolve(
                outerFrontWheelEdge,
                outerRearWheelEdge,
                expectedSeparation,
                options.MaximumSeparationErrorMetres);
            if (!pairing.IsSuccess)
            {
                return Failed(
                    WheelTrackReconstructionStatus.PairingFailed,
                    $"The traces are not vertex-paired ({outerFrontWheelEdge.Count} front vertices against " +
                    $"{outerRearWheelEdge.Count} rear) and could not be reconciled. {pairing.Message}");
            }

            pairedFront = pairing.PairedFrontVertices;
            pairingNote = $" {pairing.Message}";
        }
        var worstSeparationError = 0.0;
        var chordTotal = 0.0;
        var chordMinimum = double.PositiveInfinity;
        var chordMaximum = 0.0;
        var raw = new List<(Point3 Point, double Heading)>();

        for (var segment = 0; segment < pairedFront.Count - 1; segment++)
        {
            var frontStart = pairedFront[segment];
            var frontEnd = pairedFront[segment + 1];
            var rearStart = outerRearWheelEdge[segment];
            var rearEnd = outerRearWheelEdge[segment + 1];
            var stepLength = Math.Max(frontStart.DistanceTo(frontEnd), rearStart.DistanceTo(rearEnd));
            var steps = Math.Max(1, (int)Math.Ceiling(stepLength / options.SampleSpacingMetres));

            // Every segment after the first opens on the vertex the previous one closed on.
            for (var step = segment == 0 ? 0 : 1; step <= steps; step++)
            {
                var t = (double)step / steps;
                var frontPoint = frontStart + ((frontEnd - frontStart) * t);
                var rearPoint = rearStart + ((rearEnd - rearStart) * t);
                var separationError = Math.Abs(frontPoint.DistanceTo(rearPoint) - expectedSeparation);
                if (separationError > options.MaximumSeparationErrorMetres)
                {
                    return new WheelTrackReconstruction(
                        WheelTrackReconstructionStatus.SeparationOutOfTolerance,
                        [],
                        separationError,
                        $"The paired wheel-edge chord deviates {separationError:0.000} m from the {expectedSeparation:0.000} m " +
                        $"the preset predicts, exceeding the {options.MaximumSeparationErrorMetres:0.000} m tolerance. " +
                        "The traces are probably mismatched or belong to a different vehicle.");
                }

                worstSeparationError = Math.Max(worstSeparationError, separationError);
                var chord = frontPoint.DistanceTo(rearPoint);
                chordTotal += chord;
                chordMinimum = Math.Min(chordMinimum, chord);
                chordMaximum = Math.Max(chordMaximum, chord);
                var difference = frontPoint - rearPoint;
                var heading = Geometry2D.NormalizeAngle(Math.Atan2(difference.Y, difference.X) - headingCorrection);
                var centre = new Point3(
                    rearPoint.X - (wheelOuterEdgeOffsetMetres * Math.Sin(heading)),
                    rearPoint.Y + (wheelOuterEdgeOffsetMetres * Math.Cos(heading)),
                    0.0);
                raw.Add((centre, heading));
            }
        }

        if (raw.Count < options.MinimumSampleCount)
        {
            return new WheelTrackReconstruction(
                WheelTrackReconstructionStatus.InsufficientSamples,
                [],
                worstSeparationError,
                $"Reconstruction produced {raw.Count} samples, below the {options.MinimumSampleCount} required for a certification case.");
        }

        return new WheelTrackReconstruction(
            WheelTrackReconstructionStatus.Success,
            ToRouteSamples(raw),
            worstSeparationError,
            $"Reconstructed {raw.Count} samples; measured chord {chordTotal / raw.Count:0.000} m " +
            $"against the {expectedSeparation:0.000} m the preset predicts, varying by " +
            $"{chordMaximum - chordMinimum:0.000} m along the run.{pairingNote}")
        {
            MeasuredChordMetres = chordTotal / raw.Count,
            MeasuredChordSpreadMetres = chordMaximum - chordMinimum
        };
    }

    private static IReadOnlyList<RouteSample> ToRouteSamples(IReadOnlyList<(Point3 Point, double Heading)> raw)
    {
        var stations = new double[raw.Count];
        for (var index = 1; index < raw.Count; index++)
        {
            stations[index] = stations[index - 1] + raw[index].Point.XY.DistanceTo(raw[index - 1].Point.XY);
        }

        var samples = new List<RouteSample>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var before = Math.Max(0, index - 1);
            var after = Math.Min(raw.Count - 1, index + 1);
            var span = Math.Max(stations[after] - stations[before], 1e-9);
            var curvature = Geometry2D.NormalizeAngle(raw[after].Heading - raw[before].Heading) / span;
            samples.Add(new RouteSample(
                stations[index],
                raw[index].Point,
                raw[index].Heading,
                curvature,
                TravelDirection.Forward));
        }

        return samples;
    }

    private static WheelTrackReconstruction Failed(WheelTrackReconstructionStatus status, string message) =>
        new(status, [], 0.0, message);
}
