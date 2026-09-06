namespace RhinoRoad.Core;

public static class MovementFit
{
    public static string Describe(bool envelopeAvailable, bool movementFeasible, bool siteChecked) =>
        !envelopeAvailable ? "Sweep unavailable — site fit not checked" :
        !movementFeasible ? "This movement conflicts — inspect problem areas" :
        siteChecked ? "This movement fits the selected constraints" :
        "Sweep generated — site fit not checked";
}
