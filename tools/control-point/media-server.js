// Minimal media origin server used to verify that the renderer can pull media over HTTP
// the way a real DLNA control point serves it (including Range requests).
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const root = process.argv[2] || process.cwd();
const port = Number(process.argv[3] || 18777);

const types = {
  ".mp4": "video/mp4",
  ".m4v": "video/mp4",
  ".mkv": "video/x-matroska",
  ".webm": "video/webm",
  ".mp3": "audio/mpeg",
  ".m4a": "audio/mp4",
  ".flac": "audio/flac",
  ".wav": "audio/wav",
};

const server = http.createServer((req, res) => {
  const name = decodeURIComponent(new URL(req.url, "http://localhost").pathname).slice(1);
  const file = path.join(root, name);

  if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404, { "content-type": "text/plain" });
    res.end("not found");
    return;
  }

  const size = fs.statSync(file).size;
  const type = types[path.extname(file).toLowerCase()] || "application/octet-stream";
  const range = req.headers.range;

  if (range) {
    const match = /^bytes=(\d*)-(\d*)$/.exec(range);
    const start = match && match[1] ? Number(match[1]) : 0;
    const end = match && match[2] ? Number(match[2]) : size - 1;
    if (start >= size || end >= size || start > end) {
      res.writeHead(416, { "content-range": `bytes */${size}` });
      res.end();
      return;
    }

    res.writeHead(206, {
      "content-type": type,
      "content-length": end - start + 1,
      "content-range": `bytes ${start}-${end}/${size}`,
      "accept-ranges": "bytes",
    });
    fs.createReadStream(file, { start, end }).pipe(res);
    return;
  }

  res.writeHead(200, {
    "content-type": type,
    "content-length": size,
    "accept-ranges": "bytes",
  });
  fs.createReadStream(file).pipe(res);
});

server.listen(port, "0.0.0.0", () => {
  console.log(`media server listening on http://0.0.0.0:${port} root=${root}`);
});
