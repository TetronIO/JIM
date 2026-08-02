using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationChangePreviewTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PreviewActivityId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConfigurationChangePreviews",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Surface = table.Column<int>(type: "integer", nullable: false),
                    ProposedConfigurationSnapshot = table.Column<string>(type: "text", nullable: true),
                    ValidationStatus = table.Column<int>(type: "integer", nullable: false),
                    ValidationStarted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidationCompleted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImpactCountsStatus = table.Column<int>(type: "integer", nullable: false),
                    ImpactCountsStarted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImpactCountsCompleted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SummaryStatus = table.Column<int>(type: "integer", nullable: false),
                    SummaryStarted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SummaryCompleted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeltasStatus = table.Column<int>(type: "integer", nullable: false),
                    DeltasStarted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeltasCompleted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedAffectedObjects = table.Column<int>(type: "integer", nullable: false),
                    EstimatedDeltaRows = table.Column<long>(type: "bigint", nullable: false),
                    DeltaPersistence = table.Column<int>(type: "integer", nullable: false),
                    DispatchedToWorker = table.Column<bool>(type: "boolean", nullable: false),
                    StalenessBaseline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationChangePreviews", x => x.ActivityId);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangePreviews_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationChangePreviewGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitionType = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseObjectTypeName = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    AttributeName = table.Column<string>(type: "text", nullable: true),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    PatternKey = table.Column<string>(type: "text", nullable: true),
                    ObjectCount = table.Column<int>(type: "integer", nullable: false),
                    DeltasSampled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationChangePreviewGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangePreviewGroups_ConfigurationChangePreview~",
                        column: x => x.ActivityId,
                        principalTable: "ConfigurationChangePreviews",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationChangePreviewDeltas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitionType = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ObjectDisplayName = table.Column<string>(type: "text", nullable: true),
                    ObjectTypeName = table.Column<string>(type: "text", nullable: true),
                    AttributeName = table.Column<string>(type: "text", nullable: true),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    PatternKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationChangePreviewDeltas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangePreviewDeltas_ConfigurationChangePreview~",
                        column: x => x.ActivityId,
                        principalTable: "ConfigurationChangePreviews",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangePreviewDeltas_ConfigurationChangePrevie~1",
                        column: x => x.GroupId,
                        principalTable: "ConfigurationChangePreviewGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangePreviewDeltas_ActivityId_GroupId",
                table: "ConfigurationChangePreviewDeltas",
                columns: new[] { "ActivityId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangePreviewDeltas_ActivityId_TransitionType",
                table: "ConfigurationChangePreviewDeltas",
                columns: new[] { "ActivityId", "TransitionType" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangePreviewDeltas_GroupId",
                table: "ConfigurationChangePreviewDeltas",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangePreviewGroups_ActivityId",
                table: "ConfigurationChangePreviewGroups",
                column: "ActivityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationChangePreviewDeltas");

            migrationBuilder.DropTable(
                name: "ConfigurationChangePreviewGroups");

            migrationBuilder.DropTable(
                name: "ConfigurationChangePreviews");

            migrationBuilder.DropColumn(
                name: "PreviewActivityId",
                table: "Activities");
        }
    }
}
