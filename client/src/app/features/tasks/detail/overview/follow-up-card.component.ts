import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TaskDetail } from '../../../../core/models/task.models';
import { describeFollowUp } from '../../../../core/util/schedule-text';
import { AgoPipe, DateOnlyPipe, DateTimePipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { DialogRequest } from '../task-actions';
import { daysSince, isClosed } from '../task-detail.util';

/** The automatic follow-up: schedule, next / last reminder, pause state, and how long the assignee has been silent. */
@Component({
  selector: 'fu-follow-up-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, AgoPipe, DateOnlyPipe, DateTimePipe, HumanizePipe],
  template: `
    @let f = detail().followUp;
    <section class="card">
      <header class="card-header">
        <h2>Follow-up</h2>
        <span class="spacer"></span>
        @if (canManage() && !closed() && !f.pausedUntil) {
          <button type="button" class="btn btn-xs btn-ghost" [disabled]="busy()" (click)="open.emit({ kind: 'snooze' })"><fu-icon name="pause" [size]="13" /> Snooze</button>
        }
      </header>
      <div class="card-body stack">
        @if (f.pausedUntil) {
          <div class="alert tone-warning small">
            <fu-icon name="pause" />
            <div class="spacer">
              <div class="strong">Paused until {{ f.pausedUntil | dateOnly: 'weekday' }}</div>
              @if (f.pauseReason) {
                <div>Waiting on: {{ f.pauseReason | humanize }}</div>
              }
              @if (f.pauseNote) {
                <div class="muted">“{{ f.pauseNote }}”</div>
              }
            </div>
            @if (canManage()) {
              <button type="button" class="btn btn-xs" [disabled]="busy()" (click)="resume.emit()"><fu-icon name="play" [size]="12" /> Resume</button>
            }
          </div>
        }
        <div class="row-top">
          <fu-icon name="repeat" [size]="15" class="muted" />
          <span>{{ scheduleText() }}</span>
        </div>
        <dl class="kv small">
          @if (f.nextAt && !closed()) {
            <dt>Next follow-up</dt>
            <dd [attr.title]="f.nextAt | dateTime">{{ f.nextAt | dateTime: 'short' }} <span class="muted">({{ f.nextAt | ago }})</span></dd>
          }
          @if (f.lastAt) {
            <dt>Last follow-up</dt>
            <dd>{{ f.lastAt | ago }}</dd>
          }
          <dt>Since last response</dt>
          <dd [class.text-orange]="f.remindersSinceResponse >= 3">
            {{ f.remindersSinceResponse }} reminder{{ f.remindersSinceResponse === 1 ? '' : 's' }}
            @if (f.lastReminderAt) {
              <span class="muted">· last {{ f.lastReminderAt | ago }}</span>
            }
          </dd>
        </dl>
        @if (noResponseDays() > 0) {
          <div class="silence small"><fu-icon name="clock" [size]="14" /> No response for {{ noResponseDays() }} day{{ noResponseDays() === 1 ? '' : 's' }}</div>
        }
        @if (canManage() && !closed()) {
          <div class="row">
            <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'reminder', mode: 'update' })"><fu-icon name="refresh" [size]="14" /> Request update</button>
          </div>
        }
      </div>
    </section>
  `,
  styles: `
    .silence { display: inline-flex; align-items: center; gap: 6px; color: var(--orange); font-weight: 600; }
  `,
})
export class FollowUpCardComponent {
  readonly detail = input.required<TaskDetail>();
  readonly busy = input(false);
  readonly open = output<DialogRequest>();
  readonly resume = output<void>();

  protected readonly canManage = computed(() => this.detail().permissions.canManage);
  protected readonly closed = computed(() => isClosed(this.detail().summary.status));
  protected readonly scheduleText = computed(() => {
    const f = this.detail().followUp;
    return f.description?.trim() || describeFollowUp(f.schedule);
  });
  /** Prefer the server's count; fall back to the no-response timestamp. */
  protected readonly noResponseDays = computed(() => {
    const d = this.detail();
    return d.summary.noResponseDays || daysSince(d.followUp.noResponseSince);
  });
}
