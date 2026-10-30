// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// A configuration change was saved, but JIM could not record that the Connected System's next synchronisation must
/// process every object of the Connected System Object Type it affects (#2615). The change itself stands; what is missing
/// is the instruction that carries it to the objects that already exist. The save's Activity is failed with this message
/// rather than completed, because completing it would tell the administrator the change would take effect when it
/// will not. A Full Synchronisation of the Connected System processes every object of every type, so it applies the
/// change whatever was lost.
/// </summary>
public class ConfigurationChangeSynchronisationNotRecordedException : OperationalException
{
    /// <param name="connectedSystemName">The Connected System whose synchronisation must apply the change.</param>
    /// <param name="objectTypeName">The Connected System Object Type whose objects the change can affect.</param>
    /// <param name="innerException">The failure that stopped the row being recorded.</param>
    public ConfigurationChangeSynchronisationNotRecordedException(string connectedSystemName, string objectTypeName, Exception innerException)
        : base($"The change was saved, but JIM could not record that the next synchronisation of {connectedSystemName} must process every " +
               $"{objectTypeName} object, so it may not reach the objects that already exist. Run a Full Synchronisation of " +
               $"{connectedSystemName} to apply it.", innerException)
    {
        ConnectedSystemName = connectedSystemName;
        ObjectTypeName = objectTypeName;
    }

    /// <summary>The Connected System whose synchronisation must apply the change.</summary>
    public string ConnectedSystemName { get; }

    /// <summary>The Connected System Object Type whose objects the change can affect.</summary>
    public string ObjectTypeName { get; }
}
