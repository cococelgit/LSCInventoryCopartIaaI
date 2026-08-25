// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({ useQuery: vi.fn() }));
mocks.useQuery.mockReturnValue({ data: {
  page: 1, pageSize: 25, total: 24, totalPages: 2,
  items: [{ evaluatedAt: "2026-08-25T22:01:53Z", evaluation: { decision: "DESCARTAR", load_to_system: false, lot_number: "64317406", auction_source: "copart", vin_masked: "***4078", discard_reasons: [{ code: "D10", name: "Título que impide titular", explanation: "Título pendiente.", source_fields: ["sale_document.is_pending"], observed_values: { "sale_document.is_pending": "true" } }], flags: [], data_quality_notes: [], evaluated_fields: ["sale_document.is_pending"], rule_version: "filtro_elegibilidad_subasta_v3" } }],
  ruleSummary: [{ code: "D10", name: "Título que impide titular", count: 16 }],
}, isLoading: false, isError: false });

vi.mock("@/_core/hooks/useAuth", () => ({ useAuth: () => ({ user: { role: "admin", name: "Admin" }, loading: false }) }));
vi.mock("@/components/DashboardLayout", () => ({ default: ({ children }: { children: unknown }) => createElement("div", {}, children as never) }));
vi.mock("@/lib/trpc", () => ({ trpc: { internalAudit: { discarded: { useQuery: mocks.useQuery } } } }));

import InternalDiscards from "./InternalDiscards";

afterEach(() => cleanup());

describe("Internal discard panel", () => {
  it("shows protected audit facts and applies the rule filter", () => {
    render(createElement(InternalDiscards));

    expect(screen.getByRole("heading", { name: "Vehículos descartados" })).toBeTruthy();
    expect(screen.getByText("#64317406")).toBeTruthy();
    expect(screen.getByText("***4078")).toBeTruthy();
    expect(screen.getAllByText("Título que impide titular").length).toBeGreaterThanOrEqual(2);
    expect(screen.getByRole("table").parentElement?.className).toContain("overflow-x-auto");
    expect(screen.getByRole("table").className).toContain("min-w-[980px]");

    fireEvent.click(screen.getByRole("button", { name: /D10/ }));
    expect(mocks.useQuery).toHaveBeenLastCalledWith(expect.objectContaining({ ruleCode: "D10" }), expect.any(Object));
  });
});
