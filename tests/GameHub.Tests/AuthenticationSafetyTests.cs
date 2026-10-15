using GameHub.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace GameHub.Tests;

/// <summary>Protects both services against implicit anonymous production startup.</summary>
public sealed class AuthenticationSafetyTests
{
    [Theory]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Development", false, false)]
    [InlineData("Development", true, true)]
    public void Startup_AnonymousAccessRequiresDevelopmentAndExplicitOptIn(string environment, bool optIn, bool allowed)
    {
        var configuration = Config(new() { [AuthenticationSafety.AnonymousDevelopmentKey] = optIn.ToString() });
        var host = Mock.Of<IHostEnvironment>(x => x.EnvironmentName == environment);
        if (allowed) AuthenticationSafety.Validate(configuration, host);
        else Assert.Throws<InvalidOperationException>(() => AuthenticationSafety.Validate(configuration, host));
    }

    [Theory]
    [InlineData("Authentication:ApiKey")]
    [InlineData("Authentication:Jwt:SigningKey")]
    [InlineData("Authentication:ServiceKey")]
    public void Startup_ConfiguredAuthenticationDoesNotRequireAnonymousOptIn(string key)
    {
        AuthenticationSafety.Validate(Config(new() { [key] = "configured-credential-for-tests-0123456789" }),
            Mock.Of<IHostEnvironment>(x => x.EnvironmentName == "Production"));
    }

    [Fact]
    public void Startup_AnonymousOptInCannotBeCombinedWithFrontendLogin()
    {
        Assert.Throws<InvalidOperationException>(() => AuthenticationSafety.Validate(Config(new()
        {
            [AuthenticationSafety.AnonymousDevelopmentKey] = "true",
            ["Authentication:Frontend:Enabled"] = "true"
        }), Mock.Of<IHostEnvironment>(x => x.EnvironmentName == "Development")));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("a long credential containing spaces is rejected")]
    [InlineData("                                ")]
    public void Startup_WeakProductionOperatorKeyIsRejected(string key)
    {
        Assert.Throws<InvalidOperationException>(() => AuthenticationSafety.Validate(
            Config(new() { ["Authentication:ApiKey"] = key }),
            Mock.Of<IHostEnvironment>(x => x.EnvironmentName == "Production")));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
