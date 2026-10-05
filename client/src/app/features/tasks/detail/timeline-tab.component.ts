import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { TimelineItem } from '../../../core/models/task.models';
import { humanize } from '../../../core/util/humanize';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { TimelineGroup, isMessageKind, timelineGroup, timelineIcon } from './timeline-kinds';

type GroupFilter = 'all' | TimelineGroup;

interface TimelineEntry {
  /** Stable key: index in the server list + instant + kind (items have no id). */
  key: string;
  item: TimelineItem;
  icon: string;
  group: TimelineGroup;
  isMessage: boolean;
  /** Body or metadata too long to show in full by default. */
  expandable: boolean;
  meta: [string, string][];
}

const GROUPS: { key: GroupFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'communications', label: 'Communications' },
  { key: 'updates', label: 'Updates' },
  { key: 'system', label: 'System' },
];

function isLong(body: string | null | undefined): boolean {
  if (!body) return false;
  return body.length > 220 || body.split('\n').length > 3;
}

/** Everything that happened on the task, merged chronologically, filterable by kind group. */
@Component({
  selector: 'fu-timeline-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, EmptyStateComponent, AgoPipe, DateTimePipe, HumanizePipe],
  template: `
    <section class="card">
      <header class="card-header wrap">
        <h2>Timeline</h2>
        <span class="count-pill">{{ items().length }}</span>
        <span class="spacer"></span>
        <div class="filters" role="group" aria-label="Filter the timeline">
          @for (g of groups; track g.key) {
            <button type="button" class="chip selectable" [class.on]="group() === g.key" [attr.aria-pressed]="group() === g.key" (click)="group.set(g.key)">
              {{ g.label }} <span class="num muted">{{ counts()[g.key] }}</span>
            </button>
          }
        </div>
        <button type="button" class="btn btn-xs btn-ghost" (click)="newestFirst.set(!newestFirst())">
          <fu-icon [name]="newestFirst() ? 'arrow-down' : 'arrow-up'" [size]="13" />
          {{ newestFirst() ? 'Newest first' : 'Oldest first' }}
        </button>
      </header>
      <div class="card-body">
        @if (visible().length) {
          <ol class="timeline">
            @for (e of visible(); track e.key) {
              <li>
                <span class="t-icon" [attr.data-group]="e.group"><fu-icon [name]="e.icon" [size]="13" /></span>
                <div class="t-head">
                  <span class="t-title">{{ e.item.title }}</span>
                  @if (e.item.channel) {
                    <span class="chip sm">{{ e.item.channel | humanize }}</span>
                  }
                </div>
                <div class="t-meta">
                  @if (e.item.actor) {
                    <span>{{ e.item.actor.name }}</span>
                    <span aria-hidden="true">·</span>
                  }
                  <time [attr.datetime]="e.item.at" [attr.title]="e.item.at | dateTime">{{ e.item.at | ago }}</time>
                  <span aria-hidden="true">·</span>
                  <span>{{ kindLabel(e.item.kind) }}</span>
                </div>
                @if (e.item.body) {
                  <div class="t-body pre" [class.msg]="e.isMessage" [class.clamp-2]="e.expandable && !isExpanded(e.key)">{{ e.item.body }}</div>
                }
                @if (e.meta.length && isExpanded(e.key)) {
                  <dl class="kv xsmall t-extra">
                    @for (m of e.meta; track m[0]) {
                      <dt>{{ m[0] | humanize }}</dt>
                      <dd>{{ m[1] }}</dd>
                    }
                  </dl>
                }
                @if (e.expandable) {
                  <button type="button" class="btn btn-link xsmall" [attr.aria-expanded]="isExpanded(e.key)" (click)="toggle(e.key)">
                    {{ isExpanded(e.key) ? 'Show less' : e.isMessage ? 'Show full message' : 'Show more' }}
                  </button>
                }
              </li>
            }
          </ol>
        } @else {
          <fu-empty icon="activity" [title]="items().length ? 'Nothing in this group' : 'No activity yet'" [compact]="true" />
        }
      </div>
    </section>
  `,
  styles: `
    .wrap { flex-wrap: wrap; }
    .filters { display: flex; flex-wrap: wrap; gap: 6px; }
    .t-icon[data-group='communications'] { color: var(--info); }
    .t-icon[data-group='updates'] { color: var(--accent-text); }
    .t-head { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; min-height: 25px; }
    .t-title { font-weight: 500; }
    .t-meta { display: flex; flex-wrap: wrap; gap: 4px; font-size: 11.5px; color: var(--muted); }
    .t-body { margin-top: 6px; font-size: 13px; color: var(--text-2); overflow-wrap: anywhere; }
    .t-body.msg { background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 8px 10px; }
    .t-extra { margin-top: 6px; }
    .btn-link { margin-top: 4px; }
  `,
})
export class TimelineTabComponent {
  readonly items = input<TimelineItem[]>([]);

  protected readonly groups = GROUPS;
  protected readonly group = signal<GroupFilter>('all');
  protected readonly newestFirst = signal(true);
  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  private readonly entries = computed<TimelineEntry[]>(() =>
    this.items().map((item, i) => {
      const meta = Object.entries(item.meta ?? {}).filter(([, v]) => v !== null && v !== undefined && v !== '');
      return {
        key: `${i}|${item.at}|${item.kind}`,
        item,
        icon: timelineIcon(item.kind),
        group: timelineGroup(item.kind),
        isMessage: isMessageKind(item.kind),
        expandable: isLong(item.body) || meta.length > 0,
        meta,
      };
    }),
  );

  protected readonly counts = computed(() => {
    const counts: Record<GroupFilter, number> = { all: 0, communications: 0, updates: 0, system: 0 };
    for (const e of this.entries()) {
      counts.all++;
      counts[e.group]++;
    }
    return counts;
  });

  protected readonly visible = computed(() => {
    const g = this.group();
    const dir = this.newestFirst() ? -1 : 1;
    return this.entries()
      .filter((e) => g === 'all' || e.group === g)
      .sort((a, b) => dir * (a.item.at ?? '').localeCompare(b.item.at ?? ''));
  });

  protected isExpanded(key: string): boolean {
    return this.expanded().has(key);
  }

  protected toggle(key: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  protected kindLabel(kind: string): string {
    return humanize(kind.replace(/[._-]+/g, ' ').trim()) || 'Activity';
  }
}
