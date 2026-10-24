"use client";

import { useSyncExternalStore } from "react";

const subscribe = () => () => {};
const readBrowserOrigin = () => window.location.origin;
const readServerOrigin = () => "http://localhost:3005";

/** Read the browser-owned origin after hydration without copying it into component state. */
export function useBrowserOrigin(): string {
    return useSyncExternalStore(subscribe, readBrowserOrigin, readServerOrigin);
}
