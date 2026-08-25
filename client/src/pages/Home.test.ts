import { describe, expect, it } from "vitest";
import { auctionDateInEastern, buildFacilityLabel, buildOptionCounts, doesEstimatedTotalOverlap, estimateBasePurchaseTotal, isAuctionDateInRange, parseOdometer } from "./Home";

describe("buildOptionCounts", () => {
  it("groups and alphabetizes values supplied by the inventory feed", () => {
    expect(buildOptionCounts(["Salvage", "No reportado", "Salvage", "Clean"]))
      .toEqual([["Clean", 1], ["No reportado", 1], ["Salvage", 2]]);
  });
});

describe("parseOdometer", () => {
  it("accepts numeric values and rejects non-reported mileage", () => {
    expect(parseOdometer("64,250 mi")).toBe(64250);
    expect(parseOdometer(1240)).toBe(1240);
    expect(parseOdometer("No reportado")).toBeNull();
  });
});

describe("buildFacilityLabel", () => {
  it("keeps the reported location together with its facility identifier", () => {
    expect(buildFacilityLabel("Clewiston (FL)", "366")).toBe("Clewiston (FL) · Facility 366");
    expect(buildFacilityLabel("Clewiston (FL)", null)).toBeNull();
  });
});

describe("estimateBasePurchaseTotal", () => {
  it("uses the visible LSC broker-fee range and refuses to invent a price without a current bid", () => {
    expect(estimateBasePurchaseTotal(5000)).toEqual({ min: 5399, max: 5699 });
    expect(estimateBasePurchaseTotal(null)).toBeNull();
  });
});

describe("auctionDateInEastern", () => {
  it("applies the Miami time zone to auction-date filtering", () => {
    expect(auctionDateInEastern("2026-08-25T01:00:00+00:00")).toBe("2026-08-24");
    expect(auctionDateInEastern(null)).toBeNull();
  });
});

describe("range predicates", () => {
  it("keeps only auction dates inside the selected Miami-date window", () => {
    expect(isAuctionDateInRange("2026-08-25", "2026-08-25", "2026-08-25")).toBe(true);
    expect(isAuctionDateInRange("2026-08-24", "2026-08-25", "")).toBe(false);
    expect(isAuctionDateInRange(null, "2026-08-25", "")).toBe(false);
  });

  it("includes an estimate when its disclosed range overlaps the selected budget", () => {
    const estimate = estimateBasePurchaseTotal(5000);
    expect(doesEstimatedTotalOverlap(estimate, "5500", "5600")).toBe(true);
    expect(doesEstimatedTotalOverlap(estimate, "5700", "6000")).toBe(false);
    expect(doesEstimatedTotalOverlap(null, "1", "10000")).toBe(false);
  });
});
