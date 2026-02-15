namespace JIM.Models.Exceptions;

/// <summary>
/// Represents an exception to the Metaverse Object Matching process.
/// </summary>
public class MultipleMatchesException : OperationalException
{
    public MultipleMatchesException(string message, List<Guid> matches) : base(message)
    {
        Matches = matches;
    }

    /// <summary>
    /// Contains the unique identifiers for the matches found.
    /// </summary>
    public List<Guid> Matches { get; set; }
}