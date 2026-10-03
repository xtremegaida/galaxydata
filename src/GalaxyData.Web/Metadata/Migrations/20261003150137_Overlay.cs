using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalaxyData.Web.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class Overlay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OverlayEntitySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Entity = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayColumn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    ColumnsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverlayEntitySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OverlayNavigations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Entity = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Navigation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RenameTo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverlayNavigations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OverlayRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    From = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    FromColumns = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ToColumns = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    InverseName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverlayRelations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OverlayVirtualEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Query = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverlayVirtualEntities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OverlayEntitySettings_Entity",
                table: "OverlayEntitySettings",
                column: "Entity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OverlayNavigations_Entity_Navigation",
                table: "OverlayNavigations",
                columns: new[] { "Entity", "Navigation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OverlayVirtualEntities_Name",
                table: "OverlayVirtualEntities",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OverlayEntitySettings");

            migrationBuilder.DropTable(
                name: "OverlayNavigations");

            migrationBuilder.DropTable(
                name: "OverlayRelations");

            migrationBuilder.DropTable(
                name: "OverlayVirtualEntities");
        }
    }
}
