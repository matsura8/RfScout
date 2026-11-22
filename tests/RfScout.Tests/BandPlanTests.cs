using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// The band plan is data the whole app trusts for its "band" column and (from M4) its decoder
/// choice, so the shipped file must load and every range must be sane. These tests read the real
/// file copied to the test output, not a fixture, so a bad edit fails CI rather than the user's UI.
/// </summary>
public sealed class BandPlanTests
{
    [Fact]
    public void ShippedPlanLoadsAndHasAUsDefaultRegion()
    {
        BandPlan plan = BandPlan.LoadShipped();

        Assert.Equal("US", plan.DefaultRegion);
        Assert.Contains("US", plan.Regions);
        Assert.Contains("EU", plan.Regions);
        Assert.NotEmpty(plan.Allocations);
    }

    [Fact]
    public void EveryAllocationHasAValidOrderedInRange()
    {
        BandPlan plan = BandPlan.LoadShipped();
        foreach (string region in plan.Regions)
        {
            foreach (BandAllocation allocation in plan.AllocationsFor(region))
            {
                Assert.False(string.IsNullOrWhiteSpace(allocation.Name));
                Assert.True(allocation.StartHz > 0, $"{region}/{allocation.Name}: start must be positive");
                Assert.True(allocation.StopHz >= allocation.StartHz, $"{region}/{allocation.Name}: stop below start");
                Assert.NotNull(allocation.Modulations);
                Assert.NotNull(allocation.Decoders);
            }
        }
    }

    [Fact]
    public void KnownFrequenciesLandInTheRightBands()
    {
        BandPlan plan = BandPlan.LoadShipped();

        Assert.Equal("FM broadcast", plan.BestMatch(98_700_000)?.Name);
        Assert.Equal("ADS-B / Mode S", plan.BestMatch(1_090_000_000)?.Name);
        Assert.Equal("ISM 315 MHz", plan.BestMatch(315_000_000)?.Name);
        Assert.Equal("GNSS GPS L1", plan.BestMatch(1_575_420_000)?.Name);
        Assert.Equal("Satellite/comms 138-156 MHz", plan.BestMatch(150_000_000)?.Name);
    }

    [Fact]
    public void NarrowestAllocationWinsWhereBandsOverlap()
    {
        BandPlan plan = BandPlan.Parse("""
        {
          "version": 1,
          "defaultRegion": "US",
          "regions": {
            "US": [
              { "name": "wide", "startHz": 400000000, "stopHz": 500000000, "modulations": [], "decoders": [] },
              { "name": "narrow", "startHz": 433000000, "stopHz": 435000000, "modulations": ["OOK"], "decoders": ["rtl_433"] }
            ]
          }
        }
        """);

        Assert.Equal("narrow", plan.BestMatch(433_920_000)?.Name);
        IReadOnlyList<BandAllocation> all = plan.AllocationsAt(433_920_000);
        Assert.Equal(2, all.Count);
        Assert.Equal("narrow", all[0].Name);
    }

    [Fact]
    public void BadFilesThrowInsteadOfHalfLoading()
    {
        _ = Assert.Throws<InvalidDataException>(() => BandPlan.Parse("{ not json"));
        _ = Assert.Throws<InvalidDataException>(() => BandPlan.Parse("""
            { "version": 1, "defaultRegion": "US", "regions": { "US": [
              { "name": "backwards", "startHz": 200000000, "stopHz": 100000000, "modulations": [], "decoders": [] }
            ] } }
            """));
        _ = Assert.Throws<InvalidDataException>(() => BandPlan.Parse("""
            { "version": 1, "defaultRegion": "EU", "regions": { "US": [
              { "name": "ok", "startHz": 1000000, "stopHz": 2000000, "modulations": [], "decoders": [] }
            ] } }
            """));
        _ = Assert.Throws<InvalidDataException>(() => BandPlan.Parse("""
            { "version": 1, "defaultRegion": "US", "regions": { "US": [
              { "name": "", "startHz": 1000000, "stopHz": 2000000, "modulations": [], "decoders": [] }
            ] } }
            """));
    }

    [Fact]
    public void UnknownRegionFallsBackToTheDefault()
    {
        BandPlan plan = BandPlan.LoadShipped();
        Assert.Equal(plan.Allocations.Count, plan.AllocationsFor("MARS").Count);
    }
}
