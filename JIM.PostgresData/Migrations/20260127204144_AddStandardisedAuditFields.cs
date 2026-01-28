using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddStandardisedAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_MetaverseObjects_CreatedById",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRules_MetaverseObjects_CreatedById",
                table: "SyncRules");

            migrationBuilder.DropIndex(
                name: "IX_SyncRules_CreatedById",
                table: "SyncRules");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappings_CreatedById",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "LastModifiedBy",
                table: "ServiceSettingItems");

            // Rename CreatedBy (old string field) to CreatedByName (preserves existing data)
            migrationBuilder.RenameColumn(
                name: "CreatedBy",
                table: "TrustedCertificates",
                newName: "CreatedByName");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "TrustedCertificates",
                newName: "Created");

            migrationBuilder.RenameColumn(
                name: "LastModified",
                table: "ServiceSettingItems",
                newName: "LastUpdated");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "ApiKeys",
                newName: "Created");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "TrustedCertificates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "TrustedCertificates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "TrustedCertificates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "TrustedCertificates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "TrustedCertificates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "TrustedCertificates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "SyncRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "SyncRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "SyncRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "SyncRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "SyncRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "SyncRuleMappings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "SyncRuleMappings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "SyncRuleMappings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "SyncRuleMappings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "ServiceSettingItems",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ServiceSettingItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ServiceSettingItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ServiceSettingItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ServiceSettingItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ServiceSettingItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ServiceSettingItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "Roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "Roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "Roles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "Roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "Roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ObjectMatchingRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ObjectMatchingRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ObjectMatchingRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "ObjectMatchingRules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ObjectMatchingRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ObjectMatchingRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ObjectMatchingRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "MetaverseObjectTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "MetaverseObjectTypes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "MetaverseObjectTypes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "MetaverseObjectTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "MetaverseObjectTypes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "MetaverseAttributes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "MetaverseAttributes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "MetaverseAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "MetaverseAttributes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "MetaverseAttributes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "MetaverseAttributes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "MetaverseAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ConnectorDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ConnectorDefinitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ConnectorDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ConnectorDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ConnectorDefinitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ConnectorDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastUpdated",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ConnectedSystems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ConnectedSystems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ConnectedSystems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ConnectedSystems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ConnectedSystems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ConnectedSystems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "ConnectedSystemRunProfiles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ConnectedSystemRunProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ConnectedSystemRunProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "ConnectedSystemRunProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ConnectedSystemRunProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ConnectedSystemRunProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "TrustedCertificates");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "TrustedCertificates");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "TrustedCertificates");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "TrustedCertificates");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "TrustedCertificates");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "Created",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ServiceSettingItems");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ConnectorDefinitions");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "Created",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "TrustedCertificates");

            migrationBuilder.RenameColumn(
                name: "CreatedByName",
                table: "TrustedCertificates",
                newName: "CreatedBy");

            migrationBuilder.RenameColumn(
                name: "Created",
                table: "TrustedCertificates",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "LastUpdated",
                table: "ServiceSettingItems",
                newName: "LastModified");

            migrationBuilder.RenameColumn(
                name: "Created",
                table: "ApiKeys",
                newName: "CreatedAt");

            migrationBuilder.AddColumn<string>(
                name: "LastModifiedBy",
                table: "ServiceSettingItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastUpdated",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_CreatedById",
                table: "SyncRules",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_CreatedById",
                table: "SyncRuleMappings",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_MetaverseObjects_CreatedById",
                table: "SyncRuleMappings",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRules_MetaverseObjects_CreatedById",
                table: "SyncRules",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
