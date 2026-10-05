import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CreateActionTasksRequest,
  InstantiateTemplateRequest,
  MeetingDto,
  MeetingInput,
  ProjectInput,
  TemplateDto,
  TemplateInput,
} from '../models/admin.models';
import { Guid } from '../models/common.models';
import { TaskDetail } from '../models/task.models';
import { ProjectDetail, ProjectSummary } from '../models/view.models';
import { enc } from './http-params';

/** Projects, meetings and templates (docs/API.md § Projects, meetings, templates). */
@Injectable({ providedIn: 'root' })
export class PlanningApi {
  private readonly http = inject(HttpClient);

  // ── Projects ──
  projects(): Observable<ProjectSummary[]> {
    return this.http.get<ProjectSummary[]>('/api/projects');
  }

  project(id: Guid): Observable<ProjectDetail> {
    return this.http.get<ProjectDetail>(`/api/projects/${enc(id)}`);
  }

  createProject(input: ProjectInput): Observable<ProjectSummary> {
    return this.http.post<ProjectSummary>('/api/projects', input);
  }

  updateProject(id: Guid, input: ProjectInput): Observable<ProjectSummary> {
    return this.http.put<ProjectSummary>(`/api/projects/${enc(id)}`, input);
  }

  // ── Meetings ──
  meetings(): Observable<MeetingDto[]> {
    return this.http.get<MeetingDto[]>('/api/meetings');
  }

  meeting(id: Guid): Observable<MeetingDto> {
    return this.http.get<MeetingDto>(`/api/meetings/${enc(id)}`);
  }

  createMeeting(input: MeetingInput): Observable<MeetingDto> {
    return this.http.post<MeetingDto>('/api/meetings', input);
  }

  updateMeeting(id: Guid, input: MeetingInput): Observable<MeetingDto> {
    return this.http.put<MeetingDto>(`/api/meetings/${enc(id)}`, input);
  }

  createActionTasks(id: Guid, req: CreateActionTasksRequest): Observable<MeetingDto> {
    return this.http.post<MeetingDto>(`/api/meetings/${enc(id)}/create-tasks`, req);
  }

  // ── Templates ──
  templates(): Observable<TemplateDto[]> {
    return this.http.get<TemplateDto[]>('/api/templates');
  }

  template(id: Guid): Observable<TemplateDto> {
    return this.http.get<TemplateDto>(`/api/templates/${enc(id)}`);
  }

  createTemplate(input: TemplateInput): Observable<TemplateDto> {
    return this.http.post<TemplateDto>('/api/templates', input);
  }

  updateTemplate(id: Guid, input: TemplateInput): Observable<TemplateDto> {
    return this.http.put<TemplateDto>(`/api/templates/${enc(id)}`, input);
  }

  instantiateTemplate(id: Guid, req: InstantiateTemplateRequest): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`/api/templates/${enc(id)}/instantiate`, req);
  }
}
