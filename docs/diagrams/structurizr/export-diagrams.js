const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const FILE_PREFIX = 'jim-structurizr-1-';
const SVG_FORMAT = 'svg';
const IMAGE_VIEW_TYPE = 'Image';

const url = process.argv[2] || 'http://localhost:8085/workspace/diagrams';
const outputDir = process.argv[3] || path.resolve(__dirname, '../images');

let expectedNumberOfExports = 0;
let actualNumberOfExports = 0;

(async () => {
  console.log('Starting SVG diagram export...');
  console.log(`  URL: ${url}`);
  console.log(`  Output: ${outputDir}`);

  if (!fs.existsSync(outputDir)) {
    fs.mkdirSync(outputDir, { recursive: true });
  }

  const browser = await puppeteer.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });
  const page = await browser.newPage();

  // Navigate to diagrams page and wait for full load
  console.log('  Opening diagrams page...');
  await page.goto(url, { waitUntil: 'networkidle0', timeout: 60000 });
  console.log('  Waiting for diagram to render...');
  await page.waitForFunction(
    'typeof structurizr !== "undefined" && structurizr.scripting && structurizr.scripting.isDiagramRendered() === true',
    { timeout: 60000 }
  );

  // Get all views
  const views = await page.evaluate(() => {
    return structurizr.scripting.getViews();
  });

  views.forEach(function(view) {
    if (view.type === IMAGE_VIEW_TYPE) {
      expectedNumberOfExports++;
    } else {
      expectedNumberOfExports += 2; // diagram + key
    }
  });

  console.log(`  Found ${views.length} views, exporting ${expectedNumberOfExports} files...`);

  for (let i = 0; i < views.length; i++) {
    const view = views[i];

    await page.evaluate((v) => {
      structurizr.scripting.changeView(v.key);
    }, view);

    await page.waitForFunction(
      'structurizr.scripting.isDiagramRendered() === true',
      { timeout: 30000 }
    );

    // Export diagram SVG
    const diagramFilename = `${FILE_PREFIX}${view.key}.svg`;
    const diagramPath = path.join(outputDir, diagramFilename);

    const svgForDiagram = await page.evaluate(() => {
      return structurizr.scripting.exportCurrentDiagramToSVG({ includeMetadata: true });
    });

    fs.writeFileSync(diagramPath, svgForDiagram);
    console.log(`  + ${diagramFilename}`);
    actualNumberOfExports++;

    // Export key SVG (unless it's an image view)
    if (view.type !== IMAGE_VIEW_TYPE) {
      const keyFilename = `${FILE_PREFIX}${view.key}-key.svg`;
      const keyPath = path.join(outputDir, keyFilename);

      const svgForKey = await page.evaluate(() => {
        return structurizr.scripting.exportCurrentDiagramKeyToSVG();
      });

      fs.writeFileSync(keyPath, svgForKey);
      console.log(`  + ${keyFilename}`);
      actualNumberOfExports++;
    }
  }

  console.log(`  Exported ${actualNumberOfExports}/${expectedNumberOfExports} files.`);
  await browser.close();
  console.log('Done.');
})();
