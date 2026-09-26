'use strict';
// Reuse the cover's vector logo verbatim; no separate icon design or remote asset.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
(async () => {
    const root = path.resolve(__dirname, '..');
    const cover = fs.readFileSync(path.join(root, 'promo/cover.html'), 'utf8');
    const defs = cover.match(/<defs>[\s\S]*?<\/defs>/)[0];
    const sizes = [16, 20, 24, 32, 48, 64, 128, 256], frames = [];
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    try {
        const page = await browser.newPage({ deviceScaleFactor: 1 });
        await page.route('**/*', route => route.abort());
        for (const size of sizes) {
            await page.setViewportSize({ width: size, height: size });
            const mark = size <= 32 ? defs.replace('translate(22 22) scale(2.3333)', 'translate(-4 -3) scale(4.3)').replace('stroke-width="2"/>', 'stroke-width="2.3"/>').replace('stroke-width="1.5"', 'stroke-width="1.8"') : defs;
            await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block}</style><svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 100 100">${mark}<use href="#icon" width="100" height="100"/></svg>`);
            frames.push(await page.screenshot({ type: 'png', omitBackground: true }));
        }
    } finally { await browser.close(); }
    const header = Buffer.alloc(6 + frames.length * 16);
    header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
    let offset = header.length;
    frames.forEach((frame, i) => {
        const pos = 6 + i * 16;
        header[pos] = header[pos + 1] = sizes[i] === 256 ? 0 : sizes[i];
        header.writeUInt16LE(1, pos + 4); header.writeUInt16LE(32, pos + 6);
        header.writeUInt32LE(frame.length, pos + 8); header.writeUInt32LE(offset, pos + 12);
        offset += frame.length;
    });
    fs.writeFileSync(path.join(root, 'src/BetterDownload.ico'), Buffer.concat([header, ...frames]));
    console.log('Rendered src/BetterDownload.ico (8 sizes)');
})().catch(error => { console.error(error); process.exitCode = 1; });
