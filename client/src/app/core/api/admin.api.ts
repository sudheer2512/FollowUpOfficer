import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AccountInput,
  AuditEntryDto,
  AuditQuery,
  AuditVerifyResult,
  CalendarDto,
  CalendarInput,
  DepartmentDto,
  DepartmentInput,
  EmployeeDto,
  EmployeeInput,
  EscalationPolicyDto,
  EscalationPolicyInput,
  HolidayInput,
  IntegrationErrorDto,
  JobDto,
  JobExecutionDto,
  MonitorOverview,
  RuntimeSettings,
  TeamDto,
  TeamInput,
} from '../models/admin.models';
import { Guid } from '../models/common.models';
import { NotificationStatus } from '../models/enums';
import { NotificationRow } from '../models/view.models';
import { enc, toParams } from './http-params';

/** /api/admin/* (Administrator; monitor and audit also for executives). */
@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);

  // ── Organisation ──
  departments(): Observable<DepartmentDto[]> {
    return this.http.get<DepartmentDto[]>('/api/admin/departments');
  }
  createDepartment(input: DepartmentInput): Observable<DepartmentDto> {
    return this.http.post<DepartmentDto>('/api/admin/departments', input);
  }
  updateDepartment(id: Guid, input: DepartmentInput): Observable<DepartmentDto> {
    return this.http.put<DepartmentDto>(`/api/admin/departments/${enc(id)}`, input);
  }

  teams(): Observable<TeamDto[]> {
    return this.http.get<TeamDto[]>('/api/admin/teams');
  }
  createTeam(input: TeamInput): Observable<TeamDto> {
    return this.http.post<TeamDto>('/api/admin/teams', input);
  }
  updateTeam(id: Guid, input: TeamInput): Observable<TeamDto> {
    return this.http.put<TeamDto>(`/api/admin/teams/${enc(id)}`, input);
  }

  employees(): Observable<EmployeeDto[]> {
    return this.http.get<EmployeeDto[]>('/api/admin/employees');
  }
  createEmployee(input: EmployeeInput): Observable<EmployeeDto> {
    return this.http.post<EmployeeDto>('/api/admin/employees', input);
  }
  updateEmployee(id: Guid, input: EmployeeInput): Observable<EmployeeDto> {
    return this.http.put<EmployeeDto>(`/api/admin/employees/${enc(id)}`, input);
  }
  updateAccount(id: Guid, input: AccountInput): Observable<EmployeeDto> {
    return this.http.put<EmployeeDto>(`/api/admin/employees/${enc(id)}/account`, input);
  }

  // ── Calendars ──
  calendars(): Observable<CalendarDto[]> {
    return this.http.get<CalendarDto[]>('/api/admin/calendars');
  }
  createCalendar(input: CalendarInput): Observable<CalendarDto> {
    return this.http.post<CalendarDto>('/api/admin/calendars', input);
  }
  updateCalendar(id: Guid, input: CalendarInput): Observable<CalendarDto> {
    return this.http.put<CalendarDto>(`/api/admin/calendars/${enc(id)}`, input);
  }
  addHoliday(calendarId: Guid, input: HolidayInput): Observable<CalendarDto> {
    return this.http.post<CalendarDto>(`/api/admin/calendars/${enc(calendarId)}/holidays`, input);
  }
  removeHoliday(calendarId: Guid, holidayId: Guid): Observable<CalendarDto> {
    return this.http.delete<CalendarDto>(`/api/admin/calendars/${enc(calendarId)}/holidays/${enc(holidayId)}`);
  }

  // ── Escalation policies ──
  escalationPolicies(): Observable<EscalationPolicyDto[]> {
    return this.http.get<EscalationPolicyDto[]>('/api/admin/escalation-policies');
  }
  createEscalationPolicy(input: EscalationPolicyInput): Observable<EscalationPolicyDto> {
    return this.http.post<EscalationPolicyDto>('/api/admin/escalation-policies', input);
  }
  updateEscalationPolicy(id: Guid, input: EscalationPolicyInput): Observable<EscalationPolicyDto> {
    return this.http.put<EscalationPolicyDto>(`/api/admin/escalation-policies/${enc(id)}`, input);
  }

  // ── Settings ──
  settings(): Observable<RuntimeSettings> {
    return this.http.get<RuntimeSettings>('/api/admin/settings');
  }
  saveSettings(settings: RuntimeSettings): Observable<RuntimeSettings> {
    return this.http.put<RuntimeSettings>('/api/admin/settings', settings);
  }

  // ── Integration monitor (spec §67) ──
  monitor(): Observable<MonitorOverview> {
    return this.http.get<MonitorOverview>('/api/admin/monitor');
  }
  jobExecutions(key: string): Observable<JobExecutionDto[]> {
    return this.http.get<JobExecutionDto[]>(`/api/admin/jobs/${enc(key)}/executions`);
  }
  runJob(key: string): Observable<void> {
    return this.http.post<void>(`/api/admin/jobs/${enc(key)}/run`, {});
  }
  setJobEnabled(key: string, enabled: boolean): Observable<JobDto> {
    return this.http.put<JobDto>(`/api/admin/jobs/${enc(key)}`, { enabled });
  }
  resetCheckpoint(id: Guid, fromScratch: boolean): Observable<void> {
    return this.http.post<void>(`/api/admin/checkpoints/${enc(id)}/reset`, { fromScratch });
  }
  setCheckpointEnabled(id: Guid, enabled: boolean): Observable<void> {
    return this.http.put<void>(`/api/admin/checkpoints/${enc(id)}`, { enabled });
  }
  runSyncNow(): Observable<void> {
    return this.http.post<void>('/api/admin/sync/run-now', {});
  }
  notifications(status?: NotificationStatus | null): Observable<NotificationRow[]> {
    return this.http.get<NotificationRow[]>('/api/admin/notifications', { params: toParams({ status }) });
  }
  retryNotifications(ids: Guid[]): Observable<{ retried: number }> {
    return this.http.post<{ retried: number }>('/api/admin/notifications/retry', { ids });
  }
  errors(): Observable<IntegrationErrorDto[]> {
    return this.http.get<IntegrationErrorDto[]>('/api/admin/errors');
  }

  // ── Audit ──
  audit(q: AuditQuery = {}): Observable<AuditEntryDto[]> {
    return this.http.get<AuditEntryDto[]>('/api/admin/audit', { params: toParams({ ...q }) });
  }
  verifyAudit(taskId: Guid): Observable<AuditVerifyResult> {
    return this.http.get<AuditVerifyResult>(`/api/admin/audit/verify/${enc(taskId)}`);
  }
}
