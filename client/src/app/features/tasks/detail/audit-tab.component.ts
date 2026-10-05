import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { AuditEntryDto } from '../../../core/models/admin.models';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';

/** The task's audit trail (GET /api/tasks/{id}/audit), loaded by the page the first time this tab opens. */
@Component({
  selector: 'fu-audit-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, EmptyStateComponent, SkeletonComponent, DateTimePipe, AgoPipe, HumanizePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Audit trail</h2>
        @if (entries(); as list) {
          <span class="count-pill">{{ list.length }}</span>
        }
        <span class="spacer"></span>
        <div class="input-with-icon filter">
          <fu-icon name="search" [size]="14" />
          <label for="audit-filter" class="sr-only">Filter the audit trail</label>
          <input id="audit-filter" class="input input-sm" type="search" placeholder="Filter by action, field, person…" [value]="text()" (input)="text.set($any($event.target).value)" />
        </div>
      </header>
      <div class="card-body flush">
        @if (error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" />
              <span class="spacer">Couldn't load the audit trail. {{ error() }}</span>
              <button type="button" class="btn btn-sm" (click)="retry.emit()">Retry</button>
            </div>
          </div>
        } @else if (loading() && !entries()) {
          <fu-skeleton variant="table" [rows]="6" />
        } @else if (visible().length) {
          <div class="table-wrap">
            <table class="table compact">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Who</th>
                  <th>Action</th>
                  <th>Change</th>
                  <th>Source</th>
                </tr>
              </thead>
              <tbody>
                @for (e of visible(); track e.id) {
                  <tr>
                    <td class="nowrap small" [attr.title]="e.at | dateTime">{{ e.at | dateTime: 'short' }}<div class="xsmall muted">{{ e.at | ago }}</div></td>
                    <td class="nowrap">{{ e.actorName || 'System' }}</td>
                    <td>
                      <div>{{ e.action | humanize }}</div>
                      @if (e.entityType && e.entityType !== 'Task') {
                        <div class="xsmall muted">{{ e.entityType | humanize }}</div>
                      }
                    </td>
                    <td class="change">
                      @if (e.field) {
                        <div class="small strong">{{ e.field | humanize }}</div>
                        <div class="small vals">
                          <span class="old" [attr.title]="e.oldValue ?? ''">{{ e.oldValue || '∅' }}</span>
                          <fu-icon name="arrow-right" [size]="11" class="muted" />
                          <span class="new" [attr.title]="e.newValue ?? ''">{{ e.newValue || '∅' }}</span>
                        </div>
                      }
                      @if (e.details) {
                        <div class="xsmall muted clamp-2" [attr.title]="e.details">{{ e.details }}</div>
                      }
                    </td>
                    <td><span class="chip sm">{{ e.source | humanize }}</span></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <fu-empty icon="shield" [title]="text() ? 'No matching entries' : 'No audit entries'" [compact]="true" />
        }
      </div>
    </section>
  `,
  styles: `
    .filter { width: 240px; max-width: 50vw; }
    .change { min-width: 220px; max-width: 420px; }
    .vals { display: flex; align-items: center; gap: 6px; min-width: 0; }
    .vals span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 170px; }
    .old { color: var(--muted); text-decoration: line-through; text-decoration-color: var(--subtle); }
    .new { color: var(--text); }
  `,
})
export class AuditTabComponent {
  readonly entries = input<AuditEntryDto[] | undefined>(undefined);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly retry = output<void>();

  protected readonly text = signal('');
  protected readonly visible = computed(() => {
    const list = [...(this.entries() ?? [])].sort((a, b) => b.at.localeCompare(a.at));
    const q = this.text().trim().toLowerCase();
    if (!q) return list;
    return list.filter((e) =>
      [e.actorName, e.action, e.field, e.oldValue, e.newValue, e.details, e.entityType, e.source].some((v) => (v ?? '').toLowerCase().includes(q)),
    );
  });
}
