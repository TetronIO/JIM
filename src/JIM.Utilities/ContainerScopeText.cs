// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Models.Staging;
namespace JIM.Utilities;

/// <summary>
/// Advanced Mode: Container Scope stated as text, for selections the tree control cannot practically be clicked
/// through, and the canonical text projected back from the Container rows.
/// </summary>
/// <remarks>
/// The text is an editor for the same <see cref="ConnectedSystemContainer.Selected"/> and
/// <see cref="ConnectedSystemContainer.Excluded"/> flags the tree edits, resolved against the discovered hierarchy
/// at the moment it is applied. It is deliberately not a stored list of paths re-resolved on every run: a path is
/// Distinguished Name text, and a Container renamed or moved in the directory would silently change what such a
/// list matched, which is the defect <see cref="ConnectedSystemContainer.StableId"/> exists to prevent. Resolving
/// once, into rows keyed on that identifier, means Advanced Mode inherits rename-safety rather than reintroducing
/// the problem.
///
/// Two consequences follow, both deliberate. Applying text is all-or-nothing, because a partially applied scope
/// takes objects out of import scope without anyone asking for it. And a path naming no Container is an error, never
/// a line quietly ignored: silence there means an administrator believes a branch is excluded when it is not, or
/// included when it is not, and both end in objects being obsoleted or exposed with nothing said.
/// </remarks>
public static class ContainerScopeText
{
    private const string IncludeDirective = "include";
    private const string ExcludeDirective = "exclude";
    private const string OneLevelModifier = "one-level";

    /// <summary>
    /// Reads Container Scope text into the statements it makes, without resolving them against any hierarchy.
    /// </summary>
    /// <remarks>
    /// Everything answerable from the text alone is answered here (what each line says, and whether it says
    /// anything at all); everything needing the hierarchy is answered by <see cref="Apply"/>. That split is what
    /// lets the portal check syntax as an administrator types without going near the database.
    /// </remarks>
    public static ContainerScopeTextResult Parse(string? text)
    {
        var statements = new List<ContainerScopeStatement>();
        var errors = new List<ContainerScopeTextError>();
        var lines = (text ?? string.Empty).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();

            // Comments are whole-line only, because a Distinguished Name may carry an unescaped '#' inside a value
            // and treating it as the start of a comment would silently truncate the path.
            if (line.Length == 0 || line[0] == '#')
                continue;

            var statement = ReadStatement(line, lineNumber, out var error);
            if (statement != null)
                statements.Add(statement);
            else if (error != null)
                errors.Add(error);
        }

        return new ContainerScopeTextResult { Statements = statements, Errors = errors };
    }

    /// <summary>
    /// Resolves Container Scope text against a Connected System's hierarchy and applies it, replacing every
    /// statement the hierarchy currently carries.
    /// </summary>
    /// <param name="text">The text to apply. Empty text states nothing, which clears the whole selection.</param>
    /// <param name="partitions">
    /// The partitions to apply it to, with their Container trees loaded. Only partitions holding a Container the
    /// text names have their own selection touched: the text states Container Scope, and a Connector whose scope is
    /// partitions alone has no Containers for it to state.
    /// </param>
    /// <returns>
    /// Everything that stopped the text being applied, empty on success. Where this is not empty, nothing has been
    /// changed.
    /// </returns>
    public static IReadOnlyList<ContainerScopeTextError> Apply(
        string? text,
        IReadOnlyList<ConnectedSystemPartition> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);

        var parsed = Parse(text);
        if (!parsed.IsValid)
            return parsed.Errors;

        var containersByPath = IndexByPath(partitions);
        var resolved = new List<(ContainerScopeStatement Statement, ConnectedSystemContainer Container)>();
        var statementsByContainerId = new Dictionary<int, ContainerScopeStatement>();
        var errors = new List<ContainerScopeTextError>();

        foreach (var statement in parsed.Statements)
        {
            if (!containersByPath.TryGetValue(NormalisePath(statement.Path), out var container))
            {
                errors.Add(new ContainerScopeTextError(statement.LineNumber,
                    $"No Container with the path '{statement.Path}' was found. Check the path, or retrieve the " +
                    "Connected System's hierarchy if the Container has been created since it was last read."));
                continue;
            }

            if (statementsByContainerId.TryGetValue(container.Id, out var existing))
            {
                errors.Add(new ContainerScopeTextError(statement.LineNumber,
                    $"'{statement.Path}' is already stated on line {existing.LineNumber}. A Container states one " +
                    "thing about itself, so it cannot appear twice."));
                continue;
            }

            statementsByContainerId[container.Id] = statement;
            resolved.Add((statement, container));
        }

        errors.AddRange(RedundantStatements(resolved, statementsByContainerId));

        if (errors.Count > 0)
            return errors;

        ApplyResolvedStatements(partitions, resolved);
        return [];
    }

    /// <summary>
    /// Writes the canonical text for what a hierarchy's Containers currently state, in hierarchy order.
    /// </summary>
    /// <remarks>
    /// Canonical means one statement per Container that says something about itself, and nothing else: no header,
    /// no restatement of what an ancestor already covers. Text projected from a selection and applied back to it
    /// therefore round-trips exactly, which is what makes moving between the tree and the text a free choice rather
    /// than a decision an administrator has to confirm.
    /// </remarks>
    public static string Project(IEnumerable<ConnectedSystemPartition> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);

        var lines = partitions
            .SelectMany(ContainerSelectionEditor.Flatten)
            .Where(c => c.Selected || c.Excluded)
            .Select(ProjectStatement);

        return string.Join('\n', lines);
    }

    private static string ProjectStatement(ConnectedSystemContainer container)
    {
        var directive = container.Selected ? IncludeDirective : ExcludeDirective;
        var modifier = container.Scope == ConnectedSystemContainerScope.OneLevel ? $" {OneLevelModifier}" : string.Empty;

        return $"{directive}{modifier} {container.ExternalId}";
    }

    private static ContainerScopeStatement? ReadStatement(string line, int lineNumber, out ContainerScopeTextError? error)
    {
        error = null;

        var (directive, remainder) = SplitDirective(line);
        ContainerScopeStatementKind kind;

        switch (directive.ToLowerInvariant())
        {
            case IncludeDirective or "+":
                kind = ContainerScopeStatementKind.Include;
                break;
            case ExcludeDirective or "-":
                kind = ContainerScopeStatementKind.Exclude;
                break;
            default:
                error = new ContainerScopeTextError(lineNumber,
                    $"'{directive}' is not something a Container Scope statement can say. Every line begins with " +
                    $"{IncludeDirective} (or +) or {ExcludeDirective} (or -).");
                return null;
        }

        var (scope, path) = SplitScope(remainder);

        if (path.Length == 0)
        {
            error = new ContainerScopeTextError(lineNumber,
                $"'{directive}' names no Container. Follow it with the Container's path, for example: " +
                $"{directive} OU=Corp,DC=example,DC=com");
            return null;
        }

        return new ContainerScopeStatement(lineNumber, kind, path, scope);
    }

    /// <summary>
    /// Separates a line's directive from the rest of it, accepting the shorthand written with no space after it.
    /// </summary>
    private static (string Directive, string Remainder) SplitDirective(string line)
    {
        if (line[0] is '+' or '-')
            return (line[..1], line[1..].Trim());

        var separator = line.IndexOf(' ', StringComparison.Ordinal);

        return separator < 0
            ? (line, string.Empty)
            : (line[..separator], line[(separator + 1)..].Trim());
    }

    /// <summary>
    /// Takes the optional one-level modifier off the front of a statement's path.
    /// </summary>
    /// <remarks>
    /// The modifier leads rather than trails because a Distinguished Name may end in anything, and a trailing
    /// keyword could not be told apart from the last component of a path. It is only a modifier when it stands
    /// alone: "one-level" is a legal LDAP attribute type name, so a path may genuinely begin "one-level=".
    /// </remarks>
    private static (ConnectedSystemContainerScope Scope, string Path) SplitScope(string remainder)
    {
        var separator = remainder.IndexOf(' ', StringComparison.Ordinal);
        if (separator < 0)
            return (ConnectedSystemContainerScope.Subtree, remainder);

        var leadingToken = remainder[..separator];

        return leadingToken.Equals(OneLevelModifier, StringComparison.OrdinalIgnoreCase)
            ? (ConnectedSystemContainerScope.OneLevel, remainder[(separator + 1)..].Trim())
            : (ConnectedSystemContainerScope.Subtree, remainder);
    }

    /// <summary>
    /// The statements that only restate what an ancestor's statement already says.
    /// </summary>
    /// <remarks>
    /// Refused rather than normalised away, because the canonical projection never writes one: dropping the line
    /// silently would hand back text that differs from the text just saved, and leaving it standing would put a
    /// Container into a state the tree cannot reach (selected and covered at once). The ancestor that decides is
    /// the nearest one making a statement that reaches this far, which is the same walk the tree performs, so a
    /// re-inclusion inside a carved-out branch is correctly not redundant.
    /// </remarks>
    private static IEnumerable<ContainerScopeTextError> RedundantStatements(
        IReadOnlyList<(ContainerScopeStatement Statement, ConnectedSystemContainer Container)> resolved,
        IReadOnlyDictionary<int, ContainerScopeStatement> statementsByContainerId)
    {
        return resolved
            .Select(entry => (entry.Statement, Decider: DecidingStatement(entry.Container, statementsByContainerId)))
            .Where(entry => entry.Decider != null && entry.Decider.Kind == entry.Statement.Kind)
            .Select(entry => new ContainerScopeTextError(entry.Statement.LineNumber,
                $"'{entry.Statement.Path}' is already covered by the statement on line {entry.Decider!.LineNumber} " +
                $"('{entry.Decider.Path}'), so this line changes nothing. Remove it, or narrow line " +
                $"{entry.Decider.LineNumber} to {OneLevelModifier}."));
    }

    /// <summary>
    /// The statement above a Container that decides its fate: the nearest ancestor stated in the text whose
    /// statement reaches beyond itself.
    /// </summary>
    private static ContainerScopeStatement? DecidingStatement(
        ConnectedSystemContainer container,
        IReadOnlyDictionary<int, ContainerScopeStatement> statementsByContainerId)
    {
        for (var ancestor = container.ParentContainer; ancestor != null; ancestor = ancestor.ParentContainer)
        {
            if (statementsByContainerId.TryGetValue(ancestor.Id, out var statement) &&
                statement.Scope == ConnectedSystemContainerScope.Subtree)
            {
                return statement;
            }
        }

        return null;
    }

    private static void ApplyResolvedStatements(
        IReadOnlyList<ConnectedSystemPartition> partitions,
        IReadOnlyList<(ContainerScopeStatement Statement, ConnectedSystemContainer Container)> resolved)
    {
        foreach (var partition in partitions)
            ContainerSelectionEditor.ClearSelection(partition);

        foreach (var (statement, container) in resolved)
        {
            container.Selected = statement.Kind == ContainerScopeStatementKind.Include;
            container.Excluded = statement.Kind == ContainerScopeStatementKind.Exclude;
            container.Scope = statement.Scope;
        }

        var statedContainerIds = resolved.Select(entry => entry.Container.Id).ToHashSet();

        foreach (var partition in partitions)
        {
            // A Container cannot be in scope while the partition around it is not, so naming one selects its
            // partition, exactly as ticking one in the tree does. A partition the text says nothing about keeps
            // whatever selection it had: this text states Container Scope, not partition scope.
            if (ContainerSelectionEditor.Flatten(partition).Any(c => statedContainerIds.Contains(c.Id)))
                partition.Selected = true;

            ContainerSelectionEditor.RecalculateCoverage(partition);
        }
    }

    private static Dictionary<string, ConnectedSystemContainer> IndexByPath(
        IReadOnlyList<ConnectedSystemPartition> partitions)
    {
        var index = new Dictionary<string, ConnectedSystemContainer>(StringComparer.OrdinalIgnoreCase);

        foreach (var container in partitions.SelectMany(ContainerSelectionEditor.Flatten))
            index.TryAdd(NormalisePath(container.ExternalId), container);

        return index;
    }

    /// <summary>
    /// Puts a path into the form both sides of a comparison are held in: no optional whitespace after a separator.
    /// </summary>
    /// <remarks>
    /// RFC 4514 permits a space after each separator and some directories emit one, so an administrator pasting a
    /// Distinguished Name from elsewhere routinely arrives with it. An escaped comma is part of a value rather than
    /// a separator (<c>CN=Smith\, John,OU=People</c>), so the space after it is part of the name and is kept.
    /// Case is left alone here and handled by the comparer the index is built with.
    /// </remarks>
    private static string NormalisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim();
        var normalised = new StringBuilder(trimmed.Length);
        var skippingWhitespaceAfterSeparator = false;

        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];

            if (skippingWhitespaceAfterSeparator && char.IsWhiteSpace(character))
                continue;

            skippingWhitespaceAfterSeparator = character == ',' && (index == 0 || trimmed[index - 1] != '\\');
            normalised.Append(character);
        }

        return normalised.ToString();
    }
}
