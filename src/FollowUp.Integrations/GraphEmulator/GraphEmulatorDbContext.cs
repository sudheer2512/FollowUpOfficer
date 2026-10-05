using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FollowUp.Integrations.GraphEmulator;

/// <summary>
/// EF Core store of the Graph emulator. SQL Server: schema "m365emu" with its own migrations history table.
/// SQLite (no schemas): tables are prefixed "m365emu_" so they can share a database file with the application.
/// </summary>
public sealed class GraphEmulatorDbContext(DbContextOptions<GraphEmulatorDbContext> options) : DbContext(options)
{
    public const string Schema = "m365emu";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<EmulatorUser> Users => Set<EmulatorUser>();
    public DbSet<EmulatorTeam> Teams => Set<EmulatorTeam>();
    public DbSet<EmulatorChannel> Channels => Set<EmulatorChannel>();
    public DbSet<EmulatorTeamMember> TeamMembers => Set<EmulatorTeamMember>();
    public DbSet<EmulatorChat> Chats => Set<EmulatorChat>();
    public DbSet<EmulatorChatMember> ChatMembers => Set<EmulatorChatMember>();
    public DbSet<EmulatorMailItem> MailItems => Set<EmulatorMailItem>();
    public DbSet<EmulatorTeamsMessage> TeamsMessages => Set<EmulatorTeamsMessage>();
    public DbSet<EmulatorEvent> Events => Set<EmulatorEvent>();
    public DbSet<EmulatorActivityNotification> ActivityNotifications => Set<EmulatorActivityNotification>();
    public DbSet<EmulatorIssuedToken> IssuedTokens => Set<EmulatorIssuedToken>();
    public DbSet<EmulatorSettings> Settings => Set<EmulatorSettings>();

    /// <summary>Configures the provider (SQL Server with the m365emu migrations history table, or SQLite).</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString, bool useSqlite)
    {
        if (useSqlite) builder.UseSqlite(connectionString);
        else builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema));
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var sqlite = Database.IsSqlite();
        var prefix = sqlite ? Schema + "_" : "";
        if (!sqlite) b.HasDefaultSchema(Schema);

        b.Entity<EmulatorUser>(e =>
        {
            e.ToTable(prefix + "Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Mail).HasMaxLength(256);
            e.Property(x => x.UserPrincipalName).HasMaxLength(256);
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Department).HasMaxLength(200);
            e.Property(x => x.ManagerId).HasMaxLength(100);
            e.HasIndex(x => x.Mail);
            e.HasIndex(x => x.UserPrincipalName);
        });

        b.Entity<EmulatorTeam>(e =>
        {
            e.ToTable(prefix + "Teams");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(256);
        });

        b.Entity<EmulatorChannel>(e =>
        {
            e.ToTable(prefix + "Channels");
            e.HasKey(x => new { x.TeamId, x.Id });
            e.Property(x => x.TeamId).HasMaxLength(100);
            e.Property(x => x.Id).HasMaxLength(150);
            e.Property(x => x.DisplayName).HasMaxLength(256);
        });

        b.Entity<EmulatorTeamMember>(e =>
        {
            e.ToTable(prefix + "TeamMembers");
            e.HasKey(x => new { x.TeamId, x.UserId });
            e.Property(x => x.TeamId).HasMaxLength(100);
            e.Property(x => x.UserId).HasMaxLength(100);
        });

        b.Entity<EmulatorChat>(e =>
        {
            e.ToTable(prefix + "Chats");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(250);
            e.Property(x => x.ChatType).HasMaxLength(20);
            e.Property(x => x.Topic).HasMaxLength(256);
            e.Property(x => x.OneOnOneKey).HasMaxLength(210);
            e.HasIndex(x => x.OneOnOneKey).IsUnique();
        });

        b.Entity<EmulatorChatMember>(e =>
        {
            e.ToTable(prefix + "ChatMembers");
            e.HasKey(x => new { x.ChatId, x.UserId });
            e.Property(x => x.ChatId).HasMaxLength(250);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Roles).HasMaxLength(50);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<EmulatorMailItem>(e =>
        {
            e.ToTable(prefix + "MailItems");
            e.HasKey(x => x.Pk);
            e.Property(x => x.Id).HasMaxLength(100).IsRequired();
            e.Property(x => x.Mailbox).HasMaxLength(256);
            e.Property(x => x.Folder).HasMaxLength(32);
            e.Property(x => x.InternetMessageId).HasMaxLength(300);
            e.Property(x => x.ConversationId).HasMaxLength(100);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.BodyContentType).HasMaxLength(10);
            e.Property(x => x.BodyPreview).HasMaxLength(300);
            e.Property(x => x.FromAddress).HasMaxLength(256);
            e.Property(x => x.FromName).HasMaxLength(256);
            e.Property(x => x.InReplyTo).HasMaxLength(300);
            e.Property(x => x.Importance).HasMaxLength(10);
            e.HasIndex(x => x.Id).IsUnique();
            e.HasIndex(x => new { x.Mailbox, x.Folder, x.ChangeSeq });
            e.HasIndex(x => x.InternetMessageId);
        });

        b.Entity<EmulatorTeamsMessage>(e =>
        {
            e.ToTable(prefix + "TeamsMessages");
            e.HasKey(x => x.Pk);
            e.Property(x => x.ContainerId).HasMaxLength(260);
            e.Property(x => x.ChatId).HasMaxLength(250);
            e.Property(x => x.TeamId).HasMaxLength(100);
            e.Property(x => x.ChannelId).HasMaxLength(150);
            e.Property(x => x.MessageId).HasMaxLength(40);
            e.Property(x => x.ReplyToId).HasMaxLength(40);
            e.Property(x => x.MessageType).HasMaxLength(40);
            e.Property(x => x.FromUserId).HasMaxLength(100);
            e.Property(x => x.FromDisplayName).HasMaxLength(256);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.BodyContentType).HasMaxLength(10);
            e.Property(x => x.Importance).HasMaxLength(10);
            e.HasIndex(x => new { x.ContainerId, x.MessageId }).IsUnique();
            e.HasIndex(x => new { x.ContainerId, x.LastModifiedDateTime });
            e.HasIndex(x => new { x.ContainerId, x.ChangeSeq });
            e.HasIndex(x => new { x.ContainerId, x.ReplyToId });
        });

        b.Entity<EmulatorEvent>(e =>
        {
            e.ToTable(prefix + "Events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.Mailbox).HasMaxLength(256);
            e.Property(x => x.TransactionId).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.BodyContentType).HasMaxLength(10);
            e.Property(x => x.StartDateTime).HasMaxLength(40);
            e.Property(x => x.StartTimeZone).HasMaxLength(100);
            e.Property(x => x.EndDateTime).HasMaxLength(40);
            e.Property(x => x.EndTimeZone).HasMaxLength(100);
            e.Property(x => x.Importance).HasMaxLength(10);
            e.Property(x => x.ShowAs).HasMaxLength(20);
            e.HasIndex(x => new { x.Mailbox, x.TransactionId }).IsUnique();
        });

        b.Entity<EmulatorActivityNotification>(e =>
        {
            e.ToTable(prefix + "ActivityNotifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.ActivityType).HasMaxLength(100);
            e.Property(x => x.TopicValue).HasMaxLength(500);
            e.Property(x => x.TopicWebUrl).HasMaxLength(1000);
            e.Property(x => x.PreviewText).HasMaxLength(500);
            e.Property(x => x.SenderAppId).HasMaxLength(100);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<EmulatorIssuedToken>(e =>
        {
            e.ToTable(prefix + "IssuedTokens");
            e.HasKey(x => x.TokenHash);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.ClientId).HasMaxLength(100);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<EmulatorSettings>(e =>
        {
            e.ToTable(prefix + "Settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        // Timestamps are stored as UTC; restore DateTimeKind.Utc on read so they serialise with "Z".
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
            else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
        }
    }
}
