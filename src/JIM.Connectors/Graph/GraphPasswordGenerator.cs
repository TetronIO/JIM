// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;

namespace JIM.Connectors.Graph;

/// <summary>
/// The password a new user is created with. Graph will not create a user without one, and nobody is meant to know it:
/// it is sent once, never stored, logged or returned, and JIM's Initial Password replaces it moments later where the
/// Synchronisation Rule asks for one.
/// </summary>
internal static class GraphPasswordGenerator
{
    /// <summary>Well inside Entra ID's limit of 256, and far beyond its minimum of 8.</summary>
    public const int Length = 32;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";

    // Symbols Entra ID accepts that need no escaping anywhere the password could travel.
    private const string Symbols = "!#$%*+-=?@^_";

    private static readonly string All = Upper + Lower + Digits + Symbols;

    /// <summary>
    /// A password with every character category Entra ID counts, so its three-of-four rule always holds, from a
    /// cryptographic random source.
    /// </summary>
    public static string Generate()
    {
        var characters = new char[Length];
        characters[0] = Pick(Upper);
        characters[1] = Pick(Lower);
        characters[2] = Pick(Digits);
        characters[3] = Pick(Symbols);
        for (var i = 4; i < Length; i++)
            characters[i] = Pick(All);

        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new string(characters);
    }

    private static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
