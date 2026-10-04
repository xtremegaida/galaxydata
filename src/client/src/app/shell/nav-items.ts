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
}

/** The application's pages, in the navigation's order. */
export const navItems: readonly NavItem[] = [
  { label: 'Start', icon: 'home', link: '/', exact: true },
];

/** The pages a user may open. */
export function navItemsFor(
  permissions: Permissions,
  items: readonly NavItem[] = navItems,
): NavItem[] {
  return items.filter((item) => item.needs === undefined || permissions[item.needs]);
}
