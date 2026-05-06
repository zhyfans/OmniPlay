#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync, gzipSync } from 'node:zlib';

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const packageName = 'OmniPlay';
const infoTemplatePath = path.join(rootDir, 'packaging/synology/INFO.template');
const synologyDir = path.join(rootDir, 'packaging/synology');
const archInput = process.argv[2] ?? process.env.ARCH ?? 'x64';
const buildDuringPublish = process.env.OMNIPLAY_DOTNET_BUILD === '1';
const restoreDuringPublish = process.env.OMNIPLAY_DOTNET_RESTORE === '1';

const infoTemplate = readFileSync(infoTemplatePath, 'utf8');
const templateVersion = infoTemplate.match(/^version="([^"]+)"/m)?.[1];
const version = process.env.VERSION ?? templateVersion;
if (!version) {
  throw new Error(`Unable to read package version from ${infoTemplatePath}`);
}

const archMap = new Map([
  ['x64', { rid: 'linux-x64', spkArch: 'x86_64', label: 'x64' }],
  ['linux-x64', { rid: 'linux-x64', spkArch: 'x86_64', label: 'x64' }],
  ['x86_64', { rid: 'linux-x64', spkArch: 'x86_64', label: 'x64' }],
  ['amd64', { rid: 'linux-x64', spkArch: 'x86_64', label: 'x64' }],
  ['arm64', { rid: 'linux-arm64', spkArch: 'armv8', label: 'arm64' }],
  ['linux-arm64', { rid: 'linux-arm64', spkArch: 'armv8', label: 'arm64' }],
  ['aarch64', { rid: 'linux-arm64', spkArch: 'armv8', label: 'arm64' }],
  ['armv8', { rid: 'linux-arm64', spkArch: 'armv8', label: 'arm64' }],
]);

const selectedArch = archMap.get(archInput);
if (!selectedArch) {
  console.error(`Unsupported architecture: ${archInput}`);
  console.error('Usage: scripts/package-synology.sh [x64|arm64]');
  process.exit(2);
}

const spkArch = process.env.SPK_ARCH ?? selectedArch.spkArch;
const buildDir = path.join(rootDir, 'build/synology', selectedArch.label);
const publishDir = path.join(buildDir, 'publish');
const dotnetPublishDir = path.relative(rootDir, publishDir);
const packageDir = path.join(buildDir, 'package');
const spkRoot = path.join(buildDir, 'spk-root');
const distDir = path.join(rootDir, 'dist/synology');
const spkPath = path.join(distDir, `${packageName}-${version}-${spkArch}.spk`);
const prebuiltPublishDir = process.env.OMNIPLAY_PUBLISH_DIR
  ? path.resolve(rootDir, process.env.OMNIPLAY_PUBLISH_DIR)
  : path.join(rootDir, 'server/src/OmniPlay.Api/bin/Release/net10.0', selectedArch.rid);

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    cwd: options.cwd ?? rootDir,
    env: {
      ...process.env,
      COPY_EXTENDED_ATTRIBUTES_DISABLE: '1',
      COPYFILE_DISABLE: '1',
    },
    stdio: options.stdio ?? ['ignore', 'inherit', 'inherit'],
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
  return result;
}

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let index = 0; index < 8; index += 1) {
      crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
    }
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const typeBuffer = Buffer.from(type, 'ascii');
  const length = Buffer.alloc(4);
  const checksum = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  checksum.writeUInt32BE(crc32(Buffer.concat([typeBuffer, data])), 0);
  return Buffer.concat([length, typeBuffer, data, checksum]);
}

function createDefaultIcon(size) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  const center = (size - 1) / 2;
  const outerRadius = size * 0.5;
  const cornerRadius = size * 0.18;
  const triangle = {
    left: size * 0.39,
    right: size * 0.68,
    top: size * 0.31,
    bottom: size * 0.69,
  };

  for (let y = 0; y < size; y += 1) {
    const rowOffset = y * (size * 4 + 1);
    raw[rowOffset] = 0;
    for (let x = 0; x < size; x += 1) {
      const pixelOffset = rowOffset + 1 + x * 4;
      const nx = Math.abs(x - center) - (outerRadius - cornerRadius);
      const ny = Math.abs(y - center) - (outerRadius - cornerRadius);
      const outsideDistance = Math.hypot(Math.max(nx, 0), Math.max(ny, 0)) + Math.min(Math.max(nx, ny), 0);
      if (outsideDistance > cornerRadius) {
        raw[pixelOffset + 3] = 0;
        continue;
      }

      const shade = Math.round(34 + (x / Math.max(size - 1, 1)) * 28 + (y / Math.max(size - 1, 1)) * 18);
      raw[pixelOffset] = 24;
      raw[pixelOffset + 1] = Math.min(132, shade + 56);
      raw[pixelOffset + 2] = Math.min(176, shade + 96);
      raw[pixelOffset + 3] = 255;

      const inTriangle =
        x >= triangle.left &&
        x <= triangle.right &&
        y >= triangle.top &&
        y <= triangle.bottom &&
        x >= triangle.left + Math.abs(y - center) * 0.56;
      const distanceFromCenter = Math.hypot(x - center, y - center);
      const inRing = distanceFromCenter > size * 0.285 && distanceFromCenter < size * 0.35;

      if (inTriangle || inRing) {
        raw[pixelOffset] = 255;
        raw[pixelOffset + 1] = 255;
        raw[pixelOffset + 2] = 255;
        raw[pixelOffset + 3] = 245;
      }
    }
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

function copyOrCreateIcon(sourcePath, targetPath, size) {
  if (existsSync(sourcePath)) {
    cpSync(sourcePath, targetPath);
    return;
  }
  writeFileSync(targetPath, createDefaultIcon(size));
}

function writeString(buffer, offset, length, value) {
  buffer.fill(0, offset, offset + length);
  Buffer.from(value, 'utf8').copy(buffer, offset, 0, length);
}

function writeOctal(buffer, offset, length, value) {
  const text = value.toString(8).padStart(length - 1, '0').slice(-(length - 1));
  writeString(buffer, offset, length, text);
}

function splitTarPath(entryName) {
  if (Buffer.byteLength(entryName) <= 100) {
    return { name: entryName, prefix: '' };
  }

  const parts = entryName.split('/');
  for (let index = parts.length - 1; index > 0; index -= 1) {
    const prefix = parts.slice(0, index).join('/');
    const name = parts.slice(index).join('/');
    if (Buffer.byteLength(name) <= 100 && Buffer.byteLength(prefix) <= 155) {
      return { name, prefix };
    }
  }

  throw new Error(`Path is too long for ustar: ${entryName}`);
}

function createTarHeader(entry) {
  const header = Buffer.alloc(512);
  const { name, prefix } = splitTarPath(entry.name);
  writeString(header, 0, 100, name);
  writeOctal(header, 100, 8, entry.mode);
  writeOctal(header, 108, 8, 0);
  writeOctal(header, 116, 8, 0);
  writeOctal(header, 124, 12, entry.type === 'file' ? entry.data.length : 0);
  writeOctal(header, 136, 12, entry.mtime ?? Math.floor(Date.now() / 1000));
  header.fill(0x20, 148, 156);
  header[156] = entry.type === 'directory' ? '5'.charCodeAt(0) : '0'.charCodeAt(0);
  writeString(header, 257, 6, 'ustar');
  writeString(header, 263, 2, '00');
  writeString(header, 265, 32, 'root');
  writeString(header, 297, 32, 'root');
  writeOctal(header, 329, 8, 0);
  writeOctal(header, 337, 8, 0);
  writeString(header, 345, 155, prefix);

  let checksum = 0;
  for (const byte of header) {
    checksum += byte;
  }
  const checksumText = checksum.toString(8).padStart(6, '0');
  Buffer.from(`${checksumText}\0 `, 'ascii').copy(header, 148);
  return header;
}

function createTarBuffer(entries) {
  const chunks = [];
  for (const entry of entries) {
    chunks.push(createTarHeader(entry));
    if (entry.type === 'file') {
      chunks.push(entry.data);
      const paddingSize = (512 - (entry.data.length % 512)) % 512;
      if (paddingSize > 0) {
        chunks.push(Buffer.alloc(paddingSize));
      }
    }
  }
  chunks.push(Buffer.alloc(1024));
  return Buffer.concat(chunks);
}

function collectTarEntries(sourceDir, rootEntryNames) {
  const entries = [];
  const visit = (absolutePath, entryName) => {
    const stats = statSync(absolutePath);
    if (stats.isDirectory()) {
      const directoryName = entryName.endsWith('/') ? entryName : `${entryName}/`;
      if (entryName) {
        entries.push({
          name: directoryName,
          type: 'directory',
          mode: 0o755,
          mtime: Math.floor(stats.mtimeMs / 1000),
        });
      }
      for (const childName of readdirSync(absolutePath).filter((name) => name !== '.DS_Store').sort()) {
        visit(path.join(absolutePath, childName), entryName ? `${directoryName}${childName}` : childName);
      }
      return;
    }

    if (!stats.isFile()) {
      throw new Error(`Unsupported package entry type: ${absolutePath}`);
    }

    entries.push({
      name: entryName,
      type: 'file',
      mode: stats.mode & 0o111 ? 0o755 : 0o644,
      mtime: Math.floor(stats.mtimeMs / 1000),
      data: readFileSync(absolutePath),
    });
  };

  for (const entryName of rootEntryNames) {
    visit(path.join(sourceDir, entryName), entryName);
  }

  return entries;
}

console.log('==> Building Web UI');
run('npm', ['--prefix', 'web', 'run', 'build']);

console.log(`==> Preparing server payload (${selectedArch.rid})`);
rmSync(buildDir, { recursive: true, force: true });
mkdirSync(publishDir, { recursive: true });
mkdirSync(packageDir, { recursive: true });
mkdirSync(spkRoot, { recursive: true });
mkdirSync(distDir, { recursive: true });
let payloadSourceDir = prebuiltPublishDir;
if (buildDuringPublish) {
  console.log('    running dotnet publish inside packager');
  run('dotnet', [
    'publish',
    'server/src/OmniPlay.Api/OmniPlay.Api.csproj',
    '-c',
    'Release',
    '-r',
    selectedArch.rid,
    '--self-contained',
    'true',
    ...(restoreDuringPublish ? [] : ['--no-restore']),
    '-o',
    dotnetPublishDir,
    '/p:PublishSingleFile=false',
  ]);
  payloadSourceDir = publishDir;
} else {
  console.log(`    using prebuilt payload: ${payloadSourceDir}`);
  console.log('    run dotnet publish directly first on a fresh machine, or set OMNIPLAY_DOTNET_BUILD=1');
}

if (!existsSync(path.join(payloadSourceDir, 'OmniPlay.Api'))) {
  console.error(`Missing prebuilt OmniPlay.Api executable in ${payloadSourceDir}`);
  process.exit(1);
}

console.log('==> Staging package payload');
cpSync(payloadSourceDir, packageDir, { recursive: true });
const webDistDir = path.join(rootDir, 'web/dist');
const packageWwwroot = path.join(packageDir, 'wwwroot');
if (!existsSync(path.join(webDistDir, 'index.html'))) {
  console.error(`Missing Web build output in ${webDistDir}`);
  process.exit(1);
}
rmSync(packageWwwroot, { recursive: true, force: true });
cpSync(webDistDir, packageWwwroot, { recursive: true });
chmodSync(path.join(packageDir, 'OmniPlay.Api'), 0o755);
writeFileSync(
  path.join(packageDir, 'PACKAGE_NOTES.txt'),
  `OmniPlay NAS server payload.

Runtime data path on DSM:
/var/packages/${packageName}/home

Default listen URL:
http://0.0.0.0:8096
`,
);

console.log('==> Creating package.tgz');
const packageEntries = readdirSync(packageDir).filter((entry) => entry !== '.DS_Store').sort();
const packageTgzPath = path.join(spkRoot, 'package.tgz');
writeFileSync(packageTgzPath, gzipSync(createTarBuffer(collectTarEntries(packageDir, packageEntries)), { level: 9 }));
const packageChecksum = createHash('md5').update(readFileSync(packageTgzPath)).digest('hex');

console.log('==> Creating SPK metadata');
let info = infoTemplate
  .replaceAll('{{ARCH}}', spkArch)
  .replace(/^version=.*/m, `version="${version}"`);
if (info.match(/^checksum=/m)) {
  info = info.replace(/^checksum=.*/m, `checksum="${packageChecksum}"`);
} else {
  info = `${info.trimEnd()}\nchecksum="${packageChecksum}"\n`;
}
writeFileSync(path.join(spkRoot, 'INFO'), info);

mkdirSync(path.join(spkRoot, 'scripts'), { recursive: true });
mkdirSync(path.join(spkRoot, 'conf'), { recursive: true });
cpSync(path.join(synologyDir, 'scripts'), path.join(spkRoot, 'scripts'), { recursive: true });
for (const scriptName of readdirSync(path.join(spkRoot, 'scripts'))) {
  chmodSync(path.join(spkRoot, 'scripts', scriptName), 0o755);
}
cpSync(path.join(synologyDir, 'conf/privilege'), path.join(spkRoot, 'conf/privilege'));

const iconPath = path.join(synologyDir, 'icons/PACKAGE_ICON.PNG');
const largeIconPath = path.join(synologyDir, 'icons/PACKAGE_ICON_256.PNG');
copyOrCreateIcon(iconPath, path.join(spkRoot, 'PACKAGE_ICON.PNG'), 64);
copyOrCreateIcon(largeIconPath, path.join(spkRoot, 'PACKAGE_ICON_256.PNG'), 256);

console.log(`==> Packing ${spkPath}`);
rmSync(spkPath, { force: true });
rmSync(`${spkPath}.sha256`, { force: true });
const spkEntries = ['INFO', 'package.tgz', 'scripts', 'conf', 'PACKAGE_ICON.PNG', 'PACKAGE_ICON_256.PNG'];
writeFileSync(spkPath, createTarBuffer(collectTarEntries(spkRoot, spkEntries)));

const checksum = spawnSync('shasum', ['-a', '256', spkPath], {
  cwd: rootDir,
  encoding: 'utf8',
  env: {
    ...process.env,
    COPY_EXTENDED_ATTRIBUTES_DISABLE: '1',
    COPYFILE_DISABLE: '1',
  },
});
if (checksum.status === 0 && checksum.stdout) {
  writeFileSync(`${spkPath}.sha256`, checksum.stdout);
}

console.log(`SPK created: ${spkPath}`);
