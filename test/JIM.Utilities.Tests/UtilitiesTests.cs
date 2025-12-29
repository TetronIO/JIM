using JIM.Utilities;

namespace JIM.Utilities.Tests;

public class UtilitiesTests
{
    #region SplitOnCapitalLetters Tests

    [Test]
    public void SplitOnCapitalLetters_WithPascalCase_SplitsCorrectly()
    {
        // Arrange
        var input = "PersonObject";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("Person Object"));
    }

    [Test]
    public void SplitOnCapitalLetters_WithMultiplePascalCaseWords_SplitsCorrectly()
    {
        // Arrange
        var input = "ConnectedSystemObject";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("Connected System Object"));
    }

    [Test]
    public void SplitOnCapitalLetters_WithLowercaseString_ReturnsOriginal()
    {
        // Arrange
        var input = "person";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("person"));
    }

    [Test]
    public void SplitOnCapitalLetters_WithEmptyString_ReturnsEmpty()
    {
        // Arrange
        var input = "";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void SplitOnCapitalLetters_WithNull_ReturnsNull()
    {
        // Arrange
        string? input = null;

        // Act
        #pragma warning disable CS8604 // Possible null reference argument - testing null handling
        var result = input.SplitOnCapitalLetters();
        #pragma warning restore CS8604

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void SplitOnCapitalLetters_WithSingleWord_ReturnsWord()
    {
        // Arrange
        var input = "Person";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("Person"));
    }

    [Test]
    public void SplitOnCapitalLetters_WithAllCaps_ReturnsOriginal()
    {
        // Arrange
        var input = "PERSON";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("PERSON"));
    }

    [Test]
    public void SplitOnCapitalLetters_WithMixedCase_ExtractsPascalCaseWords()
    {
        // Arrange
        var input = "PersonObjectTYPE";

        // Act
        var result = input.SplitOnCapitalLetters();

        // Assert
        Assert.That(result, Is.EqualTo("Person Object"));
    }

    #endregion

    #region AreByteArraysTheSame Tests - ReadOnlySpan overload

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithIdenticalArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3, 4, 5 };

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithDifferentArrays_ReturnsFalse()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3, 4, 6 };

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithDifferentLengths_ReturnsFalse()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3, 4 };

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithEmptyArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = Array.Empty<byte>();
        byte[] array2 = Array.Empty<byte>();

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithSingleByteArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = { 42 };
        byte[] array2 = { 42 };

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_ReadOnlySpan_WithLargeIdenticalArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = new byte[1000];
        byte[] array2 = new byte[1000];
        for (int i = 0; i < 1000; i++)
        {
            array1[i] = (byte)(i % 256);
            array2[i] = (byte)(i % 256);
        }

        // Act
        var result = Utilities.AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region AreByteArraysTheSame Tests - Nullable byte[] overload

    [Test]
    public void AreByteArraysTheSame_Nullable_WithIdenticalArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3, 4, 5 };

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithDifferentArrays_ReturnsFalse()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3, 4, 6 };

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithBothNull_ReturnsTrue()
    {
        // Arrange
        byte[]? array1 = null;
        byte[]? array2 = null;

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithFirstNull_ReturnsFalse()
    {
        // Arrange
        byte[]? array1 = null;
        byte[] array2 = { 1, 2, 3 };

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithSecondNull_ReturnsFalse()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3 };
        byte[]? array2 = null;

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithEmptyArrays_ReturnsTrue()
    {
        // Arrange
        byte[] array1 = Array.Empty<byte>();
        byte[] array2 = Array.Empty<byte>();

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreByteArraysTheSame_Nullable_WithDifferentLengths_ReturnsFalse()
    {
        // Arrange
        byte[] array1 = { 1, 2, 3, 4, 5 };
        byte[] array2 = { 1, 2, 3 };

        // Act
        var result = Utilities.AreByteArraysTheSame(array1, array2);

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion
}
