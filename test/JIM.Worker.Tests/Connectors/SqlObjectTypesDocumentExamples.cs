// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Object Types documents the SQL Connector tests share.
/// </summary>
internal static class SqlObjectTypesDocumentExamples
{
    /// <summary>
    /// An Object Types document with both Delta Import modes configured: a change log and watermark columns on
    /// the source and its related table. Only the mode chosen is read, so one document serves both modes' tests.
    /// </summary>
    internal const string DeltaConfiguration = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "V_EMPLOYEES",
              "anchorColumns": [ "EMPLOYEE_ID" ],
              "watermarkColumn": "LAST_MODIFIED",
              "relatedTables": [
                {
                  "attributeName": "PhoneNumbers",
                  "schema": "HR",
                  "table": "EMPLOYEE_PHONES",
                  "valueColumn": "PHONE_NUMBER",
                  "joinColumns": [ "EMPLOYEE_ID" ],
                  "watermarkColumn": "LAST_MODIFIED"
                }
              ],
              "changeLog": {
                "schema": "HR",
                "table": "IDM_CHANGE_LOG",
                "anchorColumns": [ "EMPLOYEE_ID" ],
                "sequenceColumn": "CHANGED_AT",
                "changeTypeColumn": "CHANGE_TYPE",
                "createValues": [ "I" ],
                "updateValues": [ "U" ],
                "deleteValues": [ "D" ]
              }
            }
          ]
        }
        """;
}
