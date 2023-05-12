using JIM.Models.Core;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP
{
    internal class LdapConnectorSchema
    {
        private readonly LdapConnection _connection;
        private readonly ConnectorSchema _schema;
        private readonly string _root;

        internal LdapConnectorSchema(LdapConnection ldapConnection, string rootDistinguishedName) 
        {
            _connection = ldapConnection;
            _schema = new ConnectorSchema();
            _root = rootDistinguishedName;
        }

        internal async Task<ConnectorSchema> GetSchemaAsync()
        {
            // future improvement: work out how to get the default naming context programatically, so we don't have to ask the
            // user for the root via a setting value.

            return await Task.Run(() => { 
                // query: classes, structural, don't return hidden by default classes
                var filter = "(&(objectClass=classSchema)(objectClassCategory=1)(defaultHidingValue=FALSE))";
                var dn = $"CN=Schema,CN=Configuration,{_root}";
                var request = new SearchRequest(dn, filter, SearchScope.Subtree);
                var response = (SearchResponse)_connection.SendRequest(request);

                if (response.ResultCode != ResultCode.Success)
                    throw new Exception($"No success getting object types. Result code: {response.ResultCode}");

                if (response.Entries.Count == 0)
                    throw new Exception($"Couldn't get object types. Non returned from connected system. Result code: {response.ResultCode}");

                // enumerate each object class entry
                foreach (SearchResultEntry entry in response.Entries)
                {
                    var name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name");
                    if (name == null)
                        throw new Exception($"No name on object class entry: {entry.DistinguishedName}");

                    var objectType = new ConnectorSchemaObjectType(name);

                    // now go and work out which attributes the object type has and add them to the object type
                    if (AddObjectTypeAttributes(objectType))
                        _schema.ObjectTypes.Add(objectType);
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
            string objectClassName = objectType.Name;

            while (continueGettingClasses)
            {
                var objectClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={objectClassName})");
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
            var objectClassName = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "ldapdisplayname");
            if (objectClassName == null)
                throw new Exception($"No ldapdisplayname value on {objectClassEntry.DistinguishedName}");

            if (objectClassEntry.Attributes.Contains("maycontain"))
                foreach (string attributeName in objectClassEntry.Attributes["maycontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, false));

            if (objectClassEntry.Attributes.Contains("mustcontain"))
                foreach (string attributeName in objectClassEntry.Attributes["mustcontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, true));

            if (objectClassEntry.Attributes.Contains("systemmaycontain"))
                foreach (string attributeName in objectClassEntry.Attributes["systemmaycontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, false));

            if (objectClassEntry.Attributes.Contains("systemmustcontain"))
                foreach (string attributeName in objectClassEntry.Attributes["systemmustcontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, true));

            // now recurse into any auxliary and system auxiliary classes
            var auxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "auxiliaryclass");
            var systemAuxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "systemauxiliaryclass");

            if (auxiliaryClasses != null)
            {
                foreach (var auxiliaryClass in auxiliaryClasses)
                {
                    var auxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={auxiliaryClass})");
                    if (auxiliaryClassEntry == null)
                        throw new Exception($"Couldn't find auxiliary class entry: {auxiliaryClass}");

                    GetObjectClassAttributesRecursively(auxiliaryClassEntry, objectType);
                }
            }

            if (systemAuxiliaryClasses != null)
            {
                foreach (var systemAuxiliaryClass in systemAuxiliaryClasses)
                {
                    var systemAuxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={systemAuxiliaryClass})");
                    if (systemAuxiliaryClassEntry == null)
                        throw new Exception($"Couldn't find auxiliary class entry: {systemAuxiliaryClass}");

                    GetObjectClassAttributesRecursively(systemAuxiliaryClassEntry, objectType);
                }
            }
        }

        private ConnectorSchemaAttribute GetSchemaAttribute(string attributeName, string objectClass, bool required)
        {
            var attributeEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={attributeName})");
            if (attributeEntry == null)
                throw new Exception($"Couldn't retrieve schema attribute: {attributeName}");

            var description = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "description");
            var admindescription = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "admindescription");
            var isSingleValued = LdapConnectorUtilities.GetEntryAttributeBooleanValue(attributeEntry, "issinglevalued");
            var attributePlurality = (isSingleValued == true || isSingleValued == null) ? AttributePlurality.SingleValued : AttributePlurality.MultiValued;
            var attribute = new ConnectorSchemaAttribute(attributeName, AttributeDataType.String, attributePlurality, required, objectClass);

            if (!string.IsNullOrEmpty(description))
                attribute.Description = description;
            else if (!string.IsNullOrEmpty(admindescription))
                attribute.Description = admindescription;

            return attribute;
        }
    }
}
