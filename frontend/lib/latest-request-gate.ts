/**
 * Tracks request generations so a slower obsolete response cannot replace newer UI state even if
 * an HTTP client or test double does not honor AbortSignal.
 */
export function createLatestRequestGate() {
    let latestGeneration = 0;

    return {
        begin() {
            latestGeneration += 1;
            return latestGeneration;
        },
        invalidate() {
            latestGeneration += 1;
        },
        isLatest(generation: number) {
            return generation === latestGeneration;
        },
    };
}
