package com.omniplay.tv;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.os.Bundle;

public final class LauncherActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Class<?> target = isTelevision() ? MainActivity.class : PhoneMainActivity.class;
        Intent intent = new Intent(this, target);
        startActivity(intent);
        finish();
    }

    private boolean isTelevision() {
        int uiModeType = getResources().getConfiguration().uiMode & Configuration.UI_MODE_TYPE_MASK;
        return uiModeType == Configuration.UI_MODE_TYPE_TELEVISION ||
                getPackageManager().hasSystemFeature(PackageManager.FEATURE_LEANBACK);
    }
}
