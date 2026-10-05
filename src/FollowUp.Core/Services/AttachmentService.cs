using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// Attachments and evidence files (spec §32, §69): size and file-type checks, encrypted storage through
/// <see cref="IFileStorage"/>, and downloads authorised through the owning task's visibility.
/// </summary>
public sealed class AttachmentService(FollowUpDbContext db, ActorContext actor, IClock clock, IFileStorage storage, TaskService tasks, AuditService audit)
{
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>Executable and script types are never accepted.</summary>
    public static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".bat", ".cmd", ".msi", ".msp", ".msix", ".appx", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".scr", ".cpl", ".hta", ".jar", ".sh", ".reg", ".lnk", ".pif", ".application", ".gadget", ".inf", ".sys", ".drv", ".ocx", ".apk",
    };

    public async Task<AttachmentDto> UploadAsync(Stream content, string? fileName, string? contentType, long length, Guid? taskId, bool isEvidence, CancellationToken ct)
    {
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to upload files.");
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        if (name.Length == 0) throw new ValidationException("file_name_required", "The file needs a name.");
        if (name.Length > 200) name = name[..(200 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
        if (length <= 0) throw new ValidationException("file_empty", "The file is empty.");
        if (length > MaxBytes) throw new ValidationException("file_too_large", "Files can be at most 25 MB.");
        if (BlockedExtensions.Contains(Path.GetExtension(name))) throw new ValidationException("file_type_blocked", "Executable and script files cannot be attached.");
        if (taskId is { } tid) await tasks.LoadAsync(tid, ct);                    // 404 unless the task is visible

        var stored = await storage.SaveAsync(content, name, ct);
        if (stored.SizeBytes > MaxBytes)
        {
            await storage.DeleteAsync(stored.StorageKey, ct);
            throw new ValidationException("file_too_large", "Files can be at most 25 MB.");
        }
        var attachment = new Attachment
        {
            TaskId = taskId,
            FileName = name,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Length > 150 ? "application/octet-stream" : contentType,
            SizeBytes = stored.SizeBytes,
            StorageKey = stored.StorageKey,
            Sha256 = stored.Sha256,
            UploadedById = me,
            UploadedAt = clock.UtcNow,
            Source = CommunicationSource.Manual,
            IsEvidence = isEvidence,
        };
        db.Attachments.Add(attachment);
        audit.Record("AttachmentAdded", nameof(Attachment), attachment.Id, taskId, "File", null, name);
        if (taskId is not null) UiEvents.Publish(db, clock, UiEvents.TaskChanged, taskId);
        await db.SaveChangesAsync(ct);
        var uploader = await db.Employees.AsNoTracking().Where(e => e.Id == me).Select(e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email)).FirstOrDefaultAsync(ct);
        return new AttachmentDto(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.UploadedAt, uploader, attachment.IsEvidence);
    }

    /// <summary>Opens an attachment for download after checking the caller may see it.</summary>
    public async Task<(Attachment Attachment, Stream Content)> OpenAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Attachment");
        if (a.TaskId is { } taskId) await tasks.LoadAsync(taskId, ct);
        else if (!actor.IsSystem && a.UploadedById != actor.EmployeeId && !actor.Viewer.IsExecutive && !actor.Viewer.IsAdmin)
            throw new NotFoundException("Attachment");
        return (a, await storage.OpenReadAsync(a.StorageKey, ct));
    }
}
