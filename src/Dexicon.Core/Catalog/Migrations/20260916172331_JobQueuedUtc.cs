using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class JobQueuedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "QueuedUtc",
                table: "jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill, or every job already in the catalogue sorts to year 0001 and the
            // history reads as though it happened before everything else. A job was queued
            // no later than it started, so StartedUtc is the best estimate available; a job
            // that never started leaves only FinishedUtc.
            migrationBuilder.Sql(
                """
                UPDATE jobs
                   SET QueuedUtc = COALESCE(StartedUtc, FinishedUtc, QueuedUtc)
                 WHERE QueuedUtc < '0002'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueuedUtc",
                table: "jobs");
        }
    }
}
