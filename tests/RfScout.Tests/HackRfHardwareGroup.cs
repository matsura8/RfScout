namespace RfScout.Tests;

/// <summary>
/// There is one radio. xUnit runs test classes in parallel, so every class that opens the HackRF
/// joins this collection and they run one after another instead of racing for the device
/// (the loser sees HACKRF_ERROR_NOT_FOUND).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HackRfHardwareGroup
{
    public const string Name = "HackRF hardware";
}
