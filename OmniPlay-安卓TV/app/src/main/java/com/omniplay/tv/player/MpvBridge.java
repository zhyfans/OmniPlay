package com.omniplay.tv.player;

import android.view.Surface;

final class MpvBridge {
    interface EventListener {
        void onMpvEvent(String eventName, String detail);
    }

    static {
        try {
            System.loadLibrary("mpv");
        } catch (UnsatisfiedLinkError ignored) {
            // The native bridge also tries dlopen("libmpv.so") and reports failure cleanly.
        }
        System.loadLibrary("mpvbridge");
    }

    private MpvBridge() {
    }

    static native long nativeCreate();

    static native boolean nativeInitialize(long handle);

    static native void nativeSetPlaybackMode(long handle, boolean directMode);

    static native void nativeSetEventListener(long handle, EventListener listener);

    static native void nativeAttachSurface(long handle, Surface surface, int width, int height);

    static native void nativeDetachSurface(long handle);

    static native boolean nativeLoad(long handle, String url, String cookieHeader, String userAgent, double startSeconds, boolean isoPlayback);

    static native boolean nativeAddSubtitle(long handle, String url, String cookieHeader);

    static native String nativeLastError(long handle);

    static native void nativeSetPaused(long handle, boolean paused);

    static native void nativeSeek(long handle, double seconds);

    static native void nativeSeekAbsolute(long handle, double seconds);

    static native void nativeSetString(long handle, String property, String value);

    static native String nativeGetString(long handle, String property, String fallback);

    static native double nativeGetDouble(long handle, String property, double fallback);

    static native void nativeDestroy(long handle);
}
