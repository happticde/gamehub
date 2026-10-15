using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using GameHub.Core.Models.Auth;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GameHub.Core.Services;

/// <summary>Limits credential attempts independently of the shared BFF transport address.</summary>
public sealed class AuthAttemptLimiter(TimeProvider? clock = null) : IDisposable
{
    /// <summary>Bounds memory even when anonymous callers submit unique email/token values.</summary>
    public const int MaximumPartitions = 4096;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Started, int Attempts)> _windows = new();
    private long _lastCleanup;
    private readonly ConcurrencyLimiter _concurrency = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 32,
        QueueLimit = 0
    });
    /// <summary>Acquires a process-wide concurrency slot without queuing expensive authentication.</summary>
    public RateLimitLease AcquireConcurrency() => _concurrency.AttemptAcquire();

    /// <summary>Applies the attempt budget to a hash, never to plaintext credentials.</summary>
    public bool TryAcquireAttempt(string partition)
    {
        var now = _clock.GetTimestamp();
        lock (_gate)
        {
            if (_windows.TryGetValue(partition, out var window)
                && _clock.GetElapsedTime(window.Started, now) < TimeSpan.FromMinutes(1))
            {
                if (window.Attempts >= 10) return false;
                _windows[partition] = (window.Started, window.Attempts + 1);
                return true;
            }

            if (_windows.Count >= MaximumPartitions && !_windows.ContainsKey(partition))
            {
                // Reclaim expired buckets at most once per second, never evict a live budget.
                if (_clock.GetElapsedTime(_lastCleanup, now) >= TimeSpan.FromSeconds(1))
                {
                    foreach (var key in _windows.Where(entry =>
                        _clock.GetElapsedTime(entry.Value.Started, now) >= TimeSpan.FromMinutes(1))
                        .Select(entry => entry.Key).ToArray())
                    {
                        _windows.Remove(key);
                    }
                    _lastCleanup = now;
                }
                if (_windows.Count >= MaximumPartitions) return false;
            }
            _windows[partition] = (now, 1);
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _concurrency.Dispose();
    }
}

/// <summary>
/// Runs after bounded model binding and authentication, before credential verification. Request
/// identity here selects only a throttle bucket; it never selects a tenant or authorizes access.
/// </summary>
public sealed class AuthAttemptLimitFilter(AuthAttemptLimiter limiter) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var concurrency = limiter.AcquireConcurrency();
        if (!concurrency.IsAcquired)
        {
            Reject(context);
            return;
        }
        if (!limiter.TryAcquireAttempt(Partition(context)))
        {
            Reject(context);
            return;
        }
        await next();
    }

    private static void Reject(ActionExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Builds bounded-lived, opaque buckets from the actual authentication operation.</summary>
    public static string Partition(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        var account = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenant = user.FindFirstValue(GameHubClaimTypes.TenantId);
        if (user.Identity?.IsAuthenticated == true && account is not null && tenant is not null)
        {
            return Hash("account", tenant + ":" + account);
        }
        foreach (var argument in context.ActionArguments.Values)
        {
            switch (argument)
            {
                case TokenRequest token:
                    return Hash("password", token.Email?.Trim().ToUpperInvariant() ?? "");
                case RefreshTokenRequest refresh:
                    return Hash("refresh", refresh.RefreshToken ?? "");
                case OidcTokenRequest oidc:
                    return Hash("oidc", oidc.IdToken ?? "");
            }
        }
        return "invalid-authentication-request";
    }

    private static string Hash(string operation, string value) =>
        operation + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
