package com.omniplay.tv.player;

import android.os.Handler;
import android.os.Looper;
import android.view.Surface;

public final class MpvPlayer {
    public interface PlaybackEventListener {
        void onPlaybackStarted();

        void onPlaybackError(String message);
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    private long handle;
    private boolean initialized;
    private boolean paused;
    private boolean directPlaybackMode;
    private String lastError;
    private PlaybackEventListener playbackEventListener;
    private final MpvBridge.EventListener nativeEventListener = (eventName, detail) -> main.post(() -> {
        if ("playback-restart".equals(eventName)) {
            PlaybackEventListener listener = playbackEventListener;
            if (listener != null) {
                listener.onPlaybackStarted();
            }
        } else if ("error".equals(eventName)) {
            if (detail != null && !detail.isEmpty()) {
                lastError = detail;
            }
            PlaybackEventListener listener = playbackEventListener;
            if (listener != null) {
                listener.onPlaybackError(lastError == null || lastError.isEmpty() ? "播放失败。" : lastError);
            }
        }
    });

    private boolean ensureHandle() {
        if (handle != 0) {
            return true;
        }

        handle = MpvBridge.nativeCreate();
        if (handle == 0) {
            lastError = "无法创建 libmpv 播放器。";
            return false;
        }
        MpvBridge.nativeSetPlaybackMode(handle, directPlaybackMode);
        MpvBridge.nativeSetEventListener(handle, nativeEventListener);
        return true;
    }

    public void setPlaybackEventListener(PlaybackEventListener listener) {
        playbackEventListener = listener;
        if (handle != 0) {
            MpvBridge.nativeSetEventListener(handle, listener == null ? null : nativeEventListener);
        }
    }

    public void setDirectPlaybackMode(boolean enabled) {
        directPlaybackMode = enabled;
        if (handle != 0 && !initialized) {
            MpvBridge.nativeSetPlaybackMode(handle, enabled);
        }
    }

    public boolean initialize() {
        if (initialized) {
            return true;
        }

        try {
            initialized = ensureHandle() && MpvBridge.nativeInitialize(handle);
            if (!initialized) {
                lastError = "无法初始化 libmpv，请确认 APK 内包含当前 ABI 的 libmpv.so。";
                if (handle != 0) {
                    MpvBridge.nativeDestroy(handle);
                    handle = 0;
                }
            }
            return initialized;
        } catch (UnsatisfiedLinkError error) {
            lastError = "无法加载 mpvbridge/libmpv：" + error.getMessage();
            return false;
        }
    }

    public void attachSurface(Surface surface, int width, int height) {
        try {
            if (!ensureHandle()) {
                return;
            }
            MpvBridge.nativeAttachSurface(handle, surface, Math.max(0, width), Math.max(0, height));
            initialize();
        } catch (UnsatisfiedLinkError error) {
            lastError = "无法加载 mpvbridge/libmpv：" + error.getMessage();
        }
    }

    public void detachSurface() {
        if (initialized) {
            MpvBridge.nativeDetachSurface(handle);
        }
    }

    public boolean load(String url, String cookieHeader, double startSeconds) {
        if (!initialize()) {
            return false;
        }

        paused = false;
        boolean loaded = MpvBridge.nativeLoad(handle, url, cookieHeader, "OmniPlay-Android/0.1", startSeconds);
        if (!loaded) {
            String detail = MpvBridge.nativeLastError(handle);
            lastError = detail == null || detail.isEmpty()
                    ? "libmpv 加载播放地址失败。"
                    : "libmpv 加载播放地址失败：" + detail;
        }
        return loaded;
    }

    public boolean addSubtitle(String url, String cookieHeader) {
        return initialized && MpvBridge.nativeAddSubtitle(handle, url, cookieHeader);
    }

    public void togglePaused() {
        if (!initialized) {
            return;
        }

        paused = !paused;
        MpvBridge.nativeSetPaused(handle, paused);
    }

    public void seek(double seconds) {
        if (initialized) {
            MpvBridge.nativeSeek(handle, seconds);
        }
    }

    public void seekTo(double seconds) {
        if (initialized) {
            MpvBridge.nativeSeekAbsolute(handle, Math.max(0, seconds));
        }
    }

    public void setAudioTrack(String value) {
        if (initialized) {
            MpvBridge.nativeSetString(handle, "aid", value == null || value.isEmpty() ? "auto" : value);
        }
    }

    public void setSubtitleTrack(String value) {
        if (initialized) {
            MpvBridge.nativeSetString(handle, "sid", value == null || value.isEmpty() ? "auto" : value);
        }
    }

    public double currentTimeSeconds() {
        if (!initialized) {
            return 0;
        }

        double playbackTime = MpvBridge.nativeGetDouble(handle, "playback-time", -1);
        if (Double.isFinite(playbackTime) && playbackTime > 0) {
            return playbackTime;
        }

        double timePosition = MpvBridge.nativeGetDouble(handle, "time-pos", -1);
        if (Double.isFinite(timePosition) && timePosition > 0) {
            return timePosition;
        }

        return Double.isFinite(playbackTime) && playbackTime >= 0 ? playbackTime : 0;
    }

    public double durationSeconds() {
        return initialized ? MpvBridge.nativeGetDouble(handle, "duration", 0) : 0;
    }

    public double remainingTimeSeconds() {
        if (!initialized) {
            return -1;
        }

        double playtimeRemaining = MpvBridge.nativeGetDouble(handle, "playtime-remaining", -1);
        if (Double.isFinite(playtimeRemaining) && playtimeRemaining >= 0) {
            return playtimeRemaining;
        }

        double timeRemaining = MpvBridge.nativeGetDouble(handle, "time-remaining", -1);
        return Double.isFinite(timeRemaining) && timeRemaining >= 0 ? timeRemaining : -1;
    }

    public double percentPosition() {
        double value = initialized ? MpvBridge.nativeGetDouble(handle, "percent-pos", -1) : -1;
        return Double.isFinite(value) ? Math.max(0, Math.min(100, value)) : -1;
    }

    public String lastError() {
        return lastError;
    }

    public void destroy() {
        if (handle != 0) {
            MpvBridge.nativeSetEventListener(handle, null);
            MpvBridge.nativeDestroy(handle);
            handle = 0;
            initialized = false;
        }
        playbackEventListener = null;
    }
}
