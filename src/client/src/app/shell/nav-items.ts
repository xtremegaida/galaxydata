import type { Permissions } from '../core/auth/auth-store';

/** A page the navigation leads to. */
export interface NavItem {
  readonly label: string;
  /** A Material Symbols name. */
  readonly icon: string;
  readonly link: string;
  /** What the user must be allowed to do to see it. */
  readonly needs?: keyof Permissions;
  /** Whether it is the page only at its own address, not those under it (the start). */
  readonly exact?: boolean;
  /** The heading of the pages it is shown under. */
  readonly section?: string;
}

/** Pages shown together, under a heading or none. */
export interface NavSection {
  readonly label: string | null;
  readonly items: readonly NavItem[];
}

/** The application's pages, in the navigation's order. */
export const navItems: readonly NavItem[] = [
  { label: 'Start', icon: 'home', link: '/', exact: true },
  { label: 'Browse', icon: 'account_tree', link: '/browse', needs: 'canRead' },
  {
    label: 'Users',
    icon: 'group',
    link: '/admin/users',
    needs: 'canAdmin',
    section: 'Administration',
  },
  {
    label: 'Connections',
    icon: 'database',
    link: '/admin/connections',
    needs: 'canAdmin',
    section: 'Administration',
  },
];

/** The pages a user may open, by section, in order. */
export function navItemsFor(
  permissions: Permissions,
  items: readonly NavItem[] = navItems,
): NavSection[] {
  const sections: { label: string | null; items: NavItem[] }[] = [];
  for (const item of items) {
    if (item.needs !== undefined && !permissions[item.needs]) {
      continue;
    }
    const label = item.section ?? null;
    const last = sections.at(-1);
    if (last && last.label === label) {
      last.items.push(item);
    } else {
      sections.push({ label, items: [item] });
    }
  }
  return sections;
}
