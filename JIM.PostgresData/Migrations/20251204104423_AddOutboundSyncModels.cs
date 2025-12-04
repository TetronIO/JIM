using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundSyncModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ErrorCount",
                table: "PendingExports",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PendingExports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "HasUnresolvedReferences",
                table: "PendingExports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptedAt",
                table: "PendingExports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorMessage",
                table: "PendingExports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "PendingExports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "PendingExports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceMetaverseObjectId",
                table: "PendingExports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeferredReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCsoId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeName = table.Column<string>(type: "text", nullable: false),
                    TargetMvoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSystemId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeferredReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeferredReferences_ConnectedSystemObjects_SourceCsoId",
                        column: x => x.SourceCsoId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeferredReferences_ConnectedSystems_TargetSystemId",
                        column: x => x.TargetSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeferredReferences_MetaverseObjects_TargetMvoId",
                        column: x => x.TargetMvoId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_SourceMetaverseObjectId",
                table: "PendingExports",
                column: "SourceMetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DeferredReferences_SourceCsoId",
                table: "DeferredReferences",
                column: "SourceCsoId");

            migrationBuilder.CreateIndex(
                name: "IX_DeferredReferences_TargetMvoId_TargetSystemId",
                table: "DeferredReferences",
                columns: new[] { "TargetMvoId", "TargetSystemId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeferredReferences_TargetSystemId",
                table: "DeferredReferences",
                column: "TargetSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_MetaverseObjects_SourceMetaverseObjectId",
                table: "PendingExports",
                column: "SourceMetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_MetaverseObjects_SourceMetaverseObjectId",
                table: "PendingExports");

            migrationBuilder.DropTable(
                name: "DeferredReferences");

            migrationBuilder.DropIndex(
                name: "IX_PendingExports_SourceMetaverseObjectId",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "HasUnresolvedReferences",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "LastAttemptedAt",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "LastErrorMessage",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "SourceMetaverseObjectId",
                table: "PendingExports");

            migrationBuilder.AlterColumn<int>(
                name: "ErrorCount",
                table: "PendingExports",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
