'use strict';
// Code-rendered cover, adapted from NCM-BetterDownload's promo/preview.html.
// Needs Playwright and Microsoft Edge. No remote assets or user files are used.
const { chromium } = require('playwright');
const path = require('path');
const { pathToFileURL } = require('url');
const fs = require('fs');

(async () => {
    const root = path.resolve(__dirname, '..');
    const output = path.join(root, 'docs', 'images');
    fs.mkdirSync(output, { recursive: true });
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage({ viewport: { width: 880, height: 440 }, deviceScaleFactor: 2 });
        await page.route('**/*', route => route.request().url().startsWith('file:') ? route.continue() : route.abort());
        await page.goto(pathToFileURL(path.join(root, 'promo', 'cover.html')).href);
        await page.evaluate(() => document.fonts.ready);
        await page.screenshot({ path: path.join(output, 'cover.jpg'), type: 'jpeg', quality: 100 });
        console.log('Rendered docs/images/cover.jpg');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
