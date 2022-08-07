using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class LongIds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExport_ConnectedSystemObjects_ConnectedSystemObjectId",
                table: "PendingExport");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChange_ConnectedSystemAttributes~",
                table: "PendingExportAttributeValueChange");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChange_PendingExport_PendingExpo~",
                table: "PendingExportAttributeValueChange");

            migrationBuilder.DropForeignKey(
                name: "FK_SynchronisationRuns_ConnectedSystems_ConnectedSystemId",
                table: "SynchronisationRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObject_ConnectedSystemObjects_ConnectedSystemObjectId",
                table: "SyncRunObject");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObject_PendingExport_PendingExportId",
                table: "SyncRunObject");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObject_SynchronisationRuns_SynchronisationRunId",
                table: "SyncRunObject");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRunObject",
                table: "SyncRunObject");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SynchronisationRuns",
                table: "SynchronisationRuns");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PendingExportAttributeValueChange",
                table: "PendingExportAttributeValueChange");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PendingExport",
                table: "PendingExport");

            migrationBuilder.RenameTable(
                name: "SyncRunObject",
                newName: "SyncRunObjects");

            migrationBuilder.RenameTable(
                name: "SynchronisationRuns",
                newName: "SyncRuns");

            migrationBuilder.RenameTable(
                name: "PendingExportAttributeValueChange",
                newName: "PendingExportAttributeValueChanges");

            migrationBuilder.RenameTable(
                name: "PendingExport",
                newName: "PendingExports");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObject_SynchronisationRunId",
                table: "SyncRunObjects",
                newName: "IX_SyncRunObjects_SynchronisationRunId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObject_PendingExportId",
                table: "SyncRunObjects",
                newName: "IX_SyncRunObjects_PendingExportId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObject_ConnectedSystemObjectId",
                table: "SyncRunObjects",
                newName: "IX_SyncRunObjects_ConnectedSystemObjectId");

            migrationBuilder.RenameIndex(
                name: "IX_SynchronisationRuns_ConnectedSystemId",
                table: "SyncRuns",
                newName: "IX_SyncRuns_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExportAttributeValueChange_PendingExportId",
                table: "PendingExportAttributeValueChanges",
                newName: "IX_PendingExportAttributeValueChanges_PendingExportId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExportAttributeValueChange_AttributeId",
                table: "PendingExportAttributeValueChanges",
                newName: "IX_PendingExportAttributeValueChanges_AttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExport_ConnectedSystemObjectId",
                table: "PendingExports",
                newName: "IX_PendingExports_ConnectedSystemObjectId");

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "MetaverseObjectAttributeValues",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "ConnectedSystemObjects",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "ConnectedSystemObjectId",
                table: "ConnectedSystemAttributeValue",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "ConnectedSystemAttributeValue",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "SynchronisationRunId",
                table: "SyncRunObjects",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<long>(
                name: "PendingExportId",
                table: "SyncRunObjects",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "ConnectedSystemObjectId",
                table: "SyncRunObjects",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "SyncRunObjects",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "SyncRuns",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "PendingExportId",
                table: "PendingExportAttributeValueChanges",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "PendingExportAttributeValueChanges",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "ConnectedSystemObjectId",
                table: "PendingExports",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "PendingExports",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRunObjects",
                table: "SyncRunObjects",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuns",
                table: "SyncRuns",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PendingExportAttributeValueChanges",
                table: "PendingExportAttributeValueChanges",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PendingExports",
                table: "PendingExports",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_ConnectedSystemAttribute~",
                table: "PendingExportAttributeValueChanges",
                column: "AttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObjects_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "SyncRunObjects",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObjects_PendingExports_PendingExportId",
                table: "SyncRunObjects",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObjects_SyncRuns_SynchronisationRunId",
                table: "SyncRunObjects",
                column: "SynchronisationRunId",
                principalTable: "SyncRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_ConnectedSystems_ConnectedSystemId",
                table: "SyncRuns",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_ConnectedSystemAttribute~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObjects_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "SyncRunObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObjects_PendingExports_PendingExportId",
                table: "SyncRunObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRunObjects_SyncRuns_SynchronisationRunId",
                table: "SyncRunObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuns_ConnectedSystems_ConnectedSystemId",
                table: "SyncRuns");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuns",
                table: "SyncRuns");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRunObjects",
                table: "SyncRunObjects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PendingExports",
                table: "PendingExports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PendingExportAttributeValueChanges",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.RenameTable(
                name: "SyncRuns",
                newName: "SynchronisationRuns");

            migrationBuilder.RenameTable(
                name: "SyncRunObjects",
                newName: "SyncRunObject");

            migrationBuilder.RenameTable(
                name: "PendingExports",
                newName: "PendingExport");

            migrationBuilder.RenameTable(
                name: "PendingExportAttributeValueChanges",
                newName: "PendingExportAttributeValueChange");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuns_ConnectedSystemId",
                table: "SynchronisationRuns",
                newName: "IX_SynchronisationRuns_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObjects_SynchronisationRunId",
                table: "SyncRunObject",
                newName: "IX_SyncRunObject_SynchronisationRunId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObjects_PendingExportId",
                table: "SyncRunObject",
                newName: "IX_SyncRunObject_PendingExportId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRunObjects_ConnectedSystemObjectId",
                table: "SyncRunObject",
                newName: "IX_SyncRunObject_ConnectedSystemObjectId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId",
                table: "PendingExport",
                newName: "IX_PendingExport_ConnectedSystemObjectId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId",
                table: "PendingExportAttributeValueChange",
                newName: "IX_PendingExportAttributeValueChange_PendingExportId");

            migrationBuilder.RenameIndex(
                name: "IX_PendingExportAttributeValueChanges_AttributeId",
                table: "PendingExportAttributeValueChange",
                newName: "IX_PendingExportAttributeValueChange_AttributeId");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "MetaverseObjectAttributeValues",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "ConnectedSystemObjectId",
                table: "ConnectedSystemAttributeValue",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "ConnectedSystemAttributeValue",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "SynchronisationRuns",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "SynchronisationRunId",
                table: "SyncRunObject",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<int>(
                name: "PendingExportId",
                table: "SyncRunObject",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ConnectedSystemObjectId",
                table: "SyncRunObject",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "SyncRunObject",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "ConnectedSystemObjectId",
                table: "PendingExport",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "PendingExport",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "PendingExportId",
                table: "PendingExportAttributeValueChange",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "PendingExportAttributeValueChange",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SynchronisationRuns",
                table: "SynchronisationRuns",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRunObject",
                table: "SyncRunObject",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PendingExport",
                table: "PendingExport",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PendingExportAttributeValueChange",
                table: "PendingExportAttributeValueChange",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExport_ConnectedSystemObjects_ConnectedSystemObjectId",
                table: "PendingExport",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChange_ConnectedSystemAttributes~",
                table: "PendingExportAttributeValueChange",
                column: "AttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChange_PendingExport_PendingExpo~",
                table: "PendingExportAttributeValueChange",
                column: "PendingExportId",
                principalTable: "PendingExport",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SynchronisationRuns_ConnectedSystems_ConnectedSystemId",
                table: "SynchronisationRuns",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObject_ConnectedSystemObjects_ConnectedSystemObjectId",
                table: "SyncRunObject",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObject_PendingExport_PendingExportId",
                table: "SyncRunObject",
                column: "PendingExportId",
                principalTable: "PendingExport",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRunObject_SynchronisationRuns_SynchronisationRunId",
                table: "SyncRunObject",
                column: "SynchronisationRunId",
                principalTable: "SynchronisationRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
