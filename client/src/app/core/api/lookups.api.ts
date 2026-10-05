import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { HealthInfo, Lookups, SavedViewDto, SavedViewInput, SystemInfo } from '../models/admin.models';
import { Guid } from '../models/common.models';
import { SearchResult } from '../models/view.models';
import { SILENT_ERRORS } from '../http/problem';
import { enc, toParams } from './http-params';

/** Lookups, global search, saved views and system info. */
@Injectable({ providedIn: 'root' })
export class LookupsApi {
  private readonly http = inject(HttpClient);

  lookups(): Observable<Lookups> {
    return this.http.get<Lookups>('/api/lookups');
  }

  search(q: string): Observable<SearchResult> {
    return this.http.get<SearchResult>('/api/search', { params: toParams({ q }) });
  }

  views(): Observable<SavedViewDto[]> {
    return this.http.get<SavedViewDto[]>('/api/views');
  }

  createView(input: SavedViewInput): Observable<SavedViewDto> {
    return this.http.post<SavedViewDto>('/api/views', input);
  }

  updateView(id: Guid, input: SavedViewInput): Observable<SavedViewDto> {
    return this.http.put<SavedViewDto>(`/api/views/${enc(id)}`, input);
  }

  deleteView(id: Guid): Observable<void> {
    return this.http.delete<void>(`/api/views/${enc(id)}`);
  }

  systemInfo(): Observable<SystemInfo> {
    return this.http.get<SystemInfo>('/api/system/info', { context: new HttpContext().set(SILENT_ERRORS, true) });
  }

  health(): Observable<HealthInfo> {
    return this.http.get<HealthInfo>('/api/health', { context: new HttpContext().set(SILENT_ERRORS, true) });
  }
}
