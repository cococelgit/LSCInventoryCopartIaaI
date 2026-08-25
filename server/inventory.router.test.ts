import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { appRouter } from "./routers";
import type { TrpcContext } from "./_core/context";

const context = {
  user: null,
  req: { protocol: "https", headers: {} },
  res: { clearCookie: vi.fn() },
} as unknown as TrpcContext;

const responsePayload = {
  source: "lsc-inventory-postgres",
  generatedAt: "2026-08-25T16:21:14.871055+00:00",
  vehicles: [{
    lot: "41623946",
    observedAt: "2026-08-25T16:21:14.871055+00:00",
    title: "2022 HONDA ACCORD LX",
    year: 2022,
    make: "HONDA",
    model: "ACCORD LX",
    vehicleType: "AUTOMOBILE",
    color: null,
    fuelType: null,
    transmission: null,
    driveType: null,
    odometer: null,
    damage: null,
    auctionAt: "2026-08-27T01:00:00+00:00",
    lotStatus: "open",
    currentBidUsd: 3800,
    buyNowUsd: null,
    location: "Clewiston (FL)",
    state: null,
    titleType: null,
    photos: ["https://cs.copart.com/v1/sample-photo.jpg"],
  }],
};

describe("inventory router", () => {
  beforeEach(() => {
    process.env.INVENTORY_API_TOKEN = "test-server-only-token";
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    delete process.env.INVENTORY_API_TOKEN;
  });

  it("uses the server-side token to return live vehicles and their real photo URLs", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(responsePayload), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await appRouter.createCaller(context).inventory.recent({ take: 3 });

    expect(result.vehicles).toHaveLength(1);
    expect(result.vehicles[0]?.lot).toBe("41623946");
    expect(result.vehicles[0]?.photos).toEqual(["https://cs.copart.com/v1/sample-photo.jpg"]);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/api/v1/inventory/recent?take=3"),
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: "Bearer test-server-only-token" }),
      }),
    );
    expect(JSON.stringify(result)).not.toContain("test-server-only-token");
  });

  it("surfaces an upstream error instead of falling back to local inventory", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("unavailable", { status: 503 })));

    await expect(appRouter.createCaller(context).inventory.recent({ take: 3 }))
      .rejects.toThrow("Inventory API returned 503");
  });
});
