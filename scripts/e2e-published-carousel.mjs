import { chromium } from "playwright-core";

const baseUrl = "https://lsc-inv-revi-zyn4tlbw.manus.space";
const browser = await chromium.launch({
  executablePath: "/usr/bin/chromium",
  headless: true,
  args: ["--no-sandbox", "--disable-dev-shm-usage"],
});

try {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    hasTouch: true,
    isMobile: false,
  });
  const page = await context.newPage();
  await page.goto(baseUrl, { waitUntil: "networkidle", timeout: 120_000 });

  const card = page.locator(".browse-row").first();
  await card.waitFor({ state: "visible", timeout: 120_000 });

  const counter = card.locator(".browse-photo-count");
  const image = card.locator(".browse-photo img");
  const next = card.getByRole("button", { name: /Foto siguiente de/ });
  const previous = card.getByRole("button", { name: /Foto anterior de/ });
  const detailLink = card.locator(".browse-row-details-link");

  const firstCounter = (await counter.textContent())?.trim();
  const firstImage = await image.getAttribute("src");
  if (!firstCounter?.startsWith("1 / ")) throw new Error(`Unexpected initial counter: ${firstCounter}`);
  if (!firstImage?.includes("vis.iaai.com")) throw new Error("First card is not using a real IAAI image");

  await card.hover();
  await next.click();
  await page.waitForFunction(
    () => document.querySelector(".browse-row .browse-photo-count")?.textContent?.trim().startsWith("2 /"),
    undefined,
    { timeout: 10_000 },
  );
  const secondCounter = (await counter.textContent())?.trim();
  const secondImage = await image.getAttribute("src");
  if (secondImage === firstImage) throw new Error("Next control did not change the photo URL");

  await previous.click();
  await page.waitForFunction(
    () => document.querySelector(".browse-row .browse-photo-count")?.textContent?.trim().startsWith("1 /"),
    undefined,
    { timeout: 10_000 },
  );

  const pagesBeforeSwipe = context.pages().length;
  await card.locator(".browse-photo").evaluate((element) => {
    const dispatchTouch = (type, clientX) => {
      const touch = new Touch({ identifier: 1, target: element, clientX, clientY: 160 });
      element.dispatchEvent(new TouchEvent(type, {
        bubbles: true,
        cancelable: true,
        touches: type === "touchend" ? [] : [touch],
        targetTouches: type === "touchend" ? [] : [touch],
        changedTouches: [touch],
      }));
    };
    dispatchTouch("touchstart", 220);
    dispatchTouch("touchend", 40);
  });
  await page.waitForFunction(
    () => document.querySelector(".browse-row .browse-photo-count")?.textContent?.trim().startsWith("2 /"),
    undefined,
    { timeout: 10_000 },
  );
  if (context.pages().length !== pagesBeforeSwipe) throw new Error("Swipe opened the vehicle detail unexpectedly");

  const detailHref = await detailLink.getAttribute("href");
  const detailTarget = await detailLink.getAttribute("target");
  if (!detailHref?.startsWith("/vehiculo/")) throw new Error(`Unexpected detail href: ${detailHref}`);
  if (detailTarget !== "_blank") throw new Error(`Unexpected detail target: ${detailTarget}`);

  const popupPromise = page.waitForEvent("popup", { timeout: 10_000 });
  await detailLink.click();
  const popup = await popupPromise;
  await popup.waitForLoadState("domcontentloaded", { timeout: 30_000 });
  if (!popup.url().includes("/vehiculo/")) throw new Error(`Detail did not open in a new tab: ${popup.url()}`);

  console.log(JSON.stringify({
    baseUrl,
    firstCounter,
    secondCounter,
    swipeCounter: (await counter.textContent())?.trim(),
    realIaaiImagesChanged: secondImage !== firstImage,
    swipeStayedOnList: context.pages().length === pagesBeforeSwipe + 1,
    detailOpenedInNewTab: true,
    detailPathPattern: "/vehiculo/{lot}",
  }, null, 2));
} finally {
  await browser.close();
}
