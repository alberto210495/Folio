using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Folio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientOrderIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_AccountId",
                table: "Orders");

            migrationBuilder.AddColumn<string>(
                name: "ClientOrderId",
                table: "Trades",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientOrderId",
                table: "Orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId_ClientOrderId",
                table: "Trades",
                columns: new[] { "AccountId", "ClientOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId_ClientOrderId",
                table: "Orders",
                columns: new[] { "AccountId", "ClientOrderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId_ClientOrderId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Orders_AccountId_ClientOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ClientOrderId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "ClientOrderId",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId",
                table: "Orders",
                column: "AccountId");
        }
    }
}
