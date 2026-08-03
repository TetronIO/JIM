// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim;

/// <summary>
/// Well-known SCIM 2.0 schema and message URNs (RFC 7643, RFC 7644).
/// Shared by the client connector and JIM's own service provider so both sides name schemas identically.
/// </summary>
public static class ScimUrns
{
    // Core resource schemas (RFC 7643)
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    // Extension schemas (RFC 7643 section 4.3)
    public const string EnterpriseUser = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    // Protocol messages (RFC 7644)
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string SearchRequest = "urn:ietf:params:scim:api:messages:2.0:SearchRequest";
    public const string BulkRequest = "urn:ietf:params:scim:api:messages:2.0:BulkRequest";
    public const string BulkResponse = "urn:ietf:params:scim:api:messages:2.0:BulkResponse";
}
