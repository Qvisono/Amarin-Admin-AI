// Рендер SVG и HTML в PNG через Chromium (Playwright): node render.mjs jobs.json
// jobs.json: [{ "svg": "a.svg", "out": "a-256.png", "size": 256 },
//             { "html": "sheet.html", "out": "sheet.png", "width": 1200, "height": 800, "scale": 2 }]
import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Playwright берём из локального node_modules, а нет его — из глобального (npm root -g).
function loadPlaywright() {
  const local = createRequire(import.meta.url);
  try {
    return local('playwright');
  } catch {
    const root = execSync('npm root -g', { encoding: 'utf8' }).trim();
    return createRequire(path.join(root, 'noop.js'))('playwright');
  }
}

const { chromium } = loadPlaywright();
const jobs = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const browser = await chromium.launch();
try {
  for (const job of jobs) {
    if (job.svg) {
      const size = job.size;
      const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
      const svg = readFileSync(job.svg, 'utf8');
      await page.setContent(`<!doctype html><html><head><style>
        html,body{margin:0;padding:0;background:transparent;overflow:hidden}
        svg{display:block;width:${size}px;height:${size}px}
      </style></head><body>${svg}</body></html>`);
      await page.screenshot({ path: job.out, omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
      await page.close();
    } else if (job.html) {
      const page = await browser.newPage({
        viewport: { width: job.width, height: job.height },
        deviceScaleFactor: job.scale ?? 1,
      });
      await page.goto(pathToFileURL(path.resolve(job.html)).href);
      await page.waitForTimeout(150);
      await page.screenshot({ path: job.out, fullPage: true });
      await page.close();
    }
  }
} finally {
  await browser.close();
}
