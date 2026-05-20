package com.omniplay.tv.ui;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.util.LruCache;
import android.widget.ImageView;

import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class ImageLoader {
    private final ExecutorService executor = Executors.newFixedThreadPool(4);
    private final Handler main = new Handler(Looper.getMainLooper());
    private final LruCache<String, Bitmap> cache = new LruCache<String, Bitmap>(96 * 1024) {
        @Override
        protected int sizeOf(String key, Bitmap value) {
            return Math.max(1, value.getByteCount() / 1024);
        }
    };

    public void load(ImageView imageView, String url, String cookieHeader) {
        imageView.setAdjustViewBounds(false);
        imageView.setCropToPadding(false);
        imageView.setTag(url);
        if (url == null || url.isEmpty()) {
            imageView.setImageDrawable(null);
            imageView.setBackground(placeholder());
            return;
        }

        Bitmap cached = cache.get(url);
        if (cached != null) {
            imageView.setImageBitmap(cached);
            return;
        }

        imageView.setImageDrawable(null);
        imageView.setBackground(placeholder());
        executor.execute(() -> {
            Bitmap bitmap = fetch(url, cookieHeader);
            if (bitmap == null) {
                return;
            }

            cache.put(url, bitmap);
            main.post(() -> {
                Object tag = imageView.getTag();
                if (url.equals(tag)) {
                    imageView.setImageBitmap(bitmap);
                }
            });
        });
    }

    private static Bitmap fetch(String url, String cookieHeader) {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(url).openConnection();
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(20000);
            connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
            if (cookieHeader != null && !cookieHeader.isEmpty()) {
                connection.setRequestProperty("Cookie", cookieHeader);
            }

            try (InputStream input = connection.getInputStream()) {
                return BitmapFactory.decodeStream(input);
            }
        } catch (Exception ignored) {
            return null;
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private static GradientDrawable placeholder() {
        GradientDrawable drawable = new GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM,
                new int[]{Color.rgb(243, 244, 246), Color.rgb(229, 231, 235)});
        drawable.setCornerRadius(10);
        return drawable;
    }
}
