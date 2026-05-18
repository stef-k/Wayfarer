import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  uniqueName
} from './tripEditorTestUtils';

type EditorState = Record<string, any>;

test.describe.serial('Trip Editor rich notes real persistence contract', () => {
  test('metadata rich notes save through the real endpoint and reload as canonical HTML', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);
    const originalState = await loadEditorStateFixture(page) as EditorState;
    const originalMetadata = { ...originalState.metadata };
    const imageUrl = 'https://images.example.test/rich-notes-persistence.png';
    const runText = uniqueName('PW rich notes persisted');
    await routeProxyImage(page, imageUrl);

    try {
      const form = page.locator('#trip-editor-metadata-form');
      const editor = richEditor(form).locator('.ql-editor');
      await editor.click();
      await page.keyboard.press('Control+A');
      await page.keyboard.press('Backspace');
      await page.keyboard.type(runText);
      await page.keyboard.press('Enter');
      await richEditor(form).locator('button.ql-align[value="center"]').click();
      await page.keyboard.type('Centered persisted note');
      await insertImageUrl(form, imageUrl);

      await page.getByRole('button', { name: 'Save & Continue' }).click();
      await expectSaved(page);

      await expectState(page, state => {
        const notes = state.metadata.notesHtml as string;
        expect(notes).toContain(runText);
        expect(notes).toContain('<p class="ql-align-center">Centered persisted note');
        expect(notes).toContain(`src="${imageUrl}"`);
        expect(notes).not.toContain('/Public/ProxyImage');
        expect(notes).not.toContain('<p><br></p>');
      });

      await page.reload();
      await expectMountedWorkspace(page);
      const reloadedForm = page.locator('#trip-editor-metadata-form');
      const reloadedEditor = richEditor(reloadedForm).locator('.ql-editor');
      await expect(reloadedEditor).toContainText(runText);
      await expect(reloadedEditor.locator('p.ql-align-center')).toContainText('Centered persisted note');
      await expect(reloadedEditor.locator('img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=https%3A%2F%2Fimages\.example\.test%2Frich-notes-persistence\.png$/);
    } finally {
      if (!page.isClosed()) {
        await restoreMetadata(page, originalMetadata);
      }
    }
  });
});

async function routeProxyImage(page: Page, url: string): Promise<void> {
  const escaped = encodeURIComponent(url).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/Public/ProxyImage\\?url=${escaped}$`, 'i'), async route => {
    await route.fulfill({
      status: 200,
      contentType: 'image/svg+xml',
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#dbeafe"/><text x="24" y="96" font-family="Arial" font-size="24" fill="#1e293b">Rich notes persistence</text></svg>'
    });
  });
}

function richEditor(form: Locator): Locator {
  return form.locator('.trip-editor-rich-notes');
}

async function insertImageUrl(form: Locator, url: string): Promise<void> {
  await richEditor(form).locator('.ql-image').click();
  const dialog = form.page().getByRole('dialog', { name: 'Insert image URL' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Image URL').fill(url);
  await dialog.getByRole('button', { name: 'Insert Image' }).click();
  await expect(dialog).toHaveCount(0);
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /saved/i }).first()).toBeVisible();
}

async function expectState(page: Page, assertion: (state: EditorState) => void): Promise<void> {
  assertion(await loadEditorStateFixture(page) as EditorState);
}

async function restoreMetadata(page: Page, metadata: Record<string, any>): Promise<void> {
  const token = await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue().catch(() => '');
  if (!token) {
    return;
  }

  const response = await page.request.patch(absoluteUrl(`${editorApiPath}/metadata`), {
    data: {
      name: metadata.name,
      notesHtml: metadata.notesHtml,
      isPublic: metadata.isPublic,
      coverImage: metadata.coverImage,
      center: metadata.center,
      zoom: metadata.zoom
    },
    headers: { RequestVerificationToken: token }
  });
  expect(response.ok(), `metadata cleanup PATCH returned ${response.status()}: ${await response.text()}`).toBeTruthy();
}
