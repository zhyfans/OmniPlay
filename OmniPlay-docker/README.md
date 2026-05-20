# OmniPlay Docker

这是从 `OmniPlay-群晖-飞牛` 套件版拆出的 Docker 版，包含同一套服务端、Web UI、刮削、媒体库扫描、WebDAV、本地目录浏览、HLS 播放和 FFmpeg 转码能力。

## 快速运行

```sh
cd OmniPlay-docker
docker compose up -d --build
```

默认访问地址：

```text
http://NAS_IP:45721
```

首次打开后注册管理员账号。

## 目录挂载

`compose.yml` 默认挂载：

- `./data:/var/lib/omniplay`：数据库、设置、海报、缩略图、HLS 和 WebDAV 缓存。
- `/volume1:/media/volume1:ro`：媒体目录，只读挂载。

普通 Linux、飞牛或其他 NAS 需要把 `compose.yml` 里的左侧路径改成实际媒体目录，例如：

```yaml
volumes:
  - ./data:/var/lib/omniplay
  - /mnt/media:/media/video:ro
```

然后在 OmniPlay Web UI 中添加容器内路径，例如 `/media/video`。

## 构建镜像

当前目录的 `Dockerfile` 是多阶段构建，会自动完成 Web UI 构建和 .NET 服务端发布：

```sh
./scripts/build-image.sh
```

指定架构：

```sh
IMAGE=omniplay-docker:arm64 ./scripts/build-image.sh arm64
IMAGE=omniplay-docker:x64 ./scripts/build-image.sh x64
```

## 硬件转码

容器内默认安装系统 `ffmpeg`、`ffprobe`、VAAPI 基础库、Intel media driver、libvpl、Mesa VA 驱动和中文字体，并默认按 Intel VAAPI/QSV 硬解配置启动。宿主机需要存在 `/dev/dri`，`compose.yml` 默认挂载：

```yaml
devices:
  - /dev/dri:/dev/dri
environment:
  OMNIPLAY_VAAPI_DEVICE: /dev/dri/renderD128
  OMNIPLAY_ENABLE_QSV: "1"
```

如果宿主机没有 `/dev/dri` 或不需要硬解，删除 `compose.yml` 中的 `devices` 段，并移除 `OMNIPLAY_VAAPI_DEVICE`、`OMNIPLAY_ENABLE_QSV`。

## 常用环境变量

- `OMNIPLAY_APP_ROOT`：容器内数据根目录，默认 `/var/lib/omniplay`。
- `OMNIPLAY_LOCAL_SHARE_ROOTS`：本地目录浏览根，默认 `/media`。
- `OMNIPLAY_TMDB_ACCESS_TOKEN` / `OMNIPLAY_TMDB_API_KEY`：可选 TMDB 凭据。
- `OMNIPLAY_FFMPEG_PATH` / `OMNIPLAY_FFPROBE_PATH`：FFmpeg 工具路径。
- `OMNIPLAY_SCAN_PROBE_CONCURRENCY`：扫描时媒体探测并发数。

## 和套件版的关系

Docker 版保留套件版业务逻辑，去掉 DSM/fnOS 原生安装、端口注册、套件生命周期脚本等包装层。运行期依赖通过 Docker 环境变量、卷挂载和容器内 FFmpeg 提供。
