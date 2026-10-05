import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject, bufferTime, filter } from 'rxjs';
import { Guid } from '../models/common.models';

/** Topics the hub sends (docs/API.md). */
export type RealtimeTopic =
  | 'task.changed'
  | 'task.created'
  | 'attention.changed'
  | 'communication.imported'
  | 'notification.queued'
  | 'inbox.changed'
  | 'sync.status'
  | 'job.status';

export interface RealtimeEvent {
  topic: RealtimeTopic | string;
  taskId?: Guid | null;
  payload?: unknown;
}

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * SignalR connection to /hubs/events. Started after sign-in, stopped on sign-out, reconnects automatically.
 * The SignalR client is imported lazily so it stays out of the initial bundle.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private connection: HubConnection | null = null;
  private readonly events$ = new Subject<RealtimeEvent>();
  readonly state = signal<ConnectionState>('disconnected');

  /** Every event, unfiltered. */
  readonly events: Observable<RealtimeEvent> = this.events$.asObservable();

  async start(): Promise<void> {
    if (this.connection) return;
    this.state.set('connecting');
    try {
      const signalR = await import('@microsoft/signalr');
      const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/events', {
          withCredentials: true,
          headers: { 'X-Requested-With': 'FollowUp' },
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000, 30000, 60000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();
      connection.on('event', (e: RealtimeEvent) => {
        if (e && typeof e.topic === 'string') this.events$.next(e);
      });
      connection.onreconnecting(() => this.state.set('reconnecting'));
      connection.onreconnected(() => {
        this.state.set('connected');
        // Data may have changed while we were away: nudge every listener once.
        this.events$.next({ topic: 'task.changed' });
        this.events$.next({ topic: 'attention.changed' });
        this.events$.next({ topic: 'inbox.changed' });
      });
      connection.onclose(() => {
        this.state.set('disconnected');
        this.connection = null;
      });
      this.connection = connection;
      await connection.start();
      this.state.set('connected');
    } catch {
      // The hub is optional: pages still work with manual refresh. Retry later.
      this.state.set('disconnected');
      const stale = this.connection;
      this.connection = null;
      try {
        await stale?.stop();
      } catch {
        /* ignore */
      }
      setTimeout(() => void this.retryIfIdle(), 15000);
    }
  }

  private retryAfterStop = false;

  private async retryIfIdle(): Promise<void> {
    if (this.retryAfterStop) return;
    if (!this.connection) await this.start();
  }

  async stop(): Promise<void> {
    this.retryAfterStop = true;
    const c = this.connection;
    this.connection = null;
    this.state.set('disconnected');
    try {
      await c?.stop();
    } catch {
      /* ignore */
    }
    this.retryAfterStop = false;
  }

  /**
   * Events for the given topics, batched over `debounceMs` (one emission per burst, carrying the batch).
   * Optionally restricted to one task id.
   */
  on(topics: readonly string[], opts: { debounceMs?: number; taskId?: Guid | null } = {}): Observable<RealtimeEvent[]> {
    const wanted = new Set(topics);
    const ms = opts.debounceMs ?? 800;
    return this.events$.pipe(
      filter((e) => wanted.has(e.topic) && (!opts.taskId || !e.taskId || e.taskId === opts.taskId)),
      bufferTime(ms),
      filter((batch) => batch.length > 0),
    );
  }
}

/**
 * Component helper: call in a constructor / field initialiser to re-run `refresh` when any of `topics` arrive
 * (debounced). Unsubscribes automatically on destroy.
 *
 *   constructor() { refreshOn(['task.changed', 'attention.changed'], () => this.reload(true)); }
 */
export function refreshOn(
  topics: readonly string[],
  refresh: (events: RealtimeEvent[]) => void,
  opts: { debounceMs?: number; taskId?: () => Guid | null | undefined } = {},
): void {
  const realtime = inject(RealtimeService);
  const destroyRef = inject(DestroyRef);
  realtime
    .on(topics, { debounceMs: opts.debounceMs })
    .pipe(takeUntilDestroyed(destroyRef))
    .subscribe((batch) => {
      const id = opts.taskId?.();
      if (id && !batch.some((e) => !e.taskId || e.taskId === id)) return;
      refresh(batch);
    });
}
