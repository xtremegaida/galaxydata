using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalaxyData.Web.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class Dashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true),
                    OwnerName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    WorkingJson = table.Column<string>(type: "TEXT", nullable: false),
                    WorkingHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublishedJson = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PublishedNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PublishedByName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SharedWithEveryone = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicToken = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PublicEnabledById = table.Column<int>(type: "INTEGER", nullable: true),
                    PublicEnabledByName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PublicEnabledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmbedOrigins = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dashboards_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Dashboards_Users_PublicEnabledById",
                        column: x => x.PublicEnabledById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DashboardRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedById = table.Column<int>(type: "INTEGER", nullable: true),
                    PublishedByName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardRevisions_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DashboardRevisions_Users_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DashboardShares",
                columns: table => new
                {
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardShares", x => new { x.DashboardId, x.UserId });
                    table.ForeignKey(
                        name: "FK_DashboardShares_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DashboardShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardRevisions_DashboardId_Number",
                table: "DashboardRevisions",
                columns: new[] { "DashboardId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardRevisions_PublishedById",
                table: "DashboardRevisions",
                column: "PublishedById");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_OwnerId_Name",
                table: "Dashboards",
                columns: new[] { "OwnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_PublicEnabledById",
                table: "Dashboards",
                column: "PublicEnabledById");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_PublicToken",
                table: "Dashboards",
                column: "PublicToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_SharedWithEveryone",
                table: "Dashboards",
                column: "SharedWithEveryone");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardShares_UserId",
                table: "DashboardShares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardRevisions");

            migrationBuilder.DropTable(
                name: "DashboardShares");

            migrationBuilder.DropTable(
                name: "Dashboards");
        }
    }
}
