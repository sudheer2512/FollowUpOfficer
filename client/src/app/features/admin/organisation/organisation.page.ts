import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { TabItem, TabsComponent } from '../../../shared/ui/tabs.component';
import { DepartmentsTabComponent } from './departments-tab.component';
import { EmployeesTabComponent } from './employees-tab.component';
import { TeamsTabComponent } from './teams-tab.component';

type OrgTab = 'departments' | 'teams' | 'employees';

const TABS: TabItem[] = [
  { key: 'departments', label: 'Departments' },
  { key: 'teams', label: 'Teams' },
  { key: 'employees', label: 'Employees' },
];

function asTab(value: string | null | undefined): OrgTab {
  return value === 'teams' || value === 'employees' ? value : 'departments';
}

/** /admin/organisation — departments, teams and employees (tab synced to `?tab=`). */
@Component({
  selector: 'fu-organisation-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TabsComponent, DepartmentsTabComponent, TeamsTabComponent, EmployeesTabComponent],
  template: `
    <div class="page-header">
      <div class="titles">
        <h2>Organisation</h2>
        <div class="subtitle">Departments, teams and people. Pickers across the app refresh as soon as you save.</div>
      </div>
    </div>
    <fu-tabs [tabs]="tabs" [active]="active()" (activeChange)="select($event)" ariaLabel="Organisation sections" />
    <div class="mt-16">
      @switch (active()) {
        @case ('teams') {
          <fu-teams-tab />
        }
        @case ('employees') {
          <fu-employees-tab />
        }
        @default {
          <fu-departments-tab />
        }
      }
    </div>
  `,
})
export class OrganisationPage {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** `?tab=` via router component-input binding. */
  readonly tab = input<string | undefined>();

  protected readonly tabs = TABS;
  protected readonly active = computed(() => asTab(this.tab()));

  protected select(key: string): void {
    const tab = asTab(key);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'departments' ? null : tab },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
