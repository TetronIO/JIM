// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Reflection;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Builds System.DirectoryServices.Protocols response objects for tests.
/// <para>
/// Every response type in that library has internal constructors and no public way to create one, and
/// SearchResponse's entry collection is populated through an internal setter. Reflection is the only route, so it
/// lives here once rather than being re-derived in every LDAP test fixture.
/// </para>
/// </summary>
internal static class LdapTestResponses
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Creates a DirectoryResponse of the given type carrying a result code and optional diagnostic message.
    /// </summary>
    internal static T Create<T>(ResultCode resultCode, string? errorMessage = null) where T : DirectoryResponse =>
        (T)Activator.CreateInstance(typeof(T), NonPublicInstance, binder: null,
            args: ["", Array.Empty<DirectoryControl>(), resultCode, errorMessage ?? "", Array.Empty<Uri>()],
            culture: null)!;

    /// <summary>
    /// Creates a successful SearchResponse containing a single entry with the given attributes.
    /// </summary>
    internal static SearchResponse SearchResponseWith(string distinguishedName, params (string Name, string Value)[] attributes)
    {
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", NonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;

        foreach (var (name, value) in attributes)
            add.Invoke(attributeCollection, [name, new DirectoryAttribute(name, value)]);

        var entry = (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), NonPublicInstance, binder: null,
            args: [distinguishedName, attributeCollection], culture: null)!;

        return SearchResponseWithEntries(entry);
    }

    /// <summary>
    /// Creates a successful SearchResponse holding one entry whose attributes carry binary values, as security
    /// descriptors and security identifiers do.
    /// </summary>
    internal static SearchResponse SearchResponseWithBinary(string distinguishedName, params (string Name, byte[][] Values)[] attributes)
    {
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", NonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;

        foreach (var (name, values) in attributes)
        {
            var attribute = new DirectoryAttribute { Name = name };
            foreach (var value in values)
                attribute.Add(value);

            add.Invoke(attributeCollection, [name, attribute]);
        }

        var entry = (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), NonPublicInstance, binder: null,
            args: [distinguishedName, attributeCollection], culture: null)!;

        return SearchResponseWithEntries(entry);
    }

    /// <summary>
    /// Creates a successful SearchResponse containing the given entries, or none.
    /// </summary>
    internal static SearchResponse SearchResponseWithEntries(params SearchResultEntry[] entries)
    {
        var entryCollection = (SearchResultEntryCollection)Activator.CreateInstance(typeof(SearchResultEntryCollection), nonPublic: true)!;
        var add = typeof(SearchResultEntryCollection).GetMethod("Add", NonPublicInstance, [typeof(SearchResultEntry)])!;

        foreach (var entry in entries)
            add.Invoke(entryCollection, [entry]);

        var response = Create<SearchResponse>(ResultCode.Success);
        typeof(SearchResponse).GetMethod("set_Entries", NonPublicInstance)!.Invoke(response, [entryCollection]);
        return response;
    }

    /// <summary>
    /// Creates a successful but empty SearchResponse. Worth naming explicitly: a directory returns exactly this
    /// both when nothing matches and when the caller has no rights to see what does.
    /// </summary>
    internal static SearchResponse EmptySearchResponse() => SearchResponseWithEntries();

    /// <summary>
    /// Creates an entry with the given attributes, for building multi-entry responses.
    /// </summary>
    internal static SearchResultEntry Entry(string distinguishedName, params (string Name, string Value)[] attributes)
    {
        var attributeCollection = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        var add = typeof(SearchResultAttributeCollection).GetMethod("Add", NonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!;

        foreach (var (name, value) in attributes)
            add.Invoke(attributeCollection, [name, new DirectoryAttribute(name, value)]);

        return (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), NonPublicInstance, binder: null,
            args: [distinguishedName, attributeCollection], culture: null)!;
    }
}
