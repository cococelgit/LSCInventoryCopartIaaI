// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const liveInventory = {
  source: "lsc-inventory-postgres",
  generatedAt: "2026-08-25T18:40:00.000Z",
  vehicles: [
    {
      lot: "11111111", observedAt: "2026-08-25T18:40:00.000Z", title: "2020 ALPHA SEDAN", year: 2020, make: "ALPHA", model: "SEDAN", vehicleType: "AUTOMOBILE", color: "Blue",
      fuelType: "Gas", transmission: "Automatic", driveType: "FRONT WHEEL DRIVE", odometer: 20000, damage: "Front end", secondaryDamage: "None", lossType: "Collision", auctionAt: "2026-08-25T14:00:00.000Z", lotStatus: "open", currentBidUsd: 1000, buyNowUsd: null,
      bodyStyle: "Sedan", startCode: "Run and Drive", hasKey: true, sellerType: "Insurance", engineSizeLiters: "2.0", engineHorsepower: 180, engineLayout: "I", cylinders: "4", estimatedPriceFromUsd: 8000, estimatedPriceToUsd: 10000,
      location: "Clewiston (FL)", state: "FL", titleType: "CT", facilityId: "366", photos: [],
    },
    {
      lot: "22222222", observedAt: "2026-08-25T18:40:00.000Z", title: "2021 BETA SUV", year: 2021, make: "BETA", model: "SUV", vehicleType: "AUTOMOBILE", color: "Black",
      fuelType: "Gas", transmission: "Automatic", driveType: "ALL WHEEL DRIVE", odometer: 30000, damage: "Side", secondaryDamage: "Rear", lossType: "Theft", auctionAt: "2026-08-26T14:00:00.000Z", lotStatus: "live", currentBidUsd: 5000, buyNowUsd: 16000,
      bodyStyle: "SUV", startCode: "Starts", hasKey: false, sellerType: "Fleet", engineSizeLiters: "3.5", engineHorsepower: 300, engineLayout: "V", cylinders: "6", estimatedPriceFromUsd: 15000, estimatedPriceToUsd: 18000, isBuyNow: true,
      location: "Clewiston (FL)", state: "FL", titleType: "CT", facilityId: "366", photos: [],
    },
    {
      lot: "33333333", observedAt: "2026-08-25T18:40:00.000Z", title: "2019 SPECIAL PARTS CAR", year: 2019, make: "SPECIAL", model: "PARTS CAR", vehicleType: "AUTOMOBILE", color: "White",
      fuelType: "Gas", transmission: "Automatic", driveType: "FRONT WHEEL DRIVE", odometer: 40000, damage: "Front end", secondaryDamage: null, lossType: "Collision", auctionAt: "2026-08-25T14:00:00.000Z", lotStatus: "open", currentBidUsd: 500, buyNowUsd: null,
      bodyStyle: "Coupe", startCode: "Stationary", hasKey: false, sellerType: "Other", engineSizeLiters: "1.8", engineHorsepower: 140, engineLayout: "I", cylinders: "4", estimatedPriceFromUsd: 2000, estimatedPriceToUsd: 3500,
      location: "Clewiston (FL)", state: "FL", titleType: "CERTIFICATE OF DESTRUCTION", facilityId: "366", photos: [],
    },
  ],
};

vi.mock("../lib/trpc", () => ({
  trpc: { inventory: { recent: { useQuery: () => ({ data: liveInventory, isLoading: false }) } } },
}));

import Home from "./Home";

afterEach(() => cleanup());

describe("Home live range filters", () => {
  it("reduces results when a Miami auction-date range is selected", () => {
    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Fecha de subasta desde"), { target: { value: "2026-08-25" } });
    fireEvent.change(screen.getByLabelText("Fecha de subasta hasta"), { target: { value: "2026-08-25" } });
    expect(screen.getByText("2020 ALPHA SEDAN")).toBeTruthy();
    expect(screen.queryByText("2021 BETA SUV")).toBeNull();
    expect(screen.getByRole("heading", { name: "1 vehículos" })).toBeTruthy();
  });

  it("reduces results when a disclosed LSC-base-budget range is selected", () => {
    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Presupuesto LSC mínimo"), { target: { value: "1300" } });
    fireEvent.change(screen.getByLabelText("Presupuesto LSC máximo"), { target: { value: "1700" } });
    expect(screen.getByText("2020 ALPHA SEDAN")).toBeTruthy();
    expect(screen.queryByText("2021 BETA SUV")).toBeNull();
    expect(screen.getByText("$1,399 – $1,699")).toBeTruthy();
  });

  it("filters by the provider-estimated price range", () => {
    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Precio estimado mínimo"), { target: { value: "14000" } });
    fireEvent.change(screen.getByLabelText("Precio estimado máximo"), { target: { value: "16000" } });
    expect(screen.getByText("2021 BETA SUV")).toBeTruthy();
    expect(screen.queryByText("2020 ALPHA SEDAN")).toBeNull();
  });

  it("filters the results using the Buy Now auction-status tab", () => {
    render(createElement(Home));
    fireEvent.click(screen.getByRole("tab", { name: "Buy Now" }));
    expect(screen.getByText("2021 BETA SUV")).toBeTruthy();
    expect(screen.queryByText("2020 ALPHA SEDAN")).toBeNull();
  });

  it("offers every audited sort mode", () => {
    render(createElement(Home));
    expect((screen.getByLabelText("Ordenar resultados") as HTMLSelectElement).options).toHaveLength(12);
  });

  it.each([
    { label: "loss type", name: /Theft/, included: "2021 BETA SUV", excluded: "2020 ALPHA SEDAN" },
    { label: "color", name: /Black/, included: "2021 BETA SUV", excluded: "2020 ALPHA SEDAN" },
    { label: "engine layout", name: /^V/, included: "2021 BETA SUV", excluded: "2020 ALPHA SEDAN" },
    { label: "seller type", name: /Fleet/, included: "2021 BETA SUV", excluded: "2020 ALPHA SEDAN" },
  ])("filters by $label", ({ name, included, excluded }) => {
    render(createElement(Home));
    fireEvent.click(screen.getByRole("checkbox", { name }));
    expect(screen.getByText(included)).toBeTruthy();
    expect(screen.queryByText(excluded)).toBeNull();
  });

  it("filters specifically by body style without confusing it with model", () => {
    render(createElement(Home));
    const section = screen.getByText("Body style").closest("section");
    expect(section).toBeTruthy();
    fireEvent.click(within(section!).getByRole("checkbox", { name: /^SUV/ }));
    expect(screen.getByText("2021 BETA SUV")).toBeTruthy();
    expect(screen.queryByText("2020 ALPHA SEDAN")).toBeNull();
  });

  it("filters by key availability, engine size and horsepower", () => {
    render(createElement(Home));
    fireEvent.click(screen.getByRole("button", { name: "Con llave" }));
    fireEvent.change(screen.getByLabelText("Motor litros mínimo"), { target: { value: "1.9" } });
    fireEvent.change(screen.getByLabelText("Motor litros máximo"), { target: { value: "2.1" } });
    fireEvent.change(screen.getByLabelText("Potencia mínima"), { target: { value: "170" } });
    fireEvent.change(screen.getByLabelText("Potencia máxima"), { target: { value: "190" } });
    expect(screen.getByText("2020 ALPHA SEDAN")).toBeTruthy();
    expect(screen.queryByText("2021 BETA SUV")).toBeNull();
  });

  it("renders dense provider fields when IAAI reports them", () => {
    render(createElement(Home));
    expect(screen.getAllByText("Insurance").length).toBeGreaterThan(0);
    expect(screen.getByText("2L")).toBeTruthy();
    expect(screen.getAllByText("4 cil.").length).toBeGreaterThan(0);
    expect(screen.getByText("180 HP")).toBeTruthy();
    expect(screen.getByText("$8,000 – $10,000")).toBeTruthy();
    expect(screen.getByText("$1,000")).toBeTruthy();
  });

  it("supports keyboard tab navigation and exposes filter state", () => {
    render(createElement(Home));
    const allTab = screen.getByRole("tab", { name: "Todos" });
    allTab.focus();
    fireEvent.keyDown(allTab, { key: "ArrowRight" });
    const openTab = screen.getByRole("tab", { name: "Abierta" });
    expect(document.activeElement).toBe(openTab);
    expect(openTab.getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("group", { name: "Disponibilidad de llave" })).toBeTruthy();
    const withKey = screen.getByRole("button", { name: "Con llave" });
    expect(withKey.getAttribute("aria-pressed")).toBe("false");
    fireEvent.click(withKey);
    expect(withKey.getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByLabelText("Puja máxima")).toBeTruthy();
  });

  it("keeps special titles stored but hidden until their title filter is selected", () => {
    render(createElement(Home));
    expect(screen.queryByText("2019 SPECIAL PARTS CAR")).toBeNull();
    const specialTitleFilter = screen.getByRole("checkbox", { name: /CERTIFICATE OF DESTRUCTION/ }) as HTMLInputElement;
    expect(specialTitleFilter.checked).toBe(false);
    fireEvent.click(specialTitleFilter);
    expect(screen.getByText("2019 SPECIAL PARTS CAR")).toBeTruthy();
  });

  it("allows a direct lot search to find a special-title vehicle", () => {
    render(createElement(Home));
    fireEvent.change(screen.getByLabelText("Buscar vehículos"), { target: { value: "33333333" } });
    expect(screen.getByText("2019 SPECIAL PARTS CAR")).toBeTruthy();
  });

  it("opens and closes the responsive filter drawer", () => {
    render(createElement(Home));
    const sidebar = document.querySelector(".browse-sidebar")!;
    expect(sidebar.classList.contains("browse-sidebar--open")).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Filtros" }));
    expect(sidebar.classList.contains("browse-sidebar--open")).toBe(true);
    const closeButtons = screen.getAllByRole("button", { name: "Cerrar filtros" });
    expect(closeButtons.length).toBeGreaterThanOrEqual(1);
    fireEvent.click(closeButtons[0]);
    expect(sidebar.classList.contains("browse-sidebar--open")).toBe(false);
  });
});
