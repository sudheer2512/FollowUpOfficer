using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Core.Abstractions;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// AI ports (spec §23, §27, §28, §33, §48–§50, §56). Implemented in FollowUp.AI by a provider-independent
// layer: a deterministic heuristic engine that always works offline, optionally backed by an LLM
// (Anthropic / OpenAI-compatible). An AI failure must never block processing (spec §78) — implementations
// fall back to the heuristic engine.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public sealed record MessageForAnalysis(
    Guid MessageId,
    CommunicationChannel Channel,
    string? Subject,
    string Body,
    string? SenderName,
    Guid? SenderEmployeeId,
    DateTime SentAtUtc,
    IReadOnlyList<string> ParticipantNames);

/// <summary>A task offered to semantic matching, with the deterministic pre-score and signals Core already found.</summary>
public sealed record TaskCandidate(
    Guid TaskId,
    string Number,
    string Title,
    string? Description,
    string? ProjectName,
    IReadOnlyList<string> AssigneeNames,
    IReadOnlyList<string> Keywords,
    DateOnly? DueDate,
    WorkStatus Status,
    double PriorScore,
    IReadOnlyList<string> PriorSignals);

public sealed record CandidateScore(Guid TaskId, double Confidence, string Explanation, IReadOnlyList<string> Signals);

/// <summary>Classification of a message and the facts extracted from it.</summary>
public sealed record MessageClassification(
    MessageCategory Category,
    double Confidence,
    string Summary,
    MessageFacts Facts,
    IReadOnlyList<string> Signals);

public interface IAIClassificationService
{
    string ProviderName { get; }

    /// <summary>Scores candidate tasks for a message (0–1 confidence each, with explanation and signals).</summary>
    Task<IReadOnlyList<CandidateScore>> MatchAsync(MessageForAnalysis message, IReadOnlyList<TaskCandidate> candidates, CancellationToken ct);

    /// <summary>Classifies the message (in the context of its task when known) and extracts facts.</summary>
    Task<MessageClassification> ClassifyAsync(MessageForAnalysis message, TaskCandidate? task, BusinessCalendar calendar, CancellationToken ct);
}

public sealed record ExtractedCommitment(DateOnly Date, TimeOnly? Time, string Text, string? What, double Confidence);

/// <summary>Detects promises such as "We will complete this by Friday" (spec §28).</summary>
public interface ICommitmentExtractionService
{
    Task<IReadOnlyList<ExtractedCommitment>> ExtractAsync(string text, DateTime sentAtUtc, BusinessCalendar calendar, CancellationToken ct);
}

// ── Natural language (spec §48–§50, §56) ──

public sealed record DirectoryPerson(Guid Id, string DisplayName, string? JobTitle, string? Email, Guid? DepartmentId, IReadOnlyList<string> Aliases);

public sealed record DirectoryTeamRef(Guid Id, string Name, Guid? DepartmentId, Guid? LeaderId, IReadOnlyList<string> Aliases);

public sealed record DirectoryRef(Guid Id, string Name, IReadOnlyList<string> Aliases);

/// <summary>Who and what the parser can resolve names against.</summary>
public sealed record DirectorySnapshot(
    IReadOnlyList<DirectoryPerson> People,
    IReadOnlyList<DirectoryTeamRef> Teams,
    IReadOnlyList<DirectoryRef> Departments,
    IReadOnlyList<DirectoryRef> Projects,
    Guid? CurrentUserId);

public sealed record DraftNote(string Field, string Message);

/// <summary>Structured task extracted from a natural-language or voice command — shown to the user for confirmation.</summary>
public sealed record TaskDraft
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public Guid? PrimaryAssigneeId { get; init; }
    public List<Guid> AssigneeIds { get; init; } = [];
    public List<Guid> TeamIds { get; init; } = [];
    public Guid? DepartmentId { get; init; }
    public Guid? ProjectId { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public FollowUpSchedule? FollowUp { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public Criticality Criticality { get; init; } = Criticality.Normal;
    /// <summary>"Tell me if it gets delayed".</summary>
    public bool NotifyCeoOnDelay { get; init; }
    public bool RequiresApproval { get; init; }
    public bool IsRecurring { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    public DueRule? RecurrenceDue { get; init; }
    public double Confidence { get; init; }
    public List<DraftNote> Notes { get; init; } = [];
    public List<string> UnresolvedNames { get; init; } = [];
    public bool NeedsConfirmation { get; init; } = true;
}

public enum QuestionIntent
{
    Unknown,
    AttentionRequired,
    MissedCommitments,
    CommitmentsDue,
    NoResponse,
    Overdue,
    Blocked,
    ProjectBlockers,
    RecurringMissed,
    DueSoon,
    Critical,
    CompletedRecently,
    Workload,
    AwaitingApproval,
    TaskLookup,
    Summary,
}

/// <summary>A natural-language question resolved into a structured query Core can execute.</summary>
public sealed record QuestionQuery
{
    public QuestionIntent Intent { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? EmployeeId { get; init; }
    public Guid? ProjectId { get; init; }
    public int? MinDaysOverdue { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public Criticality? MinCriticality { get; init; }
    public string? TaskNumber { get; init; }
    /// <summary>Restrict to commitments / tasks involving the asking user ("made to me").</summary>
    public bool ToMe { get; init; }
    public string? SearchText { get; init; }
    public double Confidence { get; init; }
    /// <summary>How the question was understood, shown to the user.</summary>
    public string Interpretation { get; init; } = "";
}

public interface INaturalLanguageService
{
    string ProviderName { get; }

    Task<TaskDraft> ParseTaskCommandAsync(string command, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct);

    Task<QuestionQuery> ParseQuestionAsync(string question, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct);

    /// <summary>
    /// Optional prose paragraph for an executive summary built from deterministic, drill-down-able points.
    /// Returns null when no LLM is configured (the UI then shows the points only).
    /// </summary>
    Task<string?> NarrateAsync(string heading, IReadOnlyList<string> points, CancellationToken ct);
}
