using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ModelMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_measurements",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxInputChars = table.Column<int>(type: "INTEGER", nullable: true),
                    TruncatesSilently = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecommendedChunkChars = table.Column<int>(type: "INTEGER", nullable: false),
                    RecommendedChunkTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CharsPerToken = table.Column<double>(type: "REAL", nullable: true),
                    MeasuredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_measurements", x => new { x.Provider, x.Model });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_measurements");
        }
    }
}
