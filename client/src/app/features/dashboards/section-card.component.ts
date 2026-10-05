import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drilldown } from '../../core/models/common.models';
import { CRITICALITY_VALUES } from '../../core/models/enums';
import { TaskListItem } from '../../core/models/task.models';
import { WorkbenchSection } from '../../core/models/view.models';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { TaskListComponent } from '../../shared/domain/task-list.component';
import { AttentionListComponent } from '../attention/attention-list.component';
import { ApprovalQueueComponent } from './approval-queue.component';

export type SectionKind = 'attention' | 'commitments' | 'approvals' | 'tasks';

/**
 * Decides how a section renders. Whichever payload list is non-empty wins; when every list is empty the key
 * decides, so the right empty state shows (e.g. "needs-attention" → attention list).
 */
export function sectionKind(s: WorkbenchSection): SectionKind {
  if (s.attention?.length) return 'attention';
  if (s.commitments?.length) return 'commitments';
  if (s.approvals?.length) return 'approvals';
  if (s.tasks?.length) return 'tasks';
  const key = (s.key ?? '').toLowerCase();
  if (key.includes('attention')) return 'attention';
  if (key.includes('commit')) return 'commitments';
  if (key.includes('approv')) return 'approvals';
  return 'tasks';
}

/** Extra line for compact task rows, chosen by section key. */
function taskDetail(key: string): string | null {
  if (key.includes('response')) return 'noResponse';
  if (key.includes('commit')) return 'commitment';
  if (key.includes('block')) return 'blocked';
  if (key.includes('complet')) return 'completed';
  return null;
}

function emptyMessage(key: string): string {
  if (key.includes('response')) return 'Everyone is responding to follow-ups.';
  if (key.includes('critical') || key.includes('risk')) return 'Nothing critical or at risk.';
  if (key.includes('overdue')) return 'Nothing overdue.';
  if (key.includes('commit')) return 'No commitments due.';
  if (key.includes('deadline') || key.includes('upcoming') || key.includes('due')) return 'No important deadlines coming up.';
  if (key.includes('complet')) return 'Nothing completed recently.';
  if (key.includes('block')) return 'Nothing is blocked.';
  return 'Nothing here.';
}

/** Count-pill tone: red for exceptions, amber for things waiting on someone. */
function pillTone(key: string, count: number): 'danger' | 'warning' | 'accent' | '' {
  if (count <= 0) return '';
  if (/overdue|critical|risk|missed|block/.test(key)) return 'danger';
  if (/response|approv|commit|deadline|due/.test(key)) return 'warning';
  if (key.includes('attention')) return 'accent';
  return '';
}

const CRITICALITY_RANK = new Map<string, number>(CRITICALITY_VALUES.map((c, i) => [c, i]));

/** Overdue tasks by severity: most critical first, then the longest overdue. */
function bySeverity(a: TaskListItem, b: TaskListItem): number {
  const crit = (CRITICALITY_RANK.get(b.criticality) ?? 0) - (CRITICALITY_RANK.get(a.criticality) ?? 0);
  return crit !== 0 ? crit : (b.daysOverdue ?? 0) - (a.daysOverdue ?? 0);
}

let sectionSeq = 0;

/**
 * One workbench / dashboard section (spec §79) as a card: header (title, description, count, View all) and a body
 * chosen by content — attention items, commitments, the approval queue or compact task rows.
 *   <fu-section-card [section]="s" (changed)="reload(true)" />
 */
@Component({
  selector: 'fu-section-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AttentionListComponent, CommitmentTableComponent, TaskListComponent, ApprovalQueueComponent],
  template: `
    <section class="card" [attr.aria-labelledby]="headingId">
      <header class="card-header">
        <div class="titles">
          <div class="row">
            <h2 [id]="headingId" class="truncate">{{ section().title }}</h2>
            <span class="count-pill" [class.danger]="pill() === 'danger'" [class.warning]="pill() === 'warning'" [class.accent]="pill() === 'accent'">
              {{ section().count }}
            </span>
          </div>
          @if (section().description) {
            <div class="desc truncate" [attr.title]="section().description">{{ section().description }}</div>
          }
        </div>
        <span class="spacer"></span>
        @if (link(); as d) {
          <a class="small nowrap" [routerLink]="d.route" [queryParams]="d.query" [attr.aria-label]="'View all: ' + section().title">View all</a>
        }
      </header>
      <div class="card-body flush">
        @switch (kind()) {
          @case ('attention') {
            <fu-attention-list [items]="attention()" [compact]="true" [limit]="limit()" (changed)="changed.emit()" />
          }
          @case ('commitments') {
            <fu-commitment-table [commitments]="commitments()" [compact]="true" [limit]="limit()" emptyTitle="" [emptyMessage]="empty()" />
          }
          @case ('approvals') {
            <fu-approval-queue [items]="approvals()" [limit]="limit()" (changed)="changed.emit()" />
          }
          @default {
            <fu-task-list [tasks]="tasks()" [limit]="limit()" [detail]="detail()" [showStatus]="showStatus()" [emptyMessage]="empty()" />
          }
        }
      </div>
    </section>
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-width: 0; }
    .card { flex: 1 1 auto; display: flex; flex-direction: column; }
    .card-body { flex: 1 1 auto; }
    .titles { min-width: 0; }
  `,
})
export class SectionCardComponent {
  readonly section = input.required<WorkbenchSection>();
  /** Rows shown before "+N more". */
  readonly limit = input<number | null>(6);
  /** Emitted after an inline action (attention / approval) changed server state. */
  readonly changed = output<void>();

  protected readonly headingId = `fu-section-${++sectionSeq}`;

  private readonly key = computed(() => (this.section().key ?? '').toLowerCase());
  protected readonly kind = computed(() => sectionKind(this.section()));
  protected readonly link = computed<Drilldown | null>(() => {
    const d = this.section().drilldown;
    return d?.route ? d : null;
  });
  protected readonly pill = computed(() => pillTone(this.key(), this.section().count ?? 0));
  protected readonly empty = computed(() => emptyMessage(this.key()));
  protected readonly detail = computed(() => taskDetail(this.key()));
  protected readonly showStatus = computed(() => /critical|risk/.test(this.key()));

  protected readonly attention = computed(() => this.section().attention ?? []);
  protected readonly commitments = computed(() => this.section().commitments ?? []);
  protected readonly approvals = computed(() => this.section().approvals ?? []);
  protected readonly tasks = computed(() => {
    const list = this.section().tasks ?? [];
    return this.key().includes('overdue') ? [...list].sort(bySeverity) : list;
  });
}
