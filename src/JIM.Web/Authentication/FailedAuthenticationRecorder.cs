// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Records failed authentication attempts through <c>SecurityAuditServer</c>, each on a background task with a
/// <see cref="JimApplication"/> from a DI scope of its own. A scope, and not <c>IJimApplicationFactory</c>, because
/// the factory resolves from the root provider, which keeps every disposable transient it hands out until the process
/// exits (#2115); a failed authentication is attacker-reachable, so that would be an unauthenticated memory leak. The
/// scope comes from the root-singleton <see cref="IServiceScopeFactory"/> rather than the request's own provider,
/// because a failed authentication responds almost at once and the request's scope is disposed while the write is
/// still running.
/// </summary>
public class FailedAuthenticationRecorder : IFailedAuthenticationRecorder
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public FailedAuthenticationRecorder(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public Task RecordAsync(string eventName, string reason, string? apiKeyPrefix, string? clientIp) =>
        Task.Run(async () =>
        {
            try
            {
                await using var scope = _serviceScopeFactory.CreateAsyncScope();
                var jim = scope.ServiceProvider.GetRequiredService<JimApplication>();
                await jim.SecurityAudit.RecordFailedAuthenticationAsync(eventName, reason, apiKeyPrefix, clientIp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SecurityAuditServer swallows its own write failures; this covers resolving the application itself.
                Log.Warning(ex, "FailedAuthenticationRecorder: Failed to record a failed authentication event ({EventName})",
                    eventName);
            }
        });
}
