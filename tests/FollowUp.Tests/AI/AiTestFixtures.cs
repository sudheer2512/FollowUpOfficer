using System.Text.Json;
using FollowUp.AI;
using FollowUp.AI.Llm;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.Extensions.Options;

namespace FollowUp.Tests.AI;

/// <summary>Shared deterministic fixtures: an IST calendar, a pinned "now" and a small directory.</summary>
internal static class AiFixtures
{
    /// <summary>Asia/Kolkata, Saturday / Sunday weekend, work day 09:30–18:30.</summary>
    public static readonly BusinessCalendar Cal = new(BusinessCalendar.ResolveTimeZone("Asia/Kolkata"), [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    /// <summary>Wednesday 7 Oct 2026, 10:00 IST.</summary>
    public static readonly DateTime Now = new(2026, 10, 7, 4, 30, 0, DateTimeKind.Utc);

    public static readonly Guid CeoId = new("00000000-0000-0000-0000-0000000000c0");
    public static readonly Guid RahulId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid PriyaId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid AnitaId = new("00000000-0000-0000-0000-000000000003");
    public static readonly Guid VikramId = new("00000000-0000-0000-0000-000000000004");
    public static readonly Guid DevTeamId = new("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid DevDeptId = new("00000000-0000-0000-0000-0000000000d1");
    public static readonly Guid FinanceDeptId = new("00000000-0000-0000-0000-0000000000d2");
    public static readonly Guid QaDeptId = new("00000000-0000-0000-0000-0000000000d3");
    public static readonly Guid SupportDeptId = new("00000000-0000-0000-0000-0000000000d4");
    public static readonly Guid EInvoiceProjectId = new("00000000-0000-0000-0000-0000000000b1");

    public static readonly DirectorySnapshot Dir = new(
        People:
        [
            new DirectoryPerson(CeoId, "Arjun Mehta", "Chief Executive Officer", "arjun.mehta@contoso.in", null, ["CEO"]),
            new DirectoryPerson(RahulId, "Rahul Sharma", "Developer", "rahul.sharma@contoso.in", DevDeptId, []),
            new DirectoryPerson(PriyaId, "Priya Nair", "Finance Manager", "priya.nair@contoso.in", FinanceDeptId, []),
            new DirectoryPerson(AnitaId, "Anita Desai", "QA Lead", "anita.desai@contoso.in", QaDeptId, []),
            new DirectoryPerson(VikramId, "Vikram Rao", "Engineering Manager", "vikram.rao@contoso.in", DevDeptId, ["Vicky"]),
        ],
        Teams: [new DirectoryTeamRef(DevTeamId, "Development Team", DevDeptId, VikramId, ["development", "dev team"])],
        Departments:
        [
            new DirectoryRef(DevDeptId, "Development", []),
            new DirectoryRef(FinanceDeptId, "Finance", []),
            new DirectoryRef(QaDeptId, "QA", ["Quality Assurance"]),
            new DirectoryRef(SupportDeptId, "Support", []),
        ],
        Projects: [new DirectoryRef(EInvoiceProjectId, "UAE e-Invoicing Launch", ["eInvoice", "e-invoicing"])],
        CurrentUserId: CeoId);

    public static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

    public static MessageForAnalysis Message(string body, string? subject = null, string? sender = "Rahul Sharma", DateTime? sentAt = null) =>
        new(Guid.NewGuid(), CommunicationChannel.Email, subject, body, sender, null, sentAt ?? Now, []);

    public static TaskCandidate Candidate(string title, string number, IReadOnlyList<string> assignees, string? description = null,
        string? project = null, IReadOnlyList<string>? keywords = null, DateOnly? due = null, double prior = 0, IReadOnlyList<string>? priorSignals = null) =>
        new(Guid.NewGuid(), number, title, description, project, assignees, keywords ?? [], due, WorkStatus.InProgress, prior, priorSignals ?? []);

    public static IOptions<AiOptions> Options(int timeoutSeconds = 30, int breakerFailures = 3, int breakerMinutes = 10, double minMatch = 0.2) =>
        Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Provider = "Fake",
            TimeoutSeconds = timeoutSeconds,
            CircuitBreakerFailures = breakerFailures,
            CircuitBreakerMinutes = breakerMinutes,
            MinMatchConfidenceForLlm = minMatch,
        });

    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>Scriptable LLM: returns <see cref="Respond"/>'s answer, throws <see cref="Throw"/>, or hangs until cancelled.</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private int _calls;

    public Func<LlmJsonRequest, JsonElement>? Respond { get; set; }
    public Exception? Throw { get; set; }
    public bool Hang { get; set; }
    public int Calls => _calls;
    public List<LlmJsonRequest> Requests { get; } = [];

    public string Name => "Fake (test-model)";
    public bool IsConfigured => true;

    public async Task<JsonElement> CompleteJsonAsync(LlmJsonRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests) Requests.Add(request);
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (Throw is not null) throw Throw;
        return Respond is null ? AiFixtures.Json(new { }) : Respond(request);
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
