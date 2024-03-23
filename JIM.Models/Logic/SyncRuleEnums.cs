namespace JIM.Models.Logic
{
    public enum SyncRuleDirection
    {
        NotSet = 0,
        Import = 1,
        Export = 2
    }

    public enum SyncRuleMappingType
    {
        NotSet = 0,
        AttributeFlow = 1,
        ObjectMatching = 2
    }

    /// <summary>
    /// Used to provide some context to the user on what type of sources configuration has been used in a sync rule mapping.
    /// </summary>
    public enum SyncRuleMappingSourcesType
    {
        NotSet = 0,
        AttributeMapping = 1,
        FunctionMapping = 2,
        ExpressionMapping = 3,
        AdvancedMapping = 4
    }
}
