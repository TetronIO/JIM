// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Holds the design system's button rule (<c>engineering/DESIGN.md</c> > Buttons): colour says what a button does.
/// Primary is the view's main action and is always filled, at most one per view; error and warning are dangerous
/// actions, outlined on a page; every other action is filled in the theme's default colour, or a text button where it
/// is lesser still. Before the rule, lesser actions were outlined in primary, so a page with no main action still
/// offered three purple buttons, each competing with the links and the Metaverse-side chips that are primary too; the
/// outlined default that followed read as a box drawn around nothing.
/// </summary>
/// <remarks>
/// <para>What the sweep can see in one file, it enforces:</para>
/// <list type="bullet">
/// <item>a filled button in a colour other than Primary, Error, Warning or the default (the rest carry no button
/// meaning);</item>
/// <item>an outlined button or menu in the default colour (an ordinary action is filled);</item>
/// <item>an outlined or text button in Primary, bar a text button on a table row, which reads as a link;</item>
/// <item>an outlined or text button in Secondary, Tertiary, Info, Success or Dark;</item>
/// <item>a filled Error or Warning button outside a dialog's actions (a destructive action on a page is outlined; it is
/// filled only as the confirming button of its own dialog);</item>
/// <item>more than one emphasised filled button (Primary, Error or Warning) that can render at once in a dialog's
/// actions;</item>
/// <item>a filled button repeated on every row of a table (a row action is a text button);</item>
/// <item>a filled button group, which fills every button in it.</item>
/// </list>
/// <para>"One filled button per page" is not enforced: a page is assembled from several components, so no single file
/// shows the whole view. That half of the rule is for review.</para>
/// <para>A deliberate exception opts out with a <c>@* button-emphasis: exempt - &lt;why&gt; *@</c> comment directly above
/// the button (or above the <c>DialogActions</c>), so the reason travels with the markup. Specimen pages that exist to
/// show every variant are excluded by path below.</para>
/// </remarks>
[TestFixture]
public class ButtonEmphasisConventionTests
{
    private const string ExemptionMarker = "button-emphasis: exempt";

    /// <summary>Pages whose job is to show the variants side by side, not to follow the rule.</summary>
    private static readonly string[] SpecimenPages =
    [
        "Pages/Admin/ThemePreview.razor",
        "Pages/Admin/LoadingPreview.razor",
        "Pages/Dev/ErrorPagePreview.razor"
    ];

    private static readonly string[] RowContexts = ["CellTemplate", "RowTemplate", "ChildRowContent", "MudTd", "td"];

    // Comments, the buttons themselves (tolerant of '>' inside quoted attribute values such as lambdas), and the
    // containers whose nesting matters.
    private static readonly Regex Token = new(
        @"@\*.*?\*@" +
        @"|<(?:MudButton|MudMenu|MudButtonGroup)\b(?:[^>""']|""[^""]*""|'[^']*')*>" +
        @"|</?(?:DialogActions|CellTemplate|RowTemplate|ChildRowContent|MudTd|td)\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex VariantAttribute = new(@"\bVariant=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ColorAttribute = new(@"\bColor=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ColourToken = new(@"\bColor\.(\w+)", RegexOptions.Compiled);
    private static readonly Regex DialogActionsBlock = new(@"<DialogActions\b[^>]*>(.*?)</DialogActions>", RegexOptions.Compiled | RegexOptions.Singleline);

    [Test]
    public void EveryButton_FollowsTheEmphasisRule()
    {
        var webRoot = Path.Join(FindRepositoryRoot(), "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(webRoot, path).Replace('\\', '/')))
            .Where(file => !SpecimenPages.Contains(file.Relative))
            .SelectMany(file => FindOffences(File.ReadAllText(file.Path), file.Relative))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Buttons must take their emphasis from the variant, one filled button per view (see engineering/DESIGN.md > " +
            "Buttons and src/JIM.Web/CLAUDE.md > Button emphasis):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [TestCase("Color.Secondary")]
    [TestCase("Color.Tertiary")]
    public void FindOffences_AFilledButtonRecolouredToLowerIt_IsReported(string colour)
    {
        var source = $"<MudButton Variant=\"Variant.Filled\" Color=\"{colour}\">Preview</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [TestCase("")]
    [TestCase(" Color=\"Color.Default\"")]
    public void FindOffences_AFilledButtonInTheDefaultColour_IsNotReported(string colour)
    {
        // An ordinary action is filled in the theme's default colour; its colour, not its variant, sets it below the
        // main action.
        var source = $"<MudButton Variant=\"Variant.Filled\"{colour}>Preview</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_EachEmphasisInItsOwnColourAndVariant_IsNotReported()
    {
        const string source = "<MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Save</MudButton>\n" +
                              "<MudButton Variant=\"Variant.Filled\">Preview</MudButton>\n" +
                              "<MudButton Variant=\"Variant.Outlined\" Color=\"Color.Error\">Delete</MudButton>\n" +
                              "<MudButton Color=\"Color.Default\">Cancel</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    [TestCase("<MudButton Variant=\"Variant.Outlined\">Preview</MudButton>")]
    [TestCase("<MudButton Variant=\"Variant.Outlined\" Color=\"Color.Default\">Preview</MudButton>")]
    [TestCase("<MudMenu Label=\"Columns\" Variant=\"Variant.Outlined\">\n</MudMenu>")]
    public void FindOffences_AnOutlinedActionInTheDefaultColour_IsReported(string source)
    {
        // An ordinary action is filled in the default colour; outlined is kept for a dangerous action on a page.
        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AFilledDefaultButtonBesideTheFilledMainActionInADialogsActions_IsNotReported()
    {
        // The dialog's one emphasised button is its recommended way out; an ordinary action beside it is filled in
        // the default colour without competing with it.
        const string source = "<DialogActions>\n" +
                              "  <MudButton OnClick=\"Cancel\">Cancel</MudButton>\n" +
                              "  <MudButton Variant=\"Variant.Filled\">Run anyway</MudButton>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Preview again</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AFilledDefaultButtonOnEveryTableRow_IsReported()
    {
        const string source = "<CellTemplate>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Size=\"Size.Small\">View</MudButton>\n" +
                              "</CellTemplate>";

        Assert.That(FindOffences(source, "List.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AnOutlinedPrimaryButton_IsReported()
    {
        // Primary marks the main action, and the main action is filled; an outlined one says "main" and "lesser" at once.
        const string source = "<MudButton Variant=\"Variant.Outlined\" Color=\"Color.Primary\">Preview</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_ATextPrimaryButtonOutsideATableRow_IsReported()
    {
        const string source = "<MudButton Variant=\"Variant.Text\" Color=\"Color.Primary\">Check again</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_APrimaryButtonWithNoVariant_IsReported()
    {
        // A button names no variant when it is a text button, MudBlazor's default.
        const string source = "<MudButton Color=\"Color.Primary\" OnClick=\"Close\">Close</MudButton>";

        Assert.That(FindOffences(source, "Dialog.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AnOutlinedPrimaryMenu_IsReported()
    {
        const string source = "<MudMenu Label=\"Columns\" Variant=\"Variant.Outlined\" Color=\"Color.Primary\">\n</MudMenu>";

        Assert.That(FindOffences(source, "List.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_ATextPrimaryButtonInAPlainTableCell_IsNotReported()
    {
        // A row action reads as a link, and links are primary; the row is the same whether a MudTd or a td draws it.
        const string source = "<tr>\n  <td>\n" +
                              "    <MudButton Variant=\"Variant.Text\" Color=\"Color.Primary\" Size=\"Size.Small\">Select</MudButton>\n" +
                              "  </td>\n</tr>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AnOutlinedPrimaryButtonOnATableRow_IsReported()
    {
        // The row exception is for text buttons, which read as links; an outlined one reads as a button.
        const string source = "<CellTemplate>\n" +
                              "  <MudButton Variant=\"Variant.Outlined\" Color=\"Color.Primary\" Size=\"Size.Small\">View</MudButton>\n" +
                              "</CellTemplate>";

        Assert.That(FindOffences(source, "List.razor"), Has.Count.EqualTo(1));
    }

    [TestCase("Color.Secondary")]
    [TestCase("Color.Tertiary")]
    [TestCase("Color.Info")]
    [TestCase("Color.Success")]
    [TestCase("Color.Dark")]
    public void FindOffences_ALesserButtonInAColourWithNoIntent_IsReported(string colour)
    {
        // A button's colour says what it does: primary for the main action, error and warning for the dangerous ones.
        // Secondary means the Connected System side in JIM, and the rest are an alert's to give (AlertButton).
        var source = $"<MudButton Variant=\"Variant.Outlined\" Color=\"{colour}\">Preview</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AFilledDestructiveButtonOnAPage_IsReported()
    {
        const string source = "<MudButton Variant=\"Variant.Filled\" Color=\"Color.Error\">Delete</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AFilledDestructiveButtonConfirmingItsDialog_IsNotReported()
    {
        const string source = "<DialogActions>\n" +
                              "  <MudButton OnClick=\"Cancel\">Cancel</MudButton>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Error\">Delete</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_TwoFilledButtonsInOneDialogsActions_IsReported()
    {
        const string source = "<DialogActions>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Save</MudButton>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Save and close</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_FilledButtonsInExclusiveBranchesOfADialogsActions_IsNotReported()
    {
        // Only one branch renders, so only one filled button is ever on screen.
        const string source = "<DialogActions>\n" +
                              "  <MudButton OnClick=\"Cancel\">Cancel</MudButton>\n" +
                              "  @if (_step == Step.Choose)\n  {\n" +
                              "    <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\" OnClick=\"@(() => { Next(); })\">Continue</MudButton>\n" +
                              "  }\n  else if (_step == Step.Confirm)\n  {\n" +
                              "    <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Trust</MudButton>\n" +
                              "  }\n  else\n  {\n" +
                              "    <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Done</MudButton>\n" +
                              "  }\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AConditionalFilledButtonBesideAnUnconditionalOne_IsReported()
    {
        // An @if without an else can render alongside what follows it.
        const string source = "<DialogActions>\n" +
                              "  @if (_canRetry)\n  {\n" +
                              "    <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Retry</MudButton>\n" +
                              "  }\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Done</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AFilledButtonOnEveryTableRow_IsReported()
    {
        const string source = "<CellTemplate>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\" Size=\"Size.Small\">View</MudButton>\n" +
                              "</CellTemplate>";

        Assert.That(FindOffences(source, "List.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_ATextButtonOnEveryTableRow_IsNotReported()
    {
        const string source = "<CellTemplate>\n" +
                              "  <MudButton Variant=\"Variant.Text\" Color=\"Color.Primary\" Size=\"Size.Small\">View</MudButton>\n" +
                              "</CellTemplate>";

        Assert.That(FindOffences(source, "List.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AFilledButtonGroup_IsReported()
    {
        const string source = "<MudButtonGroup Variant=\"Variant.Filled\" Color=\"Color.Primary\">\n" +
                              "  <MudButton>Upload</MudButton>\n  <MudButton>Reference</MudButton>\n" +
                              "</MudButtonGroup>";

        Assert.That(FindOffences(source, "List.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AColourChosenAtRunTime_IsNotJudged()
    {
        // A parameter or helper decides the colour; nothing in the file says which, so the sweep cannot.
        const string source = "<DialogActions>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"@ConfirmButtonColour\">Confirm</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AnExemptedButton_IsNotChecked()
    {
        const string source = "@* button-emphasis: exempt - the danger panel's one action *@\n" +
                              "<MudButton Variant=\"Variant.Filled\" Color=\"Color.Error\">Delete</MudButton>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AnExemptedDialogActions_IsNotCounted()
    {
        const string source = "@* button-emphasis: exempt - two equal choices *@\n" +
                              "<DialogActions>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Keep</MudButton>\n" +
                              "  <MudButton Variant=\"Variant.Filled\" Color=\"Color.Primary\">Replace</MudButton>\n" +
                              "</DialogActions>";

        Assert.That(FindOffences(source, "Dialog.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_ButtonsInsideAComment_AreIgnored()
    {
        const string source = "@* <MudButton Variant=\"Variant.Filled\" Color=\"Color.Secondary\">Old</MudButton> *@";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    /// <summary>The offences in one Razor source, as "file:line reason".</summary>
    private static List<string> FindOffences(string source, string relativePath)
    {
        var offences = new List<string>();
        var rowDepth = 0;
        var dialogActionsDepth = 0;
        string? precedingComment = null;
        var precedingCommentEnd = -1;

        foreach (Match token in Token.Matches(source))
        {
            var text = token.Value;
            var line = LineOf(source, token.Index);

            if (text.StartsWith("@*", StringComparison.Ordinal))
            {
                precedingComment = text;
                precedingCommentEnd = token.Index + token.Length;
                continue;
            }

            var exempt = precedingComment != null &&
                         precedingComment.Contains(ExemptionMarker, StringComparison.Ordinal) &&
                         string.IsNullOrWhiteSpace(source[precedingCommentEnd..token.Index]);

            if (TryTrackContainer(text, ref rowDepth, ref dialogActionsDepth))
                continue;

            if (exempt)
                continue;

            var name = Regex.Match(text, @"^<(\w+)").Groups[1].Value;
            if (!IsFilled(text))
            {
                offences.AddRange(FindLesserButtonOffences(text, name, rowDepth > 0, relativePath, line));
                continue;
            }

            if (name == "MudButtonGroup")
            {
                offences.Add($"{relativePath}:{line} a filled MudButtonGroup fills every button in it; fill only the main action");
                continue;
            }

            var colours = ColoursOf(text);
            if (colours.Count == 0 && ColorAttribute.IsMatch(text))
                continue;

            var lowered = colours.Where(c => c is not ("Primary" or "Error" or "Warning" or "Default")).ToList();
            if (lowered.Count > 0)
                offences.Add($"{relativePath}:{line} a filled {name} in {string.Join("/", lowered)}; a button's colour says what it does: Primary for the main action, Error or Warning for a dangerous one, and the default colour for every other action");

            if (colours.Any(c => c is "Error" or "Warning") && dialogActionsDepth == 0)
                offences.Add($"{relativePath}:{line} a filled destructive {name} outside a dialog's actions; outline it, and fill only the dialog's confirming button");

            if (rowDepth > 0)
                offences.Add($"{relativePath}:{line} a filled {name} on every table row; a row action is a text button");
        }

        offences.AddRange(FindCrowdedDialogActions(source, relativePath));
        return offences;
    }

    /// <summary>
    /// The offences of a button that is not filled. Its colour says what it does: the neutral default, or error or warning
    /// for a dangerous action. Primary marks the view's main action, which is filled, save for a text button on a table
    /// row: it reads as a link, and links are primary. The other colours are a chip's (secondary is the Connected System
    /// side) or an alert's to give, through AlertButton.
    /// </summary>
    private static IEnumerable<string> FindLesserButtonOffences(string tag, string name, bool onATableRow, string relativePath, int line)
    {
        var variant = VariantAttribute.Match(tag);
        // A variant chosen at run time, or a menu drawn as an icon button, says nothing the sweep can judge.
        if ((variant.Success && variant.Groups[1].Value.StartsWith('@')) ||
            (name == "MudMenu" && Regex.IsMatch(tag, @"\bIcon=""")))
            yield break;

        var isText = !variant.Success || variant.Groups[1].Value.Contains("Variant.Text", StringComparison.Ordinal);
        var kind = isText ? "a text" : "an outlined";
        var colours = ColoursOf(tag);

        if (!isText && IsDefaultColour(tag))
            yield return $"{relativePath}:{line} an outlined {name} in the default colour; an ordinary action is filled in the default colour, and outlined is kept for a dangerous action on a page";

        if (colours.Contains("Primary") && !(isText && onATableRow))
            yield return $"{relativePath}:{line} {kind} {name} in Primary; primary marks the view's main action, which is filled, so a lesser action takes the default colour";

        var withoutIntent = colours.Where(c => c is "Secondary" or "Tertiary" or "Info" or "Success" or "Dark").ToList();
        if (withoutIntent.Count > 0)
            yield return $"{relativePath}:{line} {kind} {name} in {string.Join("/", withoutIntent)}; a button's colour says what it does: default, or error or warning for a dangerous action";
    }

    /// <summary>Follows the containers whose nesting matters; true when the token was one of them.</summary>
    private static bool TryTrackContainer(string text, ref int rowDepth, ref int dialogActionsDepth)
    {
        var closing = text.StartsWith("</", StringComparison.Ordinal);
        var name = Regex.Match(text, @"^</?(\w+)").Groups[1].Value;
        var selfClosing = text.EndsWith("/>", StringComparison.Ordinal);

        if (name == "DialogActions")
        {
            if (!selfClosing)
                dialogActionsDepth += closing ? -1 : 1;
            return true;
        }

        if (RowContexts.Contains(name))
        {
            if (!selfClosing)
                rowDepth += closing ? -1 : 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Dialog actions in which more than one filled button can be on screen at once. Branches of an if/else chain are
    /// exclusive, so a chain counts as its busiest branch; anything else (a lone @if, a loop, plain markup) adds up.
    /// </summary>
    private static IEnumerable<string> FindCrowdedDialogActions(string source, string relativePath)
    {
        foreach (Match block in DialogActionsBlock.Matches(source))
        {
            var before = source[..block.Index].TrimEnd();
            if (before.EndsWith("*@", StringComparison.Ordinal) &&
                before[(before.LastIndexOf("@*", StringComparison.Ordinal) + 2)..].Contains(ExemptionMarker, StringComparison.Ordinal))
                continue;

            var content = block.Groups[1].Value;
            var filledAt = Token.Matches(content)
                .Where(m => m.Value.StartsWith("<MudButton", StringComparison.Ordinal) && IsFilled(m.Value) && !IsDefaultColour(m.Value))
                .Select(m => m.Index)
                .ToList();

            if (filledAt.Count > 1 && CountVisibleAtOnce(Mask(content), 0, content.Length, filledAt) > 1)
                yield return $"{relativePath}:{LineOf(source, block.Index)} more than one emphasised filled button (Primary, Error or Warning) can show in these dialog actions; keep one, the recommended way out";
        }
    }

    /// <summary>The most filled buttons that can render together between two positions of masked markup.</summary>
    private static int CountVisibleAtOnce(string masked, int start, int end, List<int> filledAt)
    {
        var total = 0;
        var position = start;

        while (position < end)
        {
            var next = masked.IndexOf('{', position);
            if (next < 0 || next >= end)
            {
                total += filledAt.Count(p => p >= position && p < end);
                break;
            }

            total += filledAt.Count(p => p >= position && p < next);
            var keyword = masked[position..next];
            var close = MatchingBrace(masked, next);
            var branch = CountVisibleAtOnce(masked, next + 1, close, filledAt);

            if (Regex.IsMatch(keyword, @"@if\b"))
            {
                // Walk the else chain, keeping the busiest branch.
                var busiest = branch;
                var after = close + 1;
                var chained = Regex.Match(masked[after..end], @"^\s*else\b[^{]*\{");
                while (chained.Success)
                {
                    var open = after + chained.Length - 1;
                    var closeElse = MatchingBrace(masked, open);
                    busiest = Math.Max(busiest, CountVisibleAtOnce(masked, open + 1, closeElse, filledAt));
                    after = closeElse + 1;
                    chained = Regex.Match(masked[after..end], @"^\s*else\b[^{]*\{");
                }

                total += busiest;
                position = after;
                continue;
            }

            total += branch;
            position = close + 1;
        }

        return total;
    }

    private static int MatchingBrace(string masked, int open)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == '{')
                depth++;
            else if (masked[i] == '}' && --depth == 0)
                return i;
        }

        return masked.Length - 1;
    }

    /// <summary>The markup with comments and quoted attribute values blanked, so braces inside lambdas do not count.</summary>
    private static string Mask(string content) =>
        Regex.Replace(content, @"@\*.*?\*@|""[^""]*""", m => new string(' ', m.Length), RegexOptions.Singleline);

    private static bool IsFilled(string tag)
    {
        var variant = VariantAttribute.Match(tag);
        return variant.Success && variant.Groups[1].Value.Contains("Variant.Filled", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the button takes the theme's default colour: no colour named, or <c>Color.Default</c>. A colour chosen at
    /// run time is neither.
    /// </summary>
    private static bool IsDefaultColour(string tag)
    {
        if (!ColorAttribute.IsMatch(tag))
            return true;
        var colours = ColoursOf(tag);
        return colours.Count > 0 && colours.All(c => c == "Default");
    }

    private static List<string> ColoursOf(string tag)
    {
        var colour = ColorAttribute.Match(tag);
        return colour.Success
            ? ColourToken.Matches(colour.Groups[1].Value).Select(m => m.Groups[1].Value).Distinct().ToList()
            : [];
    }

    private static int LineOf(string source, int index) => source[..index].Count(c => c == '\n') + 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }
}
