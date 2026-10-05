using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FollowUp.Core.Data;

/// <summary>
/// Fluent model configuration: keys, unique indexes that make background processing idempotent (spec §63),
/// JSON value objects, enum storage and delete behaviour (nothing cascades — tasks are never deleted, spec §78).
/// </summary>
internal static class ModelConfiguration
{
    public static void Apply(ModelBuilder b)
    {
        Organisation(b);
        Tasks(b);
        Communications(b);
        Recurrence(b);
        Planning(b);
        Workflow(b);
        Platform(b);
        Conventions(b);
    }

    private static void Organisation(ModelBuilder b)
    {
        b.Entity<Employee>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EmployeeCode).HasMaxLength(32);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.ExternalUserId).HasMaxLength(100);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.EmployeeCode).IsUnique();
            e.HasIndex(x => x.ExternalUserId);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId);
            e.HasOne<WorkCalendar>().WithMany().HasForeignKey(x => x.CalendarId);
            e.HasOne(x => x.Account).WithOne(x => x.Employee).HasForeignKey<UserAccount>(x => x.EmployeeId);
        });

        b.Entity<UserAccount>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserName).HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasMaxLength(400);
            e.Property(x => x.EntraObjectId).HasMaxLength(100);
            e.HasIndex(x => x.UserName).IsUnique();
            e.HasIndex(x => x.EmployeeId).IsUnique();
            e.HasIndex(x => x.EntraObjectId);
        });

        b.Entity<Department>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Head).WithMany().HasForeignKey(x => x.HeadId);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.ParentDepartmentId);
        });

        b.Entity<Team>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.TeamsTeamId).HasMaxLength(100);
            e.Property(x => x.TeamsChannelId).HasMaxLength(200);
            e.Property(x => x.TeamsChannelName).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne(x => x.Leader).WithMany().HasForeignKey(x => x.LeaderId);
            e.HasOne(x => x.DeputyLeader).WithMany().HasForeignKey(x => x.DeputyLeaderId);
            e.HasMany(x => x.Members).WithOne(x => x.Team).HasForeignKey(x => x.TeamId);
        });

        b.Entity<TeamMember>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.TeamId, x.EmployeeId }).IsUnique();
            e.HasIndex(x => x.EmployeeId);
            e.HasOne(x => x.Employee).WithMany(x => x.TeamMemberships).HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<WorkCalendar>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.Property(x => x.WeekendDays).HasMaxLength(100);
            e.HasMany(x => x.Holidays).WithOne().HasForeignKey(x => x.CalendarId);
        });

        b.Entity<Holiday>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.CalendarId, x.Date }).IsUnique();
        });
    }

    private static void Tasks(ModelBuilder b)
    {
        b.Entity<WorkTask>(e =>
        {
            e.ToTable("Tasks");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Number).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(400);
            e.Property(x => x.Objective).HasMaxLength(2000);
            e.Property(x => x.ExpectedResult).HasMaxLength(2000);
            e.Property(x => x.Deliverable).HasMaxLength(2000);
            e.Property(x => x.SuccessCriteria).HasMaxLength(2000);
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.BlockedReason).HasMaxLength(1000);
            e.Property(x => x.FollowUpPauseNote).HasMaxLength(1000);
            e.Property(x => x.AcknowledgementComment).HasMaxLength(2000);
            e.Property(x => x.LastUpdateSummary).HasMaxLength(1000);
            e.Property(x => x.Version).IsConcurrencyToken();
            Json(e.Property(x => x.Links));
            Json(e.Property(x => x.AttentionFactors));
            Json(e.Property(x => x.FollowUp));

            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.PrimaryAssigneeId, x.Status });
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => x.DeadlineAt);
            e.HasIndex(x => x.NextFollowUpAt);
            e.HasIndex(x => x.ParentTaskId);
            e.HasIndex(x => x.RootTaskId);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.TeamId);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.AttentionScore);
            e.HasIndex(x => x.RecurringDefinitionId);
            e.HasIndex(x => x.DelegatorId);

            e.HasOne(x => x.ParentTask).WithMany(x => x.Subtasks).HasForeignKey(x => x.ParentTaskId);
            e.HasOne(x => x.PrimaryAssignee).WithMany().HasForeignKey(x => x.PrimaryAssigneeId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.DelegatorId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.OriginalDelegatorId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.AccountableManagerId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.CreatedById);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
            e.HasOne<TaskCategory>().WithMany().HasForeignKey(x => x.CategoryId);
            e.HasMany(x => x.Assignees).WithOne().HasForeignKey(x => x.TaskId);
            e.HasMany(x => x.Followers).WithOne().HasForeignKey(x => x.TaskId);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.TaskId);
            e.Ignore(x => x.IsClosed);
            e.Ignore(x => x.IsOpen);
        });

        b.Entity<TaskAssignee>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => new { x.EmployeeId, x.RemovedAt });
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId);
        });

        b.Entity<TaskFollower>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.TaskId, x.EmployeeId }).IsUnique();
            e.HasIndex(x => x.EmployeeId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<TaskCategory>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Tag>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TaskTag>(e =>
        {
            e.HasKey(x => new { x.TaskId, x.TagId });
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
        });

        b.Entity<TaskDependency>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ExternalDescription).HasMaxLength(1000);
            e.Property(x => x.ExternalParty).HasMaxLength(200);
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.DependsOnTaskId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.DependsOnTask).WithMany().HasForeignKey(x => x.DependsOnTaskId);
        });

        b.Entity<TaskComment>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);
        });

        b.Entity<TaskStatusHistory>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.TaskId, x.ChangedAt });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<TaskDelegation>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.TaskId, x.Sequence }).IsUnique();
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<TaskAcknowledgement>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasIndex(x => x.TaskId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<Commitment>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.What).HasMaxLength(500);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => new { x.Status, x.CommitmentDate });
            e.HasIndex(x => new { x.EmployeeId, x.Status });
            e.HasIndex(x => x.CommittedToId);
            e.HasIndex(x => x.SourceMessageId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<TaskFollowUpLog>(e =>
        {
            e.ToTable("TaskFollowUps");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Reason).HasMaxLength(500);
            // One evaluation per follow-up point — a restart can never process the same point twice.
            e.HasIndex(x => new { x.TaskId, x.ScheduledAt }).IsUnique();
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<TaskEscalation>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.TaskId, x.TriggeredAt });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<CompletionSubmission>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Note).HasMaxLength(4000);
            e.HasIndex(x => new { x.TaskId, x.Round }).IsUnique();
            e.HasMany(x => x.Evidence).WithOne().HasForeignKey(x => x.SubmissionId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<CompletionEvidence>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Reference).HasMaxLength(300);
            e.HasIndex(x => x.TaskId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<TaskApproval>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasIndex(x => new { x.TaskId, x.Round, x.Stage });
            e.HasIndex(x => new { x.ApproverId, x.Decision });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.Approver).WithMany().HasForeignKey(x => x.ApproverId);
        });

        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<ExtensionRequest>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.DecisionNote).HasMaxLength(2000);
            e.HasIndex(x => new { x.TaskId, x.Status });
            e.HasIndex(x => new { x.ApproverId, x.Status });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });

        b.Entity<TaskSuggestion>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.Explanation).HasMaxLength(1000);
            e.HasIndex(x => new { x.TaskId, x.Status });
            e.HasIndex(x => x.MessageId);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });
    }

    private static void Communications(ModelBuilder b)
    {
        b.Entity<CommunicationMessage>(e =>
        {
            e.ToTable("CommunicationMessages");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ConversationKey).HasMaxLength(200);
            e.Property(x => x.ExternalId).HasMaxLength(300);
            e.Property(x => x.GraphItemId).HasMaxLength(400);
            e.Property(x => x.ConversationId).HasMaxLength(400);
            e.Property(x => x.ThreadRootExternalId).HasMaxLength(300);
            e.Property(x => x.ReplyToExternalId).HasMaxLength(300);
            e.Property(x => x.SourceKey).HasMaxLength(400);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.BodyPreview).HasMaxLength(500);
            e.Property(x => x.SenderAddress).HasMaxLength(256);
            e.Property(x => x.SenderName).HasMaxLength(200);
            e.Property(x => x.WebUrl).HasMaxLength(2000);
            e.Property(x => x.Importance).HasMaxLength(20);
            e.Property(x => x.NotificationKey).HasMaxLength(200);
            e.Property(x => x.ProcessingError).HasMaxLength(2000);
            e.Property(x => x.AnalysisSummary).HasMaxLength(1000);
            Json(e.Property(x => x.Recipients));
            Json(e.Property(x => x.Mentions));
            Json(e.Property(x => x.Attachments));
            Json(e.Property(x => x.Facts));
            // Exactly-once import (spec §63): the same Graph message can never be stored twice.
            e.HasIndex(x => new { x.Channel, x.ConversationKey, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.ProcessingStatus, x.NextProcessingAt });
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.SentAt);
            e.HasIndex(x => x.SenderEmployeeId);
            e.HasOne(x => x.SenderEmployee).WithMany().HasForeignKey(x => x.SenderEmployeeId);
        });

        b.Entity<TaskCommunicationLink>(e =>
        {
            e.ToTable("TaskMessageLinks");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Explanation).HasMaxLength(1000);
            Json(e.Property(x => x.Signals));
            e.HasIndex(x => new { x.TaskId, x.MessageId }).IsUnique();
            e.HasIndex(x => new { x.MessageId, x.Status });
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId);
        });

        b.Entity<TaskConversation>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ConversationId).HasMaxLength(400);
            e.Property(x => x.Title).HasMaxLength(500);
            e.HasIndex(x => new { x.TaskId, x.Channel, x.ConversationId }).IsUnique();
            e.HasIndex(x => new { x.Channel, x.ConversationId });
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId);
        });
    }

    private static void Recurrence(ModelBuilder b)
    {
        b.Entity<RecurringTaskDefinition>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.TitleTemplate).HasMaxLength(400);
            e.Property(x => x.PauseReason).HasMaxLength(500);
            e.Property(x => x.Version).IsConcurrencyToken();
            Json(e.Property(x => x.Rule));
            Json(e.Property(x => x.Due));
            Json(e.Property(x => x.FollowUp));
            Json(e.Property(x => x.AdditionalAssigneeIds));
            Json(e.Property(x => x.Checklist));
            e.HasIndex(x => x.SeriesId);
            e.HasIndex(x => new { x.State, x.NextOccurrenceAt });
            e.HasIndex(x => x.Code);
        });

        b.Entity<RecurringTaskInstance>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OccurrenceKey).HasMaxLength(40);
            e.Property(x => x.Reason).HasMaxLength(500);
            // A series never generates the same occurrence twice (spec §63).
            e.HasIndex(x => new { x.SeriesId, x.NominalDate }).IsUnique();
            e.HasIndex(x => x.DefinitionId);
            e.HasIndex(x => x.TaskId);
            e.HasOne<RecurringTaskDefinition>().WithMany().HasForeignKey(x => x.DefinitionId);
        });
    }

    private static void Planning(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Meeting>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.ExternalEventId).HasMaxLength(400);
            Json(e.Property(x => x.ParticipantIds));
            e.HasMany(x => x.ActionItems).WithOne().HasForeignKey(x => x.MeetingId);
            e.HasIndex(x => x.HeldAt);
        });

        b.Entity<MeetingActionItem>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Decision).HasMaxLength(2000);
            e.Property(x => x.ActionText).HasMaxLength(1000);
            e.HasIndex(x => x.TaskId);
        });

        b.Entity<TaskTemplate>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            Json(e.Property(x => x.FollowUp));
            Json(e.Property(x => x.Roles));
            Json(e.Property(x => x.Items));
            Json(e.Property(x => x.ApprovalFlow));
            e.HasIndex(x => x.Name).IsUnique();
        });
    }

    private static void Workflow(ModelBuilder b)
    {
        b.Entity<EscalationPolicy>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.AppliesTo).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.PolicyId);
        });

        b.Entity<EscalationStep>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.Property(x => x.RecipientAddress).HasMaxLength(256);
            e.Property(x => x.RecipientName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.TargetConversation).HasMaxLength(400);
            e.Property(x => x.SupersedeKey).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.ExternalMessageId).HasMaxLength(400);
            e.Property(x => x.LockedBy).HasMaxLength(200);
            // Exactly-once enqueue: the same notice can never be queued twice (spec §63).
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.RecipientEmployeeId, x.Channel, x.ReadAt });
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.SupersedeKey);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AttentionItem>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DedupeKey).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(400);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.Resolution).HasMaxLength(1000);
            e.HasIndex(x => x.DedupeKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.Score });
            e.HasIndex(x => x.TaskId);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId);
        });
    }

    private static void Platform(ModelBuilder b)
    {
        b.Entity<SynchronizationCheckpoint>(e =>
        {
            e.ToTable("SynchronizationCheckpoints");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SourceKey).HasMaxLength(400);
            e.Property(x => x.DisplayName).HasMaxLength(300);
            e.Property(x => x.LastProcessedExternalId).HasMaxLength(400);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.IntegrationType, x.SourceKey }).IsUnique();
        });

        b.Entity<ScheduledJob>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<BackgroundJobExecution>(e =>
        {
            e.ToTable("BackgroundJobExecutions");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.JobKey).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.Error).HasMaxLength(4000);
            e.HasIndex(x => new { x.JobKey, x.StartedAt });
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<IntegrationError>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Integration).HasMaxLength(100);
            e.Property(x => x.SourceKey).HasMaxLength(400);
            e.Property(x => x.Operation).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ActorName).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.Field).HasMaxLength(100);
            e.Property(x => x.OldValue).HasMaxLength(2000);
            e.Property(x => x.NewValue).HasMaxLength(2000);
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => new { x.TaskId, x.At });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.At);
        });

        b.Entity<SavedView>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<NumberSequence>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(50);
        });

        b.Entity<UiEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Topic).HasMaxLength(100);
            e.HasIndex(x => x.At);
        });

        b.Entity<IntegrationAccount>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TenantId).HasMaxLength(100);
            e.Property(x => x.ClientId).HasMaxLength(100);
            e.Property(x => x.ServiceAccountUpn).HasMaxLength(256);
            e.Property(x => x.ServiceAccountUserId).HasMaxLength(100);
            e.Property(x => x.Scopes).HasMaxLength(2000);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<CalendarEntry>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.ExternalEventId).HasMaxLength(400);
            e.Property(x => x.Fingerprint).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.TaskId, x.Kind, x.OwnerEmployeeId }).IsUnique();
        });
    }

    /// <summary>Enums stored as integers because queries compare them ordinally (criticality ≥ Important, priority, health).</summary>
    private static readonly HashSet<Type> OrdinalEnums = [typeof(Criticality), typeof(TaskPriority), typeof(TaskHealth)];

    /// <summary>
    /// Enums as strings (readable data) except [Flags] enums and ordinal enums (ints, so bit tests and range
    /// comparisons translate to SQL); no cascading deletes.
    /// </summary>
    private static void Conventions(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                if (!type.IsEnum || p.GetValueConverter() is not null) continue;
                if (type.IsDefined(typeof(FlagsAttribute), false) || OrdinalEnums.Contains(type)) continue;
                var converterType = typeof(Microsoft.EntityFrameworkCore.Storage.ValueConversion.EnumToStringConverter<>).MakeGenericType(type);
                p.SetValueConverter((Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)Activator.CreateInstance(converterType, new object?[] { null })!);
                p.SetMaxLength(40);
            }

            foreach (var fk in entity.GetForeignKeys())
                fk.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }

    private static void Json<T>(PropertyBuilder<T> p)
    {
        p.HasConversion(new JsonValueConverter<T>(), new JsonValueComparer<T>());
    }
}
