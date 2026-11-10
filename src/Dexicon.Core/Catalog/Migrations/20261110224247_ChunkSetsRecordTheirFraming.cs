using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// Give each chunk set room to record the document template its vectors were embedded with
    /// (<c>FramingRecorded</c>). See <c>ModelFraming</c> and D-47.
    ///
    /// Null for existing sets, and no statement fills it: the template a set was embedded with is not
    /// stored anywhere, and the model's profile as it is now may not be it. The first check after the
    /// upgrade records the profile as it is then, so a framing change made before that is not visible
    /// afterwards.
    ///
    /// <c>Down</c> drops the column, and the next <c>Up</c> records afresh. A version without this
    /// migration ignores the column and leaves it null in the sets it creates.
    /// </summary>
    public partial class ChunkSetsRecordTheirFraming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FramingRecorded",
                table: "chunk_sets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FramingRecorded",
                table: "chunk_sets");
        }
    }
}
