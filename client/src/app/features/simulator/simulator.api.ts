import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  SimActivity,
  SimChannel,
  SimChat,
  SimChatMessage,
  SimFaults,
  SimMailFolder,
  SimMailMessage,
  SimOneOnOneRequest,
  SimPostChannelMessageRequest,
  SimPostChatMessageRequest,
  SimSendMailRequest,
  SimUser,
  asArray,
  idOf,
  normaliseActivity,
  normaliseChannelMessages,
  normaliseChannels,
  normaliseChat,
  normaliseChatMessage,
  normaliseFaults,
  normaliseMail,
  normaliseUser,
} from './simulator.models';

const BASE = '/api/m365-sim';

function enc(value: string): string {
  return encodeURIComponent(value);
}

function present<T>(x: T | null): x is T {
  return x !== null;
}

/**
 * Client for the Microsoft 365 emulator console (Emulator mode only). Talks to /api/m365-sim directly
 * (the global interceptor adds the CSRF header and credentials) and returns normalised view types.
 */
@Injectable({ providedIn: 'root' })
export class SimulatorApi {
  private readonly http = inject(HttpClient);

  users(): Observable<SimUser[]> {
    return this.http.get<unknown>(`${BASE}/users`).pipe(
      map((r) =>
        asArray(r)
          .map((u) => normaliseUser(u))
          .filter(present)
          .sort((a, b) => a.displayName.localeCompare(b.displayName)),
      ),
    );
  }

  messages(address: string, folder: SimMailFolder): Observable<SimMailMessage[]> {
    return this.http
      .get<unknown>(`${BASE}/mailboxes/${enc(address)}/messages`, { params: { folder } })
      .pipe(map((r) => asArray(r).map((m) => normaliseMail(m)).filter(present)));
  }

  chats(): Observable<SimChat[]> {
    return this.http.get<unknown>(`${BASE}/chats`).pipe(map((r) => asArray(r).map((c) => normaliseChat(c)).filter(present)));
  }

  chatMessages(chatId: string): Observable<SimChatMessage[]> {
    return this.http
      .get<unknown>(`${BASE}/chats/${enc(chatId)}/messages`)
      .pipe(map((r) => asArray(r).map((m) => normaliseChatMessage(m)).filter(present)));
  }

  channels(): Observable<SimChannel[]> {
    return this.http.get<unknown>(`${BASE}/channels`).pipe(map((r) => normaliseChannels(r)));
  }

  channelMessages(teamId: string, channelId: string): Observable<SimChatMessage[]> {
    return this.http
      .get<unknown>(`${BASE}/teams/${enc(teamId)}/channels/${enc(channelId)}/messages`)
      .pipe(map((r) => normaliseChannelMessages(r)));
  }

  activity(): Observable<SimActivity[]> {
    return this.http.get<unknown>(`${BASE}/activity`).pipe(map((r) => asArray(r).map((a, i) => normaliseActivity(a, i))));
  }

  faults(): Observable<SimFaults> {
    return this.http.get<unknown>(`${BASE}/faults`).pipe(map((r) => normaliseFaults(r)));
  }

  /** Returns the faults now in force (the request echoed back when the response has no body). */
  setFaults(faults: SimFaults): Observable<SimFaults> {
    return this.http.put<unknown>(`${BASE}/faults`, faults).pipe(map((r) => (r ? normaliseFaults(r) : faults)));
  }

  sendMail(request: SimSendMailRequest): Observable<unknown> {
    return this.http.post<unknown>(`${BASE}/mail`, request);
  }

  postChatMessage(chatId: string, request: SimPostChatMessageRequest): Observable<unknown> {
    return this.http.post<unknown>(`${BASE}/chats/${enc(chatId)}/messages`, request);
  }

  /** Returns the id of the (new or existing) 1:1 chat, when the server reports it. */
  ensureOneOnOne(request: SimOneOnOneRequest): Observable<string | null> {
    return this.http.post<unknown>(`${BASE}/chats/one-on-one`, request).pipe(map((r) => idOf(r)));
  }

  postChannelMessage(teamId: string, channelId: string, request: SimPostChannelMessageRequest): Observable<unknown> {
    return this.http.post<unknown>(`${BASE}/teams/${enc(teamId)}/channels/${enc(channelId)}/messages`, request);
  }

  invalidateDeltaTokens(): Observable<unknown> {
    return this.http.post<unknown>(`${BASE}/delta/invalidate`, {});
  }
}
