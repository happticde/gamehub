type Props = {
    trackingWriteRequested: boolean;
    renewalRequested: boolean;
    disabled?: boolean;
    tracking?: boolean;
    onTrackingChange?: (value: boolean) => void;
};

/** The same permission labels and read-only default apply to both assistant connection flows. */
export function McpConsentPermissions({ trackingWriteRequested, renewalRequested, disabled, tracking, onTrackingChange }: Props) {
    return <fieldset className="space-y-3 rounded-md border p-4" disabled={disabled}>
        <legend className="px-1 font-medium">Access to grant</legend>
        <p className="text-sm">Read your library, game details, tags and statistics.</p>
        {trackingWriteRequested && <div className="flex items-start gap-3">
            <input id="tracking-consent" name="tracking" type="checkbox" value="true" checked={tracking}
                className="mt-1 h-4 w-4 accent-primary"
                onChange={onTrackingChange ? event => onTrackingChange(event.target.checked) : undefined} />
            <label htmlFor="tracking-consent" className="text-sm font-medium leading-relaxed">
                Also allow changes to play status and ratings.
            </label>
        </div>}
        {renewalRequested && <p className="text-sm text-muted-foreground">
            The assistant can renew this connection for up to 30 days unless access is revoked.
        </p>}
        <p className="text-sm text-muted-foreground">Access covers library data and the tracking changes you approve.</p>
    </fieldset>;
}
