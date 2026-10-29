// Proposed information architecture: docs/product/screens/README.md
export interface NavItem {
  path: string
  title: string
  group: 'Daily Work' | 'Administration'
  doc: string
}

export const navItems: NavItem[] = [
  { path: '/control-room', title: 'Daily Control Room', group: 'Daily Work', doc: 'daily-control-room.md' },
  { path: '/exceptions', title: 'Exceptions', group: 'Daily Work', doc: 'exceptions-queue.md' },
  { path: '/transactions', title: 'Transactions', group: 'Daily Work', doc: 'transaction-explorer.md' },
  { path: '/safeguarding', title: 'Safeguarding', group: 'Daily Work', doc: 'safeguarding-position.md' },
  { path: '/sign-off', title: 'Sign-off', group: 'Daily Work', doc: 'run-sign-off.md' },
  { path: '/sources', title: 'Sources', group: 'Administration', doc: 'sources.md' },
  { path: '/settings', title: 'Configuration and Access', group: 'Administration', doc: 'configuration-access.md' },
]
