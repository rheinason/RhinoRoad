namespace RhinoRoad.Core;

/// <summary>A curve parameter to sample at, and whether it is where the curve's tangent breaks.</summary>
public readonly record struct SamplingParameter(double Parameter, bool IsTangentBreak);

public static class SamplingParameters
{
    /// <summary>
    /// Combines an even division of a curve with the parameters of its tangent breaks, in order.
    /// </summary>
    /// <remarks>
    /// Spotting a break from the change of heading between neighbouring samples misses a kink gentler
    /// than the threshold, and puts a curvature spike on whichever sample straddles it. Sampling at the
    /// break itself marks it exactly. A division within <paramref name="toleranceParameter"/> of a break
    /// is dropped in its favour, so no sliver of a sample is left either side of it.
    /// </remarks>
    public static IReadOnlyList<SamplingParameter> MergeBreaks(
        IReadOnlyList<double> divisions,
        IReadOnlyList<double> breaks,
        double toleranceParameter)
    {
        ArgumentNullException.ThrowIfNull(divisions);
        ArgumentNullException.ThrowIfNull(breaks);
        var orderedBreaks = breaks.Order().ToList();
        var result = new List<SamplingParameter>(divisions.Count + orderedBreaks.Count);
        var nextBreak = 0;

        void EmitBreaksBefore(double limit)
        {
            while (nextBreak < orderedBreaks.Count && orderedBreaks[nextBreak] < limit)
            {
                var value = orderedBreaks[nextBreak++];
                if (result.Count > 0 && value - result[^1].Parameter <= toleranceParameter)
                {
                    // Too close to the sample before it to be worth a sample of its own: that one is
                    // the break.
                    result[^1] = new SamplingParameter(result[^1].Parameter, true);
                }
                else
                {
                    result.Add(new SamplingParameter(value, true));
                }
            }
        }

        foreach (var division in divisions)
        {
            EmitBreaksBefore(division - toleranceParameter);
            // A break within tolerance of this division marks the division rather than adding to it.
            var isBreak = false;
            while (nextBreak < orderedBreaks.Count && orderedBreaks[nextBreak] <= division + toleranceParameter)
            {
                isBreak = true;
                nextBreak++;
            }

            if (result.Count > 0 && division - result[^1].Parameter <= toleranceParameter)
            {
                if (isBreak) result[^1] = new SamplingParameter(result[^1].Parameter, true);
            }
            else
            {
                result.Add(new SamplingParameter(division, isBreak));
            }
        }

        EmitBreaksBefore(double.PositiveInfinity);
        return result;
    }
}
