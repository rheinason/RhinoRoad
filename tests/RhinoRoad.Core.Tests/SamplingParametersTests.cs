using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class SamplingParametersTests
{
    [Fact]
    public void A_break_between_divisions_becomes_its_own_flagged_sample()
    {
        var merged = SamplingParameters.MergeBreaks([0.0, 1.0, 2.0, 3.0], [1.4], 0.01);

        Assert.Equal([0.0, 1.0, 1.4, 2.0, 3.0], merged.Select(item => item.Parameter));
        Assert.Equal([false, false, true, false, false], merged.Select(item => item.IsTangentBreak));
    }

    [Fact]
    public void A_break_on_a_division_flags_it_without_a_duplicate()
    {
        var merged = SamplingParameters.MergeBreaks([0.0, 1.0, 2.0], [1.004], 0.01);

        Assert.Equal([0.0, 1.0, 2.0], merged.Select(item => item.Parameter));
        Assert.Equal([false, true, false], merged.Select(item => item.IsTangentBreak));
    }

    [Fact]
    public void A_break_just_before_a_division_still_collapses_into_one_sample()
    {
        var merged = SamplingParameters.MergeBreaks([0.0, 1.0, 2.0], [0.996], 0.01);

        Assert.Equal(3, merged.Count);
        Assert.True(merged[1].IsTangentBreak);
    }

    [Fact]
    public void No_breaks_leaves_the_divisions_alone_and_unordered_breaks_are_sorted()
    {
        Assert.Equal([0.0, 1.0], SamplingParameters.MergeBreaks([0.0, 1.0], [], 0.01).Select(item => item.Parameter));

        var merged = SamplingParameters.MergeBreaks([0.0, 3.0], [2.0, 1.0], 0.01);
        Assert.Equal([0.0, 1.0, 2.0, 3.0], merged.Select(item => item.Parameter));
    }
}
