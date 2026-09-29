using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveQueueItemIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queue_item_song_id",
                table: "queue_item");

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_song_id",
                table: "queue_item",
                column: "song_id",
                unique: true,
                filter: "state IN ('Queued', 'RemotelyQueued', 'Downloading', 'Completed', 'Importing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queue_item_song_id",
                table: "queue_item");

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_song_id",
                table: "queue_item",
                column: "song_id");
        }
    }
}
