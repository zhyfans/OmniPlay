# 2026-05-06 窗口缩放与播放卡顿修正

## 问题

Windows 版主窗口使用无系统边框窗口，但只实现了标题栏拖动，没有实现四边和四角的缩放拖拽，所以窗口不能自由调整大小。播放打开和关闭时，libmpv 初始化、加载、销毁等操作同步跑在调用路径上，容易造成界面短暂停顿。

## 修正

- 在 `MainWindow` 增加透明的四边和四角缩放热区，通过 Avalonia `BeginResizeDrag` 调用系统缩放逻辑，并显式保留 `CanResize=true`。
- 在 `MpvPlayer` 中加入串行 `SemaphoreSlim`，将打开、停止、状态读取和常用控制命令放到后台任务执行，避免 UI 线程直接等待 libmpv 原生调用。
- `OpenAsync` 内部负责后台初始化 libmpv，`PlayerViewModel` 不再同步初始化播放器。
- 独立播放窗口返回主界面时先退出全屏并隐藏窗口，再停止播放器和持久化播放进度，降低关闭播放页的感知停顿。

## 验证

- `dotnet build OmniPlay-win/OmniPlay.Windows.slnx -p:EnableWindowsTargeting=true -p:NuGetAudit=false` 通过。
- `dotnet test OmniPlay-win/OmniPlay.Windows.slnx --filter FullyQualifiedName~PlayerViewModelTests -p:EnableWindowsTargeting=true -p:NuGetAudit=false` 通过。
- `dotnet test OmniPlay-win/OmniPlay.Windows.slnx --filter OpenStandalonePrimaryCommand -p:EnableWindowsTargeting=true -p:NuGetAudit=false` 通过。
- `dotnet test OmniPlay-win/OmniPlay.Windows.slnx --filter Overlay -p:EnableWindowsTargeting=true -p:NuGetAudit=false` 通过。
- 整个 `PosterWallViewModelTests` 仍有一个既有的刮削缩略图用例失败：`ApplyDetailMetadataCandidateCommand_ScrapesTvShowThumbnails`，失败位置 `PosterWallViewModelTests.cs:532`，与本次窗口缩放和播放打开/关闭路径无关。
