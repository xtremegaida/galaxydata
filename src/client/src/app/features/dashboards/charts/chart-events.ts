import type { SelectionAction } from '../model/widget-context';

/** Whether the keys are a Mac's: there, ⌘ adds to what is chosen, as Ctrl does elsewhere. */
export function onMac(navigator: Navigator | undefined): boolean {
  const platform =
    (navigator as (Navigator & { userAgentData?: { platform?: string } }) | undefined)
      ?.userAgentData?.platform ??
    navigator?.platform ??
    '';
  return /mac|iphone|ipad/i.test(platform);
}

/** What a click chooses, by the keys held: Alt leaves the slice out; Ctrl (⌘ on a Mac) adds it to those chosen; else it is chosen alone. */
export function actionOf(
  event: Pick<MouseEvent, 'altKey' | 'ctrlKey' | 'metaKey'> | undefined,
  mac: boolean,
): SelectionAction {
  if (!event) {
    return 'replace';
  }
  if (event.altKey) {
    return 'exclude';
  }
  return (mac ? event.metaKey : event.ctrlKey) ? 'add' : 'replace';
}
