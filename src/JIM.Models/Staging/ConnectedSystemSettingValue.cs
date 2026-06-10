// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Defines an instance of a connector definition setting that JIM will ask an administrator to supply a value for when configuring a Connected System.
/// </summary>
public class ConnectedSystemSettingValue
{
    public int Id { get; set; }
        
    public ConnectedSystem ConnectedSystem { get; set; } = null!;

    public ConnectorDefinitionSetting Setting { get; set; } = null!;

    public string? StringValue { get; set; }
        
    /// <summary>
    /// Encrypted value for sensitive settings such as passwords.
    /// Values are encrypted at rest using ASP.NET Core Data Protection with AES-256-GCM.
    /// Encrypted values use the prefix $JIM$v1$ for version identification.
    /// </summary>
    public string? StringEncryptedValue { get; set; }

    public int? IntValue { get; set; }
        
    /// <summary>
    /// TODO: change this to nullable and add to IsValueValid()
    /// </summary>
    public bool CheckboxValue { get; set; }

    public bool IsValueValid()
    {
        // at least one value is required for the object to be valid
        return StringValue != null || StringEncryptedValue != null || IntValue.HasValue;
    }

    /// <summary>
    /// Determines whether the administrator has supplied a usable value, i.e. a non-empty string, encrypted string, or integer.
    /// Checkbox values are excluded; a boolean always has a value, so it cannot indicate administrator intent.
    /// </summary>
    public bool HasUserSuppliedValue()
    {
        return !string.IsNullOrEmpty(StringValue) || !string.IsNullOrEmpty(StringEncryptedValue) || IntValue.HasValue;
    }
}