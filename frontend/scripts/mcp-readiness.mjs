import { getManagedMcpConfig, managedMcpHeaders } from "../lib/mcp-managed-config.mjs";

// Keep diagnostics finite and independent of exception text, paths, identities and credentials.
const fixes = {
    ready: "Deployment wiring is ready. Remote-client acceptance is a separate release gate.",
    disabled: "Include compose.mcp.yaml in both service deployments after explicit setup.",
    managed_storage_invalid: "Run the documented setup for fresh volumes; restore a consistent backup for initialized storage.",
    configuration_conflict: "Remove the legacy MCP override after verified adoption; keep provider configuration unchanged.",
    invalid_enablement: "Use true or false for the managed enablement switch.",
    provider_prerequisites: "Restore working provider sign-in, frontend authentication and Secure cookies first.",
    origin_mismatch: "Use the existing public browser origin for setup and provider callback.",
    invalid_core_destination: "Restore the existing private CORE_URL service destination.",
    core_unavailable: "Start Core with the same Compose addition; check its health and matching image version.",
    pairing_rejected: "Recreate both services with their paired state volumes; do not reset initialized stores.",
    renewal_due: "Run the documented key renewal and recheck before enabling clients.",
    managed_storage_unavailable: "Check Core storage permissions and restore the matching database/key backup if damaged.",
};
let code = "core_unavailable";
try {
    const config = getManagedMcpConfig();
    if (!config) code = "disabled";
    else {
        const response = await fetch(new URL("/internal/mcp/readiness", config.core), {
            headers: { ...managedMcpHeaders(config), "X-GameHub-Mcp-Bff-Key": config.key, Origin: config.instance },
            redirect: "error", signal: AbortSignal.timeout(5000),
        });
        if (response.status === 404) code = "pairing_rejected";
        else if ([200, 503].includes(response.status)) {
            const reader = response.body?.getReader();
            const chunks = [];
            let size = 0;
            if (reader) try {
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    size += value.length;
                    if (size > 256) break;
                    chunks.push(value);
                }
            } finally { await reader.cancel(); }
            if (size <= 256) {
                const result = JSON.parse(Buffer.concat(chunks).toString("utf8"));
                if (["ready", "renewal_due", "managed_storage_unavailable"].includes(result.code)) code = result.code;
            }
        }
    }
} catch (error) {
    if (error instanceof Error && Object.hasOwn(fixes, error.message)) code = error.message;
}
console.log(JSON.stringify({ code, fix: fixes[code] }));
process.exitCode = code === "ready" ? 0 : 1;
