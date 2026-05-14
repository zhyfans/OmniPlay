# OmniPlay Docker 打包和运行说明

本文档用于阶段 6 的 Docker / 飞牛 Docker 验证。

## 构建镜像

默认构建 x64 镜像：

```sh
./scripts/package-docker.sh x64
```

构建 ARM64 镜像：

```sh
IMAGE=omniplay-nas:arm64 ./scripts/package-docker.sh arm64
```

脚本会执行：

1. `npm --prefix web run build`
2. `dotnet publish server/src/OmniPlay.Api/OmniPlay.Api.csproj`
3. 将 `web/dist` 覆盖到发布目录的 `wwwroot`
4. 使用 `packaging/docker/Dockerfile` 构建镜像

镜像内包含 `ffmpeg` 和 `ffprobe`，运行时默认监听 `8096`。

## 运行容器

```sh
docker run --rm \
  --name omniplay-nas \
  -p 8096:8096 \
  -e OMNIPLAY_APP_ROOT=/var/lib/omniplay \
  -v "$PWD/data:/var/lib/omniplay" \
  -v "/volume1:/media/volume1:ro" \
  omniplay-nas:dev
```

浏览器打开：

```text
http://NAS_IP:8096
```

## Docker Compose

`packaging/docker/compose.yml` 默认使用本地镜像 `omniplay-nas:dev`：

```sh
docker compose -f packaging/docker/compose.yml up -d
```

运行前需要先执行 `./scripts/package-docker.sh x64` 或手动准备同名镜像。

## 数据和媒体挂载

- `/var/lib/omniplay`：数据库、设置、海报、缩略图、HLS 和 WebDAV 缓存。
- `/media/volume1`：示例媒体只读挂载，对应群晖 `/volume1`。
- 飞牛或普通 Linux 可以把媒体目录挂到 `/media/<name>`，然后在 Web UI 中添加该路径。

## fnOS 当前策略

当前优先用 Docker 镜像在飞牛上验证服务端、网页播放、扫描、刮削和 ffmpeg 能力。

原生 `.fpk` 仍应保持薄封装：只负责安装、权限、端口、启动停止和升级迁移，业务逻辑继续复用同一套 Docker/Linux 服务端产物。
