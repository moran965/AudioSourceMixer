import { createServer } from 'node:http';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const page = '/tests/browser-extension-tests/browser-equalizer-runtime.html';
const browsers = [
  ['Chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'],
  ['Edge', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe']
];
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8' };

const server = createServer(async (request, response) => {
  try {
    const relative = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname).replace(/^\/+/, '');
    const path = resolve(root, relative);
    if (path !== root && !path.startsWith(`${root}${sep}`)) throw new Error('path leaves repository');
    const content = await readFile(path);
    response.writeHead(200, { 'Content-Type': mime[extname(path)] || 'application/octet-stream' });
    response.end(content);
  } catch (error) {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    response.end(error.message);
  }
});

await new Promise((done) => server.listen(0, '127.0.0.1', done));
const { port } = server.address();
const results = [];
try {
  for (const [name, executable] of browsers) {
    let metrics = null;
    for (let attempt = 1; attempt <= 3 && metrics === null; attempt++) {
      const profile = await mkdtemp(`${tmpdir()}${sep}AudioSourceMixer-${name}-EQ-`);
      try {
        const output = await run(executable, [
          '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
          `--user-data-dir=${profile}`, '--virtual-time-budget=30000', '--dump-dom',
          `http://127.0.0.1:${port}${page}`
        ]);
        const match = output.match(/PASS (\{[^<]+\})/u);
        if (!match) {
          const state = output.match(/<pre id="result">([^<]*)<\/pre>/u)?.[1] || 'result element missing';
          if (state === 'WAIT' && attempt < 3) {
            console.warn(`${name} browser-engine module load was not ready on attempt ${attempt}; retrying with a fresh profile.`);
            continue;
          }
          throw new Error(`${name} browser-engine EQ check failed: ${state}`);
        }
        metrics = JSON.parse(match[1].replaceAll('&quot;', '"').replaceAll('&amp;', '&'));
      } finally { await rm(profile, { recursive: true, force: true }); }
    }
    results.push({ browser: name, status: 'passed', volumeRatio: metrics.volumeRatio, leftLeakRatio: metrics.leftLeakRatio });
  }
} finally { await new Promise((done) => server.close(done)); }

for (const result of results) console.log(`${result.browser} Web Audio EQ runtime passed: volumeRatio=${result.volumeRatio}; leftLeakRatio=${result.leftLeakRatio}`);

function run(executable, args) {
  return new Promise((resolveRun, rejectRun) => {
    const child = spawn(executable, args, { windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8'); child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.setEncoding('utf8'); child.stderr.on('data', (chunk) => { stderr += chunk; });
    const timeout = setTimeout(() => { child.kill(); rejectRun(new Error('Browser EQ runtime check timed out.')); }, 60000);
    child.on('error', (error) => { clearTimeout(timeout); rejectRun(error); });
    child.on('close', (code) => {
      clearTimeout(timeout);
      if (code !== 0) rejectRun(new Error(`Browser exited ${code}: ${stderr.slice(-1000)}`));
      else resolveRun(stdout);
    });
  });
}
