import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { DateOnly, Guid, NamedRef, QueuedResult } from '../models/common.models';
import { AttentionStatus, CommitmentStatus } from '../models/enums';
import { CommitmentDto } from '../models/task.models';
import {
  AskAnswer,
  AskRequest,
  AttentionDecision,
  AttentionItemDto,
  CeoWorkbench,
  DigestDto,
  ExecutiveSummary,
  InboxItem,
  MyDashboard,
  ReportDto,
  ReportRequest,
  TeamDashboard,
  UnreadCount,
  WeeklyReviewDto,
} from '../models/view.models';
import { SILENT_ERRORS } from '../http/problem';
import { enc, toParams } from './http-params';

export interface CommitmentQuery {
  status?: CommitmentStatus | 'All' | null;
  toMe?: boolean | null;
  employeeId?: Guid | null;
  from?: DateOnly | null;
  to?: DateOnly | null;
}

/** Executive views: workbench, dashboards, attention, summary, digest, review, ask, commitments, reports, inbox. */
@Injectable({ providedIn: 'root' })
export class ExecutiveApi {
  private readonly http = inject(HttpClient);

  workbench(): Observable<CeoWorkbench> {
    return this.http.get<CeoWorkbench>('/api/workbench');
  }

  myDashboard(): Observable<MyDashboard> {
    return this.http.get<MyDashboard>('/api/dashboard/me');
  }

  /** Teams the user leads or manages (all teams for executives). */
  dashboardTeams(): Observable<NamedRef[]> {
    return this.http.get<NamedRef[]>('/api/dashboard/teams');
  }

  teamDashboard(teamId: Guid): Observable<TeamDashboard> {
    return this.http.get<TeamDashboard>(`/api/dashboard/team/${enc(teamId)}`);
  }

  attention(status: AttentionStatus | 'All' = 'Open'): Observable<AttentionItemDto[]> {
    return this.http.get<AttentionItemDto[]>('/api/attention', { params: toParams({ status }) });
  }

  decideAttention(id: Guid, decision: AttentionDecision): Observable<AttentionItemDto> {
    return this.http.post<AttentionItemDto>(`/api/attention/${enc(id)}/decision`, decision);
  }

  summary(): Observable<ExecutiveSummary> {
    return this.http.get<ExecutiveSummary>('/api/summary');
  }

  digestPreview(): Observable<DigestDto> {
    return this.http.get<DigestDto>('/api/digest/preview');
  }

  sendDigest(): Observable<QueuedResult> {
    return this.http.post<QueuedResult>('/api/digest/send', {});
  }

  weeklyReview(weekStart?: DateOnly | null): Observable<WeeklyReviewDto> {
    return this.http.get<WeeklyReviewDto>('/api/review/weekly', { params: toParams({ weekStart }) });
  }

  sendWeeklyReview(weekStart?: DateOnly | null): Observable<QueuedResult> {
    return this.http.post<QueuedResult>('/api/review/weekly/send', {}, { params: toParams({ weekStart }) });
  }

  ask(req: AskRequest): Observable<AskAnswer> {
    return this.http.post<AskAnswer>('/api/ask', req);
  }

  commitments(q: CommitmentQuery = {}): Observable<CommitmentDto[]> {
    return this.http.get<CommitmentDto[]>('/api/commitments', { params: toParams({ ...q }) });
  }

  reports(req: ReportRequest = {}): Observable<ReportDto> {
    return this.http.get<ReportDto>('/api/reports', { params: toParams({ ...req }) });
  }

  inbox(): Observable<InboxItem[]> {
    return this.http.get<InboxItem[]>('/api/inbox');
  }

  unreadCount(): Observable<UnreadCount> {
    return this.http.get<UnreadCount>('/api/inbox/unread-count', { context: new HttpContext().set(SILENT_ERRORS, true) });
  }

  markRead(id: Guid): Observable<void> {
    return this.http.post<void>(`/api/inbox/${enc(id)}/read`, {});
  }

  markAllRead(): Observable<void> {
    return this.http.post<void>('/api/inbox/read-all', {});
  }
}
