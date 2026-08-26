// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createElement } from "react";
import { afterEach, describe, expect, it } from "vitest";
import VehiclePhotoCarousel from "./VehiclePhotoCarousel";

afterEach(() => cleanup());

describe("VehiclePhotoCarousel", () => {
  it("navigates with controls and horizontal swipe without opening the vehicle", () => {
    const { container } = render(createElement(VehiclePhotoCarousel, {
      photos: ["https://img.test/1.jpg", "https://img.test/2.jpg", "https://img.test/3.jpg"],
      title: "2022 Honda Accord",
      lot: "12345678",
      href: "/vehiculo/12345678",
    }));

    expect(screen.getByAltText(/foto 1 de 3/i)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /foto siguiente/i }));
    expect(screen.getByAltText(/foto 2 de 3/i)).toBeTruthy();

    const carousel = container.querySelector(".browse-photo")!;
    fireEvent.touchStart(carousel, { touches: [{ clientX: 220 }] });
    fireEvent.touchEnd(carousel, { changedTouches: [{ clientX: 120 }] });
    expect(screen.getByAltText(/foto 3 de 3/i)).toBeTruthy();

    const link = screen.getByRole("link", { name: /abrir ficha/i });
    const click = new MouseEvent("click", { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: /foto anterior/i }));
    expect(screen.getByAltText(/foto 2 de 3/i)).toBeTruthy();
  });

  it("does not render navigation controls for a single photo", () => {
    render(createElement(VehiclePhotoCarousel, {
      photos: ["https://img.test/only.jpg"],
      title: "2020 Toyota Camry",
      lot: "87654321",
      href: "/vehiculo/87654321",
    }));

    expect(screen.getByAltText(/foto 1 de 1/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /foto siguiente/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /foto anterior/i })).toBeNull();
  });

  it("falls back to the next reported photo when an image URL fails", () => {
    render(createElement(VehiclePhotoCarousel, {
      photos: ["https://img.test/broken.jpg", "https://img.test/working.jpg"],
      title: "2021 Ford Escape",
      lot: "11223344",
      href: "/vehiculo/11223344",
    }));

    fireEvent.error(screen.getByAltText(/foto 1 de 2/i));
    expect(screen.getByAltText(/foto 1 de 1/i).getAttribute("src")).toBe("https://img.test/working.jpg");
  });
});
