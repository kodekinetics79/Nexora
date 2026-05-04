const fs = require('fs');
const path = require('path');

const i18nContent = fs.readFileSync('src/i18n.ts', 'utf8');
const enResourceMatch = i18nContent.match(/en:\s*\{\s*translation:\s*\{([\s\S]*?)\}\s*\}/);

if (!enResourceMatch) {
  console.log('Could not find en translations in i18n.ts');
  process.exit(1);
}

const existingKeys = new Set();
const keyRegex = /\"([a-zA-Z0-9_]+)\"\s*:/g;
let match;
while ((match = keyRegex.exec(enResourceMatch[1])) !== null) {
  existingKeys.add(match[1]);
}

console.log(`Found ${existingKeys.size} existing keys.`);

const allFiles = [];
function readDir(dir) {
  const files = fs.readdirSync(dir);
  for (const file of files) {
    const fullPath = path.join(dir, file);
    const stat = fs.statSync(fullPath);
    if (stat.isDirectory()) {
      readDir(fullPath);
    } else if (fullPath.endsWith('.tsx') || fullPath.endsWith('.ts')) {
      allFiles.push(fullPath);
    }
  }
}

readDir('src');

const usedKeys = new Set();
const tRegex = /t\(['\"]([a-zA-Z0-9_]+)['\"]\)/g;

for (const file of allFiles) {
  const content = fs.readFileSync(file, 'utf8');
  let tMatch;
  while ((tMatch = tRegex.exec(content)) !== null) {
    usedKeys.add(tMatch[1]);
  }
}

console.log(`Found ${usedKeys.size} used keys in src folder.`);

const missingKeys = [];
for (const key of usedKeys) {
  if (!existingKeys.has(key)) {
    missingKeys.push(key);
  }
}

console.log('Missing Keys:');
console.log(missingKeys.join('\n'));
