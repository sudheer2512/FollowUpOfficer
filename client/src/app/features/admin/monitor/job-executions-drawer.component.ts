import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output } from '@angular/core';
import { AdminApi } from '../../../core/api/admin.api';
import { JobDto, JobExecutionDto } from '../../../core/models/admin.models';
import { loader } from '../../../core/util/loader';
import { DrawerComponent } from '../../../shared/ui/drawer.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ExecutionsTableComponent } from './executions-table.component';

/** Side panel with the execution history of one background job. */
@Component({
  selector: 'fu-job-executions-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DrawerComponent, IconComponent, SkeletonComponent, ExecutionsTableComponent],
  template: `
    <fu-drawer [title]="job().name + ' — history'" [subtitle]="job().scheduleText || job().key" width="min(1000px, 100vw)" (closed)="closed.emit()">
      <ng-container drawer-actions>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh history" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
      </ng-container>
      @if (job().description) {
        <p class="small muted">{{ job().description }}</p>
      }
      <div class="legend small muted mb-12">
        <span><span class="badge">Schedule</span> on time</span>
        <span><span class="badge tone-warning">Catch-up</span> missed while the host was down, run late per the misfire policy</span>
        <span><span class="badge tone-accent">Startup</span> run when a host started</span>
        <span><span class="badge tone-orange">Abandoned</span> lease expired before the host finished</span>
      </div>
      @if (data.loading() && !data.value()) {
        <fu-skeleton variant="table" [rows]="8" />
      } @else if (data.error()) {
        <div class="alert tone-danger" role="alert">
          <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
          <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
        </div>
      } @else {
        <div class="card">
          <fu-executions-table [executions]="rows()" [showDue]="true" emptyMessage="This job has not run yet." />
        </div>
      }
    </fu-drawer>
  `,
  styles: `.legend { display: flex; flex-wrap: wrap; gap: 6px 16px; } .legend > span { display: inline-flex; gap: 6px; align-items: center; }`,
})
export class JobExecutionsDrawerComponent implements OnInit {
  private readonly api = inject(AdminApi);

  readonly job = input.required<JobDto>();
  readonly closed = output<void>();

  protected readonly data = loader<JobExecutionDto[]>();
  protected readonly rows = computed(() => this.data.value() ?? []);

  ngOnInit(): void {
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.jobExecutions(this.job().key));
  }
}
