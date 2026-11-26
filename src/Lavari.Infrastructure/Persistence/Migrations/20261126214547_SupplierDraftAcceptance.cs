using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lavari.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierDraftAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceImageUrlsJson",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedProductId",
                table: "LlmExtractionDrafts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceImageUrlsJson",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "AcceptedProductId",
                table: "LlmExtractionDrafts");
        }
    }
}
