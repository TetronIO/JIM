// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;
namespace JIM.Data.Repositories;

/// <summary>
/// Repository surface for system-wide administrative operations that cut across the
/// other repositories (factory reset, future maintenance routines).
/// </summary>
public interface ISystemRepository
{
    /// <summary>
    /// Wipes all customer data and configuration from the database in a single transaction,
    /// preserving the schema, EF Core migration history, and the rows seeded by
    /// <c>SeedingServer</c> (built-in metaverse attributes, built-in Metaverse Object Types,
    /// built-in roles, built-in connector definitions, built-in example data sets and templates,
    /// built-in predefined searches, the singleton service settings record, and infrastructure
    /// API keys).
    /// </summary>
    /// <remarks>
    /// Callers must enforce their own pre-conditions (no sync activity in progress, authorisation).
    /// This method does not check; it just wipes.
    /// </remarks>
    /// <param name="includeAdministrators">
    /// When <c>false</c> (the default behaviour), Metaverse Objects holding the built-in Administrator
    /// role are preserved so the operator is not locked out of the portal. When <c>true</c>, those
    /// administrator identities are removed as well, leaving a true brand-new install.
    /// </param>
    public Task<SystemResetResult> ResetSystemAsync(bool includeAdministrators);
}
