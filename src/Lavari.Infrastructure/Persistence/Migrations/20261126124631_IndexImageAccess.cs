using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lavari.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexImageAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ImageAssetId",
                table: "Receipts",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_ImageAssetId",
                table: "ProductVariants",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_PrimaryImageAssetId",
                table: "Products",
                column: "PrimaryImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MoodBoardElements_ImageAssetId",
                table: "MoodBoardElements",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_JobLineItems_ImageAssetId",
                table: "JobLineItems",
                column: "ImageAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipts_ImageAssetId",
                table: "Receipts");

            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_ImageAssetId",
                table: "ProductVariants");

            migrationBuilder.DropIndex(
                name: "IX_Products_PrimaryImageAssetId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_MoodBoardElements_ImageAssetId",
                table: "MoodBoardElements");

            migrationBuilder.DropIndex(
                name: "IX_JobLineItems_ImageAssetId",
                table: "JobLineItems");
        }
    }
}
