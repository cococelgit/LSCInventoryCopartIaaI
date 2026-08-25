import { describe, expect, it } from "vitest";
import { buildFacilityLabel, buildOptionCounts, parseOdometer } from "./Home";

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
