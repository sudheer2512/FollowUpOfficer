using System.Text.Json;
using FollowUp.AI;
using FollowUp.AI.Llm;
using FollowUp.AI.Services;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

/// <summary>The LLM layer must never change the outcome when it fails, and must never let invented ids through.</summary>
public class LlmFallbackTests
{
    private const string SpecMessage = "We are waiting for API credentials from the provider. We should finish by Friday.";
    private const string SpecCommand = "Ask Rahul and the development team to finish the dealer CRM screens by Friday. Follow up every two days and tell me if it gets delayed.";

    private static readonly TaskCandidate Crm = Candidate("Finish the dealer CRM screens", "TASK-2026-00142", ["Rahul Sharma"], project: "Dealer CRM", due: D(10, 9));
    private static readonly TaskCandidate Gst = Candidate("File the GST return for September", "TASK-2026-00150", ["Priya Nair"]);

    private static (AiClassificationService Classification, CommitmentExtractionService Commitments, NaturalLanguageService Language, LlmGateway Gateway)
        Services(ILlmClient llm, int timeoutSeconds = 30, TimeProvider? time = null)
    {
        var gateway = new LlmGateway(llm, Options(timeoutSeconds), time);
        return (new AiClassificationService(gateway), new CommitmentExtractionService(gateway), new NaturalLanguageService(gateway), gateway);
    }

    private static readonly AiClassificationService HeuristicClassification = AiClassificationService.CreateHeuristic();
    private static readonly NaturalLanguageService HeuristicLanguage = NaturalLanguageService.CreateHeuristic();

    public static TheoryData<string> Failures => ["llm-exception", "refusal", "http", "invalid-json", "out-of-range"];

    private static FakeLlmClient Failing(string kind) => kind switch
    {
        "llm-exception" => new FakeLlmClient { Throw = new LlmException(LlmFailureKind.ServerError, "boom") },
        "refusal" => new FakeLlmClient { Throw = new LlmException(LlmFailureKind.Refusal, "declined") },
        "http" => new FakeLlmClient { Throw = new HttpRequestException("connection reset") },
        "invalid-json" => new FakeLlmClient { Respond = _ => Json(new { category = "Banana", confidence = "high" }) },
        _ => new FakeLlmClient { Respond = _ => Json(new { category = "Delay", confidence = 7.5, summary = "x", commitmentDate = "not-a-date" }) },
    };

    // ───────────────────────────── classification ─────────────────────────────

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Classification_falls_back_to_the_heuristic_result(string failure)
    {
        var llm = Failing(failure);
        var (svc, _, _, _) = Services(llm);
        var message = Message(SpecMessage);

        var expected = await HeuristicClassification.ClassifyAsync(message, null, Cal, CancellationToken.None);
        var actual = await svc.ClassifyAsync(message, null, Cal, CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        AssertSame(expected, actual);
    }

    [Fact]
    public async Task Valid_llm_classification_wins_and_keeps_heuristic_signals()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                category = "Blocker", confidence = 0.93, summary = "Waiting for provider credentials; promises Friday.",
                suggestedStatus = "WaitingForExternalParty", blocker = "API credentials from the provider",
                commitmentDate = "2026-10-09", commitmentText = "We should finish by Friday", proposedDueDate = "",
                progressPercent = -1, urgent = false, signals = new[] { "waiting for API credentials" },
            }),
        };
        var (svc, _, _, _) = Services(llm);

        var r = await svc.ClassifyAsync(Message(SpecMessage), null, Cal, CancellationToken.None);

        Assert.Equal(MessageCategory.Blocker, r.Category);
        Assert.Equal(0.93, r.Confidence);
        Assert.Equal("Waiting for provider credentials; promises Friday.", r.Summary);
        Assert.Equal(D(10, 9), r.Facts.CommitmentDate);
        Assert.Contains(r.Signals, s => s.StartsWith("waiting for", StringComparison.Ordinal));          // heuristic signal kept
        Assert.Contains(r.Signals, s => s.StartsWith("Fake (test-model):", StringComparison.Ordinal));   // LLM signal recorded
        Assert.Equal("Fake (test-model)", svc.ProviderName);
        Assert.Contains("heuristicHint", llm.Requests[0].UserPrompt, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, llm.Requests[0].JsonSchema.ValueKind);
    }

    [Fact]
    public async Task Out_of_range_llm_dates_fall_back_field_by_field()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                category = "Blocker", confidence = 0.9, summary = "s", suggestedStatus = "NotAStatus", blocker = "",
                commitmentDate = "2031-01-01", commitmentText = "", proposedDueDate = "", progressPercent = 250, urgent = false, signals = Array.Empty<string>(),
            }),
        };
        var (svc, _, _, _) = Services(llm);

        var r = await svc.ClassifyAsync(Message(SpecMessage), null, Cal, CancellationToken.None);

        Assert.Equal(D(10, 9), r.Facts.CommitmentDate);                         // heuristic date, the LLM's 2031 date is rejected
        Assert.Equal(WorkStatus.WaitingForExternalParty, r.Facts.SuggestedStatus);  // unknown status → heuristic
        Assert.Null(r.Facts.ProgressPercent);                                    // 250 % rejected
    }

    // ───────────────────────────── matching ─────────────────────────────

    [Fact]
    public async Task Matching_ignores_invented_task_ids()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new { scores = new[] { new { taskId = Guid.NewGuid().ToString(), confidence = 0.99, explanation = "made up" } } }),
        };
        var (svc, _, _, _) = Services(llm);
        var message = Message("Dealer CRM screens are ready.", sender: "Rahul Sharma");

        var expected = await HeuristicClassification.MatchAsync(message, [Crm, Gst], CancellationToken.None);
        var actual = await svc.MatchAsync(message, [Crm, Gst], CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(expected.Select(s => (s.TaskId, s.Confidence, s.Explanation)), actual.Select(s => (s.TaskId, s.Confidence, s.Explanation)));
        Assert.Equal(expected.SelectMany(s => s.Signals), actual.SelectMany(s => s.Signals));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Matching_falls_back_on_any_failure(string failure)
    {
        var (svc, _, _, _) = Services(Failing(failure));
        var message = Message("Dealer CRM screens are ready.", sender: "Rahul Sharma");

        var expected = await HeuristicClassification.MatchAsync(message, [Crm, Gst], CancellationToken.None);
        var actual = await svc.MatchAsync(message, [Crm, Gst], CancellationToken.None);

        Assert.Equal(expected.Select(s => (s.TaskId, s.Confidence)), actual.Select(s => (s.TaskId, s.Confidence)));
    }

    [Fact]
    public async Task Valid_llm_scores_win_and_ids_are_constrained_in_the_schema()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                scores = new[]
                {
                    new { taskId = Crm.TaskId.ToString(), confidence = 0.97, explanation = "About the CRM screens." },
                    new { taskId = Gst.TaskId.ToString(), confidence = 0.02, explanation = "Unrelated." },
                },
            }),
        };
        var (svc, _, _, _) = Services(llm);

        var scores = await svc.MatchAsync(Message("Dealer CRM screens are ready.", sender: "Rahul Sharma"), [Crm, Gst], CancellationToken.None);

        Assert.Equal(Crm.TaskId, scores[0].TaskId);
        Assert.Equal(0.97, scores[0].Confidence);
        Assert.Equal("About the CRM screens.", scores[0].Explanation);
        Assert.Contains("sender is the assignee", scores[0].Signals);
        Assert.Contains(Crm.TaskId.ToString(), llm.Requests[0].JsonSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_skips_the_llm_when_no_candidate_is_plausible()
    {
        var llm = new FakeLlmClient();
        var (svc, _, _, _) = Services(llm);

        await svc.MatchAsync(Message("Lunch at 1?", sender: "Anita Desai"), [Crm, Gst], CancellationToken.None);

        Assert.Equal(0, llm.Calls);
    }

    // ───────────────────────────── commitments ─────────────────────────────

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Commitments_fall_back_on_any_failure(string failure)
    {
        var (_, svc, _, _) = Services(Failing(failure));

        var actual = await svc.ExtractAsync(SpecMessage, Now, Cal, CancellationToken.None);

        var c = Assert.Single(actual);
        Assert.Equal(D(10, 9), c.Date);
        Assert.Equal(new FollowUp.AI.Heuristics.HeuristicCommitmentExtractor().Extract(SpecMessage, Now, Cal), actual);
    }

    [Fact]
    public async Task Commitments_drop_llm_items_with_out_of_range_dates()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                commitments = new[]
                {
                    new { date = "2026-10-09", time = "", text = "We should finish by Friday", what = "finish", confidence = 0.8 },
                    new { date = "2020-01-01", time = "", text = "We should finish by Friday", what = "", confidence = 0.9 },
                },
            }),
        };
        var (_, svc, _, _) = Services(llm);

        var c = Assert.Single(await svc.ExtractAsync(SpecMessage, Now, Cal, CancellationToken.None));
        Assert.Equal(D(10, 9), c.Date);
        Assert.Equal("finish", c.What);
    }

    [Fact]
    public async Task Commitments_skip_the_llm_when_the_text_has_no_date()
    {
        var llm = new FakeLlmClient();
        var (_, svc, _, _) = Services(llm);

        Assert.Empty(await svc.ExtractAsync("We will look into it.", Now, Cal, CancellationToken.None));
        Assert.Equal(0, llm.Calls);
    }

    // ───────────────────────────── natural language ─────────────────────────────

    [Fact]
    public async Task Command_with_invented_ids_returns_the_heuristic_draft()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                title = "Finish the dealer CRM screens", description = "", primaryAssigneeId = Guid.NewGuid().ToString(),
                assigneeIds = Array.Empty<string>(), teamIds = Array.Empty<string>(), departmentId = "", projectId = "", startDate = "",
                dueDate = "2026-10-09", dueTime = "", followUpFrequency = "EveryNDays", followUpIntervalDays = 2, followUpWeekdays = Array.Empty<string>(),
                priority = "Normal", criticality = "Important", notifyCeoOnDelay = true, requiresApproval = false, isRecurring = false,
                recurrenceFrequency = "None", recurrenceInterval = 1, recurrenceDaysOfWeek = Array.Empty<string>(), recurrenceMonthlyPattern = "DayOfMonth",
                recurrenceDayOfMonth = 1, recurrenceCreateTime = "", dueRuleKind = "None", dueRuleValue = 0, dueRuleWeekday = "Friday", dueRuleTime = "",
                unresolvedNames = Array.Empty<string>(), notes = Array.Empty<object>(), confidence = 0.99,
            }),
        };
        var (_, _, svc, _) = Services(llm);

        var expected = await HeuristicLanguage.ParseTaskCommandAsync(SpecCommand, Dir, Now, Cal, CancellationToken.None);
        var actual = await svc.ParseTaskCommandAsync(SpecCommand, Dir, Now, Cal, CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        AssertSame(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Command_falls_back_on_any_failure(string failure)
    {
        var (_, _, svc, _) = Services(Failing(failure));

        var expected = await HeuristicLanguage.ParseTaskCommandAsync(SpecCommand, Dir, Now, Cal, CancellationToken.None);
        var actual = await svc.ParseTaskCommandAsync(SpecCommand, Dir, Now, Cal, CancellationToken.None);

        AssertSame(expected, actual);
    }

    [Fact]
    public async Task Valid_llm_command_is_used_and_checked_against_the_directory()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                title = "finish the dealer CRM screens", description = "", primaryAssigneeId = RahulId.ToString(),
                assigneeIds = new[] { RahulId.ToString() }, teamIds = new[] { DevTeamId.ToString() }, departmentId = "", projectId = "", startDate = "",
                dueDate = "2026-10-09", dueTime = "", followUpFrequency = "EveryNDays", followUpIntervalDays = 2, followUpWeekdays = Array.Empty<string>(),
                priority = "Normal", criticality = "Normal", notifyCeoOnDelay = true, requiresApproval = false, isRecurring = false,
                recurrenceFrequency = "None", recurrenceInterval = 1, recurrenceDaysOfWeek = Array.Empty<string>(), recurrenceMonthlyPattern = "DayOfMonth",
                recurrenceDayOfMonth = 1, recurrenceCreateTime = "", dueRuleKind = "None", dueRuleValue = 0, dueRuleWeekday = "Friday", dueRuleTime = "",
                unresolvedNames = Array.Empty<string>(), notes = new[] { new { field = "DueDate", message = "by Friday → 9 Oct" } }, confidence = 0.95,
            }),
        };
        var (_, _, svc, _) = Services(llm);

        var d = await svc.ParseTaskCommandAsync(SpecCommand, Dir, Now, Cal, CancellationToken.None);

        Assert.Equal("Finish the dealer CRM screens", d.Title);
        Assert.Equal(RahulId, d.PrimaryAssigneeId);
        Assert.Equal([DevTeamId], d.TeamIds);
        Assert.Equal(Criticality.Important, d.Criticality);   // delay alerts imply at least Important
        Assert.Equal(2, d.FollowUp!.IntervalDays);
        Assert.Contains(d.Notes, n => n.Field == "Source" && n.Message.Contains("Fake (test-model)", StringComparison.Ordinal));
        Assert.False(d.NeedsConfirmation);
    }

    [Fact]
    public async Task Question_with_an_invented_task_number_returns_the_heuristic_query()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                intent = "TaskLookup", departmentId = "", teamId = "", employeeId = "", projectId = "", minDaysOverdue = -1, from = "", to = "",
                minCriticality = "None", taskNumber = "TASK-2026-09999", toMe = false, searchText = "", confidence = 0.9, interpretation = "Status of TASK-2026-09999",
            }),
        };
        var (_, _, svc, _) = Services(llm);
        const string question = "Show Finance tasks overdue by more than five days.";

        var expected = await HeuristicLanguage.ParseQuestionAsync(question, Dir, Now, Cal, CancellationToken.None);
        var actual = await svc.ParseQuestionAsync(question, Dir, Now, Cal, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(QuestionIntent.Overdue, actual.Intent);
    }

    [Fact]
    public async Task Question_with_an_unknown_department_id_returns_the_heuristic_query()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                intent = "Overdue", departmentId = Guid.NewGuid().ToString(), teamId = "", employeeId = "", projectId = "", minDaysOverdue = 6, from = "", to = "",
                minCriticality = "None", taskNumber = "", toMe = false, searchText = "", confidence = 0.9, interpretation = "x",
            }),
        };
        var (_, _, svc, _) = Services(llm);
        const string question = "Show Finance tasks overdue by more than five days.";

        Assert.Equal(await HeuristicLanguage.ParseQuestionAsync(question, Dir, Now, Cal, CancellationToken.None),
                     await svc.ParseQuestionAsync(question, Dir, Now, Cal, CancellationToken.None));
    }

    [Fact]
    public async Task Valid_llm_question_is_used()
    {
        var llm = new FakeLlmClient
        {
            Respond = _ => Json(new
            {
                intent = "Overdue", departmentId = FinanceDeptId.ToString(), teamId = "", employeeId = "", projectId = "", minDaysOverdue = 6, from = "", to = "",
                minCriticality = "None", taskNumber = "", toMe = false, searchText = "", confidence = 0.97, interpretation = "Finance work more than five days late",
            }),
        };
        var (_, _, svc, _) = Services(llm);

        var q = await svc.ParseQuestionAsync("Show Finance tasks overdue by more than five days.", Dir, Now, Cal, CancellationToken.None);

        Assert.Equal(FinanceDeptId, q.DepartmentId);
        Assert.Equal(6, q.MinDaysOverdue);
        Assert.Equal("Finance work more than five days late", q.Interpretation);
    }

    // ───────────────────────────── narration ─────────────────────────────

    [Fact]
    public async Task Narration_is_null_without_an_llm() =>
        Assert.Null(await HeuristicLanguage.NarrateAsync("Weekly review", ["3 tasks are overdue"], CancellationToken.None));

    [Fact]
    public async Task Narration_is_used_when_it_only_restates_the_points()
    {
        var llm = new FakeLlmClient { Respond = _ => Json(new { paragraph = "Three items need attention. Finance has 3 overdue tasks, the oldest 12 days late." }) };
        var (_, _, svc, _) = Services(llm);

        var text = await svc.NarrateAsync("Weekly review", ["Finance: 3 overdue tasks", "Oldest overdue: 12 days"], CancellationToken.None);

        Assert.Equal("Three items need attention. Finance has 3 overdue tasks, the oldest 12 days late.", text);
    }

    [Fact]
    public async Task Narration_with_invented_numbers_is_rejected()
    {
        var llm = new FakeLlmClient { Respond = _ => Json(new { paragraph = "Finance has 7 overdue tasks costing 40 lakh." }) };
        var (_, _, svc, _) = Services(llm);

        Assert.Null(await svc.NarrateAsync("Weekly review", ["Finance: 3 overdue tasks"], CancellationToken.None));
    }

    // ───────────────────────────── timeout, cancellation, circuit breaker ─────────────────────────────

    [Fact]
    public async Task Timeout_falls_back_to_the_heuristic_result()
    {
        var llm = new FakeLlmClient { Hang = true };
        var (svc, _, _, _) = Services(llm, timeoutSeconds: 1);
        var message = Message(SpecMessage);

        var actual = await svc.ClassifyAsync(message, null, Cal, CancellationToken.None);

        AssertSame(await HeuristicClassification.ClassifyAsync(message, null, Cal, CancellationToken.None), actual);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        var (svc, _, _, _) = Services(new FakeLlmClient { Hang = true });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ClassifyAsync(Message(SpecMessage), null, Cal, cts.Token));
    }

    [Fact]
    public async Task Circuit_opens_after_consecutive_failures_and_closes_after_the_pause()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 4, 30, 0, TimeSpan.Zero));
        var llm = new FakeLlmClient { Throw = new LlmException(LlmFailureKind.ServerError, "down") };
        var (svc, _, _, gateway) = Services(llm, time: time);

        for (var i = 0; i < 5; i++) await svc.ClassifyAsync(Message(SpecMessage), null, Cal, CancellationToken.None);

        Assert.Equal(3, llm.Calls);            // default: 3 failures open the circuit
        Assert.True(gateway.Breaker.IsOpen);
        Assert.False(gateway.IsAvailable);

        time.Now = time.Now.AddMinutes(11);    // default pause: 10 minutes
        llm.Throw = null;
        llm.Respond = _ => Json(new
        {
            category = "Blocker", confidence = 0.9, summary = "s", suggestedStatus = "None", blocker = "", commitmentDate = "", commitmentText = "",
            proposedDueDate = "", progressPercent = -1, urgent = false, signals = Array.Empty<string>(),
        });
        await svc.ClassifyAsync(Message(SpecMessage), null, Cal, CancellationToken.None);

        Assert.Equal(4, llm.Calls);
        Assert.False(gateway.Breaker.IsOpen);
        Assert.Equal(0, gateway.Breaker.ConsecutiveFailures);
    }

    [Fact]
    public void Breaker_reopens_immediately_when_the_trial_call_fails()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new LlmCircuitBreaker(2, TimeSpan.FromMinutes(1), time);

        Assert.False(breaker.RecordFailure());
        Assert.True(breaker.RecordFailure());
        Assert.False(breaker.AllowRequest());

        time.Now += TimeSpan.FromMinutes(2);
        Assert.True(breaker.AllowRequest());
        Assert.True(breaker.RecordFailure());
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public async Task Invalid_answers_count_towards_the_circuit_breaker()
    {
        var llm = new FakeLlmClient { Respond = _ => Json(new { category = "Banana" }) };
        var (svc, _, _, gateway) = Services(llm);

        for (var i = 0; i < 4; i++) await svc.ClassifyAsync(Message(SpecMessage), null, Cal, CancellationToken.None);

        Assert.Equal(3, llm.Calls);
        Assert.True(gateway.Breaker.IsOpen);
    }

    // ───────────────────────────── dependency injection ─────────────────────────────

    [Fact]
    public void Default_registration_is_heuristic_only()
    {
        using var sp = Build(new Dictionary<string, string?>());

        Assert.Equal("Heuristic", sp.GetRequiredService<IAIClassificationService>().ProviderName);
        Assert.Equal("Heuristic", sp.GetRequiredService<INaturalLanguageService>().ProviderName);
        Assert.IsType<NullLlmClient>(sp.GetRequiredService<ILlmClient>());
        Assert.NotNull(sp.GetRequiredService<ICommitmentExtractionService>());
        Assert.Same(sp.GetRequiredService<IAIClassificationService>(), sp.GetRequiredService<AiClassificationService>());
    }

    [Fact]
    public void Anthropic_registration_uses_the_configured_model_without_calling_the_api()
    {
        using var sp = Build(new Dictionary<string, string?> { ["Ai:Provider"] = "Anthropic", ["Ai:ApiKey"] = "test-key-not-used" });

        var llm = Assert.IsType<AnthropicLlmClient>(sp.GetRequiredService<ILlmClient>());
        Assert.True(llm.IsConfigured);
        Assert.Equal("Anthropic (claude-opus-5-5)", sp.GetRequiredService<IAIClassificationService>().ProviderName);
    }

    [Fact]
    public void Openai_compatible_registration_needs_a_base_url_and_model()
    {
        using var unconfigured = Build(new Dictionary<string, string?> { ["Ai:Provider"] = "OpenAICompatible" });
        Assert.Equal("Heuristic", unconfigured.GetRequiredService<INaturalLanguageService>().ProviderName);

        using var sp = Build(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "OpenAICompatible", ["Ai:BaseUrl"] = "http://localhost:11434/v1", ["Ai:Model"] = "llama3.1",
        });
        Assert.Equal("OpenAI-compatible (llama3.1)", sp.GetRequiredService<INaturalLanguageService>().ProviderName);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddFollowUpAI(configuration).BuildServiceProvider();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static void AssertSame(MessageClassification expected, MessageClassification actual)
    {
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Signals, actual.Signals);
        Assert.Equal(expected.Facts.SuggestedStatus, actual.Facts.SuggestedStatus);
        Assert.Equal(expected.Facts.Blocker, actual.Facts.Blocker);
        Assert.Equal(expected.Facts.CommitmentDate, actual.Facts.CommitmentDate);
        Assert.Equal(expected.Facts.CommitmentText, actual.Facts.CommitmentText);
        Assert.Equal(expected.Facts.ProposedDueDate, actual.Facts.ProposedDueDate);
        Assert.Equal(expected.Facts.ProgressPercent, actual.Facts.ProgressPercent);
        Assert.Equal(expected.Facts.Urgent, actual.Facts.Urgent);
        Assert.Equal(expected.Facts.Signals, actual.Facts.Signals);
    }

    private static void AssertSame(TaskDraft expected, TaskDraft actual)
    {
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.PrimaryAssigneeId, actual.PrimaryAssigneeId);
        Assert.Equal(expected.AssigneeIds, actual.AssigneeIds);
        Assert.Equal(expected.TeamIds, actual.TeamIds);
        Assert.Equal(expected.DepartmentId, actual.DepartmentId);
        Assert.Equal(expected.DueDate, actual.DueDate);
        Assert.Equal(expected.DueTime, actual.DueTime);
        Assert.Equal(expected.FollowUp?.Describe(), actual.FollowUp?.Describe());
        Assert.Equal(expected.Recurrence?.Describe(), actual.Recurrence?.Describe());
        Assert.Equal(expected.RecurrenceDue?.Describe(), actual.RecurrenceDue?.Describe());
        Assert.Equal(expected.RequiresApproval, actual.RequiresApproval);
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.Criticality, actual.Criticality);
        Assert.Equal(expected.NotifyCeoOnDelay, actual.NotifyCeoOnDelay);
        Assert.Equal(expected.IsRecurring, actual.IsRecurring);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.UnresolvedNames, actual.UnresolvedNames);
        Assert.Equal(expected.NeedsConfirmation, actual.NeedsConfirmation);
    }
}
