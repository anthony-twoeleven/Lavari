using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lavari.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiImageSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AiSourceImageAssetId",
                table: "ImageAssets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_AiSourceImageAssetId",
                table: "ImageAssets",
                column: "AiSourceImageAssetId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImageAssets_AiSourceNotSelf",
                table: "ImageAssets",
                sql: "AiSourceImageAssetId IS NULL OR AiSourceImageAssetId <> Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ImageAssets_ImageAssets_AiSourceImageAssetId",
                table: "ImageAssets",
                column: "AiSourceImageAssetId",
                principalTable: "ImageAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImageAssets_ImageAssets_AiSourceImageAssetId",
                table: "ImageAssets");

            migrationBuilder.DropIndex(
                name: "IX_ImageAssets_AiSourceImageAssetId",
                table: "ImageAssets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ImageAssets_AiSourceNotSelf",
                table: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "AiSourceImageAssetId",
                table: "ImageAssets");
        }
    }
}
