// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// One Exchange command with its bound parameters, as the convergence planner decides it (#2021). Exchange's restricted
/// endpoint accepts nothing but commands and bound parameters, so this is the whole of what can be sent.
/// </summary>
/// <param name="Name">The cmdlet's name.</param>
/// <param name="Parameters">Each parameter's name and value, in order; a null value is a switch.</param>
internal sealed record LdapExchangeCommand(string Name, IReadOnlyList<KeyValuePair<string, object?>> Parameters)
{
    /// <summary>The names of the parameters, in order.</summary>
    internal IEnumerable<string> Names => Parameters.Select(p => p.Key);

    /// <summary>The value bound to <paramref name="name"/>, or null when the parameter is a switch or not bound.</summary>
    internal object? Value(string name) => Parameters.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.Ordinal)).Value;
}
