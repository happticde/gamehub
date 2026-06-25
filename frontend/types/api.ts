/** Message payload returned by Core actions that use either .NET or JSON naming. */
export interface ApiMessageResponse {
    Message?: string;
    message?: string;
}

/** Result returned after applying the retroactive library ignore rules. */
export interface RetroactiveIgnoreResponse {
    newlyIgnoredCount: number;
}
