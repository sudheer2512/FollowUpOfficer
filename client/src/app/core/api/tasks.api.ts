import { appUrl } from '../http/app-base';
import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AuditEntryDto } from '../models/admin.models';
import { Guid, PagedResult, QueuedResult } from '../models/common.models';
import {
  AcknowledgeRequest,
  ApprovalRequest,
  AssigneeChange,
  AttachmentDto,
  CommentRequest,
  CommitmentInput,
  CompleteRequest,
  CreateTaskRequest,
  DependencyInput,
  EscalateRequest,
  ExtensionDecisionRequest,
  ExtensionRequestInput,
  LinkMessageRequest,
  ProgressRequest,
  ReassignRequest,
  ReminderRequest,
  SnoozeRequest,
  StatusChangeRequest,
  TaskDetail,
  TaskFilter,
  TaskListItem,
  UpdateTaskRequest,
  WorkloadDto,
} from '../models/task.models';
import { ParseCommandRequest, ResolvedDraft } from '../models/view.models';
import { SILENT_ERRORS } from '../http/problem';
import { toQueryBody } from '../util/task-filter-query';
import { enc, toParams } from './http-params';

/** /api/tasks/* and related task commands (docs/API.md § Tasks). */
@Injectable({ providedIn: 'root' })
export class TasksApi {
  private readonly http = inject(HttpClient);

  query(filter: TaskFilter): Observable<PagedResult<TaskListItem>> {
    return this.http.post<PagedResult<TaskListItem>>('/api/tasks/query', toQueryBody(filter));
  }

  get(id: Guid): Observable<TaskDetail> {
    return this.http.get<TaskDetail>(`/api/tasks/${enc(id)}`);
  }

  create(req: CreateTaskRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>('/api/tasks', req);
  }

  update(id: Guid, req: UpdateTaskRequest): Observable<TaskDetail> {
    return this.http.put<TaskDetail>(`/api/tasks/${enc(id)}`, req);
  }

  createSubtask(parentId: Guid, req: CreateTaskRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(parentId)}/subtasks`, req);
  }

  reassign(id: Guid, req: ReassignRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/reassign`, req);
  }

  addAssignee(id: Guid, req: AssigneeChange): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/assignees`, req);
  }

  removeAssignee(id: Guid, assigneeId: Guid): Observable<TaskDetail> {
    return this.http.delete<TaskDetail>(`/api/tasks/${enc(id)}/assignees/${enc(assigneeId)}`);
  }

  follow(id: Guid, employeeId: Guid): Observable<void> {
    return this.http.post<void>(`/api/tasks/${enc(id)}/followers/${enc(employeeId)}`, {});
  }

  unfollow(id: Guid, employeeId: Guid): Observable<void> {
    return this.http.delete<void>(`/api/tasks/${enc(id)}/followers/${enc(employeeId)}`);
  }

  acknowledge(id: Guid, req: AcknowledgeRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/acknowledge`, req);
  }

  changeStatus(id: Guid, req: StatusChangeRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/status`, req);
  }

  progress(id: Guid, req: ProgressRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/progress`, req);
  }

  comment(id: Guid, req: CommentRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/comments`, req);
  }

  addCommitment(id: Guid, req: CommitmentInput): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/commitments`, req);
  }

  complete(id: Guid, req: CompleteRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/complete`, req);
  }

  approval(id: Guid, req: ApprovalRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/approval`, req);
  }

  reopen(id: Guid, reason: string): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/reopen`, { reason });
  }

  requestExtension(id: Guid, req: ExtensionRequestInput): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/extensions`, req);
  }

  decideExtension(extensionId: Guid, req: ExtensionDecisionRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/extensions/${enc(extensionId)}/decision`, req);
  }

  snooze(id: Guid, req: SnoozeRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/snooze`, req);
  }

  resumeFollowUp(id: Guid): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/resume-followup`, {});
  }

  addDependency(id: Guid, req: DependencyInput): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/dependencies`, req);
  }

  removeDependency(id: Guid, depId: Guid): Observable<TaskDetail> {
    return this.http.delete<TaskDetail>(`/api/tasks/${enc(id)}/dependencies/${enc(depId)}`);
  }

  resolveDependency(id: Guid, depId: Guid): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/dependencies/${enc(depId)}/resolve`, {});
  }

  /** Send Reminder / Request Update / Send Email / Send Teams Message quick actions. */
  remind(id: Guid, req: ReminderRequest): Observable<QueuedResult> {
    return this.http.post<QueuedResult>(`/api/tasks/${enc(id)}/remind`, req);
  }

  escalate(id: Guid, req: EscalateRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/escalate`, req);
  }

  linkMessage(id: Guid, req: LinkMessageRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/tasks/${enc(id)}/links`, req);
  }

  audit(id: Guid): Observable<AuditEntryDto[]> {
    return this.http.get<AuditEntryDto[]>(`/api/tasks/${enc(id)}/audit`);
  }

  acceptSuggestion(suggestionId: Guid): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/suggestions/${enc(suggestionId)}/accept`, {});
  }

  dismissSuggestion(suggestionId: Guid): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/suggestions/${enc(suggestionId)}/dismiss`, {});
  }

  /** multipart upload: `file`, optional `taskId` and `isEvidence`. */
  uploadAttachment(file: File, opts: { taskId?: Guid | null; isEvidence?: boolean } = {}): Observable<AttachmentDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (opts.taskId) form.append('taskId', opts.taskId);
    if (opts.isEvidence !== undefined) form.append('isEvidence', String(opts.isEvidence));
    return this.http.post<AttachmentDto>('/api/attachments', form);
  }

  attachmentUrl(attachmentId: Guid): string {
    return appUrl(`/api/attachments/${enc(attachmentId)}`);
  }

  workload(employeeIds: Guid[], teamId?: Guid | null): Observable<WorkloadDto[]> {
    return this.http.get<WorkloadDto[]>('/api/workload', {
      params: toParams({ employeeIds, teamId }),
      context: new HttpContext().set(SILENT_ERRORS, true),
    });
  }

  /** Natural-language / voice delegation → draft for confirmation. */
  parse(req: ParseCommandRequest): Observable<ResolvedDraft> {
    return this.http.post<ResolvedDraft>('/api/tasks/parse', req);
  }
}
