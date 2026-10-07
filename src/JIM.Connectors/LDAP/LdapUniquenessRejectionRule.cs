// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Text.RegularExpressions;

namespace JIM.Connectors.LDAP;

/// <summary>
/// One row of <see cref="LdapUniquenessRejectionClassifier"/>'s table.
/// </summary>
/// <param name="ServerFamily">The directory whose message this row reads.</param>
/// <param name="ResultCode">The LDAP result code the row applies to.</param>
/// <param name="MessagePattern">What the diagnostic message must match; null matches any message.</param>
/// <param name="Attribute">The attribute the rejection is attributed to, from the match and the whole message; null
/// when the server named none.</param>
/// <param name="Description">What the row recognises, for whoever reads the table.</param>
internal sealed record LdapUniquenessRejectionRule(
    string ServerFamily,
    ResultCode ResultCode,
    Regex? MessagePattern,
    Func<Match?, string, string?> Attribute,
    string Description);
