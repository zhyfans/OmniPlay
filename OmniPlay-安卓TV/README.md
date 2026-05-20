# OmniPlay Android

Android phone and Android TV client for the Synology/FnOS OmniPlay server.

The client is intentionally not browser-based. It talks to the NAS server API for
library metadata and uses libmpv to play the original
`/api/playback/files/{id}/stream` URL with HTTP Range and the OmniPlay session
cookie.

## Current scope

- Server URL and account login.
- Poster wall based on `/api/library/items`.
- Detail page based on `/api/library/items/{id}`.
- Android TV Launcher entry with remote-friendly D-pad focus.
- Android phone Launcher entry with touch-first poster grid, detail page, and landscape player.
- Direct original-file playback through libmpv, with saved progress resume.
- Sidecar subtitle discovery through the OmniPlay server and embedded subtitle auto-selection through mpv.
- Playback keys: center/enter toggles pause, left/right seek 10 seconds, back exits playback.
- Phone player controls: touch buttons for pause and +/-10 second seek.

## libmpv binaries

The app depends on `dev.jdtech.mpv:libmpv:0.5.1`, which packages Android
`libmpv.so` and its FFmpeg runtime libraries for `arm64-v8a`, `armeabi-v7a`,
and `x86_64`. `arm64-v8a` is the primary target for most Android TV boxes.

The OmniPlay JNI bridge loads `libmpv.so` dynamically and passes the Android
Surface, Cookie header, subtitle URL, resume position, and direct stream URL to
mpv.

## Build

This Mac currently has the required Android build tooling installed:

- Android Studio and Android Studio bundled JBR.
- Android SDK Platform 35 and 36.1.
- Android SDK Command-line Tools.
- Android SDK Build-Tools 34.0.0, 36.1.0, and 37.0.0.
- Android NDK 27.3.13750724.
- Android SDK CMake 3.22.1.
- Gradle wrapper.
- Android libmpv AAR dependency.

Build from this directory:

```sh
JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
GRADLE_USER_HOME="$PWD/.gradle" \
./gradlew assembleDebug
```

The debug APK is written to:

```text
app/build/outputs/apk/debug/app-debug.apk
```

The current APK contains `libmpv.so`, FFmpeg runtime libraries, and
`libmpvbridge.so` for `arm64-v8a`, `armeabi-v7a`, and `x86_64`.
