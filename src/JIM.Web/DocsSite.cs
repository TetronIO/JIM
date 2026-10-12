// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web;

/// <summary>
/// Addresses on the published documentation site, for the portal's links into it.
/// <para>
/// A portal page names its docs page as the Markdown file under <c>docs/</c> that covers it
/// (<c>configuration/connected-systems.md#the-connector-space</c>), not as a URL, so the COV-5 coverage check in
/// <c>scripts/Test-DocsCoverage.ps1</c> can read every name straight from source and fail the build when the page or
/// heading it names goes away. This class turns that name into the address the site serves it from.
/// </para>
/// </summary>
public static class DocsSite
{
    /// <summary>The published site's root, as <c>site_url</c> in <c>mkdocs.yml</c> gives it.</summary>
    public const string BaseUrl = "https://docs.junctional.io";

    /// <summary>
    /// The address of a docs page: <c>configuration/connected-systems.md#the-connector-space</c> becomes
    /// <c>https://docs.junctional.io/configuration/connected-systems/#the-connector-space</c>. MkDocs serves each page
    /// as a directory, and an <c>index.md</c> as the folder holding it.
    /// </summary>
    /// <param name="docsPage">The page's path under <c>docs/</c>, ending <c>.md</c>, optionally with a heading's anchor.</param>
    /// <exception cref="ArgumentException">The value does not name a Markdown page.</exception>
    public static string PageUrl(string docsPage)
    {
        var hash = docsPage.IndexOf('#');
        var path = hash < 0 ? docsPage : docsPage[..hash];
        var anchor = hash < 0 ? string.Empty : docsPage[hash..];

        if (!path.EndsWith(".md", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{docsPage}' is not a docs page; name the Markdown page under docs/, for example " +
                "'configuration/connected-systems.md#the-connector-space'.", nameof(docsPage));

        path = path[..^".md".Length];
        if (path == "index")
            path = string.Empty;
        else if (path.EndsWith("/index", StringComparison.Ordinal))
            path = path[..^"index".Length];
        else
            path += "/";

        return $"{BaseUrl}/{path}{anchor}";
    }

    /// <summary>The address of an entry on the glossary page, from its anchor (without the leading hash).</summary>
    public static string GlossaryUrl(string anchor) => $"{BaseUrl}/reference/glossary/#{anchor}";
}
