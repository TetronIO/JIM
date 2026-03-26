using JIM.Models.Core;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Parses RFC 4512 schema description strings from LDAP subschema subentry attributes.
/// These are the <c>objectClasses</c> and <c>attributeTypes</c> operational attributes
/// returned by querying the subschema subentry (typically <c>cn=Subschema</c>).
/// </summary>
/// <remarks>
/// RFC 4512 § 4.1.1 (Object Class Description):
/// <code>
/// ( OID NAME 'name' [DESC 'description'] [SUP superior] [ABSTRACT|STRUCTURAL|AUXILIARY] [MUST (...)] [MAY (...)] )
/// </code>
/// RFC 4512 § 4.1.2 (Attribute Type Description):
/// <code>
/// ( OID NAME 'name' [DESC 'description'] [SUP superior] [SYNTAX oid] [SINGLE-VALUE] [NO-USER-MODIFICATION] [USAGE usage] )
/// </code>
/// </remarks>
internal static class Rfc4512SchemaParser
{
    /// <summary>
    /// Parses an RFC 4512 objectClass description string into a structured representation.
    /// </summary>
    internal static Rfc4512ObjectClassDescription? ParseObjectClassDescription(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return null;

        var tokens = Tokenise(definition);
        if (tokens.Count == 0)
            return null;

        var result = new Rfc4512ObjectClassDescription();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            switch (token)
            {
                case "NAME":
                    result.Name = ReadNameValue(tokens, ref i);
                    break;
                case "DESC":
                    result.Description = ReadQuotedString(tokens, ref i);
                    break;
                case "SUP":
                    result.SuperiorName = ReadSingleValue(tokens, ref i);
                    break;
                case "ABSTRACT":
                    result.Kind = Rfc4512ObjectClassKind.Abstract;
                    break;
                case "STRUCTURAL":
                    result.Kind = Rfc4512ObjectClassKind.Structural;
                    break;
                case "AUXILIARY":
                    result.Kind = Rfc4512ObjectClassKind.Auxiliary;
                    break;
                case "MUST":
                    result.MustAttributes = ReadOidList(tokens, ref i);
                    break;
                case "MAY":
                    result.MayAttributes = ReadOidList(tokens, ref i);
                    break;
            }
        }

        return result.Name != null ? result : null;
    }

    /// <summary>
    /// Parses an RFC 4512 attributeType description string into a structured representation.
    /// </summary>
    internal static Rfc4512AttributeTypeDescription? ParseAttributeTypeDescription(string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return null;

        var tokens = Tokenise(definition);
        if (tokens.Count == 0)
            return null;

        var result = new Rfc4512AttributeTypeDescription();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            switch (token)
            {
                case "NAME":
                    result.Name = ReadNameValue(tokens, ref i);
                    break;
                case "DESC":
                    result.Description = ReadQuotedString(tokens, ref i);
                    break;
                case "SUP":
                    result.SuperiorName = ReadSingleValue(tokens, ref i);
                    break;
                case "SYNTAX":
                    result.SyntaxOid = ReadSyntaxOid(tokens, ref i);
                    break;
                case "SINGLE-VALUE":
                    result.IsSingleValued = true;
                    break;
                case "NO-USER-MODIFICATION":
                    result.IsNoUserModification = true;
                    break;
                case "USAGE":
                    result.Usage = ReadUsageValue(tokens, ref i);
                    break;
            }
        }

        return result.Name != null ? result : null;
    }

    /// <summary>
    /// Maps an RFC 4517 SYNTAX OID to JIM's <see cref="AttributeDataType"/>.
    /// Returns <see cref="AttributeDataType.Text"/> for unknown or null OIDs as a safe default.
    /// </summary>
    internal static AttributeDataType GetRfcAttributeDataType(string? syntaxOid)
    {
        if (syntaxOid == null)
            return AttributeDataType.Text;

        return syntaxOid switch
        {
            // Boolean (RFC 4517 § 3.3.3)
            "1.3.6.1.4.1.1466.115.121.1.7" => AttributeDataType.Boolean,

            // Integer (RFC 4517 § 3.3.16)
            "1.3.6.1.4.1.1466.115.121.1.27" => AttributeDataType.Number,

            // Generalised Time (RFC 4517 § 3.3.13)
            "1.3.6.1.4.1.1466.115.121.1.24" => AttributeDataType.DateTime,

            // UTC Time
            "1.3.6.1.4.1.1466.115.121.1.53" => AttributeDataType.DateTime,

            // Distinguished Name (RFC 4517 § 3.3.9) — reference to another LDAP object
            "1.3.6.1.4.1.1466.115.121.1.12" => AttributeDataType.Reference,

            // Name and Optional UID (RFC 4517 § 3.3.21) — DN with optional unique ID
            "1.3.6.1.4.1.1466.115.121.1.34" => AttributeDataType.Reference,

            // Octet String (RFC 4517 § 3.3.25)
            "1.3.6.1.4.1.1466.115.121.1.40" => AttributeDataType.Binary,

            // JPEG (RFC 4517 § 3.3.17)
            "1.3.6.1.4.1.1466.115.121.1.28" => AttributeDataType.Binary,

            // Certificate (RFC 4523)
            "1.3.6.1.4.1.1466.115.121.1.8" => AttributeDataType.Binary,

            // Certificate List (CRL)
            "1.3.6.1.4.1.1466.115.121.1.9" => AttributeDataType.Binary,

            // Certificate Pair
            "1.3.6.1.4.1.1466.115.121.1.10" => AttributeDataType.Binary,

            // All text-like syntaxes
            "1.3.6.1.4.1.1466.115.121.1.15" => AttributeDataType.Text, // Directory String
            "1.3.6.1.4.1.1466.115.121.1.26" => AttributeDataType.Text, // IA5 String
            "1.3.6.1.4.1.1466.115.121.1.44" => AttributeDataType.Text, // Printable String
            "1.3.6.1.4.1.1466.115.121.1.36" => AttributeDataType.Text, // Numeric String
            "1.3.6.1.4.1.1466.115.121.1.38" => AttributeDataType.Text, // OID
            "1.3.6.1.4.1.1466.115.121.1.50" => AttributeDataType.Text, // Telephone Number
            "1.3.6.1.4.1.1466.115.121.1.11" => AttributeDataType.Text, // Country String
            "1.3.6.1.4.1.1466.115.121.1.6"  => AttributeDataType.Text, // Bit String
            "1.3.6.1.4.1.1466.115.121.1.41" => AttributeDataType.Text, // Postal Address
            "1.3.6.1.4.1.1466.115.121.1.22" => AttributeDataType.Text, // Facsimile Telephone Number
            "1.3.6.1.4.1.1466.115.121.1.52" => AttributeDataType.Text, // Telex Number
            "1.3.6.1.4.1.1466.115.121.1.14" => AttributeDataType.Text, // Delivery Method
            "1.3.6.1.4.1.1466.115.121.1.39" => AttributeDataType.Text, // Other Mailbox
            "1.3.6.1.1.16.1"                => AttributeDataType.Text, // UUID (RFC 4530) — entryUUID

            // Unknown — default to Text as a safe fallback
            _ => AttributeDataType.Text
        };
    }

    /// <summary>
    /// Determines attribute writability from RFC 4512 schema metadata.
    /// Operational attributes (non-userApplications USAGE) and NO-USER-MODIFICATION attributes are read-only.
    /// </summary>
    internal static AttributeWritability DetermineRfcAttributeWritability(
        Rfc4512AttributeUsage usage, bool isNoUserModification)
    {
        if (isNoUserModification)
            return AttributeWritability.ReadOnly;

        if (usage != Rfc4512AttributeUsage.UserApplications)
            return AttributeWritability.ReadOnly;

        return AttributeWritability.Writable;
    }

    // -----------------------------------------------------------------------
    // Tokeniser and token readers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Splits an RFC 4512 description string into tokens, handling parenthesised groups
    /// and quoted strings as atomic units.
    /// </summary>
    private static List<string> Tokenise(string definition)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < definition.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(definition[i]))
            {
                i++;
                continue;
            }

            // Quoted string — collect everything between single quotes as one token
            if (definition[i] == '\'')
            {
                var end = definition.IndexOf('\'', i + 1);
                if (end == -1) break;
                tokens.Add(definition[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            // Parentheses are their own tokens
            if (definition[i] == '(' || definition[i] == ')')
            {
                tokens.Add(definition[i].ToString());
                i++;
                continue;
            }

            // Dollar sign separator in attribute lists
            if (definition[i] == '$')
            {
                tokens.Add("$");
                i++;
                continue;
            }

            // Unquoted word — collect until whitespace, paren, quote, or dollar
            var start = i;
            while (i < definition.Length && !char.IsWhiteSpace(definition[i]) &&
                   definition[i] != '(' && definition[i] != ')' &&
                   definition[i] != '\'' && definition[i] != '$')
            {
                i++;
            }

            if (i > start)
                tokens.Add(definition[start..i]);
        }

        return tokens;
    }

    /// <summary>
    /// Reads a NAME value which can be either a single quoted string or a parenthesised list.
    /// Returns the first name when multiple names are given.
    /// </summary>
    private static string? ReadNameValue(List<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count)
            return null;

        // Check if next token is a parenthesised list: NAME ( 'name1' 'name2' )
        if (tokens[i + 1] == "(")
        {
            i += 2; // skip "(" token
            string? firstName = null;
            while (i < tokens.Count && tokens[i] != ")")
            {
                if (tokens[i] != "$" && tokens[i] != "(")
                    firstName ??= tokens[i];
                i++;
            }
            return firstName;
        }

        // Single name: NAME 'name'
        i++;
        return tokens[i];
    }

    /// <summary>
    /// Reads the next quoted string token after a keyword like DESC.
    /// The tokeniser has already stripped the quotes.
    /// </summary>
    private static string? ReadQuotedString(List<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count)
            return null;

        // DESC values may span multiple tokens if the tokeniser split on spaces within quotes.
        // But our tokeniser preserves quoted strings as a single token, so just read the next one.
        // However, the DESC value might contain spaces and be enclosed in a single pair of quotes
        // that our tokeniser already handled. The token after DESC is the unquoted content.

        // Actually — the tokeniser collects everything between ' and ' as one token.
        // But DESC 'foo bar' means the content between quotes is "foo bar" — one token.
        // However if the description contains an apostrophe... that's an edge case we handle
        // by just reading the next token.
        i++;
        return tokens[i];
    }

    /// <summary>
    /// Reads a single unquoted value after a keyword like SUP.
    /// </summary>
    private static string? ReadSingleValue(List<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count)
            return null;

        i++;
        return tokens[i];
    }

    /// <summary>
    /// Reads a SYNTAX OID value, stripping any length constraint suffix (e.g., {64}).
    /// </summary>
    private static string? ReadSyntaxOid(List<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count)
            return null;

        i++;
        var oid = tokens[i];

        // Strip length constraint: "1.3.6.1.4.1.1466.115.121.1.15{64}" → "1.3.6.1.4.1.1466.115.121.1.15"
        var braceIndex = oid.IndexOf('{');
        if (braceIndex > 0)
            oid = oid[..braceIndex];

        return oid;
    }

    /// <summary>
    /// Reads a USAGE value (one of: userApplications, directoryOperation, distributedOperation, dSAOperation).
    /// </summary>
    private static Rfc4512AttributeUsage ReadUsageValue(List<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count)
            return Rfc4512AttributeUsage.UserApplications;

        i++;
        return tokens[i] switch
        {
            "directoryOperation" => Rfc4512AttributeUsage.DirectoryOperation,
            "distributedOperation" => Rfc4512AttributeUsage.DistributedOperation,
            "dSAOperation" => Rfc4512AttributeUsage.DsaOperation,
            _ => Rfc4512AttributeUsage.UserApplications
        };
    }

    /// <summary>
    /// Reads an OID list which can be either a single name or a parenthesised $-separated list.
    /// Used for MUST and MAY attribute lists.
    /// </summary>
    private static List<string> ReadOidList(List<string> tokens, ref int i)
    {
        var result = new List<string>();
        if (i + 1 >= tokens.Count)
            return result;

        // Check if next token starts a parenthesised list
        if (tokens[i + 1] == "(")
        {
            i += 2; // skip "("
            while (i < tokens.Count && tokens[i] != ")")
            {
                if (tokens[i] != "$")
                    result.Add(tokens[i]);
                i++;
            }
        }
        else
        {
            // Single attribute name
            i++;
            result.Add(tokens[i]);
        }

        return result;
    }
}

/// <summary>
/// Parsed representation of an RFC 4512 objectClass description.
/// </summary>
internal class Rfc4512ObjectClassDescription
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SuperiorName { get; set; }
    public Rfc4512ObjectClassKind Kind { get; set; } = Rfc4512ObjectClassKind.Structural;
    public List<string> MustAttributes { get; set; } = new();
    public List<string> MayAttributes { get; set; } = new();
}

/// <summary>
/// Parsed representation of an RFC 4512 attributeType description.
/// </summary>
internal class Rfc4512AttributeTypeDescription
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SuperiorName { get; set; }
    public string? SyntaxOid { get; set; }
    public bool IsSingleValued { get; set; }
    public bool IsNoUserModification { get; set; }
    public Rfc4512AttributeUsage Usage { get; set; } = Rfc4512AttributeUsage.UserApplications;
}

/// <summary>
/// RFC 4512 object class kinds.
/// </summary>
internal enum Rfc4512ObjectClassKind
{
    Abstract,
    Structural,
    Auxiliary
}

/// <summary>
/// RFC 4512 attribute usage types, determining whether an attribute is user-modifiable or operational.
/// </summary>
internal enum Rfc4512AttributeUsage
{
    /// <summary>User data attribute — writable by clients.</summary>
    UserApplications,
    /// <summary>Operational attribute managed by the directory server.</summary>
    DirectoryOperation,
    /// <summary>Operational attribute shared across DSAs (e.g., subschemaSubentry).</summary>
    DistributedOperation,
    /// <summary>Operational attribute local to a single DSA (e.g., createTimestamp).</summary>
    DsaOperation
}
