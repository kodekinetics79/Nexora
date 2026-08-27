/* global console */
import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';

const dist = path.resolve('dist');
const html = await readFile(path.join(dist, 'index.html'), 'utf8');
const sources = [...html.matchAll(/(?:src|href)="([^"]+\.js)"/g)].map(match => match[1]);
const initialAssets = [...new Set(sources)];
const forbidden = initialAssets.filter(asset => /(?:charts|xlsx)-vendor/.test(asset));
const sizes = await Promise.all(initialAssets.map(async asset => ({
  asset,
  bytes: (await stat(path.join(dist, asset.replace(/^\//, '')))).size,
})));
const initialBytes = sizes.reduce((total, item) => total + item.bytes, 0);
const baselineBytes = 1_683_028;
const optimizedBytes = 1_315_324;
const maximumBytes = Math.floor(optimizedBytes * 1.1);
const reductionPercent = 100 * (baselineBytes - initialBytes) / baselineBytes;

// A production build must not contain the TanStack Query developer panel. This protects both
// the client-facing UI and cached request data even if somebody accidentally restores an
// unconditional mount in main.tsx later.
const assetDirectory = path.join(dist, 'assets');
const productionJavaScript = (await readdir(assetDirectory))
  .filter(asset => asset.endsWith('.js'));
const devtoolsAssets = [];
for (const asset of productionJavaScript) {
  const source = await readFile(path.join(assetDirectory, asset), 'utf8');
  if (source.includes('Open Tanstack query devtools') || source.includes('TanStack Query Devtools')) {
    devtoolsAssets.push(asset);
  }
}

console.log(`Initial JavaScript: ${initialBytes.toLocaleString()} bytes (optimized budget ${maximumBytes.toLocaleString()}, Gate 5 reduction ${reductionPercent.toFixed(2)}%).`);
if (forbidden.length > 0) {
  throw new Error(`Route-only vendors were eagerly loaded: ${forbidden.join(', ')}`);
}
if (initialBytes > maximumBytes) {
  throw new Error(`Initial JavaScript exceeds the measured Gate 5 budget by ${(initialBytes - maximumBytes).toLocaleString()} bytes.`);
}
if (devtoolsAssets.length > 0) {
  throw new Error(`Production build contains TanStack Query Devtools: ${devtoolsAssets.join(', ')}`);
}
