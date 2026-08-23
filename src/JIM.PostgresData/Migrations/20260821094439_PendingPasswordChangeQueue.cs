using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class PendingPasswordChangeQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingPasswordChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    ExpiryBehaviour = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<int>(type: "integer", nullable: true),
                    TargetMessage = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPasswordChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingPasswordChanges_ConnectedSystemObjects_ConnectedSyst~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingPasswordChanges_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingPasswordChanges_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_ConnectedSystemId_Status_NextRetryAt",
                table: "PendingPasswordChanges",
                columns: new[] { "ConnectedSystemId", "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_ConnectedSystemObjectId",
                table: "PendingPasswordChanges",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_MetaverseObjectId",
                table: "PendingPasswordChanges",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_MetaverseObjectId_ConnectedSystemId_Unique",
                table: "PendingPasswordChanges",
                columns: new[] { "MetaverseObjectId", "ConnectedSystemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingPasswordChanges");
        }
    }
}
