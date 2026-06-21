package com.omniplay.tv.subtitle;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class PgsSubtitleCue {
    public final double startSeconds;
    public double endSeconds;
    public final int planeWidth;
    public final int planeHeight;
    public final List<Image> images;

    PgsSubtitleCue(double startSeconds, double endSeconds, int planeWidth, int planeHeight, List<Image> images) {
        this.startSeconds = Math.max(0, startSeconds);
        this.endSeconds = Math.max(this.startSeconds, endSeconds);
        this.planeWidth = Math.max(1, planeWidth);
        this.planeHeight = Math.max(1, planeHeight);
        this.images = Collections.unmodifiableList(new ArrayList<>(images));
    }

    public static final class Image {
        public final int objectId;
        public final int x;
        public final int y;
        public final int width;
        public final int height;
        public final int[] palette;
        public final byte[] rleData;

        Image(int objectId, int x, int y, int width, int height, int[] palette, byte[] rleData) {
            this.objectId = objectId;
            this.x = Math.max(0, x);
            this.y = Math.max(0, y);
            this.width = Math.max(1, width);
            this.height = Math.max(1, height);
            this.palette = palette.clone();
            this.rleData = rleData.clone();
        }
    }
}
