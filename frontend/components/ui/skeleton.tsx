import { cn } from "@/lib/utils";

/**
 * Skeleton — a placeholder loading element.
 *
 * Renders a pulsing rounded rectangle that indicates content is loading.
 * Use it to build content-shaped skeletons that match your actual UI.
 *
 * @example
 *   <Skeleton className="h-4 w-48" />         // text line
 *   <Skeleton className="h-40 w-full" />       // image area
 *   <Skeleton className="h-8 w-8 rounded-full" /> // avatar
 */
function Skeleton({
    className,
    ...props
}: React.HTMLAttributes<HTMLDivElement>) {
    return (
        <div
            className={cn("animate-pulse rounded-md bg-muted", className)}
            {...props}
        />
    );
}

export { Skeleton };
