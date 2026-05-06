# 2026-05-06 全屏详情页进度条间距修正

## 问题

全屏详情页在存在续播进度时，播放进度条离“已播/未播”按钮过远。原因是 `mainPlaybackButtons` 在横向布局中使用了 `maxWidth: .infinity`，按钮组会占满可用宽度，把后面的进度条推到右侧。

## 修正

- 将续播进度横向布局的 `HStack` 间距从 20 收紧到 12。
- 将按钮组的外层布局从撑满宽度改为 `fixedSize(horizontal: true, vertical: false)`，让进度条紧跟按钮组。
- 保留 `ViewThatFits` 的窄屏纵向回退逻辑，避免小窗口下内容挤压。

## 验证

- `xcodebuild -project OmniPlay开源-mac-ARM/OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build` 通过。
