using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemImportObjectAttribute
    {
        public string Name { get; set; } = null!;

        public AttributeDataType Type { get; set; }

        public List<string> StringValues { get; set; } = new List<string>();

        /// <summary>
        /// References from connected systems are handled as strings. 
        /// JIM will then resolve them into hard references to other Connected System Objects as part of an Import operation.
        /// </summary>
        public List<string> ReferenceValues { get; set; } = new List<string>();

        public List<int> IntValues { get; set; } = new List<int>();

        public List<DateTime> DateTimeValues { get; set; } = new List<DateTime>();

        public List<Guid> GuidValues { get; set; } = new List<Guid>();

        public List<byte[]> ByteValues { get; set; } = new List<byte[]>();

        public bool? BoolValue { get; set; }   
    }
}
