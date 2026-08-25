import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TrpcContext } from "./_core/context";
import { appRouter } from "./routers";

const createContext = (role: "admin" | "user") => ({
  user: { id: 1, openId: `${role}-open-id`, role, name: role, email: `${role}@example.com`, loginMethod: "manus", createdAt: new Date(), updatedAt: new Date(), lastSignedIn: new Date() },
  req: { protocol: "https", headers: {} },
  res: { clearCookie: vi.fn() },
}) as unknown as TrpcContext;

const auditPayload = {
  page: 1,
  pageSize: 25,
  total: 1,
  totalPages: 1,
  items: [{ evaluatedAt: "2026-08-25T22:01:53Z", evaluation: { decision: "DESCARTAR", load_to_system: false, lot_number: "64317406", auction_source: "copart", vin_masked: "***4078", discard_reasons: [{ code: "D10", name: "Título que impide titular", explanation: "Título pendiente.", source_fields: ["sale_document.is_pending"], observed_values: { "sale_document.is_pending": "true" } }], flags: [], data_quality_notes: [], evaluated_fields: ["sale_document.is_pending"], rule_version: "filtro_elegibilidad_subasta_v3" } }],
  ruleSummary: [{ code: "D10", name: "Título que impide titular", count: 1 }],
};

describe("internal discard audit router", () => {
  beforeEach(() => { process.env.INVENTORY_API_TOKEN = "server-only-token"; });
  afterEach(() => { vi.unstubAllGlobals(); delete process.env.INVENTORY_API_TOKEN; });

  it("allows admins and keeps the Azure token server-side", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(auditPayload), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await appRouter.createCaller(createContext("admin")).internalAudit.discarded({ page: 1, pageSize: 25, ruleCode: "D10" });

    expect(result.total).toBe(1);
    expect(result.items[0]?.evaluation.discard_reasons[0]?.code).toBe("D10");
    expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining("ruleCode=D10"), expect.objectContaining({ headers: expect.objectContaining({ Authorization: "Bearer server-only-token" }) }));
    expect(JSON.stringify(result)).not.toContain("server-only-token");
  });

  it("rejects non-admin users before calling Azure", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    await expect(appRouter.createCaller(createContext("user")).internalAudit.discarded({ page: 1, pageSize: 25 })).rejects.toMatchObject({ code: "FORBIDDEN" });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
