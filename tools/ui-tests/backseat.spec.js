const { test, expect } = require('@playwright/test');

// local tweak: the Backseat widget and bar. The demo answers /api/backseat with you in voice in "Evening games", where
// Maria is live and starts and stops speaking every 1.5 s.

const WIDGET = { id: 'b', type: 'backseat', x: 0, y: 0, w: 12, h: 5, options: { control: true } };
const BAR = { id: 'r', type: 'backseat-bar', x: 0, y: 5, w: 12, h: 1, options: { control: true } };

async function show(page, widgets, size = { width: 1340, height: 800 }) {
  await page.setViewportSize(size);
  await page.goto('/panel/');
  await page.evaluate((widgets) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'bs', title: 'Backseat', enabled: true, widgets }] }));
  }, widgets);
  await page.reload();
}

test('the room, who is in voice with the speaking ring, and the rooms to join', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page, [WIDGET]);
  const w = page.locator('.w-backseat');
  await expect(w.locator('.dcf-name')).toHaveText('Evening games');
  const maria = w.locator('.dcf-chip', { hasText: 'Maria' });
  await expect(maria).toHaveClass(/speaking/, { timeout: 5000 });
  await expect(maria).not.toHaveClass(/speaking/, { timeout: 5000 });
  await expect(w.locator('.dcf-chip', { hasText: 'Oleg' })).toHaveClass(/muted/);
  // Kate is in the room but not in voice
  await expect(w.locator('.dcf-hint', { hasText: 'Kate' })).toBeVisible();
  await w.locator('.dcf-chan', { hasText: 'Duo night' }).click();
  await expect(w.locator('.dcf-name')).toHaveText('Duo night');
  await expect(w.locator('.dcf-chan.cur')).toHaveText('Duo night');
  expect(errors).toEqual([]);
});

test('going live from the stream tab, then stopping', async ({ page }) => {
  await show(page, [WIDGET]);
  const w = page.locator('.w-backseat');
  await w.locator('.dcf-call.share').click();
  await w.locator('.dcf-app', { hasText: 'Path of Exile' }).click();
  await expect(w.locator('.dcf-livebox')).toContainText('1080p60');
  await expect(w.locator('.dcf-livebox')).toContainText('Maria, Oleg');
  await expect(w.locator('.dcf-call.share')).toHaveClass(/live/);
  // what is streamed now is marked, and the others switch the stream
  await expect(w.locator('.dcf-app.cur')).toHaveText(/Path of Exile/);
  await w.locator('.dcf-stop').click();
  await expect(w.locator('.dcf-livebox')).toHaveCount(0);
});

test('watching a friend, with sound and full screen', async ({ page }) => {
  await show(page, [WIDGET]);
  const w = page.locator('.w-backseat');
  await w.locator('.dcf-tab', { hasText: 'Streams' }).click();
  const row = w.locator('.bs-watch', { hasText: 'Maria' });
  await row.locator('.bs-wbtn').click();
  await expect(row).toHaveClass(/cur/);
  await expect(row.locator('.bs-wbtn')).toHaveCount(3);
  await row.locator('.bs-wbtn').first().click();
  await expect(row.locator('.bs-wbtn').first()).toHaveClass(/on/);
});

test('without control nothing can be pressed', async ({ page }) => {
  await show(page, [{ ...WIDGET, options: { control: false } }]);
  const w = page.locator('.w-backseat');
  await expect(w.locator('.dcf-call.mute')).toBeDisabled();
  await w.locator('.dcf-call.share').click();
  await expect(w.locator('.dcf-note')).toContainText('Allow control');
});

test('the bar: mute, then it hides once you leave voice and the widget below takes its row', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page, [{ ...BAR, y: 0 }, { id: 'n', type: 'note', x: 0, y: 1, w: 12, h: 2, options: { text: 'below' } }]);
  const bar = page.locator('.w-backseat-bar');
  await expect(bar.locator('.dcb-name')).toHaveText('Evening games');
  await bar.locator('.dcb-btn.mute').click();
  await expect(bar.locator('.dcb-btn.mute')).toHaveClass(/on/);
  await bar.locator('.dcb-btn.leave').click();
  await expect(bar).toBeHidden({ timeout: 8000 });
  expect(errors).toEqual([]);
});

test('screenshots', async ({ page }) => {
  await show(page, [{ ...WIDGET, h: 5 }, { ...BAR, y: 5 }]);
  await expect(page.locator('.w-backseat .dcf-name')).toHaveText('Evening games');
  await page.locator('.w-backseat .dcf-call.share').click();
  await page.locator('.w-backseat .dcf-app', { hasText: 'Path of Exile' }).click();
  await expect(page.locator('.w-backseat .dcf-livebox')).toBeVisible();
  const dir = process.env.BS_SHOTS;
  if (!dir) return;
  await page.locator('.w-backseat .dcf-tab').first().click();
  await page.waitForTimeout(400);
  await page.screenshot({ path: dir + '/backseat-rooms.png' });
  await page.locator('.w-backseat .dcf-tab', { hasText: 'My stream' }).click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: dir + '/backseat-stream.png' });
  await page.locator('.w-backseat .dcf-tab', { hasText: 'Streams' }).click();
  await page.locator('.bs-watch .bs-wbtn').click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: dir + '/backseat-watch.png' });
});
