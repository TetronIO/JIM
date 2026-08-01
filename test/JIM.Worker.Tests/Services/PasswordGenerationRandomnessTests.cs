// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using System.Reflection;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Proves that nothing on the password generation path draws on a predictable source of randomness.
/// <para>
/// This cannot be asserted from the outside. <see cref="Random"/> produces output that looks perfectly random to
/// any statistical test a unit test could run, and is still guessable by anyone who can estimate when the
/// generator ran. So the check is made against the compiled code instead: every method of every password
/// generation type is disassembled far enough to see which members it refers to.
/// </para>
/// </summary>
[TestFixture]
public class PasswordGenerationRandomnessTests
{
    [Test]
    public void PasswordGeneration_ReferencesNoPredictableRandomSource()
    {
        var offenders = PasswordGenerationMethods()
            .Where(m => ReferencedMembers(m).Any(IsPredictableRandomSource))
            .Select(Describe)
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"These use System.Random, which is predictable and must never generate a credential: {string.Join(", ", offenders)}");
    }

    [Test]
    public void PasswordGeneration_ReferencesTheCryptographicRandomSource()
    {
        // Without this, the test above would pass just as happily against a scan that had stopped working, or
        // against a generator that had been renamed out of the set being examined. It is the control.
        var users = PasswordGenerationMethods()
            .Where(m => ReferencedMembers(m).Any(IsCryptographicRandomSource))
            .ToList();

        Assert.That(users, Is.Not.Empty,
            "No password generation code was found calling RandomNumberGenerator, which means this check is not looking where it thinks it is.");
    }

    /// <summary>
    /// Every method, constructor and compiler-generated helper belonging to a password generation type.
    /// </summary>
    private static List<MethodBase> PasswordGenerationMethods()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        var types = typeof(PasswordGeneratorService).Assembly
            .GetTypes()
            .Where(t => (t.FullName ?? string.Empty).Contains("Password", StringComparison.Ordinal))
            .ToList();

        Assert.That(types, Is.Not.Empty, "No password generation types were found to examine.");

        return types
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
            .ToList();
    }

    private static bool IsPredictableRandomSource(MemberInfo member) =>
        member.DeclaringType == typeof(Random) || member as Type == typeof(Random);

    private static bool IsCryptographicRandomSource(MemberInfo member) =>
        member.DeclaringType == typeof(System.Security.Cryptography.RandomNumberGenerator) ||
        member as Type == typeof(System.Security.Cryptography.RandomNumberGenerator);

    /// <summary>
    /// Every member a method's body refers to.
    /// <para>
    /// Metadata tokens are read out of the instruction stream at every offset rather than by decoding the
    /// opcodes, which is far shorter than a real disassembler and, for a question of the form "is this member
    /// never referred to", safe: a token that does not correspond to a real row simply fails to resolve.
    /// </para>
    /// </summary>
    private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            yield break;

        var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        for (var offset = 0; offset + sizeof(int) <= il.Length; offset++)
        {
            var token = BitConverter.ToInt32(il, offset);
            MemberInfo? member;
            try
            {
                member = method.Module.ResolveMember(token, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                continue; // Not a token, or not one this module knows: the overwhelmingly common case.
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            if (member != null)
                yield return member;
        }
    }

    private static string Describe(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
}
