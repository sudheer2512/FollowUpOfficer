import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { DateOnly, Guid } from '../models/common.models';
import { OccurrenceOutcome } from '../models/enums';
import {
  OccurrencePreview,
  PreviewRequest,
  RecurringDefinitionDto,
  RecurringDefinitionInput,
  RecurringInstanceDto,
  RecurringPauseRequest,
  RecurringUpdateRequest,
  SkipOccurrenceRequest,
} from '../models/view.models';
import { SILENT_ERRORS } from '../http/problem';
import { enc, toParams } from './http-params';

export interface InstanceQuery {
  definitionId?: Guid | null;
  outcome?: OccurrenceOutcome | null;
  from?: DateOnly | null;
  to?: DateOnly | null;
  missedOnly?: boolean | null;
}

/** /api/recurring/* (docs/API.md § Recurring tasks). */
@Injectable({ providedIn: 'root' })
export class RecurringApi {
  private readonly http = inject(HttpClient);

  list(includeEnded = false): Observable<RecurringDefinitionDto[]> {
    return this.http.get<RecurringDefinitionDto[]>('/api/recurring', { params: toParams({ includeEnded }) });
  }

  get(id: Guid): Observable<RecurringDefinitionDto> {
    return this.http.get<RecurringDefinitionDto>(`/api/recurring/${enc(id)}`);
  }

  create(input: RecurringDefinitionInput): Observable<RecurringDefinitionDto> {
    return this.http.post<RecurringDefinitionDto>('/api/recurring', input);
  }

  update(id: Guid, req: RecurringUpdateRequest): Observable<RecurringDefinitionDto> {
    return this.http.put<RecurringDefinitionDto>(`/api/recurring/${enc(id)}`, req);
  }

  pause(id: Guid, req: RecurringPauseRequest): Observable<RecurringDefinitionDto> {
    return this.http.post<RecurringDefinitionDto>(`/api/recurring/${enc(id)}/pause`, req);
  }

  resume(id: Guid): Observable<RecurringDefinitionDto> {
    return this.http.post<RecurringDefinitionDto>(`/api/recurring/${enc(id)}/resume`, {});
  }

  end(id: Guid, reason: string): Observable<RecurringDefinitionDto> {
    return this.http.post<RecurringDefinitionDto>(`/api/recurring/${enc(id)}/end`, { reason });
  }

  skip(id: Guid, req: SkipOccurrenceRequest): Observable<RecurringInstanceDto> {
    return this.http.post<RecurringInstanceDto>(`/api/recurring/${enc(id)}/skip`, req);
  }

  /** Live preview; errors are silent because the form calls it on every change. */
  preview(req: PreviewRequest): Observable<OccurrencePreview[]> {
    return this.http.post<OccurrencePreview[]>('/api/recurring/preview', req, {
      context: new HttpContext().set(SILENT_ERRORS, true),
    });
  }

  instances(q: InstanceQuery = {}): Observable<RecurringInstanceDto[]> {
    return this.http.get<RecurringInstanceDto[]>('/api/recurring/instances', { params: toParams({ ...q }) });
  }
}
