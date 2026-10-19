// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Security;
using JIM.Utilities;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Checks JIM's identity provider shortly after start-up and every <see cref="Interval"/>, and on demand, and holds the
/// result for the sign-in unavailable page, the administrators' banner and the API (#2363).
/// </summary>
/// <remarks>
/// <para>
/// The first check after start-up is what puts an untrusted identity provider in the container log, naming the
/// certificate authority to trust; checking periodically is what lets administrators who are already signed in hear
/// that new sign-ins will fail before anyone else finds out.
/// </para>
/// <para>
/// On-demand checks are floored at <see cref="CheckFloor"/> and shared between callers, so the anonymous status
/// endpoint cannot be used to make JIM connect to the identity provider any faster than that.
/// </para>
/// </remarks>
public sealed class IdentityProviderTrustMonitor : BackgroundService, IIdentityProviderTrustMonitor
{
    /// <summary>
    /// How often the identity provider is checked in the background.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The least time between on-demand checks.
    /// </summary>
    public static readonly TimeSpan CheckFloor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The page every message about an untrusted identity provider points to.
    /// </summary>
    public static readonly string DocsUrl = DocsSite.PageUrl("administration/sso-setup/trusting-your-identity-provider.md");

    private readonly Func<CancellationToken, Task<IdentityProviderTrustStatus>> _check;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IdentityProviderTrustStatus _current = new() { State = IdentityProviderTrustState.NotChecked };
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Checks through the application layer.
    /// </summary>
    public IdentityProviderTrustMonitor(IJimApplicationFactory applicationFactory)
        : this(async cancellationToken =>
        {
            using var jim = applicationFactory.Create();
            return await jim.IdentityProviderTrust.CheckAsync(cancellationToken);
        }, TimeProvider.System)
    {
    }

    /// <param name="check">Checks the identity provider.</param>
    /// <param name="time">The clock the floor is measured on.</param>
    public IdentityProviderTrustMonitor(Func<CancellationToken, Task<IdentityProviderTrustStatus>> check, TimeProvider time)
    {
        _check = check;
        _time = time;
    }

    public IdentityProviderTrustStatus Current => _current;

    public Task<IdentityProviderTrustStatus> GetFreshStatusAsync(CancellationToken cancellationToken) => CheckAsync(force: false, cancellationToken);

    public Task<IdentityProviderTrustStatus> RecheckAsync(CancellationToken cancellationToken) => CheckAsync(force: true, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            await CheckAsync(force: true, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<IdentityProviderTrustStatus> CheckAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && IsFresh())
            return _current;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have checked while this one waited.
            if (!force && IsFresh())
                return _current;

            var previous = _current;
            try
            {
                _current = await _check(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The last status stands; the next check tries again.
                Log.Warning(ex, "Could not check the identity provider's certificate");
            }

            _checkedAt = _time.GetUtcNow();
            if (_current.State != previous.State)
                LogStateChange(_current);

            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh() => _time.GetUtcNow() - _checkedAt < CheckFloor;

    private static void LogStateChange(IdentityProviderTrustStatus status)
    {
        switch (status.State)
        {
            case IdentityProviderTrustState.Untrusted:
            case IdentityProviderTrustState.NameMismatch:
                Log.Warning("Sign-in is unavailable: {Message} JIM is running, and API keys still work. See {DocsUrl}",
                    LogSanitiser.Sanitise(status.Message), DocsUrl);
                break;
            case IdentityProviderTrustState.Unreachable:
                Log.Warning("Sign-in is unavailable: {Message} See {DocsUrl}", LogSanitiser.Sanitise(status.Message), DocsUrl);
                break;
            case IdentityProviderTrustState.Trusted:
            case IdentityProviderTrustState.NotHttps:
                Log.Information("Identity provider check: {Message}", LogSanitiser.Sanitise(status.Message));
                break;
        }
    }
}
