import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TaskDetail } from '../../../../core/models/task.models';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';

/** Escalations raised on the task (automatic by policy, or manual), newest first. */
@Component({
  selector: 'fu-escalations-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, HumanizePipe, AgoPipe, DateTimePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <fu-icon name="zap" [size]="15" class="text-orange" />
        <h2>Escalations</h2>
        <span class="count-pill warning">{{ escalations().length }}</span>
      </header>
      <div class="card-body flush list">
        @for (e of escalations(); track e.id) {
          <div class="list-row esc">
            <span class="lvl" [class.high]="e.level >= 3" [attr.aria-label]="'Level ' + e.level">L{{ e.level }}</span>
            <div class="main">
              <div class="title row wrap gap-4">
                {{ e.trigger | humanize }}
                @if (e.isManual) {
                  <span class="chip sm tone-accent">Manual</span>
                }
              </div>
              <div class="small">{{ e.reason }}</div>
              <div class="meta">
                <span>To {{ e.targets | humanize }}</span>
                <span [attr.title]="e.triggeredAt | dateTime">{{ e.triggeredAt | ago }}</span>
              </div>
            </div>
          </div>
        }
      </div>
    </section>
  `,
  styles: `
    .esc { align-items: flex-start; }
    .lvl { flex: none; width: 30px; height: 24px; border-radius: 6px; display: inline-flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 700; background: var(--orange-soft); color: var(--orange); }
    .lvl.high { background: var(--danger-soft); color: var(--danger); }
  `,
})
export class EscalationsCardComponent {
  readonly detail = input.required<TaskDetail>();
  protected readonly escalations = computed(() => [...this.detail().escalations].sort((a, b) => b.triggeredAt.localeCompare(a.triggeredAt)));
}
