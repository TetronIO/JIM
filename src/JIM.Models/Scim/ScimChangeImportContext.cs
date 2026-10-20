// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// What a Connected System Object change records about the import that made it: enough to find the SCIM requests
/// behind it in the journal (D17).
/// </summary>
/// <param name="ConnectedSystemId">The Connected System the change was imported into.</param>
/// <param name="ExternalId">The object's external id when the import read it, which for the SCIM 2.0 Service Provider
/// is the resource id; null when the change was not recorded against an execution item.</param>
/// <param name="WatermarkFrom">The watermark the import started from, as the Connector persisted it.</param>
/// <param name="WatermarkTo">The watermark the import reached; null when it recorded none.</param>
public sealed record ScimChangeImportContext(int ConnectedSystemId, string? ExternalId, string? WatermarkFrom, string? WatermarkTo);
