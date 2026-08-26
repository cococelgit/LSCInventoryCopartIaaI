import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("responsive inventory styles", () => {
  it("stacks pagination and hides the desktop-only audit shortcut on mobile", () => {
    const css = readFileSync(new URL("./home-filters.css", import.meta.url), "utf8");
    expect(css).toContain("@media (max-width: 760px)");
    expect(css).toContain(".inventory-pagination");
    expect(css).toContain("flex-direction: column");
    expect(css).toContain(".browse-internal-link");
    expect(css).toContain("display: none");
    expect(css).toContain(".browse-photo-controls");
    expect(css).toContain("opacity: .9");
  });
});
