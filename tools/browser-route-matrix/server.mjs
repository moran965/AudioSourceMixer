import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('.', import.meta.url));
const port = Number(process.argv[2] || 8765);
const contentTypes = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8']
]);

createServer(async (request, response) => {
  try {
    const requestUrl = new URL(request.url || '/', `http://${request.headers.host || '127.0.0.1'}`);
    const relativePath = requestUrl.pathname === '/' ? 'tone.html' : requestUrl.pathname.slice(1);
    if (!['tone.html', 'tone.js', 'tone.css'].includes(relativePath)) {
      response.writeHead(404).end('Not found');
      return;
    }
    const body = await readFile(join(root, relativePath));
    response.writeHead(200, {
      'Content-Type': contentTypes.get(extname(relativePath)) || 'application/octet-stream',
      'Cache-Control': 'no-store'
    });
    response.end(body);
  } catch (error) {
    response.writeHead(500).end(error instanceof Error ? error.message : String(error));
  }
}).listen(port, '127.0.0.1', () => {
  console.log(`Audio Source Mixer browser route matrix: http://127.0.0.1:${port}/`);
});
