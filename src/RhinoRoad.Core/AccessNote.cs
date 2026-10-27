using System.Globalization;

namespace RhinoRoad.Core;

/// <summary>
/// The words for how far a preset can be trusted, shared by the dialog and the baked note.
/// </summary>
public static class ValidationWording
{
    /// <summary>A phrase that completes "Preset X, ...".</summary>
    public static string Describe(ValidationStatus status) => status switch
    {
        ValidationStatus.ReferenceValidated => "validated against the official curves",
        ValidationStatus.ValidationFailed => "FAILED validation — do not rely on this preset",
        ValidationStatus.UserDefined => "user-defined — not from the Vejregler source, not validated",
        _ => "source-transcribed, not yet validated"
    };

    /// <summary>The shorter form used on a drawing sheet.</summary>
    public static string Short(ValidationStatus status) => status switch
    {
        ValidationStatus.ReferenceValidated => "validated against the official curves",
        ValidationStatus.ValidationFailed => "failed validation, do not rely on it",
        ValidationStatus.UserDefined => "user-defined, not validated",
        _ => "source-transcribed, not validated"
    };

    /// <summary>The line a reader must not skip when a preset is not reference-validated; null when it is.</summary>
    public static string? Warning(ValidationStatus status) => status switch
    {
        ValidationStatus.ReferenceValidated => null,
        ValidationStatus.ValidationFailed => "PRESET FAILED VALIDATION — DO NOT RELY ON THIS RESULT",
        ValidationStatus.UserDefined => "USER-DEFINED VEHICLE — NOT VALIDATED, SCREENING ONLY",
        _ => "PRESET NOT REFERENCE-VALIDATED — SCREENING ONLY"
    };
}

/// <summary>
/// The text and placement of the note baked beside a run's envelope.
/// </summary>
/// <remarks>
/// <para>
/// The note's height is a fixed 0.25 m of drawing, converted to model units, rather than the
/// document's dimension-style height: that height is paper-sized and depends on a scale the
/// document may never have set, whereas 0.25 m reads at the scale a swept path is normally viewed
/// and comes out the same size in every model.
/// </para>
/// <para>
/// It sits below the lower-left corner of the clearance envelope's bounding box, so it never
/// covers the thing it describes and two roads, whose envelopes differ, do not stack their notes
/// on one spot.
/// </para>
/// </remarks>
public static class AccessNote
{
    public const double TextHeightMetres = 0.25;

    /// <summary>Space between the envelope's lower edge and the top of the first line.</summary>
    public const double GapMetres = 0.5;

    /// <summary>The version as a reader would say it: "preset VD-GUT-2023.09", without a stray "v" prefix.</summary>
    public static string VersionText(string version)
    {
        var trimmed = (version ?? string.Empty).Trim();
        if (trimmed.Length > 1 && trimmed[0] == 'v') trimmed = trimmed[1..];
        return $"preset {trimmed}";
    }

    public static IReadOnlyList<string> Lines(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        double clearanceMetres,
        bool mirrorAllowance,
        DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        var lines = new List<string>();
        if (ValidationWording.Warning(vehicle.ValidationStatus) is { } warning) lines.Add(warning);
        lines.Add($"Vehicle: {vehicle.Name} ({vehicle.Id}, {VersionText(vehicle.Version)})");
        lines.Add($"Mode: {mode.Name}");
        lines.Add($"Clearance: {clearanceMetres.ToString("0.00", CultureInfo.CurrentCulture)} m");
        if (mirrorAllowance) lines.Add(MirrorAllowance.Basis);
        lines.Add($"Validation: {ValidationWording.Short(vehicle.ValidationStatus)}");
        lines.Add($"Date: {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return lines;
    }

    /// <summary>
    /// Where the note's top-left corner goes, in model units: below the lower-left corner of the
    /// envelope's bounding box.
    /// </summary>
    /// <param name="boundsMinimum">The bounding box's minimum corner, in model units.</param>
    /// <param name="modelUnitsPerMetre">The scale of the document.</param>
    public static Point2 Anchor(Point2 boundsMinimum, double modelUnitsPerMetre) =>
        new(boundsMinimum.X, boundsMinimum.Y - (GapMetres * modelUnitsPerMetre));
}
