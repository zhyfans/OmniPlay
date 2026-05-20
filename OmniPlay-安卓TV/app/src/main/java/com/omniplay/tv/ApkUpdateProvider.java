package com.omniplay.tv;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;

public final class ApkUpdateProvider extends ContentProvider {
    public static final String APK_NAME = "omniplay-update.apk";

    public static Uri apkUri(Context context) {
        return Uri.parse("content://" + context.getPackageName() + ".updates/" + APK_NAME);
    }

    public static File apkFile(Context context) {
        return new File(new File(context.getCacheDir(), "updates"), APK_NAME);
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public String getType(Uri uri) {
        return isValidUri(uri) ? "application/vnd.android.package-archive" : null;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!isValidUri(uri) || mode == null || mode.contains("w")) {
            throw new FileNotFoundException("Unsupported update file request.");
        }

        Context context = getContext();
        if (context == null) {
            throw new FileNotFoundException("Context is not available.");
        }

        File file = apkFile(context);
        if (!file.isFile()) {
            throw new FileNotFoundException("Update APK does not exist.");
        }

        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        if (!isValidUri(uri) || getContext() == null) {
            return null;
        }

        File file = apkFile(getContext());
        MatrixCursor cursor = new MatrixCursor(new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE});
        cursor.addRow(new Object[]{APK_NAME, file.length()});
        return cursor;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }

    private static boolean isValidUri(Uri uri) {
        return uri != null && APK_NAME.equals(uri.getLastPathSegment());
    }
}
