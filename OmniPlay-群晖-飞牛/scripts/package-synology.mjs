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
const packageIconPath = path.join(rootDir, '1.png');
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
const createNoarchCompatSpk =
  process.env.OMNIPLAY_COMPAT_NOARCH !== '0' && selectedArch.label === 'x64' && spkArch === 'x86_64';
const buildDir = path.join(rootDir, 'build/synology', selectedArch.label);
const publishDir = path.join(buildDir, 'publish');
const dotnetPublishDir = path.relative(rootDir, publishDir);
const packageDir = path.join(buildDir, 'package');
const spkRoot = path.join(buildDir, 'spk-root');
const distDir = path.join(rootDir, 'dist/synology');
const spkPathForArch = (packageArch) => path.join(distDir, `${packageName}-${version}-${packageArch}.spk`);
const prebuiltPublishDir = process.env.OMNIPLAY_PUBLISH_DIR
  ? path.resolve(rootDir, process.env.OMNIPLAY_PUBLISH_DIR)
  : path.join(rootDir, 'server/src/OmniPlay.Api/bin/Release/net10.0', selectedArch.rid);
const mediaToolsSourceDir = process.env.OMNIPLAY_MEDIA_TOOLS_DIR
  ? path.resolve(rootDir, process.env.OMNIPLAY_MEDIA_TOOLS_DIR)
  : path.join(rootDir, 'packaging/media-tools', selectedArch.label);

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
    if (resizeIcon(sourcePath, targetPath, size)) {
      return;
    }

    cpSync(sourcePath, targetPath);
    return;
  }
  writeFileSync(targetPath, createDefaultIcon(size));
}

function resizeIcon(sourcePath, targetPath, size) {
  const attempts = [
    ['sips', ['-z', String(size), String(size), sourcePath, '--out', targetPath]],
    ['magick', [sourcePath, '-resize', `${size}x${size}!`, targetPath]],
    ['convert', [sourcePath, '-resize', `${size}x${size}!`, targetPath]],
  ];

  for (const [command, args] of attempts) {
    const result = spawnSync(command, args, {
      cwd: rootDir,
      env: {
        ...process.env,
        COPY_EXTENDED_ATTRIBUTES_DISABLE: '1',
        COPYFILE_DISABLE: '1',
      },
      stdio: ['ignore', 'ignore', 'ignore'],
    });
    if (result.status === 0 && existsSync(targetPath)) {
      return true;
    }
  }

  return false;
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

function calculateDirectorySize(directory) {
  let totalSize = 0;
  const visit = (absolutePath) => {
    const stats = statSync(absolutePath);
    if (stats.isDirectory()) {
      for (const childName of readdirSync(absolutePath).filter((name) => name !== '.DS_Store')) {
        visit(path.join(absolutePath, childName));
      }
      return;
    }

    if (stats.isFile()) {
      totalSize += stats.size;
    }
  };
  visit(directory);
  return totalSize;
}

function upsertInfoField(info, fieldName, value) {
  const line = `${fieldName}="${value}"`;
  const pattern = new RegExp(`^${fieldName}=.*$`, 'm');
  if (pattern.test(info)) {
    return info.replace(pattern, line);
  }

  return `${info.trimEnd()}\n${line}\n`;
}

function normalizePayloadPermissions(directory) {
  const executableNames = new Set(['OmniPlay.Api', 'createdump', 'ffmpeg', 'ffprobe']);
  const visit = (absolutePath) => {
    const stats = statSync(absolutePath);
    if (stats.isDirectory()) {
      chmodSync(absolutePath, 0o755);
      for (const childName of readdirSync(absolutePath).filter((name) => name !== '.DS_Store')) {
        visit(path.join(absolutePath, childName));
      }
      return;
    }

    if (stats.isFile()) {
      chmodSync(absolutePath, executableNames.has(path.basename(absolutePath)) ? 0o755 : 0o644);
    }
  };
  visit(directory);
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
if (existsSync(mediaToolsSourceDir)) {
  const packageMediaToolsDir = path.join(packageDir, 'tools/ffmpeg');
  console.log(`==> Bundling media tools from ${mediaToolsSourceDir}`);
  rmSync(packageMediaToolsDir, { recursive: true, force: true });
  mkdirSync(path.dirname(packageMediaToolsDir), { recursive: true });
  cpSync(mediaToolsSourceDir, packageMediaToolsDir, { recursive: true });
  for (const executableName of ['ffmpeg', 'ffprobe']) {
    const executablePath = path.join(packageMediaToolsDir, 'bin', executableName);
    if (existsSync(executablePath)) {
      chmodSync(executablePath, 0o755);
    }
  }
}
mkdirSync(path.join(packageDir, 'port_conf'), { recursive: true });
cpSync(path.join(synologyDir, 'port_conf/OmniPlay.sc'), path.join(packageDir, 'port_conf/OmniPlay.sc'));
cpSync(path.join(synologyDir, 'ui'), path.join(packageDir, 'ui'), { recursive: true });
mkdirSync(path.join(packageDir, 'ui/images'), { recursive: true });
for (const size of [16, 24, 32, 48, 64, 72, 96, 128, 256]) {
  copyOrCreateIcon(packageIconPath, path.join(packageDir, `ui/images/omniplay_${size}.png`), size);
}
writeFileSync(
  path.join(packageDir, 'PACKAGE_NOTES.txt'),
  `OmniPlay NAS server payload.

Runtime data path on DSM:
/var/packages/${packageName}/home

Default listen URL:
http://0.0.0.0:45721
`,
);

normalizePayloadPermissions(packageDir);

console.log('==> Creating package.tgz');
const packageTgzPath = path.join(spkRoot, 'package.tgz');
const payloadTgzPath = path.join(spkRoot, 'payload.tgz');
rmSync(packageTgzPath, { force: true });
rmSync(payloadTgzPath, { force: true });
run('tar', [
  '--format',
  'ustar',
  '--no-xattrs',
  '--uid',
  '0',
  '--gid',
  '0',
  '--uname',
  'root',
  '--gname',
  'root',
  '-czf',
  payloadTgzPath,
  '-C',
  packageDir,
  '.',
]);
writeFileSync(
  packageTgzPath,
  gzipSync(createTarBuffer([
    {
      name: 'package/',
      type: 'directory',
      mode: 0o755,
      mtime: Math.floor(Date.now() / 1000),
    },
    {
      name: 'package/PACKAGE_PLACEHOLDER',
      type: 'file',
      mode: 0o644,
      mtime: Math.floor(Date.now() / 1000),
      data: Buffer.from('OmniPlay payload is extracted by preinst from payload.tgz.\n', 'utf8'),
    },
  ]), {
    level: 9,
    mtime: 0,
  }),
);
const packageChecksum = createHash('md5').update(readFileSync(packageTgzPath)).digest('hex');
const packagePayloadSize = calculateDirectorySize(packageDir);
const packageTgzSize = statSync(packageTgzPath).size;
const payloadTgzSize = statSync(payloadTgzPath).size;
const extractSizeKb = Math.ceil((packagePayloadSize + packageTgzSize + payloadTgzSize) / 1024) + 65536;

console.log('==> Creating SPK metadata');
const createInfo = (packageArch) => {
  let info = infoTemplate
    .replaceAll('{{ARCH}}', packageArch)
    .replace(/^version=.*/m, `version="${version}"`);
  info = upsertInfoField(info, 'extractsize', extractSizeKb.toString());
  info = upsertInfoField(info, 'checksum', packageChecksum);
  return info;
};
writeFileSync(path.join(spkRoot, 'INFO'), createInfo(spkArch));

mkdirSync(path.join(spkRoot, 'scripts'), { recursive: true });
mkdirSync(path.join(spkRoot, 'conf'), { recursive: true });
cpSync(path.join(synologyDir, 'scripts'), path.join(spkRoot, 'scripts'), { recursive: true });
for (const scriptName of readdirSync(path.join(spkRoot, 'scripts'))) {
  chmodSync(path.join(spkRoot, 'scripts', scriptName), 0o755);
}
cpSync(path.join(synologyDir, 'conf/privilege'), path.join(spkRoot, 'conf/privilege'));

const iconPath = existsSync(packageIconPath) ? packageIconPath : path.join(synologyDir, 'icons/PACKAGE_ICON.PNG');
const largeIconPath = existsSync(packageIconPath) ? packageIconPath : path.join(synologyDir, 'icons/PACKAGE_ICON_256.PNG');
copyOrCreateIcon(iconPath, path.join(spkRoot, 'PACKAGE_ICON.PNG'), 64);
copyOrCreateIcon(largeIconPath, path.join(spkRoot, 'PACKAGE_ICON_256.PNG'), 256);

const spkEntries = [
  'INFO',
  'package.tgz',
  'payload.tgz',
  'scripts',
  'conf',
  'PACKAGE_ICON.PNG',
  'PACKAGE_ICON_256.PNG',
];

function writeSpk(packageArch) {
  const spkPath = spkPathForArch(packageArch);
  writeFileSync(path.join(spkRoot, 'INFO'), createInfo(packageArch));
  console.log(`==> Packing ${spkPath}`);
  rmSync(spkPath, { force: true });
  rmSync(`${spkPath}.sha256`, { force: true });
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

  return spkPath;
}

const createdSpks = [writeSpk(spkArch)];
if (createNoarchCompatSpk) {
  createdSpks.push(writeSpk('noarch'));
}

for (const createdSpk of createdSpks) {
  console.log(`SPK created: ${createdSpk}`);
}
