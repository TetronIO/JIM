// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Records failed authentication attempts through <c>SecurityAuditServer</c>, each on a background task with a
/// <see cref="JIM.Application.JimApplication"/> of its own. The application comes from the root-singleton factory
/// rather than the request's service provider, because a failed authentication responds almost at once and the
/// request's scope is disposed while the write is still running.
/// </summary>
public class FailedAuthenticationRecorder : IFailedAuthenticationRecorder
{
    private readonly IJimApplicationFactory _jimApplicationFactory;

    public FailedAuthenticationRecorder(IJimApplicationFactory jimApplicationFactory)
    {
        _jimApplicationFactory = jimApplicationFactory;
    }

    /// <inheritdoc />
    public Task RecordAsync(string eventName, string reason, string? apiKeyPrefix, string? clientIp) =>
        Task.Run(async () =>
        {
            try
            {
                using var jim = _jimApplicationFactory.Create();
                await jim.SecurityAudit.RecordFailedAuthenticationAsync(eventName, reason, apiKeyPrefix, clientIp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SecurityAuditServer swallows its own write failures; this covers creating the application itself.
                Log.Warning(ex, "FailedAuthenticationRecorder: Failed to record a failed authentication event ({EventName})",
                    eventName);
            }
        });
}
