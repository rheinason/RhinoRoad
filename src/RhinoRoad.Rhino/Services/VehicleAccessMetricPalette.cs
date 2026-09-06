using System.Drawing;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>One metric-aware risk scale shared by the profile and viewport route.</summary>
internal static class VehicleAccessMetricPalette
{
    private static readonly Color Safe = Color.FromArgb(61, 151, 157);
    private static readonly Color Watch = Color.FromArgb(219, 174, 72);
    private static readonly Color NearLimit = Color.FromArgb(230, 124, 61);
    private static readonly Color Fail = Color.FromArgb(226, 81, 66);
    private static readonly Color Unavailable = Color.FromArgb(126, 130, 137);

    public static Color For(ReviewMetricKind metric, double value, VehicleAccessReview review)
    {
        if (!double.IsFinite(value)) return Unavailable;
        if (metric == ReviewMetricKind.ClearanceOrRoadMargin)
        {
            if (value < 0.0) return Fail;
            // One metre of positive margin is already comfortably safe for this screening view;
            // a fixed physical scale also keeps rendering O(n) rather than rescanning the series
            // for every coloured route segment.
            const double safeScale = 1.0;
            return Risk(0.9 * (1.0 - Math.Clamp(value / safeScale, 0.0, 1.0)));
        }

        var limit = metric switch
        {
            ReviewMetricKind.SteeringAngle => Limit(review, ReviewCheckKind.SteeringAngle),
            ReviewMetricKind.SteeringRate => Limit(review, ReviewCheckKind.SteeringRate),
            ReviewMetricKind.Grade => Limit(review, ReviewCheckKind.Grade),
            _ => null
        };
        if (limit is > 1e-9) return Risk(Math.Abs(value) / limit.Value);

        // An inactive metric still needs contrast, but must not imply an unconfigured failure.
        var observed = metric switch
        {
            ReviewMetricKind.SteeringAngle => review.MaximumSteeringAngleRadians * 180.0 / Math.PI,
            ReviewMetricKind.SteeringRate => review.MaximumSteeringRateRadiansPerSecond * 180.0 / Math.PI,
            ReviewMetricKind.Grade => review.MaximumAbsoluteGrade * 100.0,
            _ => 1.0
        };
        return Risk(0.65 * Math.Abs(value) / Math.Max(observed, 1e-9));
    }

    public static double Value(ReviewMetricKind metric, VehicleAccessMetricSample sample) => metric switch
    {
        ReviewMetricKind.SteeringAngle => sample.SteeringAngleRadians * 180.0 / Math.PI,
        ReviewMetricKind.SteeringRate => sample.SteeringRateRadiansPerSecond * 180.0 / Math.PI,
        ReviewMetricKind.ClearanceOrRoadMargin => sample.ClearanceOrRoadMarginMetres ?? double.NaN,
        ReviewMetricKind.Grade => sample.Grade * 100.0,
        _ => double.NaN
    };

    private static double? Limit(VehicleAccessReview review, ReviewCheckKind kind)
    {
        var check = review.Checks.FirstOrDefault(item => item.Kind == kind);
        return check?.State is ReviewCheckState.Off or ReviewCheckState.Unavailable ? null : check?.Limit;
    }

    private static Color Risk(double risk) => risk switch
    {
        >= 1.0 => Fail,
        >= 0.82 => NearLimit,
        >= 0.55 => Watch,
        _ => Safe
    };
}
