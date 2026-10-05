using FollowUp.Core.Contracts;
using FollowUp.Domain;

namespace FollowUp.Core.Services;

// Service abstractions named in spec §77. Core task logic depends on these and on the integration / AI ports
// in FollowUp.Core.Abstractions — never on Microsoft Graph directly.

/// <summary>Task lifecycle operations.</summary>
public interface ITaskService
{
    Task<WorkTask> CreateAsync(CreateTaskRequest request, CancellationToken ct);
    Task<WorkTask> UpdateAsync(Guid id, UpdateTaskRequest request, CancellationToken ct);
    Task<WorkTask> ReassignAsync(Guid id, ReassignRequest request, CancellationToken ct);
    Task<WorkTask> AcknowledgeAsync(Guid id, AcknowledgeRequest request, CancellationToken ct);
    Task<WorkTask> ChangeStatusAsync(Guid id, StatusChangeRequest request, CancellationToken ct);
    Task<TaskComment> CommentAsync(Guid id, CommentRequest request, CancellationToken ct);
    Task<WorkTask> CompleteAsync(Guid id, CompleteRequest request, CancellationToken ct);
    Task<WorkTask> DecideApprovalAsync(Guid id, ApprovalRequest request, CancellationToken ct);
    Task<ExtensionRequest> RequestExtensionAsync(Guid id, ExtensionRequestInput request, CancellationToken ct);
    Task<WorkTask> DecideExtensionAsync(Guid extensionId, ExtensionDecisionRequest request, CancellationToken ct);
    Task<WorkTask> SnoozeAsync(Guid id, SnoozeRequest request, CancellationToken ct);
    Task<int> SendReminderAsync(Guid id, ReminderRequest request, CancellationToken ct);
    Task<TaskEscalation> EscalateAsync(Guid id, EscalateRequest request, CancellationToken ct);
}

/// <summary>Recurring definitions and occurrence generation.</summary>
public interface IRecurringTaskService
{
    Task<int> GenerateDueAsync(CancellationToken ct);
    Task<RecurringTaskDefinition> CreateAsync(RecurringDefinitionInput input, CancellationToken ct);
    Task<RecurringTaskDefinition> UpdateAsync(Guid id, RecurringUpdateRequest request, CancellationToken ct);
    Task<RecurringTaskDefinition> PauseAsync(Guid id, RecurringPauseRequest request, CancellationToken ct);
    Task<RecurringTaskDefinition> ResumeAsync(Guid id, CancellationToken ct);
    Task<RecurringTaskDefinition> EndAsync(Guid id, string? reason, CancellationToken ct);
    Task<RecurringTaskInstance> SkipAsync(Guid id, SkipOccurrenceRequest request, CancellationToken ct);
    Task<List<OccurrencePreview>> PreviewAsync(PreviewRequest request, CancellationToken ct);
}

/// <summary>Follow-up reminders and deadline notices.</summary>
public interface IReminderService
{
    Task<int> ProcessDueFollowUpsAsync(CancellationToken ct);
    Task<int> ProcessDeadlinesAsync(CancellationToken ct);
}

/// <summary>Escalation ladders.</summary>
public interface IEscalationService
{
    Task<int> ProcessAsync(CancellationToken ct);
}

/// <summary>Persistent notification queue (enqueue side).</summary>
public interface INotificationService
{
    Task<int> EnqueueAsync(NoticeRequest notice, CancellationToken ct);
    Task<bool> EnqueueChannelAsync(string key, WorkTask task, Team team, NotificationCategory category, string headline, string? reason, CancellationToken ct);
    Task<int> RemindersTodayAsync(Guid recipientId, CancellationToken ct);
}

/// <summary>Durable synchronisation checkpoints for mail and Teams.</summary>
public interface ISynchronizationCheckpointService
{
    Task<int> DiscoverAsync(CancellationToken ct);
    Task<int> SyncMailAsync(CancellationToken ct);
    Task<int> SyncTeamsAsync(CancellationToken ct);
    Task<List<CheckpointDto>> ListAsync(CancellationToken ct);
    Task ResetAsync(Guid id, bool fromScratch, CancellationToken ct);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct);
    Task RunNowAsync(CancellationToken ct);
}
