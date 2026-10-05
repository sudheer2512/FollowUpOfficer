import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { SessionState } from '../../core/auth/session-state';
import { Drilldown } from '../../core/models/common.models';
import { CeoWorkbench } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { formatLongDate, greetingFor } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { UiTone, toneOf } from '../../core/util/tone';
import { AgoPipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TileComponent } from '../../shared/ui/tile.component';
import { SectionCardComponent, sectionKind } from '../dashboards/section-card.component';

interface SummaryPointView {
  text: string;
  count: number | null;
  tone: UiTone;
  link: Drilldown | null;
}

/**
 * CEO Workbench (`/`, executives only): greeting, AI executive summary, the §38 KPI tiles and the §79 sections —
 * "Needs my attention" full width first, then the rest in server order on a two-column grid.
 */
@Component({
  selector: 'fu-workbench-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, SkeletonComponent, TileComponent, SectionCardComponent, AgoPipe],
  templateUrl: './workbench.page.html',
  styleUrl: './workbench.page.css',
})
export class WorkbenchPage {
  private readonly api = inject(ExecutiveApi);
  private readonly session = inject(SessionState);

  protected readonly wb = loader<CeoWorkbench>();
  protected readonly today = formatLongDate();

  protected readonly greeting = computed(() => {
    const fromServer = this.wb.value()?.greeting?.trim();
    if (fromServer) return fromServer;
    const name = this.session.firstName();
    return name ? `${greetingFor()}, ${name}` : greetingFor();
  });

  protected readonly tiles = computed(() => this.wb.value()?.tiles ?? []);
  protected readonly summary = computed(() => this.wb.value()?.summary ?? null);
  protected readonly points = computed<SummaryPointView[]>(() =>
    (this.summary()?.points ?? []).map((p) => ({
      text: p.text,
      count: p.count ?? null,
      tone: toneOf(p.tone),
      link: p.drilldown?.route ? p.drilldown : null,
    })),
  );

  private readonly sections = computed(() => this.wb.value()?.sections ?? []);
  /** "Needs my attention" leads the page at full width. */
  protected readonly lead = computed(() => this.sections().find((s) => sectionKind(s) === 'attention') ?? null);
  protected readonly rest = computed(() => this.sections().filter((s) => s !== this.lead()));

  constructor() {
    this.reload();
    refreshOn(['task.changed', 'task.created', 'attention.changed', 'communication.imported'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.wb.load(this.api.workbench(), { silent });
  }
}
