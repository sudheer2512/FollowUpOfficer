using System.Text.RegularExpressions;
using FollowUp.Core;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Core.Seed;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FollowUp.Tests.Core;

/// <summary>
/// The real application core over an in-memory SQLite database with a pinned clock and fake ports.
/// Org: ceo (Executive), cto (Senior manager, head of Development), lead (team leader), dev, dev2 (Dev Team), outsider.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _temp;
    public MutableClock Clock { get; } = new();
    public ServiceProvider Services { get; }
    public FakeEmail Email { get; } = new();
    public FakeTeams Teams { get; } = new();
    public FakeMailSync MailSync { get; } = new();
    public FakeTeamsSync TeamsSync { get; } = new();
    public Dictionary<string, Guid> People { get; } = [];
    public Guid TeamId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public BusinessCalendar Calendar { get; private set; } = BusinessCalendar.Default;

    /// <summary>Monday 5 Oct 2026, 10:00 IST.</summary>
    public static readonly DateTime Monday10 = new(2026, 10, 5, 4, 30, 0, DateTimeKind.Utc);

    private TestApp()
    {
        var cs = $"Data Source=file:fu{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        _temp = Path.Combine(Path.GetTempPath(), "fu-tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeysPath"] = Path.Combine(_temp, "keys"),
            ["FollowUp:StoragePath"] = Path.Combine(_temp, "files"),
            ["FollowUp:SystemSenders:0"] = "followup@test.example",
            ["Jobs:Enabled"] = "false",
        }).Build();
        Clock.Pin(Monday10);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(Clock);
        services.AddFollowUpCore(config, "Test", cs, useSqlite: true);
        services.AddSingleton<IEmailService>(Email);
        services.AddSingleton<ITeamsService>(Teams);
        services.AddSingleton<IMailSynchronizationService>(MailSync);
        services.AddSingleton<ITeamsSynchronizationService>(TeamsSync);
        services.AddSingleton<IAIClassificationService, FakeAi>();
        services.AddSingleton<ICommitmentExtractionService, FakeCommitments>();
        services.AddSingleton<INaturalLanguageService, FakeNl>();
        Services = services.BuildServiceProvider();
    }

    public static async Task<TestApp> CreateAsync()
    {
        var app = new TestApp();
        await Database.MigrateAsync(app.Services, NullLogger.Instance);
        await ConfigSeeder.RunAsync(app.Services, NullLogger.Instance, default);
        await app.SeedOrgAsync();
        using var scope = app.Services.CreateScope();
        app.Calendar = await scope.ServiceProvider.GetRequiredService<CalendarProvider>().GetDefaultAsync();
        return app;
    }

    private async Task SeedOrgAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var dept = new Department { Code = "DEV", Name = "Development" };
        DepartmentId = dept.Id;
        db.Departments.Add(dept);
        foreach (var (key, roles) in new[] { ("ceo", AppRole.Executive), ("cto", AppRole.SeniorManager), ("lead", AppRole.TeamLeader), ("dev", AppRole.TeamMember), ("dev2", AppRole.TeamMember), ("outsider", AppRole.TeamMember) })
        {
            var e = new Employee { EmployeeCode = key.ToUpperInvariant(), DisplayName = char.ToUpperInvariant(key[0]) + key[1..], Email = $"{key}@test.example", ExternalUserId = $"aad-{key}", DepartmentId = key == "outsider" ? null : dept.Id };
            db.Employees.Add(e);
            db.UserAccounts.Add(new UserAccount { EmployeeId = e.Id, UserName = key, Roles = roles });
            People[key] = e.Id;
        }
        await db.SaveChangesAsync();
        dept.HeadId = People["cto"];
        var team = new Team { Name = "Dev Team", DepartmentId = dept.Id, LeaderId = People["lead"], TeamsTeamId = "team-1", TeamsChannelId = "19:chan@thread.tacv2" };
        TeamId = team.Id;
        foreach (var m in new[] { "lead", "dev", "dev2" })
            team.Members.Add(new TeamMember { TeamId = team.Id, EmployeeId = People[m], Role = m == "lead" ? TeamMemberRole.Leader : TeamMemberRole.Member });
        db.Teams.Add(team);
        await db.SaveChangesAsync();
    }

    /// <summary>Runs work in a fresh scope as a user (or the system when null).</summary>
    public async Task<T> As<T>(string? who, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<ActorContext>();
        if (who is null) actor.SetSystem();
        else
        {
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            var account = await db.UserAccounts.AsNoTracking().FirstAsync(a => a.EmployeeId == People[who]);
            actor.SetUser(People[who], who, await ViewerFactory.BuildAsync(db, People[who], account.Roles, default));
        }
        return await work(scope.ServiceProvider);
    }

    public Task As(string? who, Func<IServiceProvider, Task> work) => As<int>(who, async sp => { await work(sp); return 0; });

    public async Task<T> Db<T>(Func<FollowUpDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<FollowUpDbContext>());
    }

    public DateTime Local(DateOnly date, int hour, int minute = 0) => Calendar.ToUtc(date, new TimeOnly(hour, minute));

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _keepAlive.Dispose();
        try { Directory.Delete(_temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeEmail : IEmailService
{
    public List<OutboundEmail> Sent { get; } = [];
    public Queue<IntegrationException> Failures { get; } = new();
    public HashSet<string> AlreadySent { get; } = [];
    public bool? WasSentAnswer { get; set; }

    public Task<DeliveryResult> SendAsync(OutboundEmail email, CancellationToken ct)
    {
        if (Failures.TryDequeue(out var ex)) throw ex;
        Sent.Add(email);
        return Task.FromResult(new DeliveryResult(true, $"msg-{Sent.Count}"));
    }

    public Task<bool?> WasSentAsync(string idempotencyKey, DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult(AlreadySent.Contains(idempotencyKey) ? true : WasSentAnswer);
}

public sealed class FakeTeams : ITeamsService
{
    public List<OutboundTeamsMessage> Sent { get; } = [];
    public Task<DeliveryResult> SendAsync(OutboundTeamsMessage message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.FromResult(new DeliveryResult(true, $"teams-{Sent.Count}"));
    }
}

/// <summary>Scripted mailbox: each FetchAsync call takes the next scripted step (a page or an exception).</summary>
public sealed class FakeMailSync : IMailSynchronizationService
{
    public Queue<Func<SyncCursor, SyncPage>> Steps { get; } = new();
    public List<SyncCursor> Cursors { get; } = [];

    public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SyncSourceInfo>>([new SyncSourceInfo(IntegrationType.OutlookMail, "ceo@test.example|inbox", "CEO inbox", "ceo@test.example")]);

    public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct)
    {
        Cursors.Add(cursor);
        return Task.FromResult(Steps.Count > 0 ? Steps.Dequeue()(cursor) : new SyncPage([], null, cursor.DeltaToken, cursor.Watermark, true));
    }
}

public sealed class FakeTeamsSync : ITeamsSynchronizationService
{
    public Task<IReadOnlyList<SyncSourceInfo>> DiscoverSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncSourceInfo>>([]);
    public Task<SyncPage> FetchAsync(SyncCursor cursor, CancellationToken ct) => Task.FromResult(new SyncPage([], null, null, cursor.Watermark, true));
}

/// <summary>Deterministic stand-in for the AI layer (the real heuristic engine has its own tests).</summary>
public sealed partial class FakeAi : IAIClassificationService
{
    public string ProviderName => "Fake";

    public Task<IReadOnlyList<CandidateScore>> MatchAsync(MessageForAnalysis message, IReadOnlyList<TaskCandidate> candidates, CancellationToken ct)
    {
        var text = $"{message.Subject} {message.Body}".ToLowerInvariant();
        return Task.FromResult<IReadOnlyList<CandidateScore>>(candidates.Select(c =>
        {
            var words = c.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToList();
            var hits = words.Count(text.Contains);
            var score = words.Count == 0 ? 0 : Math.Min(0.95, c.PriorScore + 0.6 * hits / words.Count);
            return new CandidateScore(c.TaskId, score, $"{hits} title words matched", [$"{hits} title words"]);
        }).ToList());
    }

    public Task<MessageClassification> ClassifyAsync(MessageForAnalysis message, TaskCandidate? task, BusinessCalendar calendar, CancellationToken ct)
    {
        var text = message.Body.ToLowerInvariant();
        MessageClassification Result(MessageCategory c, MessageFacts f) => new(c, 0.9, $"{c}: {message.Body}", f, [c.ToString()]);
        if (text.Contains("completed") || text.Contains("done"))
            return Task.FromResult(Result(MessageCategory.Completion, new MessageFacts { SuggestedStatus = WorkStatus.CompletedByAssignee }));
        if (WaitingFor().Match(text) is { Success: true } m)
            return Task.FromResult(Result(MessageCategory.Blocker, new MessageFacts { SuggestedStatus = WorkStatus.WaitingForExternalParty, Blocker = m.Groups[1].Value.Trim() }));
        if (text.Contains("extension till"))
            return Task.FromResult(Result(MessageCategory.DeadlineChange, new MessageFacts { ProposedDueDate = calendar.Today(message.SentAtUtc).AddDays(10) }));
        if (text.Contains("urgent"))
            return Task.FromResult(Result(MessageCategory.Escalation, new MessageFacts { Urgent = true }));
        return Task.FromResult(Result(MessageCategory.ProgressUpdate, new MessageFacts()));
    }

    [GeneratedRegex(@"waiting for ([a-z ]+?)(\.|$| from)")]
    private static partial Regex WaitingFor();
}

/// <summary>Recognises "by &lt;weekday&gt;" promises by the writer.</summary>
public sealed partial class FakeCommitments : ICommitmentExtractionService
{
    public Task<IReadOnlyList<ExtractedCommitment>> ExtractAsync(string text, DateTime sentAtUtc, BusinessCalendar calendar, CancellationToken ct)
    {
        var m = Promise().Match(text);
        if (!m.Success) return Task.FromResult<IReadOnlyList<ExtractedCommitment>>([]);
        var day = Enum.Parse<DayOfWeek>(m.Groups[1].Value, true);
        var d = calendar.Today(sentAtUtc);
        do d = d.AddDays(1); while (d.DayOfWeek != day);
        return Task.FromResult<IReadOnlyList<ExtractedCommitment>>([new ExtractedCommitment(d, null, m.Value, null, 0.9)]);
    }

    [GeneratedRegex(@"(?:will|should) (?:complete|finish|be completed)[^.]*? by (monday|tuesday|wednesday|thursday|friday)", RegexOptions.IgnoreCase)]
    private static partial Regex Promise();
}

public sealed class FakeNl : INaturalLanguageService
{
    public string ProviderName => "Fake";
    public Task<TaskDraft> ParseTaskCommandAsync(string command, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct) =>
        Task.FromResult(new TaskDraft { Title = command });
    public Task<QuestionQuery> ParseQuestionAsync(string question, DirectorySnapshot directory, DateTime nowUtc, BusinessCalendar calendar, CancellationToken ct) =>
        Task.FromResult(new QuestionQuery { Intent = QuestionIntent.Unknown, SearchText = question });
    public Task<string?> NarrateAsync(string heading, IReadOnlyList<string> points, CancellationToken ct) => Task.FromResult<string?>(null);
}
