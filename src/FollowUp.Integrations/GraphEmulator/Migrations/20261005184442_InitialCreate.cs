using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FollowUp.Integrations.GraphEmulator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "m365emu");

            migrationBuilder.CreateTable(
                name: "ActivityNotifications",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActivityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TopicValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TopicWebUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PreviewText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TemplateParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SenderAppId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                schema: "m365emu",
                columns: table => new
                {
                    TeamId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Id = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => new { x.TeamId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ChatMembers",
                schema: "m365emu",
                columns: table => new
                {
                    ChatId = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Roles = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMembers", x => new { x.ChatId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Chats",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ChatType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OneOnOneKey = table.Column<string>(type: "nvarchar(210)", maxLength: 210, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Mailbox = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TransactionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BodyContentType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BodyContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartDateTime = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartTimeZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EndDateTime = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EndTimeZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsAllDay = table.Column<bool>(type: "bit", nullable: false),
                    Importance = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsReminderOn = table.Column<bool>(type: "bit", nullable: false),
                    ReminderMinutesBeforeStart = table.Column<int>(type: "int", nullable: false),
                    ShowAs = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModifiedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IssuedTokens",
                schema: "m365emu",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuedTokens", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "MailItems",
                schema: "m365emu",
                columns: table => new
                {
                    Pk = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Mailbox = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InternetMessageId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BodyContentType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BodyContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyPreview = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ToJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CcJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExtendedPropertiesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InReplyTo = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    References = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Importance = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    HasAttachments = table.Column<bool>(type: "bit", nullable: false),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    SentDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModifiedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangeSeq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailItems", x => x.Pk);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Outage = table.Column<bool>(type: "bit", nullable: false),
                    ThrottlePercent = table.Column<int>(type: "int", nullable: false),
                    AuthFailure = table.Column<bool>(type: "bit", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    DeltaEpoch = table.Column<int>(type: "int", nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                schema: "m365emu",
                columns: table => new
                {
                    TeamId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => new { x.TeamId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamsMessages",
                schema: "m365emu",
                columns: table => new
                {
                    Pk = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContainerId = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ChatId = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    TeamId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChannelId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    MessageId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReplyToId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FromUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FromDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BodyContentType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BodyContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Importance = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MentionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AttachmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModifiedDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedDateTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChangeSeq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamsMessages", x => x.Pk);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "m365emu",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Mail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UserPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ManagerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AccountEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityNotifications_UserId",
                schema: "m365emu",
                table: "ActivityNotifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMembers_UserId",
                schema: "m365emu",
                table: "ChatMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_OneOnOneKey",
                schema: "m365emu",
                table: "Chats",
                column: "OneOnOneKey",
                unique: true,
                filter: "[OneOnOneKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Mailbox_TransactionId",
                schema: "m365emu",
                table: "Events",
                columns: new[] { "Mailbox", "TransactionId" },
                unique: true,
                filter: "[TransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IssuedTokens_ExpiresAt",
                schema: "m365emu",
                table: "IssuedTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MailItems_Id",
                schema: "m365emu",
                table: "MailItems",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailItems_InternetMessageId",
                schema: "m365emu",
                table: "MailItems",
                column: "InternetMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MailItems_Mailbox_Folder_ChangeSeq",
                schema: "m365emu",
                table: "MailItems",
                columns: new[] { "Mailbox", "Folder", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamsMessages_ContainerId_ChangeSeq",
                schema: "m365emu",
                table: "TeamsMessages",
                columns: new[] { "ContainerId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamsMessages_ContainerId_LastModifiedDateTime",
                schema: "m365emu",
                table: "TeamsMessages",
                columns: new[] { "ContainerId", "LastModifiedDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamsMessages_ContainerId_MessageId",
                schema: "m365emu",
                table: "TeamsMessages",
                columns: new[] { "ContainerId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamsMessages_ContainerId_ReplyToId",
                schema: "m365emu",
                table: "TeamsMessages",
                columns: new[] { "ContainerId", "ReplyToId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Mail",
                schema: "m365emu",
                table: "Users",
                column: "Mail");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserPrincipalName",
                schema: "m365emu",
                table: "Users",
                column: "UserPrincipalName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityNotifications",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Channels",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "ChatMembers",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Chats",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "IssuedTokens",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "MailItems",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "TeamMembers",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Teams",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "TeamsMessages",
                schema: "m365emu");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "m365emu");
        }
    }
}
