import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { ATTENTION_STATUS_VALUES, ATTENTION_TYPE_VALUES, AttentionStatus, AttentionType } from '../../core/models/enums';
import { AttentionItemDto } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { humanize } from '../../core/util/humanize';
import { loader } from '../../core/util/loader';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { IconName } from '../../shared/ui/icons';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { attentionTypeIcon } from './attention-actions';
import { AttentionListComponent } from './attention-list.component';

type StatusFilter = AttentionStatus | 'All';
type SortMode = 'score' | 'newest';

const STATUS_FILTERS: readonly StatusFilter[] = [...ATTENTION_STATUS_VALUES, 'All'];

/** Score at which an item counts as critical (the red band of the score bar). */
const CRITICAL_SCORE = 75;

interface TypeCount {
  type: AttentionType;
  label: string;
  icon: IconName;
  count: number;
}

/** `?status=` value → status filter (case-insensitive; Open by default). */
function parseStatus(value: string | null | undefined): StatusFilter {
  const v = (value ?? '').toLowerCase();
  return STATUS_FILTERS.find((s) => s.toLowerCase() === v) ?? 'Open';
}

/**
 * CEO Attention Required (`/attention`): every exception ranked by score, filterable by status (synced to
 * `?status=`), type and text, with inline actions on each item.
 */
@Component({
  selector: 'fu-attention-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, SkeletonComponent, EmptyStateComponent, AttentionListComponent],
  templateUrl: './attention.page.html',
  styleUrl: './attention.page.css',
})
export class AttentionPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Query param `?status=`. */
  readonly status = input<string>();

  protected readonly statusFilters = STATUS_FILTERS;
  protected readonly activeStatus = computed(() => parseStatus(this.status()));

  protected readonly data = loader<AttentionItemDto[]>();
  protected readonly selectedTypes = signal<ReadonlySet<string>>(new Set());
  protected readonly text = signal('');
  protected readonly sort = signal<SortMode>('score');

  protected readonly all = computed(() => this.data.value() ?? []);

  /** Type chips: only types present in the loaded list, in server enum order. */
  protected readonly typeCounts = computed<TypeCount[]>(() => {
    const counts = new Map<string, TypeCount>();
    for (const a of this.all()) {
      const entry = counts.get(a.type) ?? { type: a.type, label: a.typeLabel || humanize(a.type), icon: attentionTypeIcon(a.type), count: 0 };
      entry.count++;
      counts.set(a.type, entry);
    }
    const rank = (t: string) => {
      const i = ATTENTION_TYPE_VALUES.indexOf(t as AttentionType);
      return i < 0 ? ATTENTION_TYPE_VALUES.length : i;
    };
    return [...counts.values()].sort((a, b) => rank(a.type) - rank(b.type));
  });

  /** Selected types that are still present (a stale selection never hides everything silently). */
  private readonly effectiveTypes = computed(() => {
    const present = new Set(this.typeCounts().map((t) => t.type));
    return new Set([...this.selectedTypes()].filter((t) => present.has(t as AttentionType)));
  });

  protected readonly filtered = computed(() => {
    const types = this.effectiveTypes();
    const q = this.text().trim().toLowerCase();
    const list = this.all().filter((a) => (!types.size || types.has(a.type)) && (!q || matches(a, q)));
    return this.sort() === 'newest'
      ? [...list].sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''))
      : [...list].sort((a, b) => (b.score ?? 0) - (a.score ?? 0));
  });

  protected readonly openCount = computed(() => this.all().filter((a) => a.status === 'Open').length);
  protected readonly criticalCount = computed(() => this.all().filter((a) => (a.score ?? 0) >= CRITICAL_SCORE).length);
  protected readonly criticalScore = CRITICAL_SCORE;
  protected readonly hasFilters = computed(() => this.effectiveTypes().size > 0 || this.text().trim().length > 0);

  constructor() {
    effect(() => {
      const status = this.activeStatus();
      untracked(() => this.data.load(this.api.attention(status)));
    });
    refreshOn(['attention.changed', 'task.changed'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.attention(this.activeStatus()), { silent });
  }

  protected setStatus(status: StatusFilter): void {
    if (status === this.activeStatus()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { status: status === 'Open' ? null : status },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected isTypeOn(type: string): boolean {
    return this.effectiveTypes().has(type);
  }

  protected toggleType(type: string): void {
    const next = new Set(this.effectiveTypes());
    if (next.has(type)) next.delete(type);
    else next.add(type);
    this.selectedTypes.set(next);
  }

  protected clearFilters(): void {
    this.selectedTypes.set(new Set());
    this.text.set('');
  }

  protected onText(e: Event): void {
    this.text.set((e.target as HTMLInputElement).value);
  }

  protected statusLabel(s: StatusFilter): string {
    return s === 'All' ? 'All' : humanize(s);
  }
}

/** Text filter over title, detail, type label, task number / title and responsible person. */
function matches(a: AttentionItemDto, q: string): boolean {
  const t = a.task;
  return [a.title, a.detail, a.typeLabel, t?.number, t?.title, t?.primaryAssignee?.name, t?.teamName]
    .some((v) => !!v && v.toLowerCase().includes(q));
}
