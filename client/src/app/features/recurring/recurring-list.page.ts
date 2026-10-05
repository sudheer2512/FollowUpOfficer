import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { RecurringApi } from '../../core/api/recurring.api';
import { RecurrenceState } from '../../core/models/enums';
import { RecurringDefinitionDto } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { loader } from '../../core/util/loader';
import { UiTone } from '../../core/util/tone';
import { PersonComponent } from '../../shared/domain/indicators';
import { AgoPipe, DateOnlyPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { barWidth, complianceTone, stateTone } from './recurring-display';

type StateFilter = 'All' | RecurrenceState;

const STATE_ORDER: Record<string, number> = { Active: 0, Paused: 1, Ended: 2 };

interface ListRow {
  d: RecurringDefinitionDto;
  stateTone: UiTone;
  hasHistory: boolean;
  compliance: number;
  complianceTone: string;
  bar: number;
}

/** /recurring — every recurring definition with its schedule, next occurrence, state and compliance (spec §9). */
@Component({
  selector: 'fu-recurring-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, EmptyStateComponent, SkeletonComponent, PersonComponent, DateTimePipe, DateOnlyPipe, AgoPipe],
  templateUrl: './recurring-list.page.html',
  styleUrl: './recurring-list.page.css',
})
export class RecurringListPage {
  private readonly api = inject(RecurringApi);
  private readonly router = inject(Router);

  protected readonly data = loader<RecurringDefinitionDto[]>();
  protected readonly showEnded = signal(false);
  protected readonly search = signal('');
  protected readonly stateFilter = signal<StateFilter>('All');

  protected readonly states = computed<StateFilter[]>(() =>
    this.showEnded() ? ['All', 'Active', 'Paused', 'Ended'] : ['All', 'Active', 'Paused'],
  );
  private readonly all = computed(() => this.data.value() ?? []);

  protected readonly counts = computed<Record<StateFilter, number>>(() => {
    const list = this.all();
    return {
      All: list.length,
      Active: list.filter((d) => d.state === 'Active').length,
      Paused: list.filter((d) => d.state === 'Paused').length,
      Ended: list.filter((d) => d.state === 'Ended').length,
    };
  });

  protected readonly stats = computed(() => {
    const list = this.all();
    const measured = list.filter((d) => (d.generatedCount ?? 0) > 0);
    const average = measured.length
      ? Math.round(measured.reduce((sum, d) => sum + (d.compliancePercent ?? 0), 0) / measured.length)
      : null;
    return {
      active: this.counts().Active,
      paused: this.counts().Paused,
      missed: list.reduce((sum, d) => sum + (d.missedCount ?? 0), 0),
      average,
      averageTone: average === null ? 'neutral' : complianceTone(average),
    };
  });

  protected readonly rows = computed<ListRow[]>(() => {
    const q = this.search().trim().toLowerCase();
    const state = this.stateFilter();
    return this.all()
      .filter((d) => state === 'All' || d.state === state)
      .filter(
        (d) =>
          !q ||
          [d.titleTemplate, d.code, d.ruleText, d.primaryAssignee?.name, d.team?.name, d.department?.name, d.project?.name].some((s) =>
            (s ?? '').toLowerCase().includes(q),
          ),
      )
      .sort(
        (a, b) =>
          (STATE_ORDER[a.state] ?? 3) - (STATE_ORDER[b.state] ?? 3) ||
          (a.nextOccurrenceAt ?? '￿').localeCompare(b.nextOccurrenceAt ?? '￿') ||
          (a.titleTemplate ?? '').localeCompare(b.titleTemplate ?? ''),
      )
      .map((d) => ({
        d,
        stateTone: stateTone(d.state),
        hasHistory: (d.generatedCount ?? 0) > 0,
        compliance: Math.round(d.compliancePercent ?? 0),
        complianceTone: complianceTone(d.compliancePercent),
        bar: barWidth(d.compliancePercent),
      }));
  });

  constructor() {
    this.reload();
    refreshOn(['task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.list(this.showEnded()), { silent });
  }

  protected setShowEnded(on: boolean): void {
    this.showEnded.set(on);
    if (!on && this.stateFilter() === 'Ended') this.stateFilter.set('All');
    this.reload();
  }

  protected clearFilters(): void {
    this.search.set('');
    this.stateFilter.set('All');
  }

  /** Whole-row click opens the definition; the title link stays the keyboard path. */
  protected openRow(d: RecurringDefinitionDto, event: MouseEvent): void {
    if ((event.target as HTMLElement | null)?.closest('a, button')) return;
    void this.router.navigate(['/recurring', d.id]);
  }
}
