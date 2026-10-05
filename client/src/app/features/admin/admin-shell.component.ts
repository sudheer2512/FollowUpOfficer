import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SessionState } from '../../core/auth/session-state';
import { IconComponent } from '../../shared/ui/icon.component';

interface AdminLink {
  label: string;
  route: string;
  icon: string;
}

const ADMIN_LINKS: AdminLink[] = [
  { label: 'Organisation', route: '/admin/organisation', icon: 'users' },
  { label: 'Calendars', route: '/admin/calendars', icon: 'calendar' },
  { label: 'Escalation', route: '/admin/escalation', icon: 'zap' },
  { label: 'Settings', route: '/admin/settings', icon: 'sliders' },
];

const MONITOR_LINKS: AdminLink[] = [
  { label: 'Integration monitor', route: '/admin/monitor', icon: 'activity' },
  { label: 'Audit log', route: '/admin/audit', icon: 'shield' },
];

/** Frame for every /admin page: title, role-aware sub-navigation and the child outlet. */
@Component({
  selector: 'fu-admin-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="titles">
          <h1>Administration</h1>
          <div class="subtitle">{{ subtitle() }}</div>
        </div>
      </div>
      <nav class="tabs" aria-label="Administration sections">
        @for (l of links(); track l.route) {
          <a class="tab" [routerLink]="l.route" routerLinkActive="active" ariaCurrentWhenActive="page">
            <fu-icon [name]="l.icon" [size]="15" />
            {{ l.label }}
          </a>
        }
      </nav>
      <div class="outlet">
        <router-outlet />
      </div>
    </div>
  `,
  styles: `
    .tabs .tab:hover { text-decoration: none; }
    .outlet { margin-top: 18px; }
  `,
})
export class AdminShellComponent {
  private readonly session = inject(SessionState);

  protected readonly links = computed(() => [...(this.session.isAdministrator() ? ADMIN_LINKS : []), ...MONITOR_LINKS]);

  protected readonly subtitle = computed(() =>
    this.session.isAdministrator()
      ? 'Organisation, working calendars, escalation rules, runtime settings and integration health'
      : 'Integration health and the tamper-evident audit trail',
  );
}
