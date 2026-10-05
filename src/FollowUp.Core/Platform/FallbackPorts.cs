using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FollowUp.Core.Platform;

/// <summary>
/// Safe stand-ins for the integration and AI ports so a host starts even when FollowUp.Integrations /
/// FollowUp.AI are not registered. Everything is TryAdd-registered: call this AFTER
/// <c>AddMicrosoft365Integration</c> and <c>AddFollowUpAI</c> so the real implementations win.
/// Outbound email / Teams report failure (the notification queue keeps the items and retries), inbound sync
/// returns nothing, and the AI stubs classify nothing.
/// </summary>
/// <summary>Marks a fallback (not configured) port implementation; services can skip work that would only fail.</summary>
public interface IDisabledPort;

public static class FallbackPorts
{
    public static IServiceCollection AddFallbackPorts(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmailService, DisabledEmailService>();
        services.TryAddSingleton<ITeamsService, DisabledTeamsService>();
        services.TryAddSingleton<IMailSynchronizationService, EmptyMailSync>();
        services.TryAddSingleton<ITeamsSynchronizationService, EmptyTeamsSync>();
        services.TryAddSingleton<ICalendarService, DisabledCalendarService>();
        services.TryAddSingleton<IUserDirectoryService, EmptyDirectory>();
        services.TryAddSingleton<IIntegrationHealth, DisabledIntegrationHealth>();
        services.TryAddSingleton<IAIClassificationService, UnclassifiedAiService>();
        services.TryAddSingleton<ICommitmentExtractionService, NoCommitmentExtraction>();
        services.TryAddSingleton<INaturalLanguageService, UnknownLanguageService>();
        return services;
    }

    private const string Disabled = "Microsoft 365 integration is not configured on this host.";

    private sealed class DisabledEmailService : IEmailService, IDisabledPort
    {
        public Task<DeliveryResult> SendAsync(OutboundEmail email, CancellationToken ct) => Task.FromResult(new DeliveryResult(false, null, Disabled));
        public Task<bool?> WasSentAsync(string idempotencyKey, DateTime sinceUtc, CancellationToken ct) => Task.FromResult<bool?>(null);
    }

    private sealed class DisabledTeamsService : ITeamsService, IDisabledPort
    {
        public Task<DeliveryResult> SendAsync(OutboundTeamsMessage message, CancellationToken ct) => Task.FromResult(new DeliveryResult(false, null, Disabled));
    }

    private static SyncPage EmptyPage(SyncCursor c) => new([], null, c.DeltaToken, c.Watermark, true);

    private sealed class EmptyMailSync : IMailSynchronizationService, IDisabledPort
    {
        public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncSourceInfo>>([]);
        public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct) => Task.FromResult(EmptyPage(cursor));
    }

    private sealed class EmptyTeamsSync : ITeamsSynchronizationService, IDisabledPort
    {
        public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncSourceInfo>>([]);
        public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct) => Task.FromResult(EmptyPage(cursor));
    }

    private sealed class DisabledCalendarService : ICalendarService, IDisabledPort
    {
        public Task<string> UpsertEventAsync(CalendarEventRequest request, CancellationToken ct) =>
            throw new IntegrationException(IntegrationErrorKind.Validation, Disabled);
        public Task DeleteEventAsync(string mailboxAddress, string eventId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyDirectory : IUserDirectoryService, IDisabledPort
    {
        public Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryUser>>([]);
        public Task<IReadOnlyList<DirectoryTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryTeam>>([]);
    }

    private sealed class DisabledIntegrationHealth(IClock clock) : IIntegrationHealth, IDisabledPort
    {
        public Task<IntegrationProbe> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new IntegrationProbe("Disabled", false, null, null, [], [], Disabled, clock.UtcNow));
    }

    private sealed class UnclassifiedAiService : IAIClassificationService, IDisabledPort
    {
        public string ProviderName => "None";

        public Task<IReadOnlyList<CandidateScore>> MatchAsync(MessageForAnalysis message, IReadOnlyList<TaskCandidate> candidates, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CandidateScore>>(candidates
                .Select(c => new CandidateScore(c.TaskId, c.PriorScore, c.PriorSignals.Count > 0 ? string.Join(", ", c.PriorSignals) : "Deterministic signals only", c.PriorSignals))
                .ToList());

        public Task<MessageClassification> ClassifyAsync(MessageForAnalysis message, TaskCandidate? task, BusinessCalendar calendar, CancellationToken ct) =>
            Task.FromResult(new MessageClassification(MessageCategory.Unclassified, 0, "", new MessageFacts(), []));
    }

    private sealed class NoCommitmentExtraction : ICommitmentExtractionService, IDisabledPort
    {
        public Task<IReadOnlyList<ExtractedCommitment>> ExtractAsync(string text, DateTime sentAtUtc, BusinessCalendar calendar, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExtractedCommitment>>([]);
    }

    private sealed class UnknownLanguageService : INaturalLanguageService, IDisabledPort
    {
        public string ProviderName => "None";

        public Task<TaskDraft> ParseTaskCommandAsync(string command, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct) =>
            Task.FromResult(new TaskDraft
            {
                Title = command.Length > 200 ? command[..200] : command,
                Confidence = 0,
                Notes = [new DraftNote("Title", "Natural-language parsing is not available; check every field before saving.")],
            });

        public Task<QuestionQuery> ParseQuestionAsync(string question, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct) =>
            Task.FromResult(new QuestionQuery { Intent = QuestionIntent.Unknown, SearchText = question, Interpretation = "Text search (question parsing is not available)" });

        public Task<string?> NarrateAsync(string heading, IReadOnlyList<string> points, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
