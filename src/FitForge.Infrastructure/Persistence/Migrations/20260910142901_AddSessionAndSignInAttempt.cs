using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionAndSignInAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Session",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MemberId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Session", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Session_Member",
                        column: x => x.MemberId,
                        principalTable: "Member",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignInAttempt",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    SourceHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInAttempt", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Session_MemberId",
                table: "Session",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "UQ_Session_TokenHash",
                table: "Session",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignInAttempt_Email_At",
                table: "SignInAttempt",
                columns: new[] { "NormalizedEmail", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SignInAttempt_Source_At",
                table: "SignInAttempt",
                columns: new[] { "SourceHash", "AttemptedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Session");

            migrationBuilder.DropTable(
                name: "SignInAttempt");
        }
    }
}
