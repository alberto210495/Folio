using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Folio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trades_ExecutedAt",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Snapshots_TakenAt",
                table: "Snapshots");

            migrationBuilder.DropIndex(
                name: "IX_Positions_Symbol",
                table: "Positions");

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Trades",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Positions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Accounts",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            // Existing data belongs to the existing (first) account, which becomes "My portfolio".
            migrationBuilder.Sql("UPDATE Accounts SET Name = 'My portfolio' WHERE Name = '';");
            foreach (var table in new[] { "Positions", "Trades", "Orders", "Snapshots" })
                migrationBuilder.Sql($"UPDATE {table} SET AccountId = (SELECT MIN(Id) FROM Accounts) WHERE AccountId = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId_ExecutedAt",
                table: "Trades",
                columns: new[] { "AccountId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_AccountId_TakenAt",
                table: "Snapshots",
                columns: new[] { "AccountId", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_AccountId_Symbol",
                table: "Positions",
                columns: new[] { "AccountId", "Symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId",
                table: "Orders",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Accounts_AccountId",
                table: "Orders",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Positions_Accounts_AccountId",
                table: "Positions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Snapshots_Accounts_AccountId",
                table: "Snapshots",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Trades_Accounts_AccountId",
                table: "Trades",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Accounts_AccountId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Positions_Accounts_AccountId",
                table: "Positions");

            migrationBuilder.DropForeignKey(
                name: "FK_Snapshots_Accounts_AccountId",
                table: "Snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_Trades_Accounts_AccountId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId_ExecutedAt",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Snapshots_AccountId_TakenAt",
                table: "Snapshots");

            migrationBuilder.DropIndex(
                name: "IX_Positions_AccountId_Symbol",
                table: "Positions");

            migrationBuilder.DropIndex(
                name: "IX_Orders_AccountId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Snapshots");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ExecutedAt",
                table: "Trades",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_TakenAt",
                table: "Snapshots",
                column: "TakenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Symbol",
                table: "Positions",
                column: "Symbol",
                unique: true);
        }
    }
}
