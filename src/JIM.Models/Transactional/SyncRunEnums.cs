namespace JIM.Models.Transactional
{
    public enum SyncRunType
    {
        FullImport = 0,
        DeltaImport = 1,
        FullSynchronisation = 2,
        DeltaSynchronisation = 3,
        Export = 4
    }

    public enum SyncRunItemResult
    {
        Success = 0,
        Error = 1
    }
}