namespace DeckCapture.Tests;

// These cases exercise real encoders/probes with production deadlines. Running
// unrelated media workloads concurrently turns optional-probe timing into a host
// load test; concurrency behavior is exercised explicitly inside individual cases.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaIntegrationCollection
{
    public const string Name = "Media integration";
}
