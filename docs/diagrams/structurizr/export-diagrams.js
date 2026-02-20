const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const FILE_PREFIX = 'jim-structurizr-1-';
const IMAGE_VIEW_TYPE = 'Image';

const url = process.argv[2] || 'http://localhost:8085/workspace/diagrams';
const imagesDir = process.argv[3] || path.resolve(__dirname, '../images');
const lightOutputDir = path.join(imagesDir, 'light');
const darkOutputDir = path.join(imagesDir, 'dark');

// Read JIM version from VERSION file
const versionFile = path.resolve(__dirname, '../../../VERSION');
const jimVersion = fs.existsSync(versionFile)
  ? fs.readFileSync(versionFile, 'utf8').trim()
  : null;

/**
 * Injects a "JIM v{version}" label into the SVG metadata area.
 * Structurizr renders three metadata lines at the bottom-left:
 *   - Title (font-size 36px, highest y position)
 *   - Description (font-size 24px)
 *   - Timestamp (font-size 24px, lowest y position)
 * We insert the version between the description and timestamp.
 */
function injectVersion(svg, version, fillColor = '#444444') {
  // Find the timestamp element (last metadata line, contains date/time text)
  const timestampPattern = /<g\s+id="j_\d+"\s+transform="translate\((\d+),(\d+)\)"><g id="v-\d+"><text[^>]*font-size="24px"[^>]*><tspan[^>]*>[A-Z][a-z]+day,\s/;
  const match = svg.match(timestampPattern);
  if (!match) return svg;

  const timestampX = parseInt(match[1]);
  const timestampY = parseInt(match[2]);

  // Place version line below the timestamp (34px lower, matching 24px font spacing)
  const versionY = timestampY + 34;
  const versionElement = `<g transform="translate(${timestampX},${versionY})"><g><text font-size="24px" xml:space="preserve" y="0.8em" font-weight="normal" text-anchor="start" fill="${fillColor}" pointer-events="none" display="block" font-family="Open Sans, Tahoma, Arial"><tspan dy="0" display="block">JIM v${version}</tspan></text></g></g>`;

  // Expand the viewBox height to fit the version line (need ~60px extra)
  let result = svg.replace(/viewBox="(\d+) (\d+) (\d+) (\d+)"/, (m, x, y, w, h) => {
    // Only expand the first (main) viewBox, not icon viewBoxes
    return `viewBox="${x} ${y} ${w} ${parseInt(h) + 60}"`;
  });

  // Insert before the closing </g></g></svg>
  return result.replace(/<\/g><\/g><\/svg>$/, `${versionElement}</g></g></svg>`);
}

/**
 * Opens a page in Structurizr with the specified rendering mode and waits for it to be ready.
 */
async function openDiagramsPage(browser, isDarkMode) {
  const page = await browser.newPage();

  if (isDarkMode) {
    const urlObj = new URL(url);
    await page.setCookie({
      name: 'structurizr.renderingMode',
      value: 'dark',
      domain: urlObj.hostname,
      path: '/',
    });
  }

  await page.goto(url, { waitUntil: 'networkidle0', timeout: 60000 });
  await page.waitForFunction(
    'typeof structurizr !== "undefined" && structurizr.scripting && structurizr.scripting.isDiagramRendered() === true',
    { timeout: 60000 }
  );

  return page;
}

/**
 * Exports all views from a Structurizr page to SVG files.
 */
async function exportViews(page, views, targetDir, isDarkMode) {
  const versionFill = isDarkMode ? '#cccccc' : '#444444';
  const modeLabel = isDarkMode ? 'dark' : 'light';
  let exportCount = 0;

  if (!fs.existsSync(targetDir)) {
    fs.mkdirSync(targetDir, { recursive: true });
  }

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
    const diagramPath = path.join(targetDir, diagramFilename);

    const svgForDiagram = await page.evaluate(() => {
      return structurizr.scripting.exportCurrentDiagramToSVG({ includeMetadata: true });
    });

    const finalDiagram = jimVersion ? injectVersion(svgForDiagram, jimVersion, versionFill) : svgForDiagram;
    fs.writeFileSync(diagramPath, finalDiagram);
    console.log(`    + ${diagramFilename}`);
    exportCount++;

    // Export key SVG (unless it's an image view)
    if (view.type !== IMAGE_VIEW_TYPE) {
      const keyFilename = `${FILE_PREFIX}${view.key}-key.svg`;
      const keyPath = path.join(targetDir, keyFilename);

      const svgForKey = await page.evaluate(() => {
        return structurizr.scripting.exportCurrentDiagramKeyToSVG();
      });

      fs.writeFileSync(keyPath, svgForKey);
      console.log(`    + ${keyFilename}`);
      exportCount++;
    }
  }

  return exportCount;
}

(async () => {
  console.log('Starting SVG diagram export...');
  console.log(`  URL: ${url}`);
  console.log(`  Light output: ${lightOutputDir}`);
  console.log(`  Dark output:  ${darkOutputDir}`);

  const browser = await puppeteer.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox']
  });

  // === Light mode export ===
  console.log('\n  === Light Mode ===');
  console.log('  Opening diagrams page...');
  const lightPage = await openDiagramsPage(browser, false);

  const views = await lightPage.evaluate(() => {
    return structurizr.scripting.getViews();
  });

  let expectedPerMode = 0;
  views.forEach(function(view) {
    if (view.type === IMAGE_VIEW_TYPE) {
      expectedPerMode++;
    } else {
      expectedPerMode += 2; // diagram + key
    }
  });

  const totalExpected = expectedPerMode * 2;
  console.log(`  Found ${views.length} views, exporting ${totalExpected} files (${expectedPerMode} light + ${expectedPerMode} dark)...`);

  const lightCount = await exportViews(lightPage, views, lightOutputDir, false);
  await lightPage.close();

  // === Dark mode export ===
  console.log('\n  === Dark Mode ===');
  console.log('  Opening diagrams page in dark mode...');
  const darkPage = await openDiagramsPage(browser, true);

  const darkCount = await exportViews(darkPage, views, darkOutputDir, true);
  await darkPage.close();

  const totalActual = lightCount + darkCount;
  console.log(`\n  Exported ${totalActual}/${totalExpected} files (${lightCount} light + ${darkCount} dark).`);
  await browser.close();
  console.log('Done.');
})();
