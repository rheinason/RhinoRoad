namespace RhinoRoad.Core;

/// <summary>
/// Selects the subset of poses to draw as vehicle footprints along a route.
/// </summary>
/// <remarks>
/// The analysis samples the route every few centimetres, which is far denser than anyone wants
/// stamped on a drawing. Footprints are a reading aid — they show a reviewer how the body tracks
/// through a turn — so they are placed at a readable station interval, always including the first
/// and last pose so the manoeuvre reads as starting and ending where it does.
/// </remarks>
public static class PoseSampler
{
    /// <summary>
    /// Just the first and last pose. Enough to show where the vehicle enters and leaves without
    /// burying the drawing in outlines, which is how these are usually presented for review.
    /// </summary>
    public static IReadOnlyList<VehiclePose> EndsOnly(IReadOnlyList<VehiclePose> poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        return poses.Count switch
        {
            0 => [],
            1 => [poses[0]],
            _ => [poses[0], poses[^1]]
        };
    }

    /// <summary>
    /// Poses at approximately <paramref name="intervalMetres"/> of station, plus both ends.
    /// A non-positive interval selects nothing.
    /// </summary>
    public static IReadOnlyList<VehiclePose> AtStationInterval(
        IReadOnlyList<VehiclePose> poses,
        double intervalMetres)
    {
        ArgumentNullException.ThrowIfNull(poses);
        if (intervalMetres <= 0.0 || poses.Count == 0) return [];
        if (poses.Count == 1) return [poses[0]];

        var selected = new List<VehiclePose> { poses[0] };
        var nextStation = poses[0].StationMetres + intervalMetres;
        for (var index = 1; index < poses.Count - 1; index++)
        {
            if (poses[index].StationMetres < nextStation) continue;
            selected.Add(poses[index]);

            // Advance past the pose just taken so a coarse route cannot emit two stamps at once.
            nextStation = poses[index].StationMetres + intervalMetres;
        }

        var last = poses[^1];
        // Drop a final stamp that would sit on top of the previous one.
        if (last.StationMetres - selected[^1].StationMetres < intervalMetres * 0.25 && selected.Count > 1)
        {
            selected[^1] = last;
        }
        else
        {
            selected.Add(last);
        }

        return selected;
    }
}
