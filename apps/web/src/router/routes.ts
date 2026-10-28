// Proposed information architecture: docs/product/screens/README.md
export interface NavItem {
  path: string
  title: string
  group: 'Daily Work' | 'Administration'
  doc: string
  /** Server permissions of which at least one enables the item (from getMe().permissions; never role names). */
  permissions: string[]
}

export const navItems: NavItem[] = [
  { path: '/control-room', title: 'Daily Control Room', group: 'Daily Work', doc: 'daily-control-room.md', permissions: ['run.view'] },
  { path: '/exceptions', title: 'Exceptions', group: 'Daily Work', doc: 'exceptions-queue.md', permissions: ['exception.view'] },
  { path: '/transactions', title: 'Transactions', group: 'Daily Work', doc: 'transaction-explorer.md', permissions: ['transaction.view'] },
  { path: '/safeguarding', title: 'Safeguarding', group: 'Daily Work', doc: 'safeguarding-position.md', permissions: ['safeguarding.view'] },
  { path: '/sign-off', title: 'Sign-off', group: 'Daily Work', doc: 'run-sign-off.md', permissions: ['signoff.view'] },
  { path: '/sources', title: 'Sources', group: 'Administration', doc: 'sources.md', permissions: ['source.view'] },
  { path: '/settings', title: 'Configuration and Access', group: 'Administration', doc: 'configuration-access.md', permissions: ['settings.view', 'settings.draft', 'access.manage', 'approval.view'] },
]
