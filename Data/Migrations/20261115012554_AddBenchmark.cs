using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Folio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBenchmark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BenchmarkPrice",
                table: "Snapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BenchmarkStartPrice",
                table: "Accounts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BenchmarkPrice",
                table: "Snapshots");

            migrationBuilder.DropColumn(
                name: "BenchmarkStartPrice",
                table: "Accounts");
        }
    }
}
