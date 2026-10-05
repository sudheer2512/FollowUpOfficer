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

  /** True between start() and stop(): governs the retry loop after a failed connect or a dropped connection. */
  private wanted = false;
  private starting: Promise<void> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  /** Slow retry back-off after automatic reconnect gives up: 15 s, 30 s, 60 s … capped at 2 min. */
  private retryDelayMs = 15000;

  start(): Promise<void> {
    this.wanted = true;
    if (this.connection) return Promise.resolve();
    this.starting ??= this.connect().finally(() => (this.starting = null));
    return this.starting;
  }

  async stop(): Promise<void> {
    this.wanted = false;
    this.retryDelayMs = 15000;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    const c = this.connection;
    this.connection = null;
    this.state.set('disconnected');
    try {
      await c?.stop();
    } catch {
      /* ignore */
    }
  }

  private async connect(): Promise<void> {
    this.state.set('connecting');
    let connection: HubConnection | null = null;
    try {
      const signalR = await import('@microsoft/signalr');
      connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/events', {
          withCredentials: true,
          headers: { 'X-Requested-With': 'FollowUp' },
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000, 30000, 60000])
        // Connection state is surfaced in the UI (nav footer); keep the console quiet while the hub is unreachable.
        .configureLogging(signalR.LogLevel.Critical)
        .build();
      connection.on('event', (e: RealtimeEvent) => {
        if (e && typeof e.topic === 'string') this.events$.next(e);
      });
      connection.onreconnecting(() => this.state.set('reconnecting'));
      connection.onreconnected(() => {
        this.state.set('connected');
        this.nudgeAll();
      });
      connection.onclose(() => {
        // Automatic reconnect gave up (or stop() was called): fall back to the slow retry loop.
        this.connection = null;
        this.state.set('disconnected');
        this.scheduleRetry();
      });
      await connection.start();
      if (!this.wanted) {
        await connection.stop();
        return;
      }
      this.connection = connection;
      this.retryDelayMs = 15000;
      this.state.set('connected');
    } catch {
      // The hub is optional: pages still work with manual refresh. Retry later.
      this.state.set('disconnected');
      try {
        await connection?.stop();
      } catch {
        /* ignore */
      }
      this.scheduleRetry();
    }
  }

  private scheduleRetry(): void {
    if (!this.wanted || this.retryTimer) return;
    const delay = this.retryDelayMs;
    this.retryDelayMs = Math.min(this.retryDelayMs * 2, 120000);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      if (this.wanted && !this.connection) void this.start().then(() => this.connection && this.nudgeAll());
    }, delay);
  }

  /** Data may have changed while we were disconnected: nudge every listener once. */
  private nudgeAll(): void {
    this.events$.next({ topic: 'task.changed' });
    this.events$.next({ topic: 'attention.changed' });
    this.events$.next({ topic: 'inbox.changed' });
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
