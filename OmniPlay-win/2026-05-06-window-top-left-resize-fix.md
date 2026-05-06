# Windows 窗口左上角缩放修复

## 问题

无边框主窗口已加入自定义缩放热区后，底部角落可以拖动缩放，但顶部控制层覆盖了上边缘和上角热区，导致左上角无法触发缩放拖动。

## 修复

- 在 `MainWindow` 注册窗口级 `PointerPressed` 隧道路由处理，优先根据鼠标位置判断是否位于缩放边框或四角。
- 保留原有边框热区，同时让顶部控制条区域内的外沿点击优先进入 `BeginResizeDrag`，避免被窗口拖动逻辑吞掉。
- 缩放在播放器覆盖层打开或窗口非 Normal 状态时仍保持禁用。

## 验证

- `dotnet build OmniPlay.Windows.slnx -p:EnableWindowsTargeting=true -p:NuGetAudit=false`
