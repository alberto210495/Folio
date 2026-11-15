using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Folio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Watchlist",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlist", x => x.Symbol);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_AddedAt",
                table: "Watchlist",
                column: "AddedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Watchlist");
        }
    }
}
