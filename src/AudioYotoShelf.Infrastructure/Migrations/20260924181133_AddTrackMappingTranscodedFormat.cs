using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioYotoShelf.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackMappingTranscodedFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranscodedFormat",
                table: "TrackMappings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscodedFormat",
                table: "TrackMappings");
        }
    }
}
