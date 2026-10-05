import { Routes } from '@angular/router';
import { authGuard, executiveGuard, guestGuard, monitorGuard } from './core/auth/guards';
import { ShellComponent } from './layout/shell.component';

/**
 * Route map. Every feature page is lazy-loaded. Executive-only pages fall back to /me for everyone else.
 * Admin pages carry their own guards (see features/admin/admin.routes.ts).
 */
export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    title: 'Sign in · FollowUp Officer',
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      {
        path: '',
        pathMatch: 'full',
        canActivate: [executiveGuard],
        title: 'Workbench · FollowUp Officer',
        loadComponent: () => import('./features/workbench/workbench.page').then((m) => m.WorkbenchPage),
      },
      {
        path: 'attention',
        canActivate: [executiveGuard],
        title: 'Attention required · FollowUp Officer',
        loadComponent: () => import('./features/attention/attention.page').then((m) => m.AttentionPage),
      },
      {
        path: 'me',
        title: 'My work · FollowUp Officer',
        loadComponent: () => import('./features/dashboards/my-dashboard.page').then((m) => m.MyDashboardPage),
      },
      {
        path: 'team',
        title: 'Team · FollowUp Officer',
        loadComponent: () => import('./features/dashboards/team-dashboard.page').then((m) => m.TeamDashboardPage),
      },
      {
        path: 'team/:id',
        title: 'Team · FollowUp Officer',
        loadComponent: () => import('./features/dashboards/team-dashboard.page').then((m) => m.TeamDashboardPage),
      },
      {
        path: 'tasks',
        title: 'Tasks · FollowUp Officer',
        loadComponent: () => import('./features/tasks/task-list.page').then((m) => m.TaskListPage),
      },
      {
        path: 'tasks/:id',
        title: 'Task · FollowUp Officer',
        loadComponent: () => import('./features/tasks/task-detail.page').then((m) => m.TaskDetailPage),
      },
      {
        path: 'commitments',
        title: 'Commitments · FollowUp Officer',
        loadComponent: () => import('./features/commitments/commitments.page').then((m) => m.CommitmentsPage),
      },
      {
        path: 'recurring',
        title: 'Recurring · FollowUp Officer',
        loadComponent: () => import('./features/recurring/recurring-list.page').then((m) => m.RecurringListPage),
      },
      {
        path: 'recurring/new',
        title: 'New recurring task · FollowUp Officer',
        loadComponent: () => import('./features/recurring/recurring-editor.page').then((m) => m.RecurringEditorPage),
      },
      {
        path: 'recurring/:id/edit',
        title: 'Edit recurring task · FollowUp Officer',
        loadComponent: () => import('./features/recurring/recurring-editor.page').then((m) => m.RecurringEditorPage),
      },
      {
        path: 'recurring/:id',
        title: 'Recurring task · FollowUp Officer',
        loadComponent: () => import('./features/recurring/recurring-detail.page').then((m) => m.RecurringDetailPage),
      },
      {
        path: 'projects',
        title: 'Projects · FollowUp Officer',
        loadComponent: () => import('./features/projects/projects.page').then((m) => m.ProjectsPage),
      },
      {
        path: 'projects/:id',
        title: 'Project · FollowUp Officer',
        loadComponent: () => import('./features/projects/project-detail.page').then((m) => m.ProjectDetailPage),
      },
      {
        path: 'meetings',
        title: 'Meetings · FollowUp Officer',
        loadComponent: () => import('./features/meetings/meetings.page').then((m) => m.MeetingsPage),
      },
      {
        path: 'meetings/new',
        title: 'New meeting · FollowUp Officer',
        loadComponent: () => import('./features/meetings/meeting-editor.page').then((m) => m.MeetingEditorPage),
      },
      {
        path: 'meetings/:id',
        title: 'Meeting · FollowUp Officer',
        loadComponent: () => import('./features/meetings/meeting-editor.page').then((m) => m.MeetingEditorPage),
      },
      {
        path: 'communications',
        title: 'Communications · FollowUp Officer',
        loadComponent: () => import('./features/communications/communications.page').then((m) => m.CommunicationsPage),
      },
      {
        path: 'ask',
        title: 'Ask · FollowUp Officer',
        loadComponent: () => import('./features/ask/ask.page').then((m) => m.AskPage),
      },
      {
        path: 'review',
        canActivate: [executiveGuard],
        title: 'Weekly review · FollowUp Officer',
        loadComponent: () => import('./features/review/review.page').then((m) => m.ReviewPage),
      },
      {
        path: 'reports',
        canActivate: [executiveGuard],
        title: 'Reports · FollowUp Officer',
        loadComponent: () => import('./features/reports/reports.page').then((m) => m.ReportsPage),
      },
      {
        path: 'templates',
        title: 'Templates · FollowUp Officer',
        loadComponent: () => import('./features/templates/templates.page').then((m) => m.TemplatesPage),
      },
      {
        path: 'templates/new',
        title: 'New template · FollowUp Officer',
        loadComponent: () => import('./features/templates/template-editor.page').then((m) => m.TemplateEditorPage),
      },
      {
        path: 'templates/:id',
        title: 'Template · FollowUp Officer',
        loadComponent: () => import('./features/templates/template-editor.page').then((m) => m.TemplateEditorPage),
      },
      {
        path: 'admin',
        loadChildren: () => import('./features/admin/admin.routes').then((m) => m.ADMIN_ROUTES),
      },
      {
        path: 'simulator',
        canActivate: [monitorGuard],
        title: 'Microsoft 365 simulator · FollowUp Officer',
        loadComponent: () => import('./features/simulator/simulator.page').then((m) => m.SimulatorPage),
      },
      {
        path: 'search',
        title: 'Search · FollowUp Officer',
        loadComponent: () => import('./features/search/search.page').then((m) => m.SearchPage),
      },
      {
        path: '**',
        title: 'Not found · FollowUp Officer',
        loadComponent: () => import('./features/not-found.page').then((m) => m.NotFoundPage),
      },
    ],
  },
];
