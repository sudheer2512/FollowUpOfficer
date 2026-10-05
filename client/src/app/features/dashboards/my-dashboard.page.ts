import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { SessionState } from '../../core/auth/session-state';
import { MyDashboard } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { DelegateLauncher } from '../../core/state/delegate-launcher';
import { formatLongDate, greetingFor } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TileComponent } from '../../shared/ui/tile.component';
import { SectionCardComponent, sectionKind } from './section-card.component';

/** Individual dashboard (`/me`, spec §41): my tiles and sections, plus a Delegate call to action. */
@Component({
  selector: 'fu-my-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, SkeletonComponent, EmptyStateComponent, TileComponent, SectionCardComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="titles">
          <h1>{{ greeting() }}</h1>
          <div class="subtitle">{{ today }} · everything on your plate, in one place</div>
        </div>
        <div class="actions">
          <button type="button" class="btn btn-ghost btn-icon" aria-label="Refresh dashboard" title="Refresh" [disabled]="data.loading()" (click)="reload()">
            <fu-icon name="refresh" />
          </button>
          <a class="btn" routerLink="/tasks" [queryParams]="{ scope: 'mine', open: 'true' }"><fu-icon name="tasks" /> My tasks</a>
          <button type="button" class="btn btn-primary" (click)="delegate()"><fu-icon name="plus" /> Delegate</button>
        </div>
      </div>

      @if (data.loading() && !data.value()) {
        <fu-skeleton variant="tiles" [rows]="6" />
        <div class="mt-16"><fu-skeleton variant="cards" [rows]="4" /></div>
      } @else if (data.error() && !data.value()) {
        <div class="alert tone-danger" role="alert">
          <fu-icon name="alert-circle" />
          <div class="spacer">{{ data.error() }}</div>
          <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
        </div>
      } @else if (data.value()) {
        @if (tiles().length) {
          <div class="kpi-grid" aria-label="Key indicators">
            @for (t of tiles(); track t.key) {
              <fu-tile [tile]="t" />
            }
          </div>
        }

        @if (allClear()) {
          <section class="card mt-16">
            <fu-empty icon="check-circle" title="You're all clear" message="Nothing is assigned to you or waiting on you right now. Delegate something new, or check in on your team.">
              <div class="row mt-8">
                <button type="button" class="btn btn-primary btn-sm" (click)="delegate()"><fu-icon name="plus" [size]="14" /> Delegate a task</button>
                @if (isManager()) {
                  <a class="btn btn-sm" routerLink="/team">Team dashboard</a>
                }
              </div>
            </fu-empty>
          </section>
        } @else {
          @if (lead(); as section) {
            <fu-section-card class="block" [section]="section" [limit]="6" (changed)="reload(true)" />
          }
          @if (rest().length) {
            <div class="grid-2 block">
              @for (section of rest(); track section.key) {
                <fu-section-card [section]="section" [limit]="6" (changed)="reload(true)" />
              }
            </div>
          }
        }
      }
    </div>
  `,
  styles: `.block { margin-top: 16px; }`,
})
export class MyDashboardPage {
  private readonly api = inject(ExecutiveApi);
  private readonly session = inject(SessionState);
  private readonly launcher = inject(DelegateLauncher);

  protected readonly data = loader<MyDashboard>();
  protected readonly today = formatLongDate();
  protected readonly isManager = this.session.isManager;

  protected readonly greeting = computed(() => {
    const name = (this.data.value()?.me?.name ?? '').split(' ')[0] || this.session.firstName();
    return name ? `${greetingFor()}, ${name}` : greetingFor();
  });

  protected readonly tiles = computed(() => this.data.value()?.tiles ?? []);
  private readonly sections = computed(() => this.data.value()?.sections ?? []);
  protected readonly lead = computed(() => this.sections().find((s) => sectionKind(s) === 'attention') ?? null);
  protected readonly rest = computed(() => this.sections().filter((s) => s !== this.lead()));

  /** No open work anywhere: replace a wall of empty cards with one friendly message. */
  protected readonly allClear = computed(
    () => this.tiles().every((t) => !t.value) && this.sections().every((s) => !s.count && !s.tasks?.length),
  );

  constructor() {
    this.reload();
    refreshOn(['task.changed', 'task.created', 'attention.changed'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.myDashboard(), { silent });
  }

  protected delegate(): void {
    this.launcher.open();
  }
}
