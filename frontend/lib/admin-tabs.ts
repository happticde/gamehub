export const ADMIN_TAB_VALUES = [
    "enrichment",
    "credentials",
    "ingestion",
    "sync",
    "activity",
    "backup",
] as const;

export type AdminTab = (typeof ADMIN_TAB_VALUES)[number];

export const DEFAULT_ADMIN_TAB: AdminTab = ADMIN_TAB_VALUES[0];

/** Returns a supported Admin tab, falling back to the first visible tab. */
export function getAdminTab(value: string | null): AdminTab {
    return ADMIN_TAB_VALUES.find((tab) => tab === value) ?? DEFAULT_ADMIN_TAB;
}
