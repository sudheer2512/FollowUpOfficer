import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable } from 'rxjs';
import { CommitmentQuery, ExecutiveApi } from '../../core/api/executive.api';
import { CommitmentDto } from '../../core/models/task.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { addDays, todayDateOnly } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { DateOnlyPipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { TabItem, TabsComponent } from '../../shared/ui/tabs.component';

type CommitmentTab = 'due' | 'open' | 'missed' | 'met' | 'all';

const TAB_KEYS: readonly CommitmentTab[] = ['due', 'open', 'missed', 'met', 'all'];
/** "Due soon" window in days (today included). */
const DUE_SOON_DAYS = 7;

interface CommitmentCounts {
  open: number;
  dueSoon: number;
  missed: number;
  met: number;
  all: number;
}

function parseTab(value: string | null | undefined): CommitmentTab {
  const v = (value ?? '').toLowerCase();
  return TAB_KEYS.find((t) => t === v) ?? 'due';
}

function isDueSoon(c: CommitmentDto, from: string, to: string): boolean {
  return c.status === 'Open' && !!c.commitmentDate && c.commitmentDate >= from && c.commitmentDate <= to;
}

/** Earliest commitment date first ("yyyy-MM-dd" sorts as text). */
function byDate(a: CommitmentDto, b: CommitmentDto): number {
  return (a.commitmentDate ?? '').localeCompare(b.commitmentDate ?? '');
}

/**
 * Commitments (`/commitments`): promises such as "we will finish by Friday", by tab (Due soon / Open / Missed /
 * Met / All, synced to `?tab=`) and optionally only those made to me (`?toMe=true`). A callout answers
 * "Which commitments made to me have been missed?".
 *
 * Data: the table loads per tab from the server; one `status=All` load (per "made to me" setting) feeds the
 * summary counts and the All tab.
 */
@Component({
  selector: 'fu-commitments-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, TabsComponent, CommitmentTableComponent, DateOnlyPipe],
  templateUrl: './commitments.page.html',
  styleUrl: './commitments.page.css',
})
export class CommitmentsPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Query params `?tab=` and `?toMe=`. */
  readonly tab = input<string>();
  readonly toMe = input<string>();

  protected readonly activeTab = computed(() => parseTab(this.tab()));
  protected readonly madeToMe = computed(() => (this.toMe() ?? '').toLowerCase() === 'true');

  private readonly list = loader<CommitmentDto[]>();
  private readonly all = loader<CommitmentDto[]>();
  protected readonly missedToMe = loader<CommitmentDto[]>();

  protected readonly text = signal('');
  private readonly today = todayDateOnly();
  private readonly dueSoonEnd = addDays(this.today, DUE_SOON_DAYS);

  /** The loader behind the current tab. */
  private readonly source = computed(() => (this.activeTab() === 'all' ? this.all : this.list));
  protected readonly loading = computed(() => this.source().loading());
  protected readonly error = computed(() => this.source().error());

  protected readonly rows = computed(() => {
    const tab = this.activeTab();
    let rows = this.source().value() ?? [];
    if (tab === 'due') {
      rows = rows.filter((c) => isDueSoon(c, this.today, this.dueSoonEnd)).sort(byDate);
    } else if (tab === 'missed') {
      rows = [...rows].sort((a, b) => byDate(b, a)); // most recently missed first
    }
    const q = this.text().trim().toLowerCase();
    return q ? rows.filter((c) => matches(c, q)) : rows;
  });

  protected readonly counts = computed<CommitmentCounts | null>(() => {
    const all = this.all.value();
    if (!all) return null;
    return {
      open: all.filter((c) => c.status === 'Open').length,
      dueSoon: all.filter((c) => isDueSoon(c, this.today, this.dueSoonEnd)).length,
      missed: all.filter((c) => c.status === 'Missed').length,
      met: all.filter((c) => c.status === 'Met').length,
      all: all.length,
    };
  });

  protected readonly tabs = computed<TabItem[]>(() => {
    const c = this.counts();
    return [
      { key: 'due', label: 'Due soon', count: c?.dueSoon, tone: 'warning' },
      { key: 'open', label: 'Open', count: c?.open },
      { key: 'missed', label: 'Missed', count: c?.missed, tone: c?.missed ? 'danger' : '' },
      { key: 'met', label: 'Met', count: c?.met },
      { key: 'all', label: 'All', count: c?.all },
    ];
  });

  protected readonly missedToMeCount = computed(() => this.missedToMe.value()?.length ?? 0);
  /** Most recently missed commitment made to me (for the callout sentence). */
  protected readonly latestMissedToMe = computed(() =>
    [...(this.missedToMe.value() ?? [])].sort((a, b) => byDate(b, a))[0] ?? null,
  );
  protected readonly showingMissedToMe = computed(() => this.activeTab() === 'missed' && this.madeToMe());

  protected readonly emptyTitle = computed(() => {
    if (this.text().trim()) return 'No commitments match your filter';
    switch (this.activeTab()) {
      case 'due':
        return `Nothing promised for the next ${DUE_SOON_DAYS} days`;
      case 'missed':
        return this.madeToMe() ? 'No missed commitments to you' : 'No missed commitments';
      case 'met':
        return 'No commitments met yet';
      default:
        return 'No commitments';
    }
  });

  constructor() {
    effect(() => {
      const tab = this.activeTab();
      const toMe = this.madeToMe();
      untracked(() => {
        if (tab !== 'all') this.list.load(this.tabQuery(tab, toMe));
      });
    });
    effect(() => {
      const toMe = this.madeToMe();
      untracked(() => this.all.load(this.api.commitments({ status: 'All', toMe: toMe || null })));
    });
    this.missedToMe.load(this.api.commitments({ status: 'Missed', toMe: true }));

    refreshOn(['task.changed'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    const tab = this.activeTab();
    const toMe = this.madeToMe();
    if (tab !== 'all') this.list.load(this.tabQuery(tab, toMe), { silent });
    this.all.load(this.api.commitments({ status: 'All', toMe: toMe || null }), { silent });
    this.missedToMe.load(this.api.commitments({ status: 'Missed', toMe: true }), { silent });
  }

  protected selectTab(key: string): void {
    const tab = parseTab(key);
    this.navigate({ tab: tab === 'due' ? null : tab });
  }

  protected setToMe(e: Event): void {
    this.navigate({ toMe: (e.target as HTMLInputElement).checked ? 'true' : null });
  }

  protected showMissedToMe(): void {
    this.navigate({ tab: 'missed', toMe: 'true' });
  }

  protected onText(e: Event): void {
    this.text.set((e.target as HTMLInputElement).value);
  }

  private tabQuery(tab: CommitmentTab, toMe: boolean): Observable<CommitmentDto[]> {
    const q: CommitmentQuery = { toMe: toMe || null };
    switch (tab) {
      case 'due':
        return this.api.commitments({ ...q, status: 'Open', from: this.today, to: this.dueSoonEnd });
      case 'open':
        return this.api.commitments({ ...q, status: 'Open' });
      case 'missed':
        return this.api.commitments({ ...q, status: 'Missed' });
      case 'met':
        return this.api.commitments({ ...q, status: 'Met' });
      default:
        return this.api.commitments({ ...q, status: 'All' });
    }
  }

  private navigate(queryParams: Record<string, string | null>): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }
}

/** Text filter over person, promisee, task number / title and the original wording. */
function matches(c: CommitmentDto, q: string): boolean {
  return [c.person?.name, c.committedTo?.name, c.taskNumber, c.taskTitle, c.originalText, c.what].some(
    (v) => !!v && v.toLowerCase().includes(q),
  );
}
