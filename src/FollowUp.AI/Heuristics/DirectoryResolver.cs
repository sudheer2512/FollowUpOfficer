using System.Text.RegularExpressions;
using FollowUp.AI.Text;
using FollowUp.Core.Abstractions;

namespace FollowUp.AI.Heuristics;

/// <summary>Kinds of directory entities a phrase can resolve to.</summary>
internal enum EntityKind { Person, Team, Department, Project }

/// <summary>A directory entity a phrase resolved to, with a 0–1 score and how it matched ("first name", "alias"…).</summary>
internal sealed record ResolvedEntity(EntityKind Kind, Guid Id, string Name, double Score, string How)
{
    public Guid? LeaderId { get; init; }
    public Guid? DepartmentId { get; init; }
}

/// <summary>An entity mentioned in free text at a character span.</summary>
internal sealed record EntityMention(ResolvedEntity Entity, int Index, int Length, string Text);

/// <summary>
/// Resolves people, teams, departments and projects in a <see cref="DirectorySnapshot"/> by display name, first /
/// last name, email local part, job title and aliases — case-insensitive, tolerant of one typo for names of five or
/// more characters, and of punctuation differences for projects ("eInvoice" = "e-invoice"). Immutable after
/// construction; safe to share across threads.
/// </summary>
internal sealed partial class DirectoryResolver
{
    private readonly DirectorySnapshot _dir;
    private readonly Lazy<List<SurfaceForm>> _forms;

    public DirectoryResolver(DirectorySnapshot directory)
    {
        _dir = directory;
        _forms = new Lazy<List<SurfaceForm>>(BuildForms);
    }

    public DirectorySnapshot Directory => _dir;

    // ───────────────────────────── phrase resolution ─────────────────────────────

    /// <summary>Resolves a person phrase ("Rahul", "the finance manager", "rahul.sharma"). Ambiguous names return null.</summary>
    public ResolvedEntity? ResolvePerson(string phrase, out IReadOnlyList<string> ambiguousWith)
    {
        ambiguousWith = [];
        var p = Normalize(phrase);
        if (p.Length == 0) return null;
        var pTokens = p.Split(' ');
        var scored = new List<(DirectoryPerson Person, double Score, string How)>();
        foreach (var person in _dir.People)
        {
            var best = (Score: 0.0, How: "");
            void Consider(double s, string how) { if (s > best.Score) best = (s, how); }

            var display = Normalize(person.DisplayName);
            var names = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p == display) Consider(1.0, "display name");
            foreach (var a in person.Aliases)
                if (p == Normalize(a)) Consider(0.97, "alias");
            if (EmailLocal(person.Email) is { Length: > 0 } local && (p == local || p == local.Replace('.', ' ').Replace('_', ' ')))
                Consider(0.95, "email");
            if (!string.IsNullOrWhiteSpace(person.JobTitle) && p == Normalize(person.JobTitle)) Consider(0.93, "job title");
            if (names.Length > 1 && pTokens.Length == 1)
            {
                if (p == names[0]) Consider(0.9, "first name");
                else if (p == names[^1]) Consider(0.85, "last name");
            }

            // One-typo tolerance for names of five or more characters ("Rahl Sharma" is not tolerated, "Raahul" is).
            if (best.Score < 0.75)
            {
                if (TokensFuzzyEqual(pTokens, names)) Consider(0.78, "fuzzy name");
                else if (pTokens.Length == 1 && names.Length > 0 && TextTools.FuzzyEquals(p, names[0])) Consider(0.75, "fuzzy first name");
                else if (person.Aliases.Any(a => TokensFuzzyEqual(pTokens, Normalize(a).Split(' ')))) Consider(0.75, "fuzzy alias");
                else if (!string.IsNullOrWhiteSpace(person.JobTitle) && TokensFuzzyEqual(pTokens, Normalize(person.JobTitle).Split(' '))) Consider(0.72, "fuzzy job title");
            }
            if (best.Score > 0) scored.Add((person, best.Score, best.How));
        }
        if (scored.Count == 0) return null;
        var top = scored.Max(s => s.Score);
        var winners = scored.Where(s => Math.Abs(s.Score - top) < 0.001).ToList();
        if (winners.Count > 1)
        {
            ambiguousWith = winners.Select(w => w.Person.DisplayName).ToList();
            return null;
        }
        var w0 = winners[0];
        return new ResolvedEntity(EntityKind.Person, w0.Person.Id, w0.Person.DisplayName, w0.Score, w0.How) { DepartmentId = w0.Person.DepartmentId };
    }

    /// <summary>Resolves a team phrase ("the development team", "dev team").</summary>
    public ResolvedEntity? ResolveTeam(string phrase)
    {
        var p = StripSuffix(Normalize(phrase), TeamSuffixRegex());
        if (p.Length == 0) return null;
        ResolvedEntity? best = null;
        foreach (var t in _dir.Teams)
        {
            var s = MatchName(p, t.Name, t.Aliases, TeamSuffixRegex(), out var how);
            if (s > (best?.Score ?? 0))
                best = new ResolvedEntity(EntityKind.Team, t.Id, t.Name, s, how) { LeaderId = t.LeaderId, DepartmentId = t.DepartmentId };
        }
        return best;
    }

    /// <summary>Resolves a department phrase ("Finance", "the finance department", "QA dept").</summary>
    public ResolvedEntity? ResolveDepartment(string phrase)
    {
        var p = StripSuffix(Normalize(phrase), DepartmentSuffixRegex());
        if (p.Length == 0) return null;
        ResolvedEntity? best = null;
        foreach (var d in _dir.Departments)
        {
            var s = MatchName(p, d.Name, d.Aliases, DepartmentSuffixRegex(), out var how);
            if (s > (best?.Score ?? 0)) best = new ResolvedEntity(EntityKind.Department, d.Id, d.Name, s, how);
        }
        return best;
    }

    /// <summary>Resolves a project phrase ("eInvoice", "the e-invoicing project", "UAE e-Invoicing Launch").</summary>
    public ResolvedEntity? ResolveProject(string phrase)
    {
        var p = StripSuffix(Normalize(phrase), ProjectAffixRegex());
        if (p.Length == 0) return null;
        var pc = TextTools.Compact(p);
        var pStems = TextTools.ContentTokens(p);
        ResolvedEntity? best = null;
        foreach (var proj in _dir.Projects)
        {
            var bestHere = (Score: 0.0, How: "");
            void Consider(double s, string how) { if (s > bestHere.Score) bestHere = (s, how); }
            var forms = new[] { (proj.Name, true) }.Concat(proj.Aliases.Select(a => (a, false)));
            foreach (var (form, isName) in forms)
            {
                var f = StripSuffix(Normalize(form), ProjectAffixRegex());
                var fc = TextTools.Compact(f);
                if (f.Length == 0) continue;
                if (p == f) Consider(isName ? 1.0 : 0.95, isName ? "project name" : "project alias");
                else if (pc == fc) Consider(0.92, "project name (punctuation-insensitive)");
                else if (pc.Length >= 5 && fc.Length >= 5 && (fc.Contains(pc, StringComparison.Ordinal) || pc.Contains(fc, StringComparison.Ordinal)))
                    Consider(0.8, "partial project name");
                else if (pc.Length >= 5 && TextTools.EditDistance(pc, fc, 2) <= (pc.Length >= 9 ? 2 : 1))
                    Consider(0.72, "fuzzy project name");
                var fStems = TextTools.ContentTokens(f);
                if (pStems.Count > 0 && fStems.Count > 0 && pStems.All(fStems.Contains)) Consider(0.75, "project name words");
            }
            if (bestHere.Score > (best?.Score ?? 0)) best = new ResolvedEntity(EntityKind.Project, proj.Id, proj.Name, bestHere.Score, bestHere.How);
        }
        return best;
    }

    /// <summary>
    /// Resolves an assignee phrase: a "team" suffix prefers teams, a "department" suffix departments, otherwise people
    /// first, then teams, then departments. Returns null when nothing scores at least 0.7.
    /// </summary>
    public ResolvedEntity? ResolveAssignee(string phrase, out IReadOnlyList<string> ambiguousWith)
    {
        ambiguousWith = [];
        var p = Normalize(phrase);
        if (p.Length == 0) return null;
        var team = ResolveTeam(p);
        var dept = ResolveDepartment(p);
        if (TeamSuffixRegex().IsMatch(p))
        {
            if (team is { Score: >= 0.7 }) return team;
            // "the QA team" when QA is a department rather than a team.
            if (ResolveDepartment(StripSuffix(p, TeamSuffixRegex())) is { Score: >= 0.7 } d)
                return d with { Score = d.Score * 0.95, How = d.How + " (department named as a team)" };
        }
        if (DepartmentSuffixRegex().IsMatch(p) && dept is { Score: >= 0.7 }) return dept;
        var person = ResolvePerson(p, out ambiguousWith);
        var options = new[] { person, team, dept }.Where(e => e is { Score: >= 0.7 }).ToList();
        if (options.Count == 0) return null;
        // People win ties: "Finance Manager" is a person, "Finance" is a department.
        return options.OrderByDescending(e => e!.Score + (e.Kind == EntityKind.Person ? 0.01 : 0)).First();
    }

    // ───────────────────────────── mentions in free text ─────────────────────────────

    /// <summary>All directory entities mentioned in <paramref name="text"/> (longest, strongest mention wins on overlap).</summary>
    public List<EntityMention> FindMentions(string text)
    {
        var found = new List<EntityMention>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        foreach (var f in _forms.Value)
        {
            var start = 0;
            while (start < text.Length)
            {
                var idx = text.IndexOf(f.Form, start, f.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var end = idx + f.Form.Length;
                if (IsBoundary(text, idx - 1) && IsBoundary(text, end))
                    found.Add(new EntityMention(f.Entity with { Score = f.Score, How = f.How }, idx, f.Form.Length, text.Substring(idx, f.Form.Length)));
                start = idx + 1;
            }
        }

        // Single words that are one typo away from a name, or equal to a project alias once punctuation is ignored.
        foreach (Match w in WordRunRegex().Matches(text))
        {
            var compact = TextTools.Compact(w.Value);
            if (compact.Length < 5) continue;
            foreach (var f in _forms.Value)
            {
                if (f.Entity.Kind == EntityKind.Project && f.Compact.Length >= 5 && f.Compact == compact)
                    found.Add(new EntityMention(f.Entity with { Score = f.Score * 0.97, How = f.How + " (punctuation-insensitive)" }, w.Index, w.Length, w.Value));
                else if (f.SingleWord && f.Form.Length >= 5 && !w.Value.Equals(f.Form, StringComparison.OrdinalIgnoreCase)
                         && TextTools.FuzzyEquals(w.Value, f.Form))
                    found.Add(new EntityMention(f.Entity with { Score = Math.Min(0.72, f.Score), How = "fuzzy " + f.How }, w.Index, w.Length, w.Value));
            }
        }

        var accepted = new List<EntityMention>();
        foreach (var m in found.OrderByDescending(m => m.Length).ThenByDescending(m => m.Entity.Score))
        {
            var overlapping = accepted.FirstOrDefault(a => m.Index < a.Index + a.Length && a.Index < m.Index + m.Length);
            if (overlapping is null) accepted.Add(m);
            else if (overlapping.Index == m.Index && overlapping.Length == m.Length && overlapping.Entity.Kind != m.Entity.Kind)
                accepted.Add(m); // same words naming two kinds of entity (department "Development" and team alias "development")
        }
        accepted.Sort((a, b) => a.Index.CompareTo(b.Index));
        return accepted;
    }

    private List<SurfaceForm> BuildForms()
    {
        var list = new List<SurfaceForm>();
        void Add(string? form, ResolvedEntity e, double score, string how, bool allowStopWord = false)
        {
            var f = TextTools.Squash(form);
            if (f.Length < 2) return;
            var single = !f.Contains(' ', StringComparison.Ordinal);
            if (single && !allowStopWord && TextTools.StopWords.Contains(f)) return;
            // Short single-word forms ("QA", "IT", "HR") must match case exactly to avoid catching ordinary words.
            list.Add(new SurfaceForm(f, e, score, how, single, CaseSensitive: single && f.Length <= 3, TextTools.Compact(f)));
        }

        foreach (var p in _dir.People)
        {
            var e = new ResolvedEntity(EntityKind.Person, p.Id, p.DisplayName, 1, "") { DepartmentId = p.DepartmentId };
            Add(p.DisplayName, e, 1.0, "display name");
            foreach (var a in p.Aliases) Add(a, e, 0.97, "alias");
            var names = TextTools.Squash(p.DisplayName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length > 1)
            {
                if (names[0].Length >= 3) Add(names[0], e, 0.88, "first name");
                if (names[^1].Length >= 3) Add(names[^1], e, 0.8, "last name");
            }
            if (EmailLocal(p.Email) is { Length: >= 3 } local) Add(local, e, 0.9, "email");
            if (!string.IsNullOrWhiteSpace(p.JobTitle))
                Add(p.JobTitle, e, p.JobTitle.Trim().Contains(' ', StringComparison.Ordinal) ? 0.9 : 0.6, "job title");
        }
        foreach (var t in _dir.Teams)
        {
            var e = new ResolvedEntity(EntityKind.Team, t.Id, t.Name, 1, "") { LeaderId = t.LeaderId, DepartmentId = t.DepartmentId };
            Add(t.Name, e, 1.0, "team name");
            var bare = StripSuffix(Normalize(t.Name), TeamSuffixRegex());
            if (bare.Length > 0 && bare != Normalize(t.Name)) Add(bare, e, 0.85, "team name");
            foreach (var a in t.Aliases) Add(a, e, 0.9, "team alias");
        }
        foreach (var d in _dir.Departments)
        {
            var e = new ResolvedEntity(EntityKind.Department, d.Id, d.Name, 1, "");
            Add(d.Name, e, 1.0, "department name");
            foreach (var a in d.Aliases) Add(a, e, 0.95, "department alias");
        }
        foreach (var p in _dir.Projects)
        {
            var e = new ResolvedEntity(EntityKind.Project, p.Id, p.Name, 1, "");
            Add(p.Name, e, 1.0, "project name");
            foreach (var a in p.Aliases) Add(a, e, 0.95, "project alias");
        }
        return list;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Lower-cases, squashes whitespace, strips determiners / honorifics, possessives and punctuation at the ends.</summary>
    public static string Normalize(string? phrase)
    {
        var s = TextTools.Squash(phrase).ToLowerInvariant().Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')');
        s = LeadingDeterminerRegex().Replace(s, "");
        if (s.EndsWith("'s", StringComparison.Ordinal) || s.EndsWith("’s", StringComparison.Ordinal)) s = s[..^2];
        return s.Trim();
    }

    private static string StripSuffix(string normalized, Regex suffix) => suffix.Replace(normalized, "").Trim();

    private static double MatchName(string p, string name, IReadOnlyList<string> aliases, Regex suffix, out string how)
    {
        how = "";
        var n = Normalize(name);
        var bare = StripSuffix(n, suffix);
        if (p == n || p == bare) { how = "name"; return 1.0; }
        foreach (var a in aliases)
        {
            var an = Normalize(a);
            if (p == an || p == StripSuffix(an, suffix)) { how = "alias"; return 0.95; }
        }
        var pc = TextTools.Compact(p);
        if (pc.Length > 0 && (pc == TextTools.Compact(bare) || aliases.Any(a => TextTools.Compact(StripSuffix(Normalize(a), suffix)) == pc)))
        {
            how = "name (punctuation-insensitive)";
            return 0.9;
        }
        if (TokensFuzzyEqual(p.Split(' '), bare.Split(' ')) || aliases.Any(a => TokensFuzzyEqual(p.Split(' '), StripSuffix(Normalize(a), suffix).Split(' '))))
        {
            how = "fuzzy name";
            return 0.75;
        }
        return 0;
    }

    private static bool TokensFuzzyEqual(string[] a, string[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return false;
        var anyFuzzy = false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (!TextTools.FuzzyEquals(a[i], b[i])) return false;
            anyFuzzy = true;
        }
        return anyFuzzy || a.SequenceEqual(b);
    }

    private static string? EmailLocal(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return (at > 0 ? email[..at] : email).Trim().ToLowerInvariant();
    }

    private static bool IsBoundary(string text, int i) => i < 0 || i >= text.Length || !char.IsLetterOrDigit(text[i]);

    private sealed record SurfaceForm(string Form, ResolvedEntity Entity, double Score, string How, bool SingleWord, bool CaseSensitive, string Compact);

    [GeneratedRegex(@"^(?:(?:the|our|my|your|their|his|her|mr|mrs|ms|dr|shri|sri)\.?\s+)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDeterminerRegex();

    [GeneratedRegex(@"\s+(?:team|squad|group)$", RegexOptions.CultureInvariant)]
    private static partial Regex TeamSuffixRegex();

    [GeneratedRegex(@"\s+(?:department|dept\.?|division|function|dept)$", RegexOptions.CultureInvariant)]
    private static partial Regex DepartmentSuffixRegex();

    [GeneratedRegex(@"^project\s+|\s+(?:project|programme|program|initiative)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectAffixRegex();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-_.]*[\p{L}\p{N}]", RegexOptions.CultureInvariant)]
    private static partial Regex WordRunRegex();
}
