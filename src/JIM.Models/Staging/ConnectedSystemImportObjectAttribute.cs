using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemImportObjectAttribute
    {
        public string Name { get; set; }

        public AttributeDataType Type { get; set; }

        public List<string> StringValues { get; set; } = new List<string>();

        public List<int> IntValues { get; set; } = new List<int>();

        public List<DateTime> DateTimeValues { get; set; } = new List<DateTime>();

        public List<Guid> GuidValues { get; set; } = new List<Guid>();

        public List<byte[]> ByteValues { get; set; } = new List<byte[]>();

        public bool? BoolValue { get; set; }

        // todo: what are we going to do about about references?
        // probably - they need to be imported as unresolved_references and then resolved to references to other import objects once the import is done
    }
}
