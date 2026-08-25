using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBridge.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedOutboundMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BodyPreview = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    BlockedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedOutboundMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Jid = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChatJid = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FromMe = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MediaUrl = table.Column<string>(type: "TEXT", nullable: true),
                    MediaKey = table.Column<string>(type: "TEXT", nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsHistory = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedOutboundMessages_BlockedAtUtc",
                table: "BlockedOutboundMessages",
                column: "BlockedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedOutboundMessages_UserId",
                table: "BlockedOutboundMessages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_UserId",
                table: "Chats",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_UserId_Jid",
                table: "Chats",
                columns: new[] { "UserId", "Jid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChatJid_Timestamp",
                table: "Messages",
                columns: new[] { "ChatJid", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ReceivedAt",
                table: "Messages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SessionId_MessageId",
                table: "Messages",
                columns: new[] { "SessionId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_UserId",
                table: "Messages",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedOutboundMessages");

            migrationBuilder.DropTable(
                name: "Chats");

            migrationBuilder.DropTable(
                name: "Messages");
        }
    }
}
