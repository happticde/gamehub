import { forwardMcpRequest } from "@/lib/mcp-gateway";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";
export const GET = forwardMcpRequest;
export const POST = forwardMcpRequest;
export const DELETE = forwardMcpRequest;
