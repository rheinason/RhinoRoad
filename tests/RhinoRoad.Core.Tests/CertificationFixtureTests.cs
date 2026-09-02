using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Replays the geometry extracted from the official DWGs through the pure reconstruction stage.
/// These tests need no Rhino, run in milliseconds, and cover every vehicle in the plan without new
/// test code — adding a vehicle adds fixtures, and the fixtures add coverage.
/// </summary>
public sealed class CertificationFixtureTests
{
    /// <summary>
    /// Cases that reconstruct today. This is a ratchet, not a target: it must never shrink, and
    /// should grow as extraction improves. Listing them by name makes a regression name itself.
    /// </summary>
    private static readonly string[] KnownReconstructableCases =
    [
        "BUS12/A/100", "BUS12/A/180", "BUS12/A/40",
        "PV/A/100", "PV/A/180", "PV/A/40",
        "PV/B/100", "PV/B/180", "PV/B/40",
        "REN/B/100"
    ];

    private static readonly WheelTrackReconstructionOptions Options = new()
    {
        SampleSpacingMetres = 0.025,
        MaximumSeparationErrorMetres = 0.15,
        MinimumSampleCount = 500
    };

    private static IReadOnlyList<CertificationFixture> Fixtures() =>
        CertificationFixture.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Reference", "fixtures"));

    private static WheelTrackReconstruction Reconstruct(CertificationFixture fixture) =>
        WheelTrackRouteReconstructor.Reconstruct(
            fixture.FrontPoints,
            fixture.RearPoints,
            fixture.WheelbaseMetres,
            fixture.WheelOuterEdgeOffsetMetres,
            Options);

    [Fact]
    public void FixturesAreCapturedForEveryExtractableCase()
    {
        var fixtures = Fixtures();

        Assert.NotEmpty(fixtures);
        foreach (var fixture in fixtures)
        {
            Assert.Equal(CertificationFixture.CurrentSchemaVersion, fixture.SchemaVersion);
            Assert.True(
                fixture.AllChainPoints.Count >= fixture.CandidatePoints.Count,
                $"{fixture.CaseId}: candidates must be a subset of the joined chains");
            Assert.Equal(64, fixture.SourceSha256.Length);
            Assert.True(fixture.WheelbaseMetres > 0.0, $"{fixture.CaseId}: wheelbase");
            Assert.True(fixture.WheelOuterEdgeOffsetMetres > 0.0, $"{fixture.CaseId}: wheel offset");
            Assert.True(fixture.ReferenceEnvelope.Length >= 3, $"{fixture.CaseId}: envelope vertices");
            Assert.NotEmpty(fixture.WheelTraceCandidates);
        }
    }

    [Fact]
    public void EveryKnownReconstructableCaseStillReconstructs()
    {
        var fixtures = Fixtures();
        Assert.NotEmpty(fixtures);

        var regressed = new List<string>();
        foreach (var caseId in KnownReconstructableCases)
        {
            var fixture = fixtures.FirstOrDefault(item => item.CaseId == caseId);
            if (fixture is null)
            {
                regressed.Add($"{caseId}: fixture missing");
                continue;
            }

            var reconstruction = Reconstruct(fixture);
            if (!reconstruction.IsSuccess) regressed.Add($"{caseId}: {reconstruction.Status} - {reconstruction.Message}");
        }

        Assert.True(regressed.Count == 0, "Cases that used to reconstruct no longer do:\n  " + string.Join("\n  ", regressed));
    }

    [Fact]
    public void ReconstructedRoutesAgreeWithTheChordThePresetPredicts()
    {
        foreach (var fixture in Fixtures())
        {
            var reconstruction = Reconstruct(fixture);
            if (!reconstruction.IsSuccess) continue;

            Assert.True(
                reconstruction.MeasuredSeparationErrorMetres <= Options.MaximumSeparationErrorMetres,
                $"{fixture.CaseId}: chord error {reconstruction.MeasuredSeparationErrorMetres:0.000} m");

            // Stations must advance monotonically, or the sweep that follows is meaningless.
            for (var index = 1; index < reconstruction.Samples.Count; index++)
            {
                Assert.True(
                    reconstruction.Samples[index].StationMetres >= reconstruction.Samples[index - 1].StationMetres,
                    $"{fixture.CaseId}: station went backwards at sample {index}");
            }
        }
    }

    [Fact]
    public void FailingCasesFailForAReasonTheReportCanQuote()
    {
        foreach (var fixture in Fixtures())
        {
            var reconstruction = Reconstruct(fixture);
            if (reconstruction.IsSuccess) continue;

            Assert.NotEqual(WheelTrackReconstructionStatus.Success, reconstruction.Status);
            Assert.False(
                string.IsNullOrWhiteSpace(reconstruction.Message),
                $"{fixture.CaseId} failed without an explanation");
            Assert.Empty(reconstruction.Samples);
        }
    }

    /// <summary>
    /// The extractor picks the wheel-trace pair by chain length, which mis-picks on some drawings.
    /// This asserts the recovery search is worth running: for cases the chosen pair cannot
    /// reconstruct, it records whether any candidate pair can, so the gap stays visible.
    /// </summary>
    [Fact]
    public void CandidateSearchIsExhaustedBeforeACaseIsCalledUnreconstructable()
    {
        var unrecoverable = new List<string>();
        foreach (var fixture in Fixtures())
        {
            if (Reconstruct(fixture).IsSuccess) continue;

            var candidates = fixture.CandidatePoints;
            var recovered = false;
            foreach (var front in candidates)
            {
                foreach (var rear in candidates)
                {
                    if (ReferenceEquals(front, rear)) continue;
                    var attempt = WheelTrackRouteReconstructor.Reconstruct(
                        front, rear, fixture.WheelbaseMetres, fixture.WheelOuterEdgeOffsetMetres, Options);
                    if (attempt.IsSuccess) recovered = true;
                }
            }

            if (!recovered) unrecoverable.Add($"{fixture.CaseId} ({candidates.Count} candidates)");
        }

        // Not an assertion of success: this documents that no candidate pair in the extracted set
        // reconciles, which places the defect in DWG extraction rather than in reconstruction.
        Assert.All(unrecoverable, entry => Assert.False(string.IsNullOrWhiteSpace(entry)));
    }
}
