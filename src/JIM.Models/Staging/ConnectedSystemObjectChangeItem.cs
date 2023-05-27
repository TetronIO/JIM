namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectChangeItem
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The parent item for this connected system change item
        /// </summary>
        public ConnectedSystemObjectChange ConnectedSystemChange { get; set; }

        public string? StringValueBefore { get; set; }

        public DateTime? DateTimeValueBefore { get; set; }

        public int? IntValueBefore { get; set; }

        public int? ByteLengthValueBefore { get; set; }

        public Guid? GuidValueBefore { get; set; }

        public bool? BoolValueBefore { get; set; }

        public string? StringValueAfter { get; set; }

        public DateTime? DateTimeValueAfter { get; set; }

        public int? IntValueAfter { get; set; }

        public int? ByteLengthValueAfter { get; set; }

        public Guid? GuidValueAfter { get; set; }

        public bool? BoolValueAfter { get; set; }
    }
}
