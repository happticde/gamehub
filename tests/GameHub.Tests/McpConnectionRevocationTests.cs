using System.Net;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed partial class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionRevocation_FailedCommit_RollsBackFamilyGrantAndProtocolState(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        host.FailTokenCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RevokeAsync("connection"));
        host.FailTokenCommit = false;
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(selected));
        await host.WithDbAsync(async db => Assert.Null((await db.McpGrants.SingleAsync()).RevokedAtUtc));
        Assert.True((await host.RefreshAsync(selected)).TryGetProperty("access_token", out _));
    }
}
