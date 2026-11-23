// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;

namespace JIM.Connectors.Http;

/// <summary>
/// Which refused token requests mean the authorisation server has not yet applied a credential added moments ago, and
/// how long to wait before each fresh request. An authorisation server that replicates credentials can refuse one on
/// some of its servers for minutes after another has accepted it; asking again soon after usually reaches one that has.
/// </summary>
/// <param name="IsStillBeingApplied">
/// Whether a refusal (its status and body) is of that kind. Keep it narrow: a refusal that also means a wrong setting
/// is retried into a longer wait for the same answer.
/// </param>
/// <param name="Delays">How long to wait before each further request; one request more is made than there are delays.</param>
public sealed record OAuthStillBeingAppliedRetry(Func<HttpStatusCode, string?, bool> IsStillBeingApplied, IReadOnlyList<TimeSpan> Delays);
