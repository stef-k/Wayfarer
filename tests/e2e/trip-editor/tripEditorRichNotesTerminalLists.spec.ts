import { expect, test, type Locator, type Page } from '@playwright/test';
import { absoluteUrl, editorPath, expectMountedWorkspace, signIn } from './tripEditorTestUtils';

test.describe('Trip Editor terminal rich-note lists', () => {
  test('ordered and bullet lists retain normal second-Enter, Backspace, and Delete behavior', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const host = page.locator('#trip-editor-metadata-form .trip-editor-rich-notes');
    const editor = host.locator('.ql-editor');
    for (const kind of ['ordered', 'bullet']) {
      await expectSecondEnterExit(page, host, editor, kind);
      await expectBackspaceRemoval(page, host, editor, kind);
      await expectDeletePreservesQuillExit(page, host, editor, kind);
    }
  });

  test('client canonicalization covers terminal cleanup without removing meaningful structure', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);
    const cases = canonicalCases();
    const results = await page.evaluate(async values => {
      const { normalizeNotesHtml } = await import('http://localhost:5173/ClientApps/trip-editor/src/notes/notesHtml.ts');
      return values.map(value => normalizeNotesHtml(value.input));
    }, cases);
    expect(results).toEqual(cases.map(value => value.expected));
  });
});

function canonicalCases(): Array<{ input: string; expected: string }> {
  return [
    { input: '<ol><li data-list="ordered"><br></li></ol>', expected: '' },
    { input: '<ol><li data-list="ordered">One</li><li data-list="ordered"><br></li><li data-list="ordered"><strong> </strong><br></li></ol>', expected: '<ol><li data-list="ordered">One</li></ol>' },
    { input: '<ol><li data-list="ordered">One</li><li data-list="ordered"><br></li><li data-list="ordered">Three</li></ol>', expected: '<ol><li data-list="ordered">One</li><li data-list="ordered"><br></li><li data-list="ordered">Three</li></ol>' },
    { input: '<ol><li data-list="ordered">One</li><li data-list="ordered"><br></li></ol><p>After</p>', expected: '<ol><li data-list="ordered">One</li><li data-list="ordered"><br></li></ol><p>After</p>' },
    { input: '<ul><li data-list="bullet"><a href="https://example.test">Visible link</a></li><li data-list="bullet"><br></li></ul>', expected: '<ul><li data-list="bullet"><a href="https://example.test">Visible link</a></li></ul>' },
    { input: '<ul><li data-list="bullet"><img src="https://example.test/a.jpg"></li><li data-list="bullet"><br></li></ul>', expected: '<ul><li data-list="bullet"><img src="https://example.test/a.jpg"></li></ul>' },
    { input: '<p>Before</p><ol><li data-list="ordered">Item</li></ol><h2>After</h2>', expected: '<p>Before</p><ol><li data-list="ordered">Item</li></ol><h2>After</h2>' }
  ];
}

async function expectSecondEnterExit(page: Page, host: Locator, editor: Locator, kind: string): Promise<void> {
  await startSingleItemList(page, host, editor, kind, `${kind} second Enter`);
  await page.keyboard.press('Enter');
  await page.keyboard.press('Enter');
  await expectSingleMeaningfulItem(editor, kind, `${kind} second Enter`);
  await expect(editor.locator('p').last()).toBeEmpty();
}

async function expectBackspaceRemoval(page: Page, host: Locator, editor: Locator, kind: string): Promise<void> {
  await startSingleItemList(page, host, editor, kind, `${kind} Backspace`);
  await page.keyboard.press('Enter');
  await page.keyboard.press('Backspace');
  await expectSingleMeaningfulItem(editor, kind, `${kind} Backspace`);
}

async function expectDeletePreservesQuillExit(page: Page, host: Locator, editor: Locator, kind: string): Promise<void> {
  await startSingleItemList(page, host, editor, kind, `${kind} Delete`);
  await page.keyboard.press('Enter');
  await page.keyboard.press('Delete');
  await expect(editor.locator(`li[data-list="${kind}"]`)).toHaveCount(2);
  await page.keyboard.press('Enter');
  await expectSingleMeaningfulItem(editor, kind, `${kind} Delete`);
}

async function startSingleItemList(page: Page, host: Locator, editor: Locator, kind: string, text: string): Promise<void> {
  await editor.click();
  await page.keyboard.press('Control+A');
  await page.keyboard.press('Backspace');
  await host.locator(`button.ql-list[value="${kind}"]`).click();
  await page.keyboard.type(text);
}

async function expectSingleMeaningfulItem(editor: Locator, kind: string, text: string): Promise<void> {
  const items = editor.locator(`li[data-list="${kind}"]`);
  await expect(items).toHaveCount(1);
  await expect(items.first()).toContainText(text);
}
