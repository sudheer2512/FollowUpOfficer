import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { AdminApi } from '../../../core/api/admin.api';
import { EmployeeDto } from '../../../core/models/admin.models';
import { APP_ROLE_VALUES } from '../../../core/models/enums';
import { LookupsStore } from '../../../core/state/lookups.store';
import { parseFlags } from '../../../core/util/flags';
import { loader } from '../../../core/util/loader';
import { AvatarComponent, PersonComponent } from '../../../shared/domain/indicators';
import { AgoPipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { PaginatorComponent } from '../../../shared/ui/paginator.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { EmployeeAccountModalComponent } from './employee-account-modal.component';
import { valueOf } from '../shared/form-view';
import { EmployeeEditModalComponent } from './employee-edit-modal.component';

const PAGE_SIZE = 25;
const MAX_TEAMS_SHOWN = 2;

type StatusFilter = 'active' | 'inactive' | 'all';

/** Searchable employee directory with profile and account (login / roles / password) editing. */
@Component({
  selector: 'fu-employees-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AvatarComponent,
    PersonComponent,
    AgoPipe,
    HumanizePipe,
    EmptyStateComponent,
    IconComponent,
    PaginatorComponent,
    SkeletonComponent,
    EmployeeEditModalComponent,
    EmployeeAccountModalComponent,
  ],
  templateUrl: './employees-tab.component.html',
  styles: `
    .filters { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; padding: 10px 16px; border-bottom: 1px solid var(--border); }
    .filters .search { flex: 1 1 240px; max-width: 360px; }
    .filters .select { width: auto; min-width: 150px; }
    .who { display: flex; flex-direction: column; min-width: 0; line-height: 1.3; }
    .chips { display: flex; flex-wrap: wrap; gap: 3px; max-width: 220px; }
    .login { display: flex; flex-direction: column; gap: 2px; }
    .inactive td { opacity: 0.62; }
  `,
})
export class EmployeesTabComponent {
  private readonly api = inject(AdminApi);
  private readonly lookups = inject(LookupsStore);
  private readonly toast = inject(ToastService);

  protected readonly roleValues = APP_ROLE_VALUES;
  protected readonly maxTeams = MAX_TEAMS_SHOWN;
  protected readonly pageSize = PAGE_SIZE;
  protected readonly departments = this.lookups.departments;

  protected readonly data = loader<EmployeeDto[]>();
  protected readonly query = signal('');
  protected readonly departmentId = signal('');
  protected readonly role = signal('');
  protected readonly status = signal<StatusFilter>('active');
  protected readonly page = signal(1);

  /** Employee in the profile modal ('new' = create). */
  protected readonly editing = signal<EmployeeDto | 'new' | null>(null);
  /** Employee in the account modal. */
  protected readonly account = signal<EmployeeDto | null>(null);

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    const dep = this.departmentId();
    const role = this.role();
    const status = this.status();
    return (this.data.value() ?? [])
      .map((e) => ({ ...e, teams: e.teams ?? [] }))
      .filter((e) => status === 'all' || (status === 'active' ? e.isActive : !e.isActive))
      .filter((e) => !dep || e.department?.id === dep)
      .filter((e) => !role || parseFlags(e.roles).includes(role))
      .filter(
        (e) =>
          !q ||
          (e.displayName ?? '').toLowerCase().includes(q) ||
          (e.email ?? '').toLowerCase().includes(q) ||
          (e.employeeCode ?? '').toLowerCase().includes(q) ||
          (e.jobTitle ?? '').toLowerCase().includes(q) ||
          (e.userName ?? '').toLowerCase().includes(q),
      )
      .sort((a, b) => (a.displayName ?? '').localeCompare(b.displayName ?? ''));
  });

  protected readonly visible = computed(() => {
    const start = (this.page() - 1) * PAGE_SIZE;
    return this.filtered().slice(start, start + PAGE_SIZE);
  });

  protected readonly hasFilters = computed(() => !!this.query() || !!this.departmentId() || !!this.role() || this.status() !== 'active');

  constructor() {
    this.lookups.load();
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.employees());
  }

  protected setQuery(event: Event): void {
    this.query.set(valueOf(event));
    this.page.set(1);
  }

  protected setDepartment(event: Event): void {
    this.departmentId.set(valueOf(event));
    this.page.set(1);
  }

  protected setRole(event: Event): void {
    this.role.set(valueOf(event));
    this.page.set(1);
  }

  protected setStatus(value: string): void {
    this.status.set(value === 'inactive' || value === 'all' ? value : 'active');
    this.page.set(1);
  }

  protected clearFilters(): void {
    this.query.set('');
    this.departmentId.set('');
    this.role.set('');
    this.status.set('active');
    this.page.set(1);
  }

  protected rolesOf(e: EmployeeDto): string[] {
    return parseFlags(e.roles);
  }

  /** Names of the teams beyond the ones shown as chips (tooltip). */
  protected moreTeams(e: EmployeeDto): string {
    return (e.teams ?? [])
      .slice(MAX_TEAMS_SHOWN)
      .map((t) => t.name)
      .join(', ');
  }

  protected channelsOf(e: EmployeeDto): string[] {
    return parseFlags(e.preferredChannels);
  }

  protected onProfileSaved(dto: EmployeeDto | null, wasNew: boolean): void {
    const name = dto?.displayName ?? 'Employee';
    this.toast.success(wasNew ? `${name} added` : `${name} saved`);
    this.editing.set(null);
    this.afterSave();
  }

  protected onAccountSaved(dto: EmployeeDto | null): void {
    this.toast.success(`Account for ${dto?.displayName ?? 'the employee'} saved`);
    this.account.set(null);
    this.afterSave();
  }

  private afterSave(): void {
    this.data.load(this.api.employees(), { silent: true });
    this.lookups.refresh().subscribe({ error: () => undefined });
  }
}
