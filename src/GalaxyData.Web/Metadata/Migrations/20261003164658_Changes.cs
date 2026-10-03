using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalaxyData.Web.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class Changes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeSets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommitAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ChangeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    AnyStatement = table.Column<bool>(type: "INTEGER", nullable: false),
                    CatalogVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Failure = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommitAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommitAudits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PendingChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChangeSetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Entity = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RowKey = table.Column<string>(type: "TEXT", nullable: true),
                    TempId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalJson = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingChanges_ChangeSets_ChangeSetId",
                        column: x => x.ChangeSetId,
                        principalTable: "ChangeSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommitAuditScripts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommitAuditId = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Dialect = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Statements = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RowsChanged = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommitAuditScripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommitAuditScripts_CommitAudits_CommitAuditId",
                        column: x => x.CommitAuditId,
                        principalTable: "CommitAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeSets_UserId",
                table: "ChangeSets",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommitAudits_Status",
                table: "CommitAudits",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CommitAudits_UserId",
                table: "CommitAudits",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommitAuditScripts_CommitAuditId",
                table: "CommitAuditScripts",
                column: "CommitAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingChanges_ChangeSetId_Entity_RowKey",
                table: "PendingChanges",
                columns: new[] { "ChangeSetId", "Entity", "RowKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingChanges_ChangeSetId_TempId",
                table: "PendingChanges",
                columns: new[] { "ChangeSetId", "TempId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommitAuditScripts");

            migrationBuilder.DropTable(
                name: "PendingChanges");

            migrationBuilder.DropTable(
                name: "CommitAudits");

            migrationBuilder.DropTable(
                name: "ChangeSets");
        }
    }
}
