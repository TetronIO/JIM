﻿using JIM.Models.Staging;
using JIM.Models.Transactional;
namespace JIM.Models.Interfaces;

public interface IConnectorExportUsingCalls
{
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings);

    public void Export(IList<PendingExport> pendingExports);

    public void CloseExportConnection();
}