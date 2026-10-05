using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Data;

/// <summary>
/// The application database. Every background step persists its result here (spec §60, §78): nothing the
/// processor needs to resume is kept only in memory.
/// </summary>
public sealed class FollowUpDbContext(DbContextOptions<FollowUpDbContext> options, IClock clock) : DbContext(options)
{
    // Organisation
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<WorkCalendar> WorkCalendars => Set<WorkCalendar>();
    public DbSet<Holiday> Holidays => Set<Holiday>();

    // Work
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskCategory> TaskCategories => Set<TaskCategory>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TaskTag> TaskTags => Set<TaskTag>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<TaskAssignee> TaskAssignees => Set<TaskAssignee>();
    public DbSet<TaskFollower> TaskFollowers => Set<TaskFollower>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<TaskStatusHistory> TaskStatusHistory => Set<TaskStatusHistory>();
    public DbSet<TaskDelegation> TaskDelegations => Set<TaskDelegation>();
    public DbSet<TaskAcknowledgement> TaskAcknowledgements => Set<TaskAcknowledgement>();
    public DbSet<Commitment> Commitments => Set<Commitment>();
    public DbSet<TaskFollowUpLog> TaskFollowUps => Set<TaskFollowUpLog>();
    public DbSet<TaskEscalation> TaskEscalations => Set<TaskEscalation>();
    public DbSet<CompletionSubmission> CompletionSubmissions => Set<CompletionSubmission>();
    public DbSet<CompletionEvidence> CompletionEvidence => Set<CompletionEvidence>();
    public DbSet<TaskApproval> TaskApprovals => Set<TaskApproval>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ExtensionRequest> ExtensionRequests => Set<ExtensionRequest>();
    public DbSet<TaskSuggestion> TaskSuggestions => Set<TaskSuggestion>();

    // Communications
    public DbSet<CommunicationMessage> Messages => Set<CommunicationMessage>();
    public DbSet<TaskCommunicationLink> TaskMessageLinks => Set<TaskCommunicationLink>();
    public DbSet<TaskConversation> TaskConversations => Set<TaskConversation>();

    // Recurrence, planning
    public DbSet<RecurringTaskDefinition> RecurringDefinitions => Set<RecurringTaskDefinition>();
    public DbSet<RecurringTaskInstance> RecurringInstances => Set<RecurringTaskInstance>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingActionItem> MeetingActionItems => Set<MeetingActionItem>();
    public DbSet<TaskTemplate> TaskTemplates => Set<TaskTemplate>();

    // Workflow
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationStep> EscalationSteps => Set<EscalationStep>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AttentionItem> AttentionItems => Set<AttentionItem>();

    // Platform
    public DbSet<SynchronizationCheckpoint> SyncCheckpoints => Set<SynchronizationCheckpoint>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<BackgroundJobExecution> JobExecutions => Set<BackgroundJobExecution>();
    public DbSet<IntegrationError> IntegrationErrors => Set<IntegrationError>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<UiEvent> UiEvents => Set<UiEvent>();
    public DbSet<IntegrationAccount> IntegrationAccounts => Set<IntegrationAccount>();
    public DbSet<CalendarEntry> CalendarEntries => Set<CalendarEntry>();

    public bool IsSqlite => Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ModelConfiguration.Apply(modelBuilder);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Creation/update stamps, optimistic-concurrency versions and the append-only audit guard.</summary>
    private void Stamp()
    {
        var now = clock.UtcNow;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.Entity is AuditLog && e.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit log entries are immutable.");

            if (e.Entity is IStamped s)
            {
                if (e.State == EntityState.Added)
                {
                    if (s.CreatedAt == default) s.CreatedAt = now;
                    s.UpdatedAt = now;
                }
                else if (e.State == EntityState.Modified) s.UpdatedAt = now;
            }

            if (e.State == EntityState.Modified)
            {
                switch (e.Entity)
                {
                    case WorkTask t: t.Version++; break;
                    case RecurringTaskDefinition d: d.Version++; break;
                    case SynchronizationCheckpoint c: c.Version++; break;
                    case ScheduledJob j: j.Version++; break;
                }
            }
        }
    }
}
