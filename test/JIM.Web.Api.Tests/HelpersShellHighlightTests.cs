// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersShellHighlightTests
{
    [Test]
    public void HighlightShell_NullInput_ReturnsEmptyString()
    {
        var result = Helpers.HighlightShell(null!, ShellLanguage.Bash);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void HighlightShell_EmptyInput_ReturnsEmptyString()
    {
        var result = Helpers.HighlightShell(string.Empty, ShellLanguage.Bash);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    // ─── Bash ───

    [Test]
    public void HighlightShell_Bash_LeadingWord_IsCommand()
    {
        var result = Helpers.HighlightShell("curl https://example.com", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">curl</span>"));
    }

    [Test]
    public void HighlightShell_Bash_ArgumentAfterCommand_IsNotCommand()
    {
        // The URL argument must not be coloured as a command.
        var result = Helpers.HighlightShell("curl https://example.com", ShellLanguage.Bash);
        Assert.That(result, Does.Not.Contain("jim-code-command\">https"));
        Assert.That(result, Does.Contain("https://example.com"));
    }

    [Test]
    public void HighlightShell_Bash_DoubleQuotedString_IsString()
    {
        var result = Helpers.HighlightShell("echo \"hello world\"", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-string\">&quot;hello world&quot;</span>"));
    }

    [Test]
    public void HighlightShell_Bash_SingleQuotedString_IsString()
    {
        var result = Helpers.HighlightShell("echo 'literal'", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-string\">&#39;literal&#39;</span>"));
    }

    [Test]
    public void HighlightShell_Bash_ShortFlag_IsFlag()
    {
        var result = Helpers.HighlightShell("curl -H accept", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">-H</span>"));
    }

    [Test]
    public void HighlightShell_Bash_LongFlag_IsFlag()
    {
        var result = Helpers.HighlightShell("curl --data payload", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">--data</span>"));
    }

    [Test]
    public void HighlightShell_Bash_Variable_IsVariable()
    {
        var result = Helpers.HighlightShell("echo $HOME", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-variable\">$HOME</span>"));
    }

    [Test]
    public void HighlightShell_Bash_BracedVariable_IsVariable()
    {
        var result = Helpers.HighlightShell("echo ${HOME}", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-variable\">${HOME}</span>"));
    }

    [Test]
    public void HighlightShell_Bash_LineComment_IsComment()
    {
        var result = Helpers.HighlightShell("curl x # a note", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-comment\"># a note</span>"));
    }

    [Test]
    public void HighlightShell_Bash_HashInsideUrl_IsNotComment()
    {
        // '#' not at a token boundary must not start a comment.
        var result = Helpers.HighlightShell("curl https://example.com/page#frag", ShellLanguage.Bash);
        Assert.That(result, Does.Not.Contain("jim-code-comment"));
    }

    [Test]
    public void HighlightShell_Bash_Pipe_IsOperatorAndNextWordIsCommand()
    {
        var result = Helpers.HighlightShell("cat file | grep x", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-operator\">|</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">grep</span>"));
    }

    [Test]
    public void HighlightShell_Bash_Keyword_IsKeyword()
    {
        var result = Helpers.HighlightShell("if true; then echo x; fi", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-keyword\">if</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-keyword\">then</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-keyword\">fi</span>"));
    }

    [Test]
    public void HighlightShell_Bash_Number_IsNumber()
    {
        var result = Helpers.HighlightShell("sleep 30", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-number\">30</span>"));
    }

    [Test]
    public void HighlightShell_Bash_LineContinuation_KeepsCommandContext()
    {
        // After a '\' line continuation the next line is a continuation, not a new command.
        var result = Helpers.HighlightShell("curl -H \"A: b\" \\\n  https://example.com", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">curl</span>"));
        // The continuation line's URL must not be re-coloured as a command.
        Assert.That(result, Does.Not.Contain("jim-code-command\">https"));
    }

    [Test]
    public void HighlightShell_Bash_ApiKeyCurlExample_HighlightsExpectedTokens()
    {
        var code = "curl -H \"X-API-Key: jim_ak_abc123\" \\\n  https://your-jim-server/api/v1/metaverse/objects";
        var result = Helpers.HighlightShell(code, ShellLanguage.Bash);

        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">curl</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">-H</span>"));
        Assert.That(result, Does.Contain("jim-code-string"));
        Assert.That(result, Does.Contain("jim_ak_abc123"));
        // Newline and continuation backslash preserved for the <pre> to render.
        Assert.That(result, Does.Contain("\\\n"));
    }

    // ─── PowerShell ───

    [Test]
    public void HighlightShell_PowerShell_Variable_IsVariable()
    {
        var result = Helpers.HighlightShell("Write-Host $name", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-variable\">$name</span>"));
    }

    [Test]
    public void HighlightShell_PowerShell_ScopedVariable_IsVariable()
    {
        var result = Helpers.HighlightShell("Write-Host $env:PATH", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-variable\">$env:PATH</span>"));
    }

    [Test]
    public void HighlightShell_PowerShell_Cmdlet_IsCommandNotFlag()
    {
        // The hyphen in a Verb-Noun cmdlet must not be mistaken for a flag.
        var result = Helpers.HighlightShell("Get-ChildItem", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">Get-ChildItem</span>"));
        Assert.That(result, Does.Not.Contain("jim-code-flag"));
    }

    [Test]
    public void HighlightShell_PowerShell_Parameter_IsFlag()
    {
        var result = Helpers.HighlightShell("Get-ChildItem -Path C:\\temp", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">-Path</span>"));
    }

    [Test]
    public void HighlightShell_PowerShell_Keyword_IsKeyword()
    {
        var result = Helpers.HighlightShell("foreach ($x in $xs) { }", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-keyword\">foreach</span>"));
    }

    [Test]
    public void HighlightShell_PowerShell_BlockComment_IsComment()
    {
        var result = Helpers.HighlightShell("<# a block #> Get-Item", ShellLanguage.PowerShell);
        Assert.That(result, Does.Contain("<span class=\"jim-code-comment\">&lt;# a block #&gt;</span>"));
    }

    [Test]
    public void HighlightShell_PowerShell_HeaderHashtableExample_HighlightsTokens()
    {
        var code = "Invoke-RestMethod -Uri \"https://your-jim-server/api/v1/metaverse/objects\" -Headers @{ \"X-API-Key\" = \"jim_ak_abc123\" }";
        var result = Helpers.HighlightShell(code, ShellLanguage.PowerShell);

        Assert.That(result, Does.Contain("<span class=\"jim-code-command\">Invoke-RestMethod</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">-Uri</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-code-flag\">-Headers</span>"));
        Assert.That(result, Does.Contain("jim-code-string"));
    }

    // ─── Security ───

    [Test]
    public void HighlightShell_HtmlSpecialChars_AreEncoded()
    {
        var result = Helpers.HighlightShell("echo \"<script>alert(1)</script>\"", ShellLanguage.Bash);
        Assert.That(result, Does.Not.Contain("<script>"));
        Assert.That(result, Does.Contain("&lt;script&gt;"));
    }

    [Test]
    public void HighlightShell_UnclosedString_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => Helpers.HighlightShell("echo \"unterminated", ShellLanguage.Bash));
        var result = Helpers.HighlightShell("echo \"unterminated", ShellLanguage.Bash);
        Assert.That(result, Does.Contain("jim-code-string"));
    }
}
