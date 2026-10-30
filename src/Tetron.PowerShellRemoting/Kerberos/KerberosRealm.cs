// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// A Kerberos realm and, optionally, the KDCs to use for it. With no KDCs, they are found through DNS SRV records.
/// </summary>
/// <param name="Name">The realm, for example <c>PANOPLY.LOCAL</c>. Written in upper case.</param>
/// <param name="Kdcs">Host names, optionally with a port (<c>dc1.panoply.local:88</c>). Empty to use DNS.</param>
public sealed record KerberosRealm(string Name, IReadOnlyList<string> Kdcs);
