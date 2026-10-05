import { SessionState } from '../core/auth/session-state';

export interface NavItem {
  label: string;
  route: string;
  icon: string;
  exact?: boolean;
  /** Key of a live badge count supplied by the shell ('attention'). */
  badge?: 'attention';
}

export interface NavSection {
  label: string;
  items: NavItem[];
}

/** Role-aware navigation (MeDto.isExecutive / isAdministrator / isManager). */
export function buildNav(session: SessionState, opts: { emulator: boolean }): NavSection[] {
  const sections: NavSection[] = [];
  if (session.isExecutive()) {
    sections.push({
      label: 'Executive',
      items: [
        { label: 'Workbench', route: '/', icon: 'workbench', exact: true },
        { label: 'Attention', route: '/attention', icon: 'target', badge: 'attention' },
        { label: 'Weekly review', route: '/review', icon: 'calendar' },
        { label: 'Reports', route: '/reports', icon: 'chart' },
      ],
    });
  }
  const work: NavItem[] = [
    { label: 'My work', route: '/me', icon: 'home' },
    { label: 'Tasks', route: '/tasks', icon: 'tasks' },
    { label: 'Commitments', route: '/commitments', icon: 'flag' },
    { label: 'Recurring', route: '/recurring', icon: 'repeat' },
    { label: 'Projects', route: '/projects', icon: 'briefcase' },
    { label: 'Meetings', route: '/meetings', icon: 'users' },
    { label: 'Ask', route: '/ask', icon: 'sparkles' },
  ];
  sections.push({ label: 'Work', items: work });

  const manage: NavItem[] = [];
  if (session.isManager() || session.isExecutive() || session.isAdministrator()) {
    manage.push({ label: 'Team', route: '/team', icon: 'users' });
  }
  manage.push({ label: 'Communications', route: '/communications', icon: 'messages' });
  manage.push({ label: 'Templates', route: '/templates', icon: 'layers' });
  sections.push({ label: 'Manage', items: manage });

  const admin: NavItem[] = [];
  if (session.isAdministrator()) {
    admin.push(
      { label: 'Organisation', route: '/admin/organisation', icon: 'users' },
      { label: 'Calendars', route: '/admin/calendars', icon: 'calendar' },
      { label: 'Escalation', route: '/admin/escalation', icon: 'zap' },
      { label: 'Settings', route: '/admin/settings', icon: 'sliders' },
    );
  }
  if (session.canMonitor()) {
    admin.push(
      { label: 'Integration monitor', route: '/admin/monitor', icon: 'activity' },
      { label: 'Audit log', route: '/admin/audit', icon: 'shield' },
    );
    if (opts.emulator) admin.push({ label: 'M365 simulator', route: '/simulator', icon: 'plug' });
  }
  if (admin.length) sections.push({ label: 'Administration', items: admin });
  return sections;
}
