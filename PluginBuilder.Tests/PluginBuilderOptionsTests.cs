using Microsoft.Extensions.Configuration;
using PluginBuilder.Configuration;
using PluginBuilder.Util.Extensions;
using Xunit;

namespace PluginBuilder.Tests;

public class PluginBuilderOptionsTests
{
    [Fact]
    public void BuildTimeoutDefaultsToFifteenMinutes()
    {
        var options = Configure();

        Assert.Equal(TimeSpan.FromMinutes(15), options.BuildTimeout);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("86400", 86400)]
    public void BuildTimeoutCanBeConfigured(string value, int expectedSeconds)
    {
        var options = Configure(value);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.BuildTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("86401")]
    public void InvalidBuildTimeoutIsRejected(string value)
    {
        var exception = Assert.Throws<ConfigurationException>(() => Configure(value));

        Assert.Equal("BUILD_TIMEOUT_SECONDS", exception.Key);
    }

    private static PluginBuilderOptions Configure(string? buildTimeoutSeconds = null)
    {
        var values = buildTimeoutSeconds is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["BUILD_TIMEOUT_SECONDS"] = buildTimeoutSeconds };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return PluginBuilderOptions.ConfigureDataDirAndDebugLog(configuration, null!);
    }
}
