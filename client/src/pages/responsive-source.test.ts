import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("responsive inventory styles", () => {
  it("stacks pagination and hides the desktop-only audit shortcut on mobile", () => {
    const css = readFileSync(new URL("./home-filters.css", import.meta.url), "utf8");
    const mobileBlock = css.slice(css.lastIndexOf("@media (max-width: 760px)"));
    expect(mobileBlock).toContain(".inventory-pagination");
    expect(mobileBlock).toContain("flex-direction: column");
    expect(mobileBlock).toContain(".browse-internal-link");
    expect(mobileBlock).toContain("display: none");
  });
});
