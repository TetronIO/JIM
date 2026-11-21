// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// What an access token's application permissions allow, as <see cref="GraphPermissions.Check"/> found it: an error
/// when the Connector cannot import at all, a warning naming permissions some features need, and which object types
/// it can read.
/// </summary>
internal sealed record GraphPermissionCheckResult(string? Error, string? Warning, bool CanReadUsers, bool CanReadGroups);
