import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { TemplateDto } from '../../core/models/admin.models';
import { loader } from '../../core/util/loader';
import { CriticalityChipComponent } from '../../shared/domain/indicators';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { InstantiateTemplateDialogComponent, templateRoles } from './instantiate-dialog.component';
import { maxDueOffset } from './template-tree';

const MAX_ROLE_CHIPS = 3;

/** /templates — reusable task trees (e.g. "Customer onboarding") with role placeholders; instantiate in one step. */
@Component({
  selector: 'fu-templates-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, InstantiateTemplateDialogComponent, CriticalityChipComponent, EmptyStateComponent, SkeletonComponent, IconComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="titles">
          <h1>Templates</h1>
          <div class="subtitle">Reusable task trees with roles, due offsets and dependencies — delegate a whole playbook at once.</div>
        </div>
        <div class="actions">
          <div class="input-with-icon search">
            <fu-icon name="search" [size]="15" />
            <input class="input" type="search" placeholder="Search templates…" aria-label="Search templates" [value]="query()" (input)="query.set($any($event.target).value)" />
          </div>
          <a class="btn btn-primary" routerLink="/templates/new"><fu-icon name="plus" [size]="15" /> New template</a>
        </div>
      </div>

      <section class="card">
        @if (data.loading() && !data.value()) {
          <div class="card-body"><fu-skeleton variant="table" [rows]="6" /></div>
        } @else if (data.error() && !data.value()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" />
              <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty icon="layers" [title]="query() ? 'No matching templates' : 'No templates yet'" [message]="query() ? 'Try another search.' : 'Capture a repeatable initiative — tasks, roles, offsets and dependencies — once, then instantiate it whenever needed.'">
            @if (!query()) {
              <a class="btn btn-primary btn-sm mt-8" routerLink="/templates/new"><fu-icon name="plus" [size]="14" /> New template</a>
            }
          </fu-empty>
        } @else {
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th>Template</th>
                  <th>Category</th>
                  <th class="num">Items</th>
                  <th>Roles</th>
                  <th>Criticality</th>
                  <th>Approval</th>
                  <th>Status</th>
                  <th class="actions"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.t.id) {
                  <tr class="clickable" tabindex="0" [attr.aria-label]="r.t.name" (click)="open(r.t, $event)" (keydown.enter)="open(r.t, $event)">
                    <td class="name-cell">
                      <a class="strong" [routerLink]="['/templates', r.t.id]" tabindex="-1">{{ r.t.name }}</a>
                      @if (r.t.description) {
                        <div class="xsmall muted clamp-2">{{ r.t.description }}</div>
                      }
                    </td>
                    <td class="small">{{ r.t.category || '—' }}</td>
                    <td class="num">
                      {{ r.itemCount }}
                      @if (r.within > 0) {
                        <div class="xsmall muted nowrap">{{ r.within }} wd</div>
                      }
                    </td>
                    <td>
                      <div class="row wrap gap-4">
                        @for (role of r.shownRoles; track role) {
                          <span class="chip sm">{{ role }}</span>
                        } @empty {
                          <span class="muted small">—</span>
                        }
                        @if (r.extraRoles > 0) {
                          <span class="chip sm outline" [attr.title]="r.roles.join(', ')">+{{ r.extraRoles }}</span>
                        }
                      </div>
                    </td>
                    <td><fu-criticality [value]="r.t.criticality" [small]="true" /></td>
                    <td class="small">
                      @if (r.t.requiresApproval) {
                        <span class="chip sm tone-accent">Required</span>
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td>
                      <span class="badge" [class.tone-good]="r.t.isActive">{{ r.t.isActive ? 'Active' : 'Inactive' }}</span>
                    </td>
                    <td class="actions">
                      <button
                        type="button"
                        class="btn btn-sm"
                        [disabled]="!r.t.isActive"
                        [attr.title]="r.t.isActive ? 'Create tasks from this template' : 'Activate the template first'"
                        (click)="instantiate(r.t, $event)"
                        (keydown.enter)="$event.stopPropagation()"
                      >
                        <fu-icon name="play" [size]="14" /> Instantiate
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    </div>

    @if (instantiating(); as t) {
      <fu-instantiate-template-dialog [template]="t" (closed)="instantiating.set(null)" />
    }
  `,
  styles: `
    .search { width: 240px; }
    .name-cell { min-width: 240px; max-width: 420px; }
    .name-cell a { color: var(--text); }
    @media (max-width: 700px) { .search { width: 100%; } }
  `,
})
export class TemplatesPage {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);

  protected readonly data = loader<TemplateDto[]>();
  protected readonly query = signal('');
  protected readonly instantiating = signal<TemplateDto | null>(null);

  protected readonly rows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return (this.data.value() ?? [])
      .filter((t) => !q || [t.name, t.category, t.description, ...(t.roles ?? [])].some((s) => (s ?? '').toLowerCase().includes(q)))
      .slice()
      .sort((a, b) => Number(b.isActive) - Number(a.isActive) || (a.category ?? '').localeCompare(b.category ?? '') || a.name.localeCompare(b.name))
      .map((t) => {
        const roles = templateRoles(t);
        return {
          t,
          roles,
          shownRoles: roles.slice(0, MAX_ROLE_CHIPS),
          extraRoles: Math.max(0, roles.length - MAX_ROLE_CHIPS),
          itemCount: (t.items ?? []).length,
          within: maxDueOffset(t.items ?? []),
        };
      });
  });

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.templates());
  }

  protected open(t: TemplateDto, e: Event): void {
    if (e.type === 'click' && (e.target as HTMLElement).closest('a, button')) return;
    void this.router.navigate(['/templates', t.id]);
  }

  protected instantiate(t: TemplateDto, e: Event): void {
    e.stopPropagation();
    this.instantiating.set(t);
  }
}
