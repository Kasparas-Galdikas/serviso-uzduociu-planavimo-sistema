using Xunit;

namespace Tests.Integration.Fixtures;

/// <summary>
/// xUnit collection to share container and factory across integration tests.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<TestWebApplicationFactoryFixture>
{
    public const string Name = "IntegrationCollection";
}
