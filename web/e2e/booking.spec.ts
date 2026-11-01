import { Browser, expect, Page, test } from "@playwright/test";

const API = process.env.API_URL ?? "http://localhost:5080";
const ORGANIZER_CODE = process.env.ORGANIZER_CODE ?? "local-organizer-code";

function uniqueEmail() {
  return `${crypto.randomUUID()}@test.example`;
}

async function createEvent(title: string) {
  const register = await fetch(`${API}/api/auth/register`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: uniqueEmail(), password: "correct-horse", organizerCode: ORGANIZER_CODE }),
  });
  expect(register.ok).toBe(true);
  const { token } = await register.json();
  const created = await fetch(`${API}/api/events`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify({
      title, venue: "Test Hall", startsAt: new Date(Date.now() + 86_400_000).toISOString(),
      rows: 2, seatsPerRow: 5, priceCents: 2500,
    }),
  });
  expect(created.ok).toBe(true);
}

async function openEvent(browser: Browser, title: string) {
  const page = await (await browser.newContext()).newPage();
  await page.goto("/");
  await page.getByLabel("Event").selectOption({ label: `${title}, Test Hall` });
  await expect(seat(page, 1, 1)).toBeVisible();
  return page;
}

async function signUp(page: Page) {
  await page.getByLabel("Email").fill(uniqueEmail());
  await page.getByLabel("Password").fill("correct-horse");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByRole("button", { name: "Log in" })).toBeHidden();
}

function seat(page: Page, row: number, number: number) {
  return page.getByRole("button", { name: new RegExp(`^Row ${row} seat ${number},`) });
}

test("a purchase updates another customer's seat map live", async ({ browser }) => {
  const title = `Live ${crypto.randomUUID()}`;
  await createEvent(title);
  const buyer = await openEvent(browser, title);
  const watcher = await openEvent(browser, title);
  await signUp(buyer);

  await seat(buyer, 1, 1).click();
  await seat(buyer, 1, 2).click();
  await expect(buyer.getByText("$50.00")).toBeVisible();
  await buyer.getByRole("button", { name: "Hold seats" }).click();
  await expect(buyer.getByText("2 seat(s) held.")).toBeVisible();

  // The watcher never reloaded. This can only pass through SignalR.
  await expect(seat(watcher, 1, 1)).toHaveClass(/held/);
  await expect(seat(watcher, 1, 2)).toHaveClass(/held/);
  await expect(seat(watcher, 1, 3)).toHaveClass(/available/);

  await buyer.getByRole("button", { name: "Complete demo purchase" }).click();
  await expect(buyer.getByText(/Order #\d+ confirmed: \$50\.00/)).toBeVisible();
  await expect(seat(watcher, 1, 1)).toHaveClass(/sold/);
});

test("released seats become available to others", async ({ browser }) => {
  const title = `Release ${crypto.randomUUID()}`;
  await createEvent(title);
  const buyer = await openEvent(browser, title);
  const watcher = await openEvent(browser, title);
  await signUp(buyer);

  await seat(buyer, 2, 1).click();
  await buyer.getByRole("button", { name: "Hold seats" }).click();
  await expect(seat(watcher, 2, 1)).toHaveClass(/held/);

  await buyer.getByRole("button", { name: "Release seats" }).click();
  await expect(seat(watcher, 2, 1)).toHaveClass(/available/);
});

test("losing a race shows a message and refreshes the map", async ({ browser }) => {
  const title = `Race ${crypto.randomUUID()}`;
  await createEvent(title);
  const first = await openEvent(browser, title);
  const second = await openEvent(browser, title);
  await signUp(first);
  await signUp(second);

  await seat(second, 1, 4).click();
  await seat(first, 1, 4).click();
  await first.getByRole("button", { name: "Hold seats" }).click();
  await expect(first.getByText("1 seat(s) held.")).toBeVisible();

  // The live update removes the seat from the second customer's selection.
  await expect(seat(second, 1, 4)).toHaveClass(/held/);
  await expect(second.getByRole("button", { name: "Hold seats" })).toBeHidden();
});

test("visitors must log in before choosing seats", async ({ browser }) => {
  const title = `Guest ${crypto.randomUUID()}`;
  await createEvent(title);
  const guest = await openEvent(browser, title);
  await expect(seat(guest, 1, 1)).toBeDisabled();
});
