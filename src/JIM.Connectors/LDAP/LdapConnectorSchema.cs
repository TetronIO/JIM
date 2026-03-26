using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorSchema
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private readonly LdapConnectorRootDse _rootDse;
    private readonly bool _includeAuxiliaryClasses;
    private readonly ConnectorSchema _schema;
    private string _schemaNamingContext = null!;

    internal LdapConnectorSchema(LdapConnection ldapConnection, ILogger logger, LdapConnectorRootDse rootDse, bool includeAuxiliaryClasses = false)
    {
        _connection = ldapConnection;
        _logger = logger;
        _rootDse = rootDse;
        _includeAuxiliaryClasses = includeAuxiliaryClasses;
        _schema = new ConnectorSchema();
    }

    internal async Task<ConnectorSchema> GetSchemaAsync()
    {
        return _rootDse.DirectoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD
            ? await GetActiveDirectorySchemaAsync()
            : await GetRfcSchemaAsync();
    }

    // -----------------------------------------------------------------------
    // Active Directory schema discovery (classSchema / attributeSchema)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Discovers schema using the AD-specific classSchema/attributeSchema partition.
    /// Works for both Microsoft AD and Samba AD.
    /// </summary>
    private async Task<ConnectorSchema> GetActiveDirectorySchemaAsync()
    {
        return await Task.Run(() =>
        {
            // get the DN for the schema partition
            var schemaNamingContext = GetSchemaNamingContext();
            if (string.IsNullOrEmpty(schemaNamingContext))
                throw new Exception($"Couldn't get schema naming context from rootDSE.");
            _schemaNamingContext = schemaNamingContext;

            // query: classes, structural (and optionally auxiliary), don't return hidden by default classes, exclude defunct classes
            // objectClassCategory: 1 = structural, 3 = auxiliary
            var classFilter = _includeAuxiliaryClasses
                ? "(|(objectClassCategory=1)(objectClassCategory=3))"
                : "(objectClassCategory=1)";
            var filter = $"(&(objectClass=classSchema){classFilter}(defaultHidingValue=FALSE)(!(isDefunct=TRUE)))";
            var request = new SearchRequest(_schemaNamingContext, filter, SearchScope.Subtree);
            var response = (SearchResponse)_connection.SendRequest(request);

            if (response.ResultCode != ResultCode.Success)
                throw new Exception($"No success getting object types. Result code: {response.ResultCode}");

            if (response.Entries.Count == 0)
                throw new Exception($"Couldn't get object types. Non returned from connected system. Result code: {response.ResultCode}");

            // enumerate each object class entry
            foreach (SearchResultEntry entry in response.Entries)
            {
                var name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name") ?? throw new Exception($"No name on object class entry: {entry.DistinguishedName}");
                var objectType = new ConnectorSchemaObjectType(name);

                // now go and work out which attributes the object type has and add them to the object type
                if (AddObjectTypeAttributes(objectType))
                {
                    ApplyExternalIdRecommendations(objectType);
                    _schema.ObjectTypes.Add(objectType);
                }
            }

            return _schema;
        });
    }

    private bool AddObjectTypeAttributes(ConnectorSchemaObjectType objectType)
    {
        // walk up the parent object class tree
        var objectClassEntries = new List<SearchResultEntry>();
        var continueGettingClasses = true;
        string? ldapdisplayname;
        string? subclassof;
        var objectClassName = objectType.Name;

        while (continueGettingClasses)
        {
            var objectClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={objectClassName})");
            if (objectClassEntry == null)
            {
                // some object classes do not have a schema entry, i.e. some system objects.
                return false;
            }

            ldapdisplayname = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "ldapdisplayname");
            subclassof = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "subclassof");

            if (ldapdisplayname == subclassof)
                continueGettingClasses = false;

            objectClassEntries.Add(objectClassEntry);
            if (subclassof != null)
                objectClassName = subclassof;
        }

        foreach (var objectClassEntry in objectClassEntries)
            GetObjectClassAttributesRecursively(objectClassEntry, objectType);

        // todo: it's possible there's some duplication of attributes going on due to attributes being specified on structural and auxiliary classes, so de-dupe before we return
        objectType.Attributes = objectType.Attributes.OrderBy(q => q.Name).ToList();

        return true;
    }

    /// <summary>
    /// Adds all attributes on a supplied object class entry and then recurses into any auxiliary and system auxiliary references.
    /// </summary>
    private void GetObjectClassAttributesRecursively(SearchResultEntry objectClassEntry, ConnectorSchemaObjectType objectType)
    {
        var objectClassName = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "ldapdisplayname") ??
                              throw new Exception($"No ldapdisplayname value on {objectClassEntry.DistinguishedName}");

        AddAttributesFromSchemaProperty(objectClassEntry, "maycontain", objectClassName, false, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "mustcontain", objectClassName, true, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "systemmaycontain", objectClassName, false, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "systemmustcontain", objectClassName, true, objectType);

        // now recurse into any auxiliary and system auxiliary classes
        var auxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "auxiliaryclass");
        var systemAuxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "systemauxiliaryclass");

        if (auxiliaryClasses != null)
        {
            foreach (var auxiliaryClass in auxiliaryClasses)
            {
                var auxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={auxiliaryClass})") ??
                                          throw new Exception($"Couldn't find auxiliary class entry: {auxiliaryClass}");

                GetObjectClassAttributesRecursively(auxiliaryClassEntry, objectType);
            }
        }

        if (systemAuxiliaryClasses == null)
            return;

        foreach (var systemAuxiliaryClass in systemAuxiliaryClasses)
        {
            var systemAuxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={systemAuxiliaryClass})") ??
                                            throw new Exception($"Couldn't find auxiliary class entry: {systemAuxiliaryClass}");

            GetObjectClassAttributesRecursively(systemAuxiliaryClassEntry, objectType);
        }
    }

    private void AddAttributesFromSchemaProperty(SearchResultEntry objectClassEntry, string propertyName, string objectClassName, bool required, ConnectorSchemaObjectType objectType)
    {
        if (!objectClassEntry.Attributes.Contains(propertyName))
            return;

        foreach (string attributeName in objectClassEntry.Attributes[propertyName].GetValues(typeof(string)))
        {
            if (objectType.Attributes.All(q => q.Name != attributeName))
            {
                var attr = GetAdSchemaAttribute(attributeName, objectClassName, required, objectType.Name);
                if (attr != null)
                    objectType.Attributes.Add(attr);
            }
        }
    }

    /// <summary>
    /// Looks up the AD schema entry for an attribute and returns a ConnectorSchemaAttribute with full metadata.
    /// Returns null if the attribute is defunct (should be excluded from the schema entirely).
    /// </summary>
    private ConnectorSchemaAttribute? GetAdSchemaAttribute(string attributeName, string objectClass, bool required, string objectTypeName)
    {
        var attributeEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={attributeName})") ??
                             throw new Exception($"Couldn't retrieve schema attribute: {attributeName}");

        // filter out defunct attributes — these are deprecated and should not be presented to users
        var isDefunctRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "isdefunct");
        if (bool.TryParse(isDefunctRawValue, out var isDefunct) && isDefunct)
        {
            _logger.Debug("GetAdSchemaAttribute: Skipping defunct attribute '{AttributeName}'.", attributeName);
            return null;
        }

        var description = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "description");
        var adminDescription = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "admindescription");

        // isSingleValued comes back as TRUE/FALSE string
        // if we can't convert to a bool, assume it's single-valued
        var isSingleValuedRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "issinglevalued");
        if (!bool.TryParse(isSingleValuedRawValue, out var isSingleValued))
        {
            isSingleValued = true;
            _logger.Verbose("GetAdSchemaAttribute: Could not establish if SVA/MVA for attribute {AttributeName}. Assuming SVA. Raw value: '{RawValue}'", attributeName, isSingleValuedRawValue);
        }

        var attributePlurality = isSingleValued ? AttributePlurality.SingleValued : AttributePlurality.MultiValued;

        // Active Directory SAM layer override: certain attributes are declared as multi-valued in the
        // LDAP schema but the SAM layer enforces single-valued semantics on security principals.
        // Override the plurality to match actual runtime behaviour so mapping validation works correctly.
        if (!isSingleValued && LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(attributeName, objectTypeName, _rootDse.DirectoryType))
        {
            attributePlurality = AttributePlurality.SingleValued;
            _logger.Debug("GetAdSchemaAttribute: Overriding '{AttributeName}' from multi-valued to single-valued on object type '{ObjectType}' — " +
                "Active Directory SAM layer enforces single-valued semantics on this attribute for security principals.",
                attributeName, objectTypeName);
        }

        // work out what data-type the attribute is using the shared utility method
        var omSyntax = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "omsyntax");
        var attributeDataType = AttributeDataType.Text;

        if (omSyntax.HasValue)
        {
            try
            {
                attributeDataType = LdapConnectorUtilities.GetLdapAttributeDataType(omSyntax.Value);
            }
            catch (InvalidDataException)
            {
                // Unsupported omSyntax - default to Text
                _logger.Warning("GetAdSchemaAttribute: Unsupported omSyntax {OmSyntax} for attribute {AttributeName}. Defaulting to Text.", omSyntax.Value, attributeName);
            }
        }

        // Override the data type for the external ID attribute based on directory type.
        // AD's objectGUID is declared as octet string in the schema but actually returns a binary GUID.
        // OpenLDAP's entryUUID is a string-formatted UUID (RFC 4530).
        if (attributeName.Equals(_rootDse.ExternalIdAttributeName, StringComparison.OrdinalIgnoreCase))
            attributeDataType = _rootDse.ExternalIdDataType;

        // determine writability from schema metadata: systemOnly, systemFlags (constructed bit), and linkID (back-links)
        var systemOnlyRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "systemonly");
        bool.TryParse(systemOnlyRawValue, out var systemOnly);
        var systemFlags = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "systemflags");
        var linkId = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "linkid");
        var writability = LdapConnectorUtilities.DetermineAttributeWritability(systemOnly, systemFlags, linkId);

        var attribute = new ConnectorSchemaAttribute(attributeName, attributeDataType, attributePlurality, required, objectClass, writability);

        if (!string.IsNullOrEmpty(description))
            attribute.Description = description;
        else if (!string.IsNullOrEmpty(adminDescription))
            attribute.Description = adminDescription;

        return attribute;
    }

    private string? GetSchemaNamingContext()
    {
        // get the schema naming context from an attribute on the rootDSE
        var request = new SearchRequest() { Scope = SearchScope.Base };
        request.Attributes.Add("schemaNamingContext");
        var response = (SearchResponse)_connection.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            _logger.Warning("GetSchemaNamingContext: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("GetSchemaNamingContext: Didn't get any results!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "schemaNamingContext");
    }

    // -----------------------------------------------------------------------
    // RFC 4512 schema discovery (subschema subentry)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Discovers schema using the RFC 4512 subschema subentry mechanism.
    /// Works for OpenLDAP, 389 Directory Server, and other standards-compliant directories.
    /// Queries the subschemaSubentry for objectClasses and attributeTypes operational attributes
    /// and parses the RFC 4512 description strings.
    /// </summary>
    private async Task<ConnectorSchema> GetRfcSchemaAsync()
    {
        return await Task.Run(() =>
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Step 1: Get the subschema subentry DN from rootDSE
            var subschemaDn = GetSubschemaSubentryDn();
            if (string.IsNullOrEmpty(subschemaDn))
                throw new Exception("Couldn't get subschemaSubentry DN from rootDSE. Schema discovery requires a subschema subentry.");

            _logger.Debug("GetRfcSchemaAsync: Querying subschema subentry at '{SubschemaDn}'", subschemaDn);

            // Step 2: Query the subschema subentry for objectClasses and attributeTypes
            var request = new SearchRequest(subschemaDn, "(objectClass=subschema)", SearchScope.Base);
            request.Attributes.AddRange(["objectClasses", "attributeTypes"]);
            var response = (SearchResponse)_connection.SendRequest(request);

            if (response.ResultCode != ResultCode.Success || response.Entries.Count == 0)
                throw new Exception($"Failed to query subschema subentry at '{subschemaDn}'. Result code: {response.ResultCode}");

            var subschemaEntry = response.Entries[0];

            // Step 3: Parse all attribute type definitions into a lookup dictionary
            var attributeTypeDefs = ParseAllAttributeTypes(subschemaEntry);
            _logger.Debug("GetRfcSchemaAsync: Parsed {Count} attribute type definitions", attributeTypeDefs.Count);

            // Step 4: Parse all object class definitions
            var objectClassDefs = ParseAllObjectClasses(subschemaEntry);
            _logger.Debug("GetRfcSchemaAsync: Parsed {Count} object class definitions", objectClassDefs.Count);

            // Step 5: Build the connector schema from structural (and optionally auxiliary) object classes
            foreach (var objectClassDef in objectClassDefs.Values)
            {
                // Only expose structural classes by default — these are the ones that can have objects instantiated.
                // When _includeAuxiliaryClasses is enabled, also include auxiliary classes for directories
                // where objects may use auxiliary classes as their primary structural class.
                var isStructural = objectClassDef.Kind == Rfc4512ObjectClassKind.Structural;
                var isAuxiliary = objectClassDef.Kind == Rfc4512ObjectClassKind.Auxiliary;
                if (!isStructural && !(isAuxiliary && _includeAuxiliaryClasses))
                    continue;

                var objectType = new ConnectorSchemaObjectType(objectClassDef.Name!);

                // Collect attributes by walking the class hierarchy (SUP chain)
                var allMust = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allMay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectClassAttributes(objectClassDef, objectClassDefs, allMust, allMay);

                // Build ConnectorSchemaAttribute for each attribute
                foreach (var attrName in allMust)
                {
                    var attr = BuildRfcSchemaAttribute(attrName, attributeTypeDefs, objectClassDef.Name!, required: true, objectType.Name);
                    if (attr != null && objectType.Attributes.All(a => !a.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        objectType.Attributes.Add(attr);
                }

                foreach (var attrName in allMay)
                {
                    // If the attribute is already in the MUST set, don't add it again as MAY
                    if (allMust.Contains(attrName))
                        continue;

                    var attr = BuildRfcSchemaAttribute(attrName, attributeTypeDefs, objectClassDef.Name!, required: false, objectType.Name);
                    if (attr != null && objectType.Attributes.All(a => !a.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        objectType.Attributes.Add(attr);
                }

                objectType.Attributes = objectType.Attributes.OrderBy(a => a.Name).ToList();

                ApplyExternalIdRecommendations(objectType);
                _schema.ObjectTypes.Add(objectType);
            }

            totalStopwatch.Stop();
            _logger.Information("GetRfcSchemaAsync: Completed — {ObjectTypeCount} object types discovered in {ElapsedMs}ms",
                _schema.ObjectTypes.Count, totalStopwatch.ElapsedMilliseconds);

            return _schema;
        });
    }

    /// <summary>
    /// Recursively collects MUST and MAY attributes from an object class and all its superiors.
    /// </summary>
    private static void CollectClassAttributes(
        Rfc4512ObjectClassDescription classDef,
        Dictionary<string, Rfc4512ObjectClassDescription> allClasses,
        HashSet<string> mustAttributes,
        HashSet<string> mayAttributes)
    {
        foreach (var attr in classDef.MustAttributes)
            mustAttributes.Add(attr);
        foreach (var attr in classDef.MayAttributes)
            mayAttributes.Add(attr);

        // Walk the superclass chain
        if (classDef.SuperiorName != null &&
            allClasses.TryGetValue(classDef.SuperiorName, out var superClass))
        {
            CollectClassAttributes(superClass, allClasses, mustAttributes, mayAttributes);
        }
    }

    /// <summary>
    /// Builds a ConnectorSchemaAttribute from an RFC 4512 attribute type definition.
    /// Resolves the SYNTAX OID (walking the SUP chain if needed) and maps to JIM's data types.
    /// </summary>
    private ConnectorSchemaAttribute? BuildRfcSchemaAttribute(
        string attributeName,
        Dictionary<string, Rfc4512AttributeTypeDescription> attributeTypeDefs,
        string objectClassName,
        bool required,
        string objectTypeName)
    {
        if (!attributeTypeDefs.TryGetValue(attributeName, out var attrDef))
        {
            _logger.Warning("GetRfcSchemaAsync: Attribute '{AttributeName}' referenced by object class '{ObjectClass}' " +
                "not found in schema. Skipping.", attributeName, objectClassName);
            return null;
        }

        // Resolve SYNTAX OID — walk the SUP chain if the attribute inherits its syntax
        var syntaxOid = ResolveSyntaxOid(attrDef, attributeTypeDefs);
        var attributeDataType = Rfc4512SchemaParser.GetRfcAttributeDataType(syntaxOid);

        // Override data type for external ID attribute
        if (attributeName.Equals(_rootDse.ExternalIdAttributeName, StringComparison.OrdinalIgnoreCase))
            attributeDataType = _rootDse.ExternalIdDataType;

        var plurality = attrDef.IsSingleValued ? AttributePlurality.SingleValued : AttributePlurality.MultiValued;
        var writability = Rfc4512SchemaParser.DetermineRfcAttributeWritability(attrDef.Usage, attrDef.IsNoUserModification);

        var attribute = new ConnectorSchemaAttribute(attributeName, attributeDataType, plurality, required, objectClassName, writability);

        if (!string.IsNullOrEmpty(attrDef.Description))
            attribute.Description = attrDef.Description;

        return attribute;
    }

    /// <summary>
    /// Resolves the SYNTAX OID for an attribute type, walking the SUP (superior) chain
    /// if the attribute inherits its syntax from a parent attribute type.
    /// </summary>
    private static string? ResolveSyntaxOid(
        Rfc4512AttributeTypeDescription attrDef,
        Dictionary<string, Rfc4512AttributeTypeDescription> allAttributes)
    {
        // Direct SYNTAX specified — use it
        if (attrDef.SyntaxOid != null)
            return attrDef.SyntaxOid;

        // Walk the SUP chain to find inherited SYNTAX (max 10 levels to prevent infinite loops)
        var current = attrDef;
        for (var depth = 0; depth < 10; depth++)
        {
            if (current.SuperiorName == null)
                break;

            if (!allAttributes.TryGetValue(current.SuperiorName, out var superAttr))
                break;

            if (superAttr.SyntaxOid != null)
                return superAttr.SyntaxOid;

            current = superAttr;
        }

        return null;
    }

    /// <summary>
    /// Parses all attributeTypes values from the subschema entry into a name-keyed dictionary.
    /// </summary>
    private Dictionary<string, Rfc4512AttributeTypeDescription> ParseAllAttributeTypes(SearchResultEntry subschemaEntry)
    {
        var result = new Dictionary<string, Rfc4512AttributeTypeDescription>(StringComparer.OrdinalIgnoreCase);

        if (!subschemaEntry.Attributes.Contains("attributeTypes"))
            return result;

        foreach (string definition in subschemaEntry.Attributes["attributeTypes"].GetValues(typeof(string)))
        {
            var parsed = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);
            if (parsed?.Name != null && !result.ContainsKey(parsed.Name))
                result[parsed.Name] = parsed;
        }

        return result;
    }

    /// <summary>
    /// Parses all objectClasses values from the subschema entry into a name-keyed dictionary.
    /// </summary>
    private Dictionary<string, Rfc4512ObjectClassDescription> ParseAllObjectClasses(SearchResultEntry subschemaEntry)
    {
        var result = new Dictionary<string, Rfc4512ObjectClassDescription>(StringComparer.OrdinalIgnoreCase);

        if (!subschemaEntry.Attributes.Contains("objectClasses"))
            return result;

        foreach (string definition in subschemaEntry.Attributes["objectClasses"].GetValues(typeof(string)))
        {
            var parsed = Rfc4512SchemaParser.ParseObjectClassDescription(definition);
            if (parsed?.Name != null && !result.ContainsKey(parsed.Name))
                result[parsed.Name] = parsed;
        }

        return result;
    }

    /// <summary>
    /// Gets the subschemaSubentry DN from the rootDSE (RFC 4512 § 5.1).
    /// </summary>
    private string? GetSubschemaSubentryDn()
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add("subschemaSubentry");
        var response = (SearchResponse)_connection.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            _logger.Warning("GetSubschemaSubentryDn: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("GetSubschemaSubentryDn: Didn't get any results from rootDSE!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "subschemaSubentry");
    }

    // -----------------------------------------------------------------------
    // Shared helpers — used by both AD and RFC paths
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies external ID and secondary external ID recommendations to an object type.
    /// Shared between AD and RFC schema discovery paths.
    /// </summary>
    private void ApplyExternalIdRecommendations(ConnectorSchemaObjectType objectType)
    {
        // make a recommendation on what unique identifier attribute(s) to use
        var externalIdAttrName = _rootDse.ExternalIdAttributeName;
        var externalIdSchemaAttribute = objectType.Attributes.SingleOrDefault(
            a => a.Name.Equals(externalIdAttrName, StringComparison.OrdinalIgnoreCase));

        if (externalIdSchemaAttribute != null)
        {
            objectType.RecommendedExternalIdAttribute = externalIdSchemaAttribute;
        }
        else
        {
            _logger.Warning("Schema discovery: external ID attribute '{ExternalIdAttr}' not found on object type '{ObjectType}'. " +
                "External ID recommendation will be unavailable — the administrator must select one manually.",
                externalIdAttrName, objectType.Name);
        }

        // say what the secondary external identifier needs to be for LDAP systems
        var dnSchemaAttribute = objectType.Attributes.SingleOrDefault(a => a.Name.Equals("distinguishedname", StringComparison.OrdinalIgnoreCase));
        if (dnSchemaAttribute != null)
        {
            objectType.RecommendedSecondaryExternalIdAttribute = dnSchemaAttribute;

            // override the data type for distinguishedName, we want to handle it as a string, not a reference type
            // we do this as a DN attribute on an object cannot be a reference to itself. that would make no sense.
            dnSchemaAttribute.Type = AttributeDataType.Text;

            // override writability for distinguishedName: AD marks it as systemOnly but the LDAP connector
            // needs it writable because the DN is provided by the client in Add requests to specify where
            // the object is created. It's not written as an attribute — it's the target of the LDAP Add operation.
            dnSchemaAttribute.Writability = AttributeWritability.Writable;
        }
    }
}
