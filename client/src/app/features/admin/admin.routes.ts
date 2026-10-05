import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { adminGuard, monitorGuard } from '../../core/auth/guards';
import { SessionState } from '../../core/auth/session-state';
import { AdminShellComponent } from './admin-shell.component';

/**
 * /admin/* — organisation, calendars, escalation policies and settings for administrators; the integration
 * monitor and the audit log also for executives. The shell itself requires monitor access (executive or admin).
 */
export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminShellComponent,
    canActivate: [monitorGuard],
    children: [
      {
        path: '',
        pathMatch: 'full',
        redirectTo: () => (inject(SessionState).isAdministrator() ? 'organisation' : 'monitor'),
      },
      {
        path: 'organisation',
        canActivate: [adminGuard],
        title: 'Organisation · FollowUp Officer',
        loadComponent: () => import('./organisation/organisation.page').then((m) => m.OrganisationPage),
      },
      {
        path: 'calendars',
        canActivate: [adminGuard],
        title: 'Calendars & holidays · FollowUp Officer',
        loadComponent: () => import('./calendars/calendars.page').then((m) => m.CalendarsPage),
      },
      {
        path: 'escalation',
        canActivate: [adminGuard],
        title: 'Escalation policies · FollowUp Officer',
        loadComponent: () => import('./escalation/escalation.page').then((m) => m.EscalationPage),
      },
      {
        path: 'settings',
        canActivate: [adminGuard],
        title: 'Settings · FollowUp Officer',
        loadComponent: () => import('./settings/settings.page').then((m) => m.SettingsPage),
      },
      {
        path: 'monitor',
        canActivate: [monitorGuard],
        title: 'Integration monitor · FollowUp Officer',
        loadComponent: () => import('./monitor/monitor.page').then((m) => m.MonitorPage),
      },
      {
        path: 'audit',
        canActivate: [monitorGuard],
        title: 'Audit log · FollowUp Officer',
        loadComponent: () => import('./audit/audit.page').then((m) => m.AuditPage),
      },
    ],
  },
];
