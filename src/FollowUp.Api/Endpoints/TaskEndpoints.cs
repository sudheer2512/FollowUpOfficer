using FollowUp.Core.Contracts;
using FollowUp.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace FollowUp.Api.Endpoints;

/// <summary>/api/tasks and related task resources. Commands return the refreshed <see cref="TaskDetail"/>.</summary>
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this RouteGroupBuilder api)
    {
        var t = api.MapGroup("/tasks").WithTags("Tasks");

        t.MapPost("/query", (TaskFilter f, TaskQueryService q, CancellationToken ct) => q.QueryAsync(f, ct));
        t.MapPost("/parse", (ParseCommandRequest r, AskService ask, CancellationToken ct) => ask.ParseCommandAsync(r.Text, r.Voice, ct));
        t.MapPost("", async (CreateTaskRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await s.CreateAsync(r, ct)).Id, ct));
        t.MapGet("/{id:guid}", (Guid id, TaskQueryService q, CancellationToken ct) => q.GetDetailAsync(id, ct));
        t.MapPut("/{id:guid}", (Guid id, UpdateTaskRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.UpdateAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/subtasks", async (Guid id, CreateTaskRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await s.CreateAsync(r with { ParentTaskId = id }, ct)).Id, ct));
        t.MapPost("/{id:guid}/reassign", (Guid id, ReassignRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.ReassignAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/assignees", (Guid id, AssigneeChange r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.AddAssigneeAsync(id, r, ct), ct));
        t.MapDelete("/{id:guid}/assignees/{assigneeId:guid}", (Guid id, Guid assigneeId, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.RemoveAssigneeAsync(id, assigneeId, ct), ct));
        t.MapPost("/{id:guid}/followers/{employeeId:guid}", async (Guid id, Guid employeeId, TaskService s, CancellationToken ct) =>
        {
            await s.SetFollowerAsync(id, employeeId, true, ct);
            return Results.NoContent();
        });
        t.MapDelete("/{id:guid}/followers/{employeeId:guid}", async (Guid id, Guid employeeId, TaskService s, CancellationToken ct) =>
        {
            await s.SetFollowerAsync(id, employeeId, false, ct);
            return Results.NoContent();
        });
        t.MapPost("/{id:guid}/acknowledge", (Guid id, AcknowledgeRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.AcknowledgeAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/status", (Guid id, StatusChangeRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.ChangeStatusAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/progress", (Guid id, ProgressRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.UpdateProgressAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/comments", (Guid id, CommentRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.CommentAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/commitments", (Guid id, CommitmentInput r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.AddCommitmentAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/complete", (Guid id, CompleteRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.CompleteAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/approval", (Guid id, ApprovalRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.DecideApprovalAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/reopen", (Guid id, ReasonRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.ReopenAsync(id, r.Reason, ct), ct));
        t.MapPost("/{id:guid}/extensions", (Guid id, ExtensionRequestInput r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.RequestExtensionAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/snooze", (Guid id, SnoozeRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.SnoozeAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/resume-followup", (Guid id, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.ResumeFollowUpAsync(id, ct), ct));
        t.MapPost("/{id:guid}/dependencies", (Guid id, DependencyInput r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.AddDependencyAsync(id, r, ct), ct));
        t.MapDelete("/{id:guid}/dependencies/{depId:guid}", (Guid id, Guid depId, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.RemoveDependencyAsync(id, depId, ct), ct));
        t.MapPost("/{id:guid}/dependencies/{depId:guid}/resolve", (Guid id, Guid depId, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.ResolveDependencyAsync(id, depId, ct), ct));
        t.MapPost("/{id:guid}/remind", async (Guid id, ReminderRequest r, TaskService s, CancellationToken ct) =>
            new QueuedResult(await s.SendReminderAsync(id, r, ct)));
        t.MapPost("/{id:guid}/escalate", (Guid id, EscalateRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, s.EscalateAsync(id, r, ct), ct));
        t.MapPost("/{id:guid}/links", (Guid id, LinkMessageRequest r, CommunicationService c, TaskQueryService q, CancellationToken ct) =>
            Http.Detail(q, id, c.LinkManuallyAsync(id, r.MessageId, ct), ct));
        t.MapGet("/{id:guid}/audit", (Guid id, TaskQueryService q, CancellationToken ct) => q.AuditAsync(new AuditQuery(id, null, null, null, null, 1, 500), ct));

        api.MapPost("/extensions/{id:guid}/decision", async (Guid id, ExtensionDecisionRequest r, TaskService s, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await s.DecideExtensionAsync(id, r, ct)).Id, ct)).WithTags("Tasks");
        api.MapPost("/suggestions/{id:guid}/accept", async (Guid id, TaskService s, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await s.DecideSuggestionAsync(id, true, ct)).Id, ct)).WithTags("Tasks");
        api.MapPost("/suggestions/{id:guid}/dismiss", async (Guid id, TaskService s, TaskQueryService q, CancellationToken ct) =>
            await q.GetDetailAsync((await s.DecideSuggestionAsync(id, false, ct)).Id, ct)).WithTags("Tasks");

        api.MapGet("/workload", (string? employeeIds, Guid? teamId, TaskQueryService q, CancellationToken ct) =>
            q.WorkloadAsync(Http.Guids(employeeIds), teamId, ct)).WithTags("Tasks");

        api.MapPost("/attachments", async (HttpRequest request, AttachmentService attachments, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) throw new Core.Common.ValidationException("multipart_required", "Upload the file as multipart/form-data with a 'file' field.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault() ?? throw new Core.Common.ValidationException("file_required", "Choose a file to upload.");
            Guid? taskId = Guid.TryParse(form["taskId"].ToString(), out var tid) ? tid : null;
            var evidence = bool.TryParse(form["isEvidence"].ToString(), out var ev) && ev;
            await using var stream = file.OpenReadStream();
            return await attachments.UploadAsync(stream, file.FileName, file.ContentType, file.Length, taskId, evidence, ct);
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(AttachmentService.MaxBytes + 1024 * 1024)).WithTags("Attachments");

        api.MapGet("/attachments/{id:guid}", async (Guid id, AttachmentService attachments, CancellationToken ct) =>
        {
            var (a, content) = await attachments.OpenAsync(id, ct);
            // Always a download (Content-Disposition: attachment); active content types are never served inline.
            var type = a.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) || a.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || a.ContentType.Contains("script", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : a.ContentType;
            return Results.File(content, type, a.FileName);
        }).WithTags("Attachments");
    }
}
