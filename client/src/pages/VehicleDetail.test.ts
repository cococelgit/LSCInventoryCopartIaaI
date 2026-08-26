// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const vehicle = {
  lot: "sample-lot", platform: "iaai", observedAt: "2026-08-26T01:00:00Z", title: "2023 SAMPLE SUV", year: 2023, make: "SAMPLE", model: "SUV", series: "PREMIUM", vehicleType: "AUTOMOBILE", bodyStyle: "SUV", color: "Blue",
  fuelType: "Gasoline", transmission: "Automatic", driveType: "AWD", odometer: 12000, odometerKm: 19312, odometerStatus: "Actual", damage: "Front End", secondaryDamage: "Left Side", lossType: "Collision", startCode: "Run and Drive", hasKey: true,
  auctionAt: "2026-08-27T14:00:00Z", lotStatus: "Open", lotSubStatus: "Timed", isBuyNow: true, isTimed: true, currentBidUsd: 900, preBidUsd: 850, buyNowUsd: 16000, estimatedPriceFromUsd: 14000, estimatedPriceToUsd: 18000, estimatedPriceText: "$14,000 - $18,000", actualCashValueUsd: 18500, estimatedRepairCostUsd: 7200,
  location: "Sample Branch (FL)", sendFrom: "Sample Branch", state: "FL", facilityId: "123", sellingBranch: "Sample Branch", lane: "A", aisle: "12", sellerName: "Sample Seller", sellerType: "Insurance",
  titleType: "CERTIFICATE OF TITLE", saleDocumentType: "Clean", saleDocumentGroup: "Title", saleDocumentPending: false, saleDocumentExport: true, saleDocumentRegistration: true, titleBrand: "Clean", titleNotes: "None",
  engineSizeLiters: "2.0", engineHorsepower: 240, engineLayout: "I", engineDescription: "2.0L I4", cylinders: "4", airbags: "Intact", restraintSystem: "Dual Air Bag", vinStatus: "OK", vehicleClass: "Class 1", vehicleScore: "80", manufacturedIn: "USA", options: "Navigation", has360: true, hasVideo: false,
  photos: ["https://vis.iaai.com/one.jpg", "https://vis.iaai.com/two.jpg"], media: [],
};

vi.mock("../lib/trpc", () => ({ trpc: { inventory: { vehicle: { useQuery: () => ({ data: vehicle, isLoading: false }) } } } }));

import VehicleDetail from "./VehicleDetail";

afterEach(() => cleanup());

describe("VehicleDetail extended IAAI view", () => {
  it("renders complete auction, condition, document and sale sections", () => {
    render(createElement(VehicleDetail, { params: { lot: "sample-lot" } }));
    expect(screen.getByRole("heading", { name: "2023 SAMPLE SUV" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Estado de subasta" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Información del vehículo" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Condición" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Documento y vendedor" })).toBeTruthy();
    expect(screen.getByText("$18,500")).toBeTruthy();
    expect(screen.getByText("CERTIFICATE OF TITLE")).toBeTruthy();
    expect(screen.getByText("Run and Drive")).toBeTruthy();
  });

  it("switches between real gallery photos", () => {
    render(createElement(VehicleDetail, { params: { lot: "sample-lot" } }));
    const main = screen.getByAltText("2023 SAMPLE SUV, foto 1") as HTMLImageElement;
    expect(main.src).toContain("one.jpg");
    fireEvent.click(screen.getByRole("button", { name: "Ver foto 2" }));
    expect((screen.getByAltText("2023 SAMPLE SUV, foto 2") as HTMLImageElement).src).toContain("two.jpg");
  });

  it("exposes landmarks and supports gallery navigation without a mouse", async () => {
    const user = userEvent.setup();
    render(createElement(VehicleDetail, { params: { lot: "sample-lot" } }));
    expect(screen.getByRole("region", { name: "Galería de 2023 SAMPLE SUV" })).toBeTruthy();
    expect(screen.getByRole("group", { name: "Galería de fotos" })).toBeTruthy();
    const second = screen.getByRole("button", { name: "Ver foto 2" });
    second.focus();
    await user.keyboard("{Enter}");
    expect(second.getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("link", { name: /Volver al inventario/i })).toBeTruthy();
  });
});
