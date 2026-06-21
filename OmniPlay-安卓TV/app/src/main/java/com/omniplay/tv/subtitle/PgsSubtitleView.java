package com.omniplay.tv.subtitle;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.util.LruCache;
import android.view.View;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

public final class PgsSubtitleView extends View {
    private static final int BITMAP_CACHE_BYTES = 32 * 1024 * 1024;

    private final Paint bitmapPaint = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.DITHER_FLAG);
    private final LruCache<String, Bitmap> bitmapCache = new LruCache<String, Bitmap>(BITMAP_CACHE_BYTES) {
        @Override
        protected int sizeOf(String key, Bitmap value) {
            return value == null ? 0 : value.getByteCount();
        }
    };
    private List<PgsSubtitleCue> cues = Collections.emptyList();
    private double positionSeconds;
    private int activeCueIndex = -1;

    public PgsSubtitleView(Context context) {
        super(context);
        setWillNotDraw(false);
        setFocusable(false);
        setFocusableInTouchMode(false);
        setVisibility(GONE);
    }

    public void setCues(List<PgsSubtitleCue> cues) {
        this.cues = cues == null ? Collections.emptyList() : cues;
        activeCueIndex = -1;
        bitmapCache.evictAll();
        setVisibility(this.cues.isEmpty() ? GONE : INVISIBLE);
        invalidate();
    }

    public void clear() {
        cues = Collections.emptyList();
        activeCueIndex = -1;
        bitmapCache.evictAll();
        setVisibility(GONE);
        invalidate();
    }

    public void setPlaybackPosition(double seconds) {
        positionSeconds = Math.max(0, seconds);
        int nextCueIndex = findActiveCueIndex(positionSeconds);
        if (nextCueIndex != activeCueIndex) {
            activeCueIndex = nextCueIndex;
        }
        setVisibility(activeCueIndex >= 0 ? VISIBLE : INVISIBLE);
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (activeCueIndex < 0 || activeCueIndex >= cues.size()) {
            return;
        }

        PgsSubtitleCue cue = cues.get(activeCueIndex);
        if (cue.images.isEmpty() || cue.planeWidth <= 0 || cue.planeHeight <= 0 || getWidth() <= 0 || getHeight() <= 0) {
            return;
        }

        float scale = Math.min(getWidth() / (float) cue.planeWidth, getHeight() / (float) cue.planeHeight);
        float baseX = (getWidth() - cue.planeWidth * scale) * 0.5f;
        float baseY = (getHeight() - cue.planeHeight * scale) * 0.5f;
        for (int index = 0; index < cue.images.size(); index++) {
            PgsSubtitleCue.Image image = cue.images.get(index);
            Bitmap bitmap = bitmapFor(activeCueIndex, index, image);
            if (bitmap == null) {
                continue;
            }

            RectF target = new RectF(
                    baseX + image.x * scale,
                    baseY + image.y * scale,
                    baseX + (image.x + image.width) * scale,
                    baseY + (image.y + image.height) * scale);
            canvas.drawBitmap(bitmap, null, target, bitmapPaint);
        }
    }

    private int findActiveCueIndex(double seconds) {
        if (cues.isEmpty()) {
            return -1;
        }

        if (activeCueIndex >= 0 && activeCueIndex < cues.size()) {
            PgsSubtitleCue cue = cues.get(activeCueIndex);
            if (seconds >= cue.startSeconds && seconds < cue.endSeconds) {
                return activeCueIndex;
            }
        }

        int low = 0;
        int high = cues.size() - 1;
        int candidate = -1;
        while (low <= high) {
            int mid = (low + high) >>> 1;
            PgsSubtitleCue cue = cues.get(mid);
            if (cue.startSeconds <= seconds) {
                candidate = mid;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }

        for (int index = candidate; index >= 0 && index >= candidate - 4; index--) {
            PgsSubtitleCue cue = cues.get(index);
            if (seconds >= cue.startSeconds && seconds < cue.endSeconds) {
                return index;
            }
        }
        return -1;
    }

    private Bitmap bitmapFor(int cueIndex, int imageIndex, PgsSubtitleCue.Image image) {
        String key = cueIndex + ":" + imageIndex;
        Bitmap cached = bitmapCache.get(key);
        if (cached != null) {
            return cached;
        }

        Bitmap bitmap = decodeBitmap(image);
        if (bitmap != null) {
            bitmapCache.put(key, bitmap);
        }
        return bitmap;
    }

    private static Bitmap decodeBitmap(PgsSubtitleCue.Image image) {
        long pixelCountLong = (long) image.width * (long) image.height;
        if (pixelCountLong <= 0 || pixelCountLong > 16_777_216L) {
            return null;
        }

        int pixelCount = (int) pixelCountLong;
        int[] pixels = new int[pixelCount];
        int pixelIndex = 0;
        int offset = 0;
        byte[] data = image.rleData;
        while (offset < data.length && pixelIndex < pixelCount) {
            int colorIndex = data[offset++] & 0xff;
            if (colorIndex != 0) {
                pixels[pixelIndex++] = colorForIndex(image.palette, colorIndex);
                continue;
            }

            if (offset >= data.length) {
                break;
            }

            int command = data[offset++] & 0xff;
            if (command == 0) {
                continue;
            }

            int runLength = command & 0x3f;
            if ((command & 0x40) != 0) {
                if (offset >= data.length) {
                    break;
                }
                runLength = (runLength << 8) | (data[offset++] & 0xff);
            }

            int color = 0;
            if ((command & 0x80) != 0) {
                if (offset >= data.length) {
                    break;
                }
                color = colorForIndex(image.palette, data[offset++] & 0xff);
            }

            int end = Math.min(pixelCount, pixelIndex + runLength);
            Arrays.fill(pixels, pixelIndex, end, color);
            pixelIndex = end;
        }

        try {
            return Bitmap.createBitmap(pixels, image.width, image.height, Bitmap.Config.ARGB_8888);
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    private static int colorForIndex(int[] palette, int index) {
        return index >= 0 && index < palette.length ? palette[index] : 0;
    }
}
