using NUnit.Framework;

// No namespace, so NUnit applies it to every test in this assembly.
// Umbraco's own GlobalSetupTeardown only runs for its own assembly, and it is
// what loads appsettings.Tests.json into TestConfiguration.
[SetUpFixture]
public class GlobalSetup
{
    private readonly GlobalSetupTeardown _umbraco = new();

    [OneTimeSetUp]
    public void SetUp() => _umbraco.SetUp();

    [OneTimeTearDown]
    public void TearDown() => _umbraco.TearDown();
}
