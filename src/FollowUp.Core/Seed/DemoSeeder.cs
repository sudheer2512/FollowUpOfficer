using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Seed;

/// <summary>
/// Demo data for a fresh database: a realistic organisation, projects, recurring tasks and about four weeks of
/// delegation history. History is replayed THROUGH THE REAL SERVICES with a pinned clock (reminders, escalations,
/// commitments, approvals, recurrence generation and a simulated Friday-to-Monday server outage are all produced
/// by the actual engine). Microsoft 365 emulator content (emails, Teams chats and channel posts) is left
/// unprocessed so the live background processor ingests, correlates and classifies it after start-up.
/// </summary>
public static class DemoSeeder
{
    public const string Password = "FollowUp@2026";
    public const string Domain = "wings.example";
    public const string ServiceAccount = "followup@" + Domain;

    /// <summary>Graph / Entra object id used for a mailbox in the emulator tenant — same formula as GraphEmulatorIds.ForUser.</summary>
    public static string ExternalId(string email) => Ids.Deterministic("m365-user", email.Trim().ToLowerInvariant()).ToString();

    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using (var check = services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            if (await db.Employees.AnyAsync(ct)) return;
        }
        if (services.GetRequiredService<IClock>() is not MutableClock clock)
        {
            logger.LogWarning("Demo seeding needs a MutableClock registration; skipped");
            return;
        }
        var started = DateTime.UtcNow;
        var runner = new Runner(services, clock, logger);
        try
        {
            await runner.RunAsync(ct);
        }
        finally { clock.Release(); }
        logger.LogInformation("Demo data seeded in {Seconds:N0} s", (DateTime.UtcNow - started).TotalSeconds);
    }

    private sealed record Person(string Key, string Name, string Title, string Dept, string? Manager, AppRole Roles);

    private sealed class Runner(IServiceProvider root, MutableClock clock, ILogger logger)
    {
        private readonly Dictionary<string, Guid> _people = [];
        private readonly Dictionary<string, Guid> _teams = [];
        private readonly Dictionary<string, Guid> _depts = [];
        private readonly Dictionary<string, Guid> _projects = [];
        private readonly Dictionary<string, Guid> _tasks = [];
        private readonly Dictionary<string, Guid> _recurring = [];
        private readonly List<(DateTime At, string Label, Func<Task> Action)> _events = [];
        private BusinessCalendar _cal = BusinessCalendar.Default;
        private DateOnly _today;
        private DateTime _realNow;

        private static readonly Person[] People =
        [
            new("anand", "Anand Rao", "Chief Executive Officer", "EXEC", null, AppRole.Executive),
            new("vikram", "Vikram Mehta", "Chief Technology Officer", "DEV", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("deepak", "Deepak Verma", "Development Manager", "DEV", "vikram", AppRole.TeamLeader),
            new("rahul", "Rahul Sharma", "Senior Developer — CRM & Mobile Lead", "DEV", "deepak", AppRole.TeamLeader),
            new("kavya", "Kavya Rao", "Developer", "DEV", "rahul", AppRole.TeamMember),
            new("imran", "Imran Khan", "Developer", "DEV", "deepak", AppRole.TeamMember),
            new("neha", "Neha Gupta", "Developer", "DEV", "deepak", AppRole.TeamMember),
            new("sneha", "Sneha Kulkarni", "QA Manager", "QA", "vikram", AppRole.SeniorManager | AppRole.TeamLeader),
            new("lakshmi", "Lakshmi Iyer", "QA Engineer", "QA", "sneha", AppRole.TeamMember),
            new("tarun", "Tarun Bose", "QA Engineer", "QA", "sneha", AppRole.TeamMember),
            new("arjun", "Arjun Nair", "Head of Implementation", "IMPL", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("farida", "Farida Sheikh", "Implementation Consultant (UAE)", "IMPL", "arjun", AppRole.TeamMember),
            new("gopal", "Gopal Krishnan", "Implementation Consultant", "IMPL", "arjun", AppRole.TeamMember),
            new("kiran", "Kiran Reddy", "Support Manager", "SUP", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("sunita", "Sunita Das", "Support Engineer", "SUP", "kiran", AppRole.TeamMember),
            new("ramesh", "Ramesh Yadav", "Support Engineer", "SUP", "kiran", AppRole.TeamMember),
            new("priya", "Priya Iyer", "Finance Manager", "FIN", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("sanjay", "Sanjay Patel", "Accounts Executive", "FIN", "priya", AppRole.TeamMember),
            new("aisha", "Aisha Siddiqui", "Collections Executive", "FIN", "priya", AppRole.TeamMember),
            new("rohit", "Rohit Kapoor", "Head of Sales", "SALES", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("pradeep", "Pradeep Singh", "Sales Manager", "SALES", "rohit", AppRole.TeamMember),
            new("meera", "Meera Joshi", "HR Manager", "HR", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("kavitha", "Kavitha Raman", "HR Executive", "HR", "meera", AppRole.TeamMember),
            new("suresh", "Suresh Pillai", "Administration Manager", "ADMIN", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("ravi", "Ravi Shankar", "IT Administrator", "ADMIN", "suresh", AppRole.Administrator | AppRole.TeamMember),
            new("divya", "Divya Menon", "Marketing Manager", "MKT", "anand", AppRole.SeniorManager | AppRole.TeamLeader),
            new("nikhil", "Nikhil Jain", "Digital Marketing Executive", "MKT", "divya", AppRole.TeamMember),
        ];

        private static readonly (string Code, string Name, string Head)[] Departments =
        [
            ("EXEC", "Executive Office", "anand"), ("DEV", "Development", "vikram"), ("QA", "Quality Assurance", "sneha"),
            ("IMPL", "Implementation", "arjun"), ("SUP", "Support", "kiran"), ("FIN", "Finance", "priya"), ("SALES", "Sales", "rohit"),
            ("HR", "Human Resources", "meera"), ("ADMIN", "Administration", "suresh"), ("MKT", "Marketing", "divya"),
        ];

        private sealed record TeamDef(string Key, string Name, string Dept, string Leader, string? Deputy, string[] Members, string? TeamsTeam, string? Channel, string? ChannelName);

        private static readonly TeamDef[] TeamDefs =
        [
            new("leadership", "Leadership Team", "EXEC", "anand", null, ["vikram", "arjun", "kiran", "priya", "rohit", "meera", "suresh", "divya", "sneha"], "team-leadership", "19:leadership-general@thread.tacv2", "General"),
            new("dev", "Development Team", "DEV", "deepak", "rahul", ["rahul", "kavya", "imran", "neha"], "team-development", "19:dev-general@thread.tacv2", "General"),
            new("crm", "CRM & Mobile Team", "DEV", "rahul", null, ["kavya", "neha"], null, null, null),
            new("qa", "QA Team", "QA", "sneha", null, ["lakshmi", "tarun"], null, null, null),
            new("impl", "Implementation Team", "IMPL", "arjun", "farida", ["farida", "gopal"], null, null, null),
            new("support", "Support Desk", "SUP", "kiran", "sunita", ["sunita", "ramesh"], "team-support", "19:support-escalations@thread.tacv2", "Escalations"),
            new("finance", "Finance Team", "FIN", "priya", null, ["sanjay", "aisha"], "team-finance", "19:finance-collections@thread.tacv2", "Collections"),
            new("sales", "Sales Team", "SALES", "rohit", null, ["pradeep"], null, null, null),
            new("hr", "HR Team", "HR", "meera", null, ["kavitha"], null, null, null),
            new("admin", "Admin & IT", "ADMIN", "suresh", null, ["ravi"], null, null, null),
            new("marketing", "Marketing Team", "MKT", "divya", null, ["nikhil"], null, null, null),
        ];

        public async Task RunAsync(CancellationToken ct)
        {
            _realNow = DateTime.UtcNow;
            using (var s = root.CreateScope()) _cal = await s.ServiceProvider.GetRequiredService<CalendarProvider>().GetDefaultAsync(ct);
            _today = _cal.Today(_realNow);

            var origin = At(W(-24), 8, 0);
            clock.Pin(origin);
            await SeedOrganisationAsync(ct);
            await SeedTenantAsync(ct);
            DefineScenario();
            DefineRecurring();
            DefineSimulatedDays();

            foreach (var (at, label, action) in _events.Where(e => e.At < _realNow).OrderBy(e => e.At))
            {
                clock.Pin(at);
                try { await action(); }
                catch (Exception ex) { logger.LogWarning(ex, "Demo step '{Label}' at {At:u} failed", label, at); }
            }
            clock.Release();
            await FinaliseAsync(ct);
            await SeedLiveCommunicationsAsync(ct);
        }

        // ───────────────────────────── time helpers ─────────────────────────────

        /// <summary>Working-day offset from today (W(0) = today, or the next working day if today is not one).</summary>
        private DateOnly W(int workingDays) => _cal.AddWorkingDays(_cal.IsWorkingDay(_today) ? _today : _cal.NextWorkingDay(_today), workingDays);
        private DateTime At(DateOnly d, int h, int m = 0) => _cal.ToUtc(d, new TimeOnly(h, m));
        private DateOnly NextWeekday(DayOfWeek day) { var d = _today.AddDays(1); while (d.DayOfWeek != day) d = d.AddDays(1); return d; }

        private void On(DateTime at, string label, Func<Task> action) => _events.Add((at, label, action));

        /// <summary>Runs an action in a fresh scope as a user (by key) or as the system.</summary>
        private async Task As(string? who, Func<IServiceProvider, Task> action)
        {
            using var scope = root.CreateScope();
            var sp = scope.ServiceProvider;
            var actor = sp.GetRequiredService<ActorContext>();
            if (who is null) actor.SetSystem();
            else
            {
                var db = sp.GetRequiredService<FollowUpDbContext>();
                var id = _people[who];
                var account = await db.UserAccounts.AsNoTracking().FirstAsync(a => a.EmployeeId == id);
                var name = await db.Employees.Where(e => e.Id == id).Select(e => e.DisplayName).FirstAsync();
                actor.SetUser(id, name, await ViewerFactory.BuildAsync(db, id, account.Roles, CancellationToken.None));
            }
            await action(sp);
        }

        private Task<WorkTask> Create(string who, string key, CreateTaskRequest r) => Create(who, key, r, null);

        private async Task<WorkTask> Create(string who, string key, CreateTaskRequest r, Action<WorkTask>? configure)
        {
            WorkTask? created = null;
            await As(who, async sp => created = await sp.GetRequiredService<TaskService>().CreateAsync(r, CancellationToken.None, null, configure));
            _tasks[key] = created!.Id;
            return created;
        }

        private Task Do(string who, Func<TaskService, Task> action) => As(who, sp => action(sp.GetRequiredService<TaskService>()));

        private Guid P(string key) => _people[key];
        private Guid T(string key) => _tasks[key];

        // ───────────────────────────── organisation ─────────────────────────────

        private async Task SeedOrganisationAsync(CancellationToken ct)
        {
            using var scope = root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            var now = clock.UtcNow;
            var hasher = new PasswordHasher<UserAccount>();

            foreach (var (code, name, _) in Departments)
            {
                var d = new Department { Code = code, Name = name, CreatedAt = now, UpdatedAt = now };
                _depts[code] = d.Id;
                db.Departments.Add(d);
            }
            await db.SaveChangesAsync(ct);

            var number = 1001;
            foreach (var p in People)
            {
                var email = $"{p.Key}@{Domain}";
                var e = new Employee
                {
                    EmployeeCode = $"WS{number++}", DisplayName = p.Name, Email = email, JobTitle = p.Title, DepartmentId = _depts[p.Dept],
                    CalendarId = ConfigSeeder.DefaultCalendarId, ExternalUserId = ExternalId(email), CreatedAt = now, UpdatedAt = now,
                    Phone = $"+91 98450 {number:00000}",
                };
                _people[p.Key] = e.Id;
                db.Employees.Add(e);
                var account = new UserAccount { EmployeeId = e.Id, UserName = p.Key, Roles = p.Roles, CreatedAt = now, UpdatedAt = now };
                account.PasswordHash = hasher.HashPassword(account, Password);
                db.UserAccounts.Add(account);
            }
            await db.SaveChangesAsync(ct);
            foreach (var p in People.Where(p => p.Manager is not null))
                (await db.Employees.FirstAsync(e => e.Id == _people[p.Key], ct)).ManagerId = _people[p.Manager!];
            foreach (var (code, _, head) in Departments)
                (await db.Departments.FirstAsync(d => d.Id == _depts[code], ct)).HeadId = _people[head];

            foreach (var t in TeamDefs)
            {
                var team = new Team
                {
                    Name = t.Name, DepartmentId = _depts[t.Dept], LeaderId = _people[t.Leader], DeputyLeaderId = t.Deputy is null ? null : _people[t.Deputy],
                    Email = $"{t.Key}@{Domain}", TeamsTeamId = t.TeamsTeam, TeamsChannelId = t.Channel, TeamsChannelName = t.ChannelName, CreatedAt = now, UpdatedAt = now,
                };
                _teams[t.Key] = team.Id;
                team.Members.Add(new TeamMember { TeamId = team.Id, EmployeeId = _people[t.Leader], Role = TeamMemberRole.Leader, JoinedAt = now });
                foreach (var m in t.Members.Where(m => m != t.Leader))
                    team.Members.Add(new TeamMember { TeamId = team.Id, EmployeeId = _people[m], Role = m == t.Deputy ? TeamMemberRole.Deputy : TeamMemberRole.Member, JoinedAt = now });
                db.Teams.Add(team);
            }

            foreach (var (code, name, desc, owner, dept, start, target) in new[]
            {
                ("EINV", "UAE e-Invoicing Launch", "Peppol PINT AE e-invoicing for UAE customers (aliases: eInvoice, e-invoicing).", "vikram", "DEV", -30, 45),
                ("DCRM", "Dealer CRM v2", "Dealer CRM mobile & web screens for automotive dealers.", "vikram", "DEV", -20, 15),
                ("GST27", "GST Compliance FY2026-27", "Monthly and annual GST compliance.", "priya", "FIN", -60, 200),
                ("SUPX", "Support Excellence", "Reduce backlog and improve first-response SLA.", "kiran", "SUP", -40, 60),
                ("ALNR", "Al Noor Trading Go-Live", "Wings ERP implementation for Al Noor Trading LLC, Dubai.", "arjun", "IMPL", -25, 8),
                ("HIRE27", "Hiring Plan FY27", "Critical hires for support and development.", "meera", "HR", -15, 60),
            })
            {
                var p = new Project { Code = code, Name = name, Description = desc, OwnerId = _people[owner], DepartmentId = _depts[dept], StartDate = _today.AddDays(start), TargetDate = _today.AddDays(target), CreatedAt = now, UpdatedAt = now };
                _projects[code] = p.Id;
                db.Projects.Add(p);
            }

            db.NumberSequences.Add(new NumberSequence { Key = $"TASK-{_today.Year}", NextValue = 142 });   // the spec's TASK-2026-00142 is the first demo task
            await db.SaveChangesAsync(ct);
        }

        private async Task SeedTenantAsync(CancellationToken ct)
        {
            using var scope = root.CreateScope();
            var tenant = scope.ServiceProvider.GetService<IDemoTenant>();
            if (tenant is null) { logger.LogInformation("No Microsoft 365 emulator registered — tenant content skipped"); return; }
            var users = People.Select(p => new TenantUser(ExternalId($"{p.Key}@{Domain}"), p.Name, $"{p.Key}@{Domain}", p.Title,
                    Departments.First(d => d.Code == p.Dept).Name, p.Manager is null ? null : ExternalId($"{p.Manager}@{Domain}")))
                .Append(new TenantUser(ExternalId(ServiceAccount), "Digital Follow-up Officer", ServiceAccount, "Service account", "Executive Office"))
                .Append(new TenantUser(ExternalId($"ceo.office@{Domain}"), "CEO Office", $"ceo.office@{Domain}", "Shared mailbox", "Executive Office"))
                .ToList();
            string U(string key) => ExternalId($"{key}@{Domain}");
            var teams = TeamDefs.Where(t => t.TeamsTeam is not null).Select(t => new TenantTeam(t.TeamsTeam!, "Wings " + t.Name.Replace(" Team", "").Replace(" Desk", ""),
                    t.Key == "dev"
                        ? [new TenantChannel(t.Channel!, t.ChannelName!), new TenantChannel("19:dev-einvoice@thread.tacv2", "eInvoice")]
                        : [new TenantChannel(t.Channel!, t.ChannelName!)],
                    t.Members.Append(t.Leader).Distinct().Select(U).Append(ExternalId(ServiceAccount)).ToList()))
                .ToList();
            var chats = new List<TenantChat> { new("19:einvoice-core@thread.v2", "group", "eInvoice Core Group", [U("anand"), U("vikram"), U("deepak"), U("rahul"), ExternalId(ServiceAccount)]) };
            await tenant.EnsureDirectoryAsync(users, teams, chats, ct);
        }

        // ───────────────────────────── scenario ─────────────────────────────

        private void DefineScenario()
        {
            var friday = NextWeekday(DayOfWeek.Friday);
            if (friday.DayNumber - _today.DayNumber < 2) friday = friday.AddDays(7);

            // S1 — UAE e-Invoicing (TASK-2026-00142 is the spec's "UAE eInvoice Design").
            On(At(W(-22), 7, 45), "eInvoice design", async () =>
            {
                await Create("anand", "einv-design", new CreateTaskRequest
                {
                    Title = "UAE eInvoice Design", Description = "Functional and technical design for UAE e-invoicing (Peppol PINT AE) in Wings ERP: data mapping, signing/QR, ASP integration, error handling.",
                    Objective = "A signed-off design that development can build without rework.", ExpectedResult = "Design document approved by CTO and CEO.",
                    Deliverable = "Design document + API contract", SuccessCriteria = "Covers B2B, B2G and credit notes; reviewed by QA and Implementation.",
                    PrimaryAssigneeId = P("vikram"), ProjectId = _projects["EINV"], DepartmentId = _depts["DEV"], TeamId = _teams["dev"],
                    Priority = TaskPriority.Urgent, Criticality = Criticality.Critical, DueDate = W(-3), StartDate = W(-22), NotifyCeoOnDelay = true,
                    FollowUp = FollowUpSchedule.EveryDays(2), Tags = ["einvoice", "uae", "design"], CustomerImpact = true, CustomerName = "UAE customers",
                    FinancialImpact = 4_500_000m, Links = [new TaskLink("FTA e-invoicing guidelines", "https://tax.gov.ae")],
                });
            });
            On(At(W(-22), 11, 0), "Vikram accepts and delegates design", async () =>
            {
                await Do("vikram", t => t.AcknowledgeAsync(T("einv-design"), new AcknowledgeRequest(AcknowledgementResponse.Accept, "Accepted — Deepak will own the design; I will review.", null), default));
                await Do("vikram", t => t.ReassignAsync(T("einv-design"), new ReassignRequest(P("deepak"), "Please own the design end to end. Rahul to help with data mapping."), default));
            });
            On(At(W(-21), 10, 0), "Deepak accepts", () => Do("deepak", t => t.AcknowledgeAsync(T("einv-design"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-21), 10, 30), "Design subtasks", async () =>
            {
                await Create("deepak", "einv-mapping", new CreateTaskRequest { Title = "Data mapping: Wings invoice → PINT AE", ParentTaskId = T("einv-design"), PrimaryAssigneeId = P("rahul"), DueDate = W(-12), Criticality = Criticality.Important, RequiresAcknowledgement = false });
                await Create("deepak", "einv-signing", new CreateTaskRequest { Title = "Signing, QR and hash-chain approach", ParentTaskId = T("einv-design"), PrimaryAssigneeId = P("imran"), DueDate = W(-2), Criticality = Criticality.Important, RequiresAcknowledgement = false });
                await Create("deepak", "einv-errors", new CreateTaskRequest { Title = "Error handling & retry design for ASP rejections", ParentTaskId = T("einv-design"), PrimaryAssigneeId = P("neha"), DueDate = W(2), Criticality = Criticality.Important, RequiresAcknowledgement = false });
            });
            On(At(W(-12), 17, 30), "Mapping complete", () => Do("rahul", t => t.CompleteAsync(T("einv-mapping"), new CompleteRequest(W(-12), "Mapping sheet reviewed with Farida.",
                [new EvidenceInput(EvidenceKind.Document, "PINT AE mapping sheet v3", "https://sharepoint.wings.example/einv/mapping-v3.xlsx")]), default)));
            On(At(W(-9), 16, 0), "Design progress + commitment", async () =>
            {
                await Do("deepak", t => t.UpdateProgressAsync(T("einv-design"), new ProgressRequest(60, $"Draft design 60% complete. We will complete this by {W(-3):dddd d MMM}."), default));
            });
            On(At(W(-21), 9, 0), "Provider finalisation", async () =>
            {
                await Create("anand", "einv-provider", new CreateTaskRequest
                {
                    Title = "Finalise UAE e-invoicing service provider (ASP)", PrimaryAssigneeId = P("farida"), ProjectId = _projects["EINV"], DepartmentId = _depts["IMPL"],
                    Criticality = Criticality.Critical, Priority = TaskPriority.High, DueDate = W(-12), FollowUp = FollowUpSchedule.EveryDays(3), Tags = ["einvoice", "vendor"],
                });
            });
            On(At(W(-20), 9, 40), "Farida accepts", () => Do("farida", t => t.AcknowledgeAsync(T("einv-provider"), new AcknowledgeRequest(AcknowledgementResponse.Accept, "Shortlisting three accredited ASPs.", null), default)));
            On(At(W(-13), 15, 0), "Provider done", () => Do("farida", t => t.CompleteAsync(T("einv-provider"), new CompleteRequest(W(-13), "Selected ASP after comparing pricing, SLA and API maturity.",
                [new EvidenceInput(EvidenceKind.Report, "ASP comparison & recommendation", "https://sharepoint.wings.example/einv/asp-comparison.pdf"), new EvidenceInput(EvidenceKind.CustomerConfirmation, "Signed ASP order form", null, "PO-2026-0917")]), default)));
            On(At(W(-12), 11, 0), "CEO confirms provider", () => Do("anand", t => t.DecideApprovalAsync(T("einv-provider"), new ApprovalRequest(ApprovalAction.Confirm, "Good choice — proceed."), default)));

            On(At(W(-18), 9, 30), "API study", () => Create("anand", "einv-api", new CreateTaskRequest
            {
                Title = "UAE e-invoice API study (Peppol PINT AE + ASP sandbox)", PrimaryAssigneeId = P("deepak"), AdditionalAssigneeIds = [P("imran")], ProjectId = _projects["EINV"],
                DepartmentId = _depts["DEV"], TeamId = _teams["dev"], Criticality = Criticality.Critical, Priority = TaskPriority.High, DueDate = W(-2), FollowUp = FollowUpSchedule.EveryDays(3), Tags = ["einvoice"],
            }));
            On(At(W(-18), 11, 0), "Deepak accepts API study", () => Do("deepak", t => t.AcknowledgeAsync(T("einv-api"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-2), 16, 45), "API study submitted", () => Do("deepak", t => t.CompleteAsync(T("einv-api"), new CompleteRequest(W(-2), "Sandbox calls working for invoices and credit notes; rejection codes documented.",
                [new EvidenceInput(EvidenceKind.Document, "API study notes", "https://sharepoint.wings.example/einv/api-study.docx"), new EvidenceInput(EvidenceKind.TestResult, "Sandbox test run (42/42 passed)", "https://ci.wings.example/einv/sandbox/481"),
                 new EvidenceInput(EvidenceKind.GitCommit, "Sandbox client spike", null, "wings-erp@7f3c2a1")]), default)));

            On(At(W(-16), 10, 0), "eInvoice development", async () =>
            {
                await Create("vikram", "einv-dev", new CreateTaskRequest
                {
                    Title = "Build e-invoicing module in Wings ERP", PrimaryAssigneeId = P("deepak"), TeamIds = [_teams["dev"]], ProjectId = _projects["EINV"], DepartmentId = _depts["DEV"],
                    Criticality = Criticality.Critical, Priority = TaskPriority.High, StartDate = W(-1), DueDate = W(16), FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Monday), Tags = ["einvoice"],
                    RequiresAcknowledgement = false, Dependencies = [new DependencyInput(T("einv-design"))],
                });
                await Create("vikram", "einv-cert", new CreateTaskRequest
                {
                    Title = "FTA certification & end-to-end testing", PrimaryAssigneeId = P("sneha"), ProjectId = _projects["EINV"], DepartmentId = _depts["QA"], TeamId = _teams["qa"],
                    Criticality = Criticality.Critical, DueDate = W(26), RequiresAcknowledgement = false, Dependencies = [new DependencyInput(T("einv-dev"))], Tags = ["einvoice"],
                });
                await Create("anand", "einv-rollout", new CreateTaskRequest
                {
                    Title = "Customer rollout plan for UAE e-invoicing", PrimaryAssigneeId = P("arjun"), ProjectId = _projects["EINV"], DepartmentId = _depts["IMPL"],
                    Criticality = Criticality.Important, DueDate = W(30), FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Wednesday), Tags = ["einvoice", "rollout"],
                });
            });
            On(At(W(-15), 10, 0), "Arjun accepts rollout", () => Do("arjun", t => t.AcknowledgeAsync(T("einv-rollout"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-1), 11, 30), "External dependency on design", () => Do("deepak", t => t.AddDependencyAsync(T("einv-design"), new DependencyInput(null, DependencyType.External, "API credentials (production) from the ASP", "e-invoicing service provider", W(1)), default)));

            // S2 — Dealer CRM screens (the spec's natural-language example).
            On(At(W(-5), 9, 20), "Dealer CRM screens", () => Create("anand", "dcrm", new CreateTaskRequest
            {
                Title = "Finish the dealer CRM screens", Description = "Created from: \"Ask Rahul and the development team to finish the dealer CRM screens by Friday. Follow up every two days and tell me if it gets delayed.\"",
                PrimaryAssigneeId = P("rahul"), TeamIds = [_teams["crm"]], ProjectId = _projects["DCRM"], DepartmentId = _depts["DEV"], Criticality = Criticality.Important,
                DueDate = friday, FollowUp = FollowUpSchedule.EveryDays(2), NotifyCeoOnDelay = true, Source = TaskSource.NaturalLanguage, Tags = ["dealer-crm"],
            }));
            On(At(W(-5), 10, 5), "Rahul accepts", () => Do("rahul", t => t.AcknowledgeAsync(T("dcrm"), new AcknowledgeRequest(AcknowledgementResponse.Accept, "On it — splitting across Kavya and Neha.", null), default)));
            On(At(W(-5), 10, 20), "CRM subtasks", async () =>
            {
                await Create("rahul", "dcrm-list", new CreateTaskRequest { Title = "Dealer list & search screen", ParentTaskId = T("dcrm"), PrimaryAssigneeId = P("kavya"), DueDate = W(-2), RequiresAcknowledgement = false });
                await Create("rahul", "dcrm-enquiry", new CreateTaskRequest { Title = "Enquiry follow-up screen", ParentTaskId = T("dcrm"), PrimaryAssigneeId = P("neha"), DueDate = friday, RequiresAcknowledgement = false });
                await Create("rahul", "dcrm-testdrive", new CreateTaskRequest { Title = "Test-drive booking calendar", ParentTaskId = T("dcrm"), PrimaryAssigneeId = P("kavya"), DueDate = friday, RequiresAcknowledgement = false });
            });
            On(At(W(-2), 17, 0), "Dealer list done", () => Do("kavya", t => t.CompleteAsync(T("dcrm-list"), new CompleteRequest(W(-2), "Merged to develop.", [new EvidenceInput(EvidenceKind.GitCommit, "Dealer list screen", null, "dealer-crm@a41b9e0")]), default)));
            On(At(W(-1), 15, 0), "Enquiry progress", () => Do("neha", t => t.UpdateProgressAsync(T("dcrm-enquiry"), new ProgressRequest(60, "Enquiry screen UI done, API wiring in progress."), default)));
            On(At(W(-1), 15, 10), "CRM overall progress", () => Do("rahul", t => t.UpdateProgressAsync(T("dcrm"), new ProgressRequest(55, "List screen done, enquiry and test-drive screens in progress."), default)));

            // S3 — No response.
            On(At(W(-12), 12, 0), "AMC tracker (no response)", () => Create("anand", "amc", new CreateTaskRequest
            {
                Title = "Update AMC renewal tracker for top 50 customers", PrimaryAssigneeId = P("pradeep"), DepartmentId = _depts["SALES"], TeamId = _teams["sales"],
                Criticality = Criticality.Important, Priority = TaskPriority.High, DueDate = W(-3), FollowUp = FollowUpSchedule.EveryDays(2), CustomerImpact = true,
                FinancialImpact = 1_200_000m, Tags = ["amc", "renewals"],
            }));

            // S4 — Missed commitment, then completion reported by email (live).
            On(At(W(-8), 10, 0), "Payment gateway fix", () => Create("deepak", "pgw", new CreateTaskRequest
            {
                Title = "Payment gateway retry fix for Wings POS", Description = "Card payments time out under load; implement idempotent retries.", PrimaryAssigneeId = P("imran"),
                DepartmentId = _depts["DEV"], TeamId = _teams["dev"], Criticality = Criticality.Critical, Priority = TaskPriority.Urgent, DueDate = W(-1),
                FollowUp = FollowUpSchedule.Daily(), CustomerImpact = true, CustomerName = "Retail customers (POS)", Tags = ["pos", "payments"],
            }));
            On(At(W(-8), 11, 0), "Imran accepts", () => Do("imran", t => t.AcknowledgeAsync(T("pgw"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-5), 12, 0), "Imran commits", () => Do("imran", t => t.CommentAsync(T("pgw"), new CommentRequest($"Root cause found in the timeout handling. I will complete this by {W(-2):dddd}."), default)));

            // S5 — Al Noor go-live (template structure), extension email arrives live.
            On(At(W(-14), 9, 0), "Al Noor go-live", async () =>
            {
                await Create("anand", "alnoor", new CreateTaskRequest
                {
                    Title = "Al Noor Trading — Customer Go-Live", PrimaryAssigneeId = P("arjun"), TeamIds = [_teams["impl"]], ProjectId = _projects["ALNR"], DepartmentId = _depts["IMPL"],
                    Criticality = Criticality.Important, Priority = TaskPriority.High, DueDate = W(3), FollowUp = FollowUpSchedule.OnWeekdays(DayOfWeek.Monday, DayOfWeek.Thursday),
                    CustomerImpact = true, CustomerName = "Al Noor Trading LLC", FinancialImpact = 850_000m, RequiresApproval = true, Tags = ["go-live", "uae"],
                }, t => t.TemplateId = Ids.Deterministic("template", "Customer Go-Live"));
                await Create("arjun", "alnoor-plan", new CreateTaskRequest { Title = "Go-live plan & cut-over schedule", ParentTaskId = T("alnoor"), PrimaryAssigneeId = P("arjun"), DueDate = W(-12), RequiresAcknowledgement = false });
                await Create("arjun", "alnoor-migrate", new CreateTaskRequest { Title = "Master & opening balance migration", ParentTaskId = T("alnoor"), PrimaryAssigneeId = P("gopal"), DueDate = W(-1), Criticality = Criticality.Important, RequiresAcknowledgement = false });
                await Create("arjun", "alnoor-train", new CreateTaskRequest { Title = "End-user training (finance & sales)", ParentTaskId = T("alnoor"), PrimaryAssigneeId = P("farida"), DueDate = W(1), RequiresAcknowledgement = false });
                await Create("arjun", "alnoor-uat", new CreateTaskRequest { Title = "Customer UAT sign-off", ParentTaskId = T("alnoor"), PrimaryAssigneeId = P("arjun"), DueDate = W(2), RequiresAcknowledgement = false });
            });
            On(At(W(-14), 10, 0), "Arjun accepts go-live", () => Do("arjun", t => t.AcknowledgeAsync(T("alnoor"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-12), 16, 0), "Plan done", () => Do("arjun", t => t.CompleteAsync(T("alnoor-plan"), new CompleteRequest(W(-12), "Cut-over plan agreed with the customer.", null), default)));
            On(At(W(-3), 11, 0), "Migration waiting", () => Do("gopal", t => t.ChangeStatusAsync(T("alnoor-migrate"), new StatusChangeRequest(WorkStatus.WaitingForExternalParty, "Waiting for final opening balances from the customer's auditor"), default)));

            // S6/S7/S8 — Acknowledgement states.
            On(At(W(-3), 10, 0), "Sales targets (unacknowledged)", () => Create("anand", "targets", new CreateTaskRequest
            {
                Title = "Prepare FY27 sales target proposal by region", PrimaryAssigneeId = P("rohit"), DepartmentId = _depts["SALES"], Criticality = Criticality.Important,
                DueDate = W(6), FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Thursday), Tags = ["planning"],
            }));
            On(At(W(-4), 14, 0), "Expense audit", () => Create("anand", "audit", new CreateTaskRequest
            {
                Title = "Run internal audit of travel expense claims (Q2)", PrimaryAssigneeId = P("meera"), DepartmentId = _depts["HR"], Criticality = Criticality.Important, DueDate = W(8),
            }));
            On(At(W(-3), 9, 30), "Meera cannot accept", () => Do("meera", t => t.AcknowledgeAsync(T("audit"), new AcknowledgeRequest(AcknowledgementResponse.CannotAccept,
                "An expense audit should be independent of HR — suggest Finance (Priya's team) runs it and HR supports.", null), default)));
            On(At(W(-2), 11, 0), "Referral campaign", () => Create("anand", "referral", new CreateTaskRequest
            {
                Title = "Launch partner referral campaign", PrimaryAssigneeId = P("divya"), DepartmentId = _depts["MKT"], Criticality = Criticality.Normal, DueDate = W(10), FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Tuesday),
            }));
            On(At(W(-2), 15, 0), "Divya needs clarification", () => Do("divya", t => t.AcknowledgeAsync(T("referral"), new AcknowledgeRequest(AcknowledgementResponse.NeedClarification,
                "Which partner tiers should get the higher referral fee — Gold only, or Gold and Silver?", null), default)));

            // S9 — Snoozed (waiting for government).
            On(At(W(-6), 10, 0), "GSTR-9", () => Create("priya", "gstr9", new CreateTaskRequest
            {
                Title = "File GSTR-9 annual return FY2025-26", PrimaryAssigneeId = P("sanjay"), ProjectId = _projects["GST27"], DepartmentId = _depts["FIN"], TeamId = _teams["finance"],
                Criticality = Criticality.Critical, DueDate = W(9), FollowUp = FollowUpSchedule.EveryDays(2), Tags = ["gst", "statutory"], RequiresAcknowledgement = false,
            }));
            On(At(W(-1), 12, 0), "GSTR-9 snoozed", () => Do("sanjay", t => t.SnoozeAsync(T("gstr9"), new SnoozeRequest(FollowUpPauseReason.GovernmentDepartment, W(3),
                "GSTN has not yet enabled the FY2025-26 GSTR-9 utility; resuming once it is live."), default)));

            // S10 — Cross-department blocker (also a meeting action item).
            On(At(W(-6), 10, 30), "Leadership review meeting", () => As("anand", async sp =>
            {
                var db = sp.GetRequiredService<FollowUpDbContext>();
                var tasks = sp.GetRequiredService<TaskService>();
                var meeting = new Meeting
                {
                    Title = "Monday Leadership Review", HeldAt = At(W(-6), 10, 0), OrganizerId = P("anand"), Location = "Board room / Teams",
                    Notes = "Reviewed eInvoice progress, collections risk and UAE support coverage.",
                    ParticipantIds = new[] { "anand", "vikram", "priya", "meera", "kiran", "rohit", "arjun" }.Select(P).ToList(),
                };
                var items = new (string Key, string Decision, string Action, string Owner, int Due, Criticality Crit, bool Create)[]
                {
                    ("mt-scope", "Phase 1 covers B2B invoices and credit notes only.", "Freeze eInvoice phase-1 scope and share it with Implementation", "vikram", -3, Criticality.Important, true),
                    ("mt-collections", "Collections risk to be reviewed weekly until under 10%.", "Share collections risk list for the top 20 accounts", "priya", -2, Criticality.Important, true),
                    ("mt-hire", "UAE support needs Arabic-speaking engineers before go-lives.", "Hire 2 Arabic-speaking support engineers", "meera", 15, Criticality.Important, true),
                    ("mt-pilot", "Dealer CRM pilot with three dealers after screens are done.", "Plan dealer CRM pilot with three dealers", "rohit", 8, Criticality.Normal, false),
                };
                foreach (var i in items)
                    meeting.ActionItems.Add(new MeetingActionItem { MeetingId = meeting.Id, Decision = i.Decision, ActionText = i.Action, OwnerId = P(i.Owner), DueDate = W(i.Due), Criticality = i.Crit });
                db.Meetings.Add(meeting);
                await db.SaveChangesAsync();
                foreach (var (i, item) in items.Zip(meeting.ActionItems).Where(x => x.First.Create))
                {
                    var t = await tasks.CreateAsync(new CreateTaskRequest
                    {
                        Title = i.Action, Description = $"From \"{meeting.Title}\" ({meeting.HeldAt:d MMM}). Decision: {i.Decision}", PrimaryAssigneeId = P(i.Owner),
                        DueDate = W(i.Due), Criticality = i.Crit, Source = TaskSource.Meeting, MeetingActionItemId = item.Id, FollowUp = FollowUpSchedule.EveryDays(2),
                        RequiresAcknowledgement = false,
                    }, CancellationToken.None);
                    _tasks[i.Key] = t.Id;
                }
            }));
            On(At(W(-6), 12, 0), "UAE support roster", () => Create("anand", "roster", new CreateTaskRequest
            {
                Title = "Set up UAE support desk shift roster", PrimaryAssigneeId = P("kiran"), ProjectId = _projects["SUPX"], DepartmentId = _depts["SUP"], TeamId = _teams["support"],
                Criticality = Criticality.Important, DueDate = W(5), RequiresAcknowledgement = false, Dependencies = [new DependencyInput(T("mt-hire"))],
            }));
            On(At(W(-2), 16, 0), "Scope frozen late", () => Do("vikram", t => t.CompleteAsync(T("mt-scope"), new CompleteRequest(W(-2), "Scope document shared with Arjun and Farida.", null), default)));

            // S11 — Confidential CEO-only work.
            On(At(W(-7), 18, 0), "Project Falcon", () => Create("anand", "falcon", new CreateTaskRequest
            {
                Title = "Project Falcon — evaluate acquisition of a payroll SaaS", Description = "Confidential. Commercial and technical due diligence on the target.",
                PrimaryAssigneeId = P("priya"), Visibility = TaskVisibility.CeoOnly, Criticality = Criticality.BusinessCritical, Priority = TaskPriority.High,
                DueDate = W(10), FinancialImpact = 25_000_000m, FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Friday), Tags = ["confidential"],
            }));
            On(At(W(-6), 9, 45), "Priya accepts Falcon", () => Do("priya", t => t.AcknowledgeAsync(T("falcon"), new AcknowledgeRequest(AcknowledgementResponse.Accept, "Starting with the financial model.", null), default)));

            // S16 — Team-assigned overdue work; S17 — awaiting team leader approval; pending extension to CEO.
            On(At(W(-7), 9, 30), "Support backlog", () => Create("anand", "backlog", new CreateTaskRequest
            {
                Title = "Clear Support Desk backlog older than 7 days", TeamIds = [_teams["support"]], IncludeTeamLeader = true, ProjectId = _projects["SUPX"], DepartmentId = _depts["SUP"],
                Criticality = Criticality.Important, DueDate = W(-1), FollowUp = FollowUpSchedule.Daily(), Tags = ["support", "sla"],
            }));
            On(At(W(-7), 11, 0), "Kiran accepts backlog", () => Do("kiran", t => t.AcknowledgeAsync(T("backlog"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-3), 17, 0), "Backlog progress", () => Do("sunita", t => t.UpdateProgressAsync(T("backlog"), new ProgressRequest(70, "71 of 102 old tickets closed."), default)));
            On(At(W(-9), 10, 0), "Regression", () => Create("vikram", "regression", new CreateTaskRequest
            {
                Title = "Regression testing for release 8.4", PrimaryAssigneeId = P("lakshmi"), TeamId = _teams["qa"], DepartmentId = _depts["QA"], Criticality = Criticality.Important,
                DueDate = W(-1), RequiresApproval = true, RequiresAcknowledgement = false, Tags = ["release-8.4"],
            }));
            On(At(W(-1), 17, 40), "Regression submitted", () => Do("lakshmi", t => t.CompleteAsync(T("regression"), new CompleteRequest(W(-1), "312 cases executed, 3 minor defects logged.",
                [new EvidenceInput(EvidenceKind.TestResult, "Regression report 8.4", "https://qa.wings.example/runs/8.4-final")]), default)));
            On(At(W(-9), 15, 0), "ISP migration", () => Create("anand", "isp", new CreateTaskRequest
            {
                Title = "Migrate Dubai office to the new ISP", PrimaryAssigneeId = P("suresh"), DepartmentId = _depts["ADMIN"], Criticality = Criticality.Important, DueDate = W(1), FollowUp = FollowUpSchedule.Weekly(DayOfWeek.Monday),
            }));
            On(At(W(-9), 16, 0), "Suresh accepts", () => Do("suresh", t => t.AcknowledgeAsync(T("isp"), new AcknowledgeRequest(AcknowledgementResponse.Accept, null, null), default)));
            On(At(W(-1), 10, 30), "ISP extension", () => Do("suresh", t => t.RequestExtensionAsync(T("isp"), new ExtensionRequestInput(W(6), "The new ISP needs a building NOC from the landlord; installation slot confirmed for next week."), default)));

            // S12 — Completed work for reports (some late, one reopened).
            var done = new (string Key, string Title, string Owner, string Dept, int Created, int Due, int Completed, Criticality Crit)[]
            {
                ("d1", "Q2 board presentation", "priya", "FIN", -24, -18, -18, Criticality.Important),
                ("d2", "Renew Bengaluru office lease", "suresh", "ADMIN", -23, -15, -13, Criticality.Important),
                ("d3", "Fix invoice PDF alignment for Arabic", "neha", "DEV", -22, -19, -20, Criticality.Normal),
                ("d4", "Publish September customer newsletter", "nikhil", "MKT", -20, -14, -14, Criticality.Normal),
                ("d5", "Close September books", "sanjay", "FIN", -19, -12, -11, Criticality.Important),
                ("d6", "Onboard three new support engineers", "kavitha", "HR", -19, -9, -9, Criticality.Normal),
                ("d7", "Patch production database servers", "ravi", "ADMIN", -18, -16, -16, Criticality.Critical),
                ("d8", "Customer NPS survey analysis", "kiran", "SUP", -17, -10, -8, Criticality.Normal),
                ("d9", "Price list update for dealers", "pradeep", "SALES", -16, -11, -12, Criticality.Normal),
                ("d10", "Security review of the mobile app", "tarun", "QA", -15, -7, -6, Criticality.Important),
                ("d11", "Demo environment refresh", "gopal", "IMPL", -14, -10, -10, Criticality.Normal),
                ("d12", "Vendor contract renewal — SMS gateway", "suresh", "ADMIN", -12, -6, -4, Criticality.Normal),
            };
            foreach (var d in done)
            {
                var item = d;
                On(At(W(item.Created), 9, 10), $"Created {item.Key}", () => Create("anand", item.Key, new CreateTaskRequest
                {
                    Title = item.Title, PrimaryAssigneeId = P(item.Owner), DepartmentId = _depts[item.Dept], Criticality = item.Crit, DueDate = W(item.Due), RequiresAcknowledgement = false,
                    FollowUp = FollowUpSchedule.EveryDays(3),
                }));
                On(At(W(item.Completed), 16, 30), $"Completed {item.Key}", () => Do(item.Owner, t => t.CompleteAsync(T(item.Key), new CompleteRequest(W(item.Completed), "Done.", null), default)));
                if (item.Crit >= Criticality.Critical)
                    On(At(W(item.Completed), 17, 30), $"Approved {item.Key}", () => Do("anand", t => t.DecideApprovalAsync(T(item.Key), new ApprovalRequest(ApprovalAction.Confirm, null), default)));
            }
            On(At(W(-12), 12, 0), "NPS first submission", () => Do("kiran", t => t.CompleteAsync(T("d8"), new CompleteRequest(W(-12), "Summary attached.", null), default)));
            On(At(W(-11), 10, 0), "Reopen NPS analysis", () => Do("anand", t => t.ReopenAsync(T("d8"), "Please segment the scores by region before we close this.", default)));
        }

        private void DefineRecurring()
        {
            var start = W(-22);
            On(At(start, 7, 30), "Recurring definitions", () => As("anand", async sp =>
            {
                var svc = sp.GetRequiredService<RecurringTaskService>();
                async Task Def(string key, RecurringDefinitionInput input) => _recurring[key] = (await svc.CreateAsync(input with { StartDate = start }, CancellationToken.None)).Id;

                await Def("gst", new RecurringDefinitionInput
                {
                    Code = "FIN-GSTR", TitleTemplate = "GST reconciliation — {PrevMonthYear}", Description = "Reconcile GSTR-2B with purchase books; resolve mismatches with vendors.",
                    OwnerId = P("priya"), PrimaryAssigneeId = P("sanjay"), TeamId = _teams["finance"], DepartmentId = _depts["FIN"], ProjectId = _projects["GST27"],
                    Criticality = Criticality.Important, Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, DayOfMonth = 1, CreateTime = new TimeOnly(8, 0) },
                    Due = new DueRule { Kind = DueRuleKind.DayOfMonth, Value = 5 }, FollowUp = FollowUpSchedule.EveryDays(2), Checklist = ["Download GSTR-2B", "Match invoices", "Mail vendors with mismatches"],
                });
                await Def("collections", new RecurringDefinitionInput
                {
                    Code = "FIN-COLL", TitleTemplate = "Weekly collections review — {Week} {Year}", OwnerId = P("priya"), PrimaryAssigneeId = P("aisha"), TeamId = _teams["finance"], DepartmentId = _depts["FIN"],
                    Criticality = Criticality.Important, Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday], CreateTime = new TimeOnly(8, 0) },
                    Due = new DueRule { Kind = DueRuleKind.WeekdayOfSameWeek, Weekday = DayOfWeek.Friday }, FollowUp = FollowUpSchedule.OnWeekdays(DayOfWeek.Wednesday, DayOfWeek.Friday),
                });
                await Def("collections-status", new RecurringDefinitionInput
                {
                    Code = "FIN-CEO", TitleTemplate = "Collections status to CEO — {Date}", Description = "Created from: \"Create a recurring task for the finance manager to send collections status every Monday by noon.\"",
                    OwnerId = P("anand"), PrimaryAssigneeId = P("priya"), DepartmentId = _depts["FIN"], Criticality = Criticality.Important,
                    Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday], CreateTime = new TimeOnly(8, 0) },
                    Due = new DueRule { Kind = DueRuleKind.SameDay, DueTime = new TimeOnly(12, 0) },
                });
                await Def("devreview", new RecurringDefinitionInput
                {
                    Code = "DEV-REV", TitleTemplate = "Monday development review — {Week}", OwnerId = P("vikram"), PrimaryAssigneeId = P("deepak"), TeamId = _teams["dev"], DepartmentId = _depts["DEV"],
                    Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday], CreateTime = new TimeOnly(8, 30) },
                    Due = new DueRule { Kind = DueRuleKind.SameDay, DueTime = new TimeOnly(17, 0) },
                });
                await Def("payroll", new RecurringDefinitionInput
                {
                    Code = "HR-PAY", TitleTemplate = "Monthly payroll approval — {Month} {Year}", OwnerId = P("anand"), PrimaryAssigneeId = P("meera"), DepartmentId = _depts["HR"], Criticality = Criticality.Critical,
                    RequiresApproval = true, Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, MonthlyPattern = MonthlyPattern.LastWorkingDay, CreateTime = new TimeOnly(9, 0) },
                    Due = new DueRule { Kind = DueRuleKind.SameDay, DueTime = new TimeOnly(16, 0) },
                });
                await Def("health", new RecurringDefinitionInput
                {
                    Code = "SUP-QHR", TitleTemplate = "Quarterly customer health review — {Quarter} {Year}", OwnerId = P("anand"), PrimaryAssigneeId = P("kiran"), DepartmentId = _depts["SUP"],
                    Criticality = Criticality.Important, Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Quarterly, Months = [1, 4, 7, 10], DayOfMonth = 1, CreateTime = new TimeOnly(8, 0) },
                    Due = new DueRule { Kind = DueRuleKind.AfterWorkingDays, Value = 10 },
                });
                await Def("licence", new RecurringDefinitionInput
                {
                    Code = "IT-LIC", TitleTemplate = "Renew Microsoft 365 licences (expire {Anchor})", OwnerId = P("suresh"), PrimaryAssigneeId = P("ravi"), DepartmentId = _depts["ADMIN"],
                    Criticality = Criticality.Critical, Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.AnchorDates, AnchorDates = [_today.AddDays(24)], AnchorOffsetDays = -15, RepeatAnchorsYearly = true, CreateTime = new TimeOnly(9, 0) },
                    Due = new DueRule { Kind = DueRuleKind.AnchorDate },
                });
                await Def("support-daily", new RecurringDefinitionInput
                {
                    Code = "SUP-DAILY", TitleTemplate = "Daily critical support review — {Date}", OwnerId = P("kiran"), PrimaryAssigneeId = P("sunita"), TeamId = _teams["support"], DepartmentId = _depts["SUP"],
                    Criticality = Criticality.Important, CatchUp = CatchUpPolicy.GenerateLatestOnly,
                    Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.WorkingDays, CreateTime = new TimeOnly(8, 0) }, Due = new DueRule { Kind = DueRuleKind.SameDay, DueTime = new TimeOnly(18, 0) },
                });
                await Def("mis", new RecurringDefinitionInput
                {
                    Code = "FIN-MIS", TitleTemplate = "Monthly MIS pack to the board — {PrevMonthYear}", OwnerId = P("anand"), PrimaryAssigneeId = P("priya"), DepartmentId = _depts["FIN"],
                    Criticality = Criticality.Important, CatchUp = CatchUpPolicy.GenerateWithinWindow, CatchUpWindowDays = 3,
                    Rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, MonthlyPattern = MonthlyPattern.FirstWorkingDay, CreateTime = new TimeOnly(9, 0) },
                    Due = new DueRule { Kind = DueRuleKind.AfterWorkingDays, Value = 5 },
                });
            }));
        }

        /// <summary>
        /// The background processor across the history: recurrence at 08:05, follow-up / deadline / escalation /
        /// commitment processing twice per working day — except during a simulated outage (Friday 07:00 →
        /// Monday 09:40), after which the engine catches up exactly as it would after a real restart.
        /// </summary>
        private void DefineSimulatedDays()
        {
            var outageFriday = _today.AddDays(-8);
            while (outageFriday.DayOfWeek != DayOfWeek.Friday) outageFriday = outageFriday.AddDays(-1);
            var outageStart = At(outageFriday, 7, 0);
            var outageEnd = At(outageFriday.AddDays(3), 9, 40);
            bool Down(DateTime t) => t >= outageStart && t < outageEnd;

            for (var d = W(-22); d < _today; d = d.AddDays(1))
            {
                var day = d;
                var gen = At(day, 8, 5);
                if (!Down(gen)) On(gen, $"Recurrence {day}", () => As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(CancellationToken.None)));
                if (!_cal.IsWorkingDay(day)) continue;
                foreach (var (h, m) in new[] { (10, 5), (16, 5) })
                {
                    var at = At(day, h, m);
                    if (Down(at)) continue;
                    On(at, $"Jobs {at:u}", RunJobsAsync);
                }
                On(At(day, 17, 45), $"Recurring completions {day}", () => CompleteRecurringAsync(day));
            }
            On(outageEnd, "Restart after outage", async () =>
            {
                await As(null, sp => sp.GetRequiredService<RecurringTaskService>().GenerateDueAsync(CancellationToken.None));
                await RunJobsAsync();
            });
        }

        private async Task RunJobsAsync()
        {
            await As(null, sp => sp.GetRequiredService<FollowUpService>().ProcessDueFollowUpsAsync(CancellationToken.None));
            await As(null, sp => sp.GetRequiredService<FollowUpService>().ProcessDeadlinesAsync(CancellationToken.None));
            await As(null, sp => sp.GetRequiredService<EscalationService>().ProcessAsync(CancellationToken.None));
            await As(null, sp => sp.GetRequiredService<CommitmentService>().EvaluateAsync(CancellationToken.None));
            await As(null, sp => sp.GetRequiredService<MonitoringService>().SweepAsync(CancellationToken.None));
        }

        /// <summary>Assignees complete recurring work on its due date (with realistic exceptions).</summary>
        private async Task CompleteRecurringAsync(DateOnly day)
        {
            List<(Guid Id, Guid? Def, Guid? Assignee, DateOnly? Due, string Title)> open;
            using (var scope = root.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
                open = (await db.Tasks.AsNoTracking().OpenOnly().Where(t => t.RecurringDefinitionId != null && t.ParentTaskId == null && t.DueDate != null && t.DueDate <= day)
                    .Select(t => new { t.Id, t.RecurringDefinitionId, t.PrimaryAssigneeId, t.DueDate, t.Title, t.Status }).ToListAsync())
                    .Where(t => !TaskStatusRules.IsAwaitingConfirmation(t.Status))
                    .Select(t => (t.Id, t.RecurringDefinitionId, t.PrimaryAssigneeId, t.DueDate, t.Title)).ToList();
            }
            var recentWindow = _today.AddDays(-6);
            foreach (var t in open)
            {
                var key = _recurring.FirstOrDefault(r => r.Value == t.Def).Key;
                // Exceptions that keep the demo honest: the current GST reconciliation and one weekly collections review stay open.
                if (key == "gst" && t.Due >= recentWindow) continue;
                if (key == "collections" && t.Due >= _today.AddDays(-9) && t.Due < recentWindow) continue;
                if (key == "collections-status" && t.Due >= recentWindow) continue;
                if (t.Assignee is not { } assignee) continue;
                var who = _people.First(p => p.Value == assignee).Key;
                var lateDays = key == "mis" ? 1 : 0;
                if (t.Due!.Value.AddDays(lateDays) > day) continue;
                List<(Guid Id, Guid? Assignee)> children;
                using (var scope = root.CreateScope())
                    children = (await scope.ServiceProvider.GetRequiredService<FollowUpDbContext>().Tasks.AsNoTracking().OpenOnly()
                        .Where(c => c.ParentTaskId == t.Id).Select(c => new { c.Id, c.PrimaryAssigneeId }).ToListAsync())
                        .Select(c => (c.Id, c.PrimaryAssigneeId)).ToList();
                foreach (var c in children)
                    await Do(_people.FirstOrDefault(p => p.Value == c.Assignee).Key ?? who, s => s.CompleteAsync(c.Id, new CompleteRequest(day, null, null), default));
                await Do(who, s => s.CompleteAsync(t.Id, new CompleteRequest(day, "Completed.", null, ForceWithOpenSubtasks: true), default));
                if (key == "payroll")
                    await Do("anand", s => s.DecideApprovalAsync(t.Id, new ApprovalRequest(ApprovalAction.Confirm, "Approved."), default));
            }
        }

        // ───────────────────────────── finalise ─────────────────────────────

        private async Task FinaliseAsync(CancellationToken ct)
        {
            using var scope = root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            // Historical notifications were "delivered" at the time they were queued; only today's are left for
            // the live dispatcher (they appear in the emulator mailboxes and Teams chats).
            var cutoff = DateTime.UtcNow.AddHours(-3);
            await db.Notifications.Where(n => (n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Failed) && n.CreatedAt < cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Sent).SetProperty(n => n.SentAt, n => n.CreatedAt), ct);
            await db.UiEvents.ExecuteDeleteAsync(ct);
            logger.LogInformation("Demo history: {Tasks} tasks, {Notifications} notifications, {Escalations} escalations, {Instances} recurring occurrences",
                await db.Tasks.CountAsync(ct), await db.Notifications.CountAsync(ct), await db.TaskEscalations.CountAsync(ct), await db.RecurringInstances.CountAsync(ct));
        }

        /// <summary>Emails and Teams messages that arrive "now" — the live processor ingests and correlates them after start-up.</summary>
        private async Task SeedLiveCommunicationsAsync(CancellationToken ct)
        {
            using var scope = root.CreateScope();
            var tenant = scope.ServiceProvider.GetService<IDemoTenant>();
            if (tenant is null) return;
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            string Num(string key) => db.Tasks.Where(t => t.Id == _tasks[key]).Select(t => t.Number).First();
            string U(string key) => ExternalId($"{key}@{Domain}");
            string M(string key) => $"{key}@{Domain}";
            var now = DateTime.UtcNow;
            DateTime Ago(double hours) => now.AddHours(-hours);
            var thursday = NextWeekday(DayOfWeek.Thursday);

            // Spec §23 example — a channel post with no task number: matched semantically, classified as a blocker + commitment.
            await tenant.PostChannelMessageAsync("team-development", "19:dev-einvoice@thread.tacv2",
                new TenantPost(U("deepak"), "Update on the UAE e-invoice design: we are waiting for API credentials from the provider. We should finish by Friday.", Ago(18)), ct);
            // Spec §22 example — explicit task number in a 1:1 chat with the Follow-up Officer.
            var rahulChat = await tenant.EnsureOneOnOneChatAsync(ExternalId(ServiceAccount), U("rahul"), ct);
            await tenant.PostChatMessageAsync(rahulChat, new TenantPost(U("rahul"), $"{Num("dcrm")} will be completed by Thursday.", Ago(2.5)), ct);
            // Reply to a reminder email: completion reported → suggestion only (critical work is never closed by AI).
            await tenant.SendMailAsync(new TenantMail(M("imran"), M("imran"), [ServiceAccount], [M("deepak")],
                $"RE: [{Num("pgw")}] Follow-up Required — Payment gateway retry fix for Wings POS",
                "Hi,\n\nFixed and deployed to UAT yesterday. Task completed — regression results are attached to the ticket.\n\nRegards,\nImran", Ago(3)), ct);
            // Deadline extension request by email → extension request awaiting the CEO.
            await tenant.SendMailAsync(new TenantMail(M("arjun"), M("arjun"), [M("anand")], [],
                "Al Noor Trading go-live",
                "Anand,\n\nThe customer has still not shared the final opening balances, so data migration is pending. We need an extension till the 20th for the Al Noor go-live. I will confirm the revised cut-over plan tomorrow.\n\nArjun", Ago(4)), ct);
            // External customer escalation to the CEO mailbox.
            await tenant.SendMailAsync(new TenantMail("ops@alnoortrading.ae", "ops@alnoortrading.ae", [M("anand")], [M("arjun")],
                "URGENT: e-invoices failing since this morning",
                "Dear Anand,\n\nOur dispatch is stopped because e-invoices are being rejected since 8 AM. This is critical for us — please escalate immediately.\n\nKhalid Al Mansoori\nOperations Manager, Al Noor Trading LLC", Ago(1.5)), ct);
            // Progress update + commitment from the finance manager (collections status).
            await tenant.SendMailAsync(new TenantMail(M("priya"), M("priya"), [M("anand")], [M("aisha")],
                "Collections status — this week",
                $"Anand,\n\nCollections are at 82% of target this week. Three key accounts (Gulf Motors, Prime Retail, Apex Foods) are pending; we will close two of them by {thursday:dddd}.\n\nPriya", Ago(20)), ct);
            // The government portal is down — a reply in the 1:1 chat right after a GST reminder.
            var sanjayChat = await tenant.EnsureOneOnOneChatAsync(ExternalId(ServiceAccount), U("sanjay"), ct);
            await tenant.PostChatMessageAsync(sanjayChat, new TenantPost(U("sanjay"), "The GST portal has been down since morning — waiting for the government site to come back up. Will finish the reconciliation as soon as it is available.", Ago(1)), ct);
            // Delegator nudging in a group chat.
            await tenant.PostChatMessageAsync("19:einvoice-core@thread.v2", new TenantPost(U("vikram"), "Deepak, please share the revised eInvoice design timeline with Anand today.", Ago(22)), ct);
            // Ambiguous → review queue; unrelated → ignored.
            await tenant.SendMailAsync(new TenantMail(M("kavya"), M("kavya"), [M("anand")], [], "Login screen", "Finished the login screen changes, pending review from Rahul.", Ago(5)), ct);
            await tenant.SendMailAsync(new TenantMail(M("nikhil"), M("nikhil"), [M("anand"), M("vikram"), M("priya")], [], "Team lunch on Friday", "Hi all, we are planning a team lunch this Friday at 1 pm — please confirm by Thursday.", Ago(26)), ct);
            // Root cause posted in the support escalations channel.
            await tenant.PostChannelMessageAsync("team-support", "19:support-escalations@thread.tacv2",
                new TenantPost(U("kiran"), "Al Noor e-invoice rejections: root cause is an expired signing certificate. Renewing now — ETA 2 pm today.", Ago(0.7)), ct);
        }
    }
}
