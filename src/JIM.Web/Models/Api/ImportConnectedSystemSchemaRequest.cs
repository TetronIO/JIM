// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Optional body for the schema import endpoint. Omitting the body (or every field) imports exactly as before.
/// </summary>
public class ImportConnectedSystemSchemaRequest
{
    /// <summary>
    /// When true, the refresh is applied with its dependents disabled (#1485): Synchronisation Rules bound to a
    /// removed Object Type and Attribute Flow mappings reading a removed or redefined attribute (directly or as
    /// an Expression input) are disabled with a recorded reason, so nothing runs against entries the Connected
    /// System no longer reports. Preview the plan first via the import-schema/preview endpoint, whose response
    /// names the dependents this option would disable. Mutually exclusive with <see cref="RemoveDependents"/>.
    /// </summary>
    public bool DisableDependents { get; set; }

    /// <summary>
    /// When true, the refresh is applied with its dependents removed (#1485): the invalidated Synchronisation
    /// Rules and Attribute Flow mappings are deleted, and the dependent data is removed by a queued worker task
    /// (Connected System Objects of removed Object Types are marked Obsolete and flow through the standard
    /// deprovisioning pipeline; stored values of removed attributes are deleted). This deletes configuration
    /// and identity data; preview first via the import-schema/preview endpoint, whose response names the
    /// dependents and counts the data impact. Mutually exclusive with <see cref="DisableDependents"/>.
    /// </summary>
    public bool RemoveDependents { get; set; }
}
