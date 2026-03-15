using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

[TestFixture]
public class LogSanitiserTests
{
    #region Sanitise Tests

    [Test]
    public void Sanitise_NullValue_ReturnsNullAsync()
    {
        var result = LogSanitiser.Sanitise(null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Sanitise_EmptyString_ReturnsEmptyStringAsync()
    {
        var result = LogSanitiser.Sanitise(string.Empty);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Sanitise_CleanString_ReturnsUnchangedAsync()
    {
        var result = LogSanitiser.Sanitise("hello world");
        Assert.That(result, Is.EqualTo("hello world"));
    }

    [Test]
    public void Sanitise_StringWithLineFeed_RemovesLineFeedAsync()
    {
        var result = LogSanitiser.Sanitise("injected\nfake log entry");
        Assert.That(result, Is.EqualTo("injected" + "fake log entry"));
        Assert.That(result, Does.Not.Contain("\n"));
    }

    [Test]
    public void Sanitise_StringWithCarriageReturn_RemovesCarriageReturnAsync()
    {
        var result = LogSanitiser.Sanitise("injected\rfake log entry");
        Assert.That(result, Is.EqualTo("injected" + "fake log entry"));
        Assert.That(result, Does.Not.Contain("\r"));
    }

    [Test]
    public void Sanitise_StringWithCrLf_RemovesBothAsync()
    {
        var result = LogSanitiser.Sanitise("injected\r\nfake log entry");
        Assert.That(result, Is.EqualTo("injected" + "fake log entry"));
        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Not.Contain("\n"));
    }

    [Test]
    public void Sanitise_StringWithMultipleNewlines_RemovesAllAsync()
    {
        var result = LogSanitiser.Sanitise("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1line2line3"));
    }

    [Test]
    public void Sanitise_SearchTermWithNewline_RemovesNewlineAsync()
    {
        // Simulates a malicious search query designed to inject a fake log entry via newline
        var maliciousInput = "normal search\nINFO: User admin logged in successfully";
        var result = LogSanitiser.Sanitise(maliciousInput);
        Assert.That(result, Does.Not.Contain("\n"));
        Assert.That(result, Is.EqualTo("normal searchINFO: User admin logged in successfully"));
    }

    [Test]
    public void Sanitise_PathWithNewline_SanitisesCorrectlyAsync()
    {
        var result = LogSanitiser.Sanitise("/some/path\n/injected/path");
        Assert.That(result, Does.Not.Contain("\n"));
    }

    #endregion
}
