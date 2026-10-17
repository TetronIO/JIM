// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Authentication;

/// <summary>
/// Records a failed authentication attempt in the security audit log, off the request path. Every scheme that can
/// reject a credential (API keys, SSO access tokens, interactive sign-in) records through this one service.
/// </summary>
public interface IFailedAuthenticationRecorder
{
    /// <summary>
    /// Starts recording a failed authentication attempt on a background task and returns that task. Callers on the
    /// request path discard it: authentication must never wait on, or be destabilised by, an audit write. The task
    /// never faults.
    /// </summary>
    /// <param name="eventName">The event class, one of <see cref="FailedAuthenticationEventNames"/>.</param>
    /// <param name="reason">A short, sanitised failure reason. Never a raw exception message or credential.</param>
    /// <param name="apiKeyPrefix">The API key's identifying prefix, or null when no key is involved.</param>
    /// <param name="clientIp">The client's IP address, or null when unavailable.</param>
    Task RecordAsync(string eventName, string reason, string? apiKeyPrefix, string? clientIp);
}
