package com.omniplay.tv.player;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.view.Surface;
import android.view.TextureView;

public final class MpvVideoView extends TextureView implements TextureView.SurfaceTextureListener {
    public interface PlaybackListener {
        void onLoaded();

        void onError(String message);
    }

    private final MpvPlayer player = new MpvPlayer();
    private String pendingUrl;
    private String pendingCookie;
    private double pendingStartSeconds;
    private boolean surfaceReady;
    private Surface surface;
    private PlaybackListener playbackListener;
    private boolean notifiedPlaybackStarted;

    public MpvVideoView(Context context) {
        this(context, false);
    }

    public MpvVideoView(Context context, boolean directPlaybackMode) {
        super(context);
        player.setDirectPlaybackMode(directPlaybackMode);
        setSurfaceTextureListener(this);
        setOpaque(true);
        setFocusable(true);
        setFocusableInTouchMode(false);
        player.setPlaybackEventListener(new MpvPlayer.PlaybackEventListener() {
            @Override
            public void onPlaybackStarted() {
                if (notifiedPlaybackStarted) {
                    return;
                }
                notifiedPlaybackStarted = true;
                PlaybackListener listener = playbackListener;
                if (listener != null) {
                    listener.onLoaded();
                }
            }

            @Override
            public void onPlaybackError(String message) {
                PlaybackListener listener = playbackListener;
                if (listener != null) {
                    listener.onError(message);
                }
            }
        });
    }

    public boolean play(String url, String cookieHeader, double startSeconds) {
        pendingUrl = url;
        pendingCookie = cookieHeader;
        pendingStartSeconds = Math.max(0, startSeconds);
        notifiedPlaybackStarted = false;
        if (surfaceReady) {
            return loadPending();
        }

        return true;
    }

    public void setPlaybackListener(PlaybackListener listener) {
        playbackListener = listener;
    }

    public boolean addSubtitle(String url, String cookieHeader) {
        return player.addSubtitle(url, cookieHeader);
    }

    public void togglePaused() {
        player.togglePaused();
    }

    public void seek(double seconds) {
        player.seek(seconds);
    }

    public void seekTo(double seconds) {
        player.seekTo(seconds);
    }

    public void setAudioTrack(String value) {
        player.setAudioTrack(value);
    }

    public void setSubtitleTrack(String value) {
        player.setSubtitleTrack(value);
    }

    public String lastError() {
        return player.lastError();
    }

    public double currentTimeSeconds() {
        return player.currentTimeSeconds();
    }

    public double durationSeconds() {
        return player.durationSeconds();
    }

    public double remainingTimeSeconds() {
        return player.remainingTimeSeconds();
    }

    public double percentPosition() {
        return player.percentPosition();
    }

    public void destroyPlayer() {
        playbackListener = null;
        player.setPlaybackEventListener(null);
        releaseSurface();
        player.destroy();
    }

    @Override
    public void onSurfaceTextureAvailable(SurfaceTexture surfaceTexture, int width, int height) {
        surfaceReady = true;
        if (width > 0 && height > 0) {
            surfaceTexture.setDefaultBufferSize(width, height);
        }
        surface = new Surface(surfaceTexture);
        player.attachSurface(surface, width, height);
        if (pendingUrl != null) {
            loadPending();
        }
    }

    @Override
    public void onSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height) {
        if (width > 0 && height > 0) {
            surfaceTexture.setDefaultBufferSize(width, height);
        }
        if (surface != null) {
            player.attachSurface(surface, width, height);
        }
    }

    @Override
    public boolean onSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) {
        surfaceReady = false;
        player.detachSurface();
        releaseSurface();
        return true;
    }

    @Override
    public void onSurfaceTextureUpdated(SurfaceTexture surfaceTexture) {
    }

    private void releaseSurface() {
        if (surface != null) {
            surface.release();
            surface = null;
        }
    }

    private boolean loadPending() {
        boolean loaded = player.load(pendingUrl, pendingCookie, pendingStartSeconds);
        PlaybackListener listener = playbackListener;
        if (!loaded && listener != null) {
            listener.onError(lastError());
        }
        return loaded;
    }
}
