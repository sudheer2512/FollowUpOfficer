import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MonitorOverview } from '../../../core/models/admin.models';
import { relativeTime } from '../../../core/util/dates';
import { humanize } from '../../../core/util/humanize';
import { UiTone } from '../../../core/util/tone';
import { aggregateSync, ageMs, latest } from './monitor-format';

interface StatCard {
  key: string;
  label: string;
  value: string;
  tone: UiTone;
  hint: string;
  /** Id of the section this card scrolls to. */
  target: string;
  /** Numeric zero → greyed value. */
  zero: boolean;
  /** Text values (statuses, times) render smaller than counts. */
  text: boolean;
}

const MINUTE = 60_000;

function countCard(key: string, label: string, value: number | null | undefined, badTone: UiTone, hint: string, target: string): StatCard {
  const n = value ?? 0;
  return { key, label, value: String(n), tone: n > 0 ? badTone : 'neutral', hint, target, zero: n === 0, text: false };
}

/** Spec §67 status cards: sync health, freshness, backlog and 24-hour failure counters. Click scrolls to the detail. */
@Component({
  selector: 'fu-monitor-stats',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="kpi-grid">
      @for (c of cards(); track c.key) {
        <button type="button" [class]="'kpi tone-' + c.tone" [class.zero]="c.zero" (click)="jump.emit(c.target)">
          <span class="kpi-label">{{ c.label }}</span>
          <span class="kpi-value" [class.text]="c.text">{{ c.value }}</span>
          <span class="kpi-hint">{{ c.hint }}</span>
        </button>
      }
    </div>
  `,
  styles: `
    .kpi { font: inherit; }
    .kpi-value.text { font-size: 18px; line-height: 1.35; }
  `,
})
export class MonitorStatsComponent {
  readonly overview = input.required<MonitorOverview>();
  /** Current time in ms (ticks from the page so "x min ago" stays fresh). */
  readonly now = input(Date.now());
  readonly jump = output<string>();

  protected readonly cards = computed<StatCard[]>(() => {
    const m = this.overview();
    const now = this.now();
    const checkpoints = m.checkpoints ?? [];

    const outlook = aggregateSync(checkpoints, ['OutlookMail']);
    const teams = aggregateSync(checkpoints, ['TeamsChat', 'TeamsChannel']);
    const syncCard = (key: string, label: string, agg: ReturnType<typeof aggregateSync>, noun: string): StatCard => ({
      key,
      label,
      value: agg.total ? humanize(agg.status) : 'Not set up',
      tone: agg.tone,
      hint: agg.total
        ? `${agg.enabled}/${agg.total} ${noun} · ${agg.lastSuccess ? 'last ok ' + relativeTime(agg.lastSuccess, new Date(now)) : 'never synced'}`
        : 'No checkpoints yet',
      target: 'mon-checkpoints',
      zero: false,
      text: true,
    });

    const lastSync = latest(checkpoints.map((c) => c.lastSuccessfulSyncAt));
    const age = ageMs(lastSync, now);
    const lastTone: UiTone = age === null ? (checkpoints.length ? 'warning' : 'neutral') : age > 60 * MINUTE ? 'danger' : age > 15 * MINUTE ? 'warning' : 'good';

    const awaiting = m.messagesAwaitingProcessing ?? 0;
    const failedMessages = m.messagesFailed ?? 0;
    const awaitingTone: UiTone = failedMessages > 0 ? 'danger' : awaiting > 25 ? 'warning' : awaiting > 0 ? 'info' : 'neutral';

    const failedNotifications = (m.notificationsFailed ?? 0) + (m.notificationsDeadLetter ?? 0);
    const notificationTone: UiTone = (m.notificationsDeadLetter ?? 0) > 0 ? 'danger' : failedNotifications > 0 ? 'warning' : 'neutral';

    return [
      syncCard('outlook', 'Outlook sync', outlook, 'mailboxes'),
      syncCard('teams', 'Teams sync', teams, 'chats & channels'),
      {
        key: 'last-sync',
        label: 'Last successful sync',
        value: lastSync ? relativeTime(lastSync, new Date(now)) : 'Never',
        tone: lastTone,
        hint: age !== null && age > 15 * MINUTE ? 'Sync looks stale' : 'Across all sources',
        target: 'mon-checkpoints',
        zero: false,
        text: true,
      },
      {
        key: 'awaiting',
        label: 'Messages awaiting processing',
        value: String(awaiting),
        tone: awaitingTone,
        hint: `${m.messagesNeedingReview ?? 0} need review · ${failedMessages} failed`,
        target: failedMessages > 0 ? 'mon-failed-messages' : 'mon-checkpoints',
        zero: awaiting === 0,
        text: false,
      },
      countCard('api', 'Failed API calls (24h)', m.failedApiCalls24h, 'warning', 'Graph calls that errored', 'mon-errors'),
      countCard('auth', 'Authentication problems (24h)', m.authProblems24h, 'danger', 'Token or sign-in failures', 'mon-errors'),
      countCard('throttle', 'Rate-limit events (24h)', m.rateLimitEvents24h, 'warning', '429 responses, backed off', 'mon-errors'),
      countCard('permissions', 'Expired permissions (24h)', m.permissionErrors24h, 'danger', 'Missing or revoked consent', 'mon-connection'),
      countCard('resync', 'Resync events (24h)', m.resyncEvents24h, 'warning', 'Delta tokens reset (410 Gone)', 'mon-checkpoints'),
      {
        key: 'notifications',
        label: 'Notification failures',
        value: String(failedNotifications),
        tone: notificationTone,
        hint: `${m.notificationsDeadLetter ?? 0} dead letter · ${m.notificationsPending ?? 0} pending`,
        target: 'mon-notifications',
        zero: failedNotifications === 0,
        text: false,
      },
    ];
  });
}
