import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Guid, PagedResult } from '../models/common.models';
import { MessageDto, MessageFilter } from '../models/view.models';
import { enc, toParams } from './http-params';

/** Email and Teams communications (docs/API.md § Communications). */
@Injectable({ providedIn: 'root' })
export class MessagesApi {
  private readonly http = inject(HttpClient);

  list(filter: MessageFilter = {}): Observable<PagedResult<MessageDto>> {
    return this.http.get<PagedResult<MessageDto>>('/api/messages', { params: toParams({ ...filter }) });
  }

  get(id: Guid): Observable<MessageDto> {
    return this.http.get<MessageDto>(`/api/messages/${enc(id)}`);
  }

  /** Low-confidence suggestions awaiting a human decision. */
  reviewQueue(): Observable<MessageDto[]> {
    return this.http.get<MessageDto[]>('/api/messages/review');
  }

  confirmLink(linkId: Guid): Observable<void> {
    return this.http.post<void>(`/api/messages/links/${enc(linkId)}/confirm`, {});
  }

  rejectLink(linkId: Guid): Observable<void> {
    return this.http.post<void>(`/api/messages/links/${enc(linkId)}/reject`, {});
  }

  reprocess(id: Guid): Observable<void> {
    return this.http.post<void>(`/api/messages/${enc(id)}/reprocess`, {});
  }
}
