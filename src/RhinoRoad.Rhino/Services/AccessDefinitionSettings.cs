using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Maps a saved road onto the settings the Road dialog edits, and the dialog's answers back onto
/// the road. Shared by <c>RRRoad</c>'s configure path and the Edit Road palette, which changes the
/// settings of its unsaved draft without running the command — running it committed the draft.
/// </summary>
internal static class AccessDefinitionSettings
{
    public static void Apply(VehicleAccessDefinition definition, VehicleAccessSettings settings)
    {
        settings.Source = definition.SourceKind;
        settings.VehicleId = definition.VehicleId;
        settings.ModeId = definition.ModeId;
        settings.Direction = definition.SourceKind == PathSourceKind.Interactive
            ? definition.Manoeuvre?.StartDirection ?? definition.ExistingCurveDirection
            : definition.ExistingCurveDirection;
        settings.EdgeMethod = definition.RoadEdgeMethod;
        settings.ClearanceMetres = definition.ClearanceMetres;
        settings.LeftWidthMetres = definition.LeftWidthMetres;
        settings.RightWidthMetres = definition.RightWidthMetres;
        settings.CheckMaximumGrade = definition.CheckMaximumGrade;
        settings.MaximumGradePercent = definition.MaximumGradePercent;
        settings.CheckObstacles = definition.CheckObstacles;
        settings.CheckAllowedArea = definition.CheckAllowedArea;
        settings.DrapeTerrain = definition.TerrainObjectId.HasValue;
        settings.Footprints = definition.FootprintMode;
        settings.FootprintIntervalMetres = definition.FootprintIntervalMetres;
        settings.PreviewBeforeBaking = definition.PreviewBeforeBaking;
        settings.ReplaceExisting = definition.ReplaceExisting;
        settings.BakeWheelEdgePaths = definition.BakeWheelEdgePaths;
        settings.IncludeMirrorAllowance = definition.IncludeMirrorAllowance;
    }

    public static VehicleAccessDefinition WithSettings(
        VehicleAccessDefinition definition,
        VehicleAccessSettings settings)
    {
        var manoeuvre = definition.Manoeuvre is null
            ? null
            : ManoeuvreControlReconciler.WithStartDirection(definition.Manoeuvre, settings.Direction);
        return definition with
        {
            VehicleId = settings.VehicleId,
            ModeId = settings.ModeId,
            ExistingCurveDirection = settings.Direction,
            Manoeuvre = manoeuvre,
            ClearanceMetres = settings.ClearanceMetres,
            RoadEdgeMethod = settings.EdgeMethod,
            LeftWidthMetres = settings.LeftWidthMetres,
            RightWidthMetres = settings.RightWidthMetres,
            CheckMaximumGrade = settings.CheckMaximumGrade,
            MaximumGradePercent = settings.MaximumGradePercent,
            CheckObstacles = settings.CheckObstacles,
            CheckAllowedArea = settings.CheckAllowedArea,
            ObstacleObjectIds = settings.CheckObstacles ? definition.ObstacleObjectIds : Array.Empty<Guid>(),
            AllowedBoundaryObjectIds = settings.CheckAllowedArea ? definition.AllowedBoundaryObjectIds : Array.Empty<Guid>(),
            TerrainObjectId = settings.DrapeTerrain ? definition.TerrainObjectId : null,
            FootprintMode = settings.Footprints,
            FootprintIntervalMetres = settings.FootprintIntervalMetres,
            PreviewBeforeBaking = settings.PreviewBeforeBaking,
            ReplaceExisting = settings.ReplaceExisting,
            BakeWheelEdgePaths = settings.BakeWheelEdgePaths,
            IncludeMirrorAllowance = settings.IncludeMirrorAllowance
        };
    }
}
