package com.omniplay.tv;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.data.OmniPlayApi;
import com.omniplay.tv.player.MpvVideoView;
import com.omniplay.tv.ui.ImageLoader;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.Collator;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

public final class MainActivity extends Activity {
    private static final String UI_PREFS = "omniplay_tv_ui";
    private static final String KEY_SHOW_EPISODE_DETAILS = "show_episode_details";
    private static final String KEY_DIRECT_4K_PLAYBACK = "direct_4k_playback";
    private static final int COLOR_BACKGROUND = Color.rgb(245, 247, 250);
    private static final int COLOR_SURFACE = Color.rgb(255, 255, 255);
    private static final int COLOR_SURFACE_ALT = Color.rgb(243, 244, 246);
    private static final int COLOR_BORDER = Color.rgb(221, 226, 235);
    private static final int COLOR_TEXT = Color.rgb(17, 24, 39);
    private static final int COLOR_MUTED = Color.rgb(75, 85, 99);
    private static final int COLOR_SUBTLE = Color.rgb(107, 114, 128);
    private static final int COLOR_FOCUS = Color.rgb(37, 99, 235);
    private static final int COLOR_ACCENT = Color.rgb(20, 184, 166);
    private static final int COLOR_DANGER = Color.rgb(185, 28, 28);
    private static final int COLOR_PLAYER_TEXT = Color.WHITE;
    private static final int COLOR_PLAYER_MUTED = Color.rgb(229, 231, 235);
    private static final int UNKNOWN_MOVIE_PART_INDEX = Integer.MAX_VALUE;
    private static final String[] MOVIE_PART_PREFIXES = {"volume", "part", "disc", "disk", "dvd", "vol", "pt", "cd"};

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService io = Executors.newFixedThreadPool(4);
    private final Collator titleCollator = Collator.getInstance(Locale.CHINA);
    private FrameLayout root;
    private OmniPlayApi api;
    private ImageLoader imageLoader;
    private SharedPreferences uiPreferences;
    private List<Models.LibraryItem> libraryItems = Collections.emptyList();
    private Models.LibraryDetail currentDetail;
    private MpvVideoView playerView;
    private ScrollView homeScrollView;
    private View settingsDim;
    private View settingsPanel;
    private View settingsInitialFocus;
    private Models.VideoFile activePlaybackFile;
    private List<Models.VideoFile> activePlaybackQueue = Collections.emptyList();
    private int activePlaybackIndex;
    private String activePlaybackTicket;
    private List<Models.SubtitleTrack> activeExternalSubtitles = Collections.emptyList();
    private List<SubtitleCue> activeSubtitleCues = Collections.emptyList();
    private View playerMenuPanel;
    private View playerTopOverlay;
    private View playerControlsPanel;
    private TextView playerCurrentTime;
    private TextView playerTotalTime;
    private TextView playerStatusText;
    private TextView playerSubtitleOverlay;
    private ImageButton playerPlayPauseButton;
    private View playerProgressFill;
    private FrameLayout playerProgressTrack;
    private final List<View> playerProgressSegmentViews = new ArrayList<>();
    private Runnable playerUiUpdater;
    private Runnable progressReporter;
    private Runnable playerChromeHider;
    private Runnable subtitleOverlayUpdater;
    private long playerLoadStartedAtMs;
    private Runnable pendingSearchRender;
    private int homeScrollY;
    private boolean restoreHomeScroll;
    private boolean advancingPlaybackPart;
    private Screen screen = Screen.SETUP;
    private boolean isSearchOpen;
    private boolean isSortOpen;
    private boolean isSettingsOpen;
    private boolean showEpisodeDetails = true;
    private boolean direct4KPlayback = true;
    private boolean activeDirectPlayback;
    private boolean playerChromeHidden;
    private boolean playerPaused;
    private String searchText = "";
    private SortKey sortKey = SortKey.YEAR;
    private boolean sortDescending = true;
    private String selectedSeasonId = "";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN, WindowManager.LayoutParams.FLAG_FULLSCREEN);

        api = new OmniPlayApi(this);
        imageLoader = new ImageLoader();
        uiPreferences = getApplicationContext().getSharedPreferences(UI_PREFS, MODE_PRIVATE);
        showEpisodeDetails = uiPreferences.getBoolean(KEY_SHOW_EPISODE_DETAILS, true);
        direct4KPlayback = uiPreferences.getBoolean(KEY_DIRECT_4K_PLAYBACK, true);
        root = new FrameLayout(this);
        applyAppBackground();
        setContentView(root);

        if (api.hasServerUrl()) {
            checkSession();
        } else {
            showSetup(null);
        }
    }

    private void checkSession() {
        showLoading("正在连接 OmniPlay 服务端");
        runAsync(
                () -> api.authStatus(),
                status -> {
                    if (status.isAuthenticated) {
                        showHome();
                    } else {
                        showSetup(status.requiresSetup ? "请先在网页端完成管理员初始化。" : null);
                    }
                },
                error -> showSetup(error.getMessage()));
    }

    private void showSetup(String message) {
        screen = Screen.SETUP;
        currentDetail = null;
        destroyPlayer();
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        root.addView(scroll, match());

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER_HORIZONTAL);
        page.setPadding(dp(96), dp(28), dp(96), dp(28));
        scroll.addView(page, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(40), dp(26), dp(40), dp(28));
        panel.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(8)));
        panel.setElevation(dp(12));
        page.addView(panel, new LinearLayout.LayoutParams(dp(720), ViewGroup.LayoutParams.WRAP_CONTENT));

        panel.addView(title("OmniPlay TV"));
        panel.addView(body("连接群晖套件版服务端后，安卓 TV 端将直接播放原文件。"));

        EditText serverInput = input("服务端地址，例如 http://192.168.1.10:45721", api.serverUrl());
        panel.addView(serverInput, margin(matchWidth(), 0, dp(18), 0, 0));
        TextView discoveryStatus = text("", 13, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(discoveryStatus, margin(matchWidth(), 0, dp(6), 0, dp(8)));

        EditText usernameInput = input("用户名", "");
        panel.addView(usernameInput, margin(matchWidth(), 0, dp(10), 0, 0));

        EditText passwordInput = input("密码", "");
        passwordInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        panel.addView(passwordInput, margin(matchWidth(), 0, dp(10), 0, 0));

        if (message != null && !message.isEmpty()) {
            TextView error = body(message);
            error.setTextColor(COLOR_DANGER);
            panel.addView(error, margin(matchWidth(), 0, 0, 0, dp(16)));
        }

        Button login = button("登录");
        panel.addView(login, margin(new LinearLayout.LayoutParams(dp(180), dp(52)), 0, dp(14), 0, 0));
        login.setOnClickListener(view -> {
            String server = serverInput.getText().toString();
            String username = usernameInput.getText().toString();
            String password = passwordInput.getText().toString();
            showLoading("正在登录");
            runAsync(
                    () -> api.login(server, username, password),
                    status -> showHome(),
                    error -> showSetup(error.getMessage()));
        });
        serverInput.requestFocus();
        startServerDiscovery(serverInput, discoveryStatus);
    }

    private void startServerDiscovery(EditText serverInput, TextView discoveryStatus) {
        if (serverInput.getText().toString().trim().length() > 0) {
            return;
        }

        discoveryStatus.setText("正在扫描局域网服务端");
        runAsync(
                this::discoverServerUrl,
                serverUrl -> {
                    if (screen != Screen.SETUP) {
                        return;
                    }
                    if (serverUrl != null && !serverUrl.isEmpty() && serverInput.getText().toString().trim().isEmpty()) {
                        serverInput.setText(serverUrl);
                        serverInput.setSelection(serverInput.getText().length());
                        discoveryStatus.setText("已发现服务端");
                    } else {
                        discoveryStatus.setText("未自动发现服务端，可手动输入");
                    }
                },
                error -> {
                    if (screen == Screen.SETUP) {
                        discoveryStatus.setText("未自动发现服务端，可手动输入");
                    }
                });
    }

    private String discoverServerUrl() throws Exception {
        Set<String> candidates = new LinkedHashSet<>();
        Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
        while (interfaces != null && interfaces.hasMoreElements()) {
            NetworkInterface networkInterface = interfaces.nextElement();
            if (!networkInterface.isUp() || networkInterface.isLoopback()) {
                continue;
            }

            Enumeration<InetAddress> addresses = networkInterface.getInetAddresses();
            while (addresses.hasMoreElements()) {
                InetAddress address = addresses.nextElement();
                if (!(address instanceof Inet4Address) || address.isLoopbackAddress() || !address.isSiteLocalAddress()) {
                    continue;
                }

                byte[] bytes = address.getAddress();
                int first = bytes[0] & 0xff;
                int second = bytes[1] & 0xff;
                int third = bytes[2] & 0xff;
                int own = bytes[3] & 0xff;
                for (int host = 1; host <= 254; host++) {
                    if (host != own) {
                        candidates.add(first + "." + second + "." + third + "." + host);
                    }
                }
            }
        }

        if (candidates.isEmpty()) {
            return "";
        }

        ExecutorService scanner = Executors.newFixedThreadPool(Math.min(32, candidates.size()));
        ExecutorCompletionService<String> completion = new ExecutorCompletionService<>(scanner);
        ArrayList<Future<String>> futures = new ArrayList<>();
        for (String host : candidates) {
            futures.add(completion.submit(() -> probeServer(host)));
        }

        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(8);
        try {
            for (int remaining = futures.size(); remaining > 0; remaining--) {
                long waitNanos = deadline - System.nanoTime();
                if (waitNanos <= 0) {
                    break;
                }

                Future<String> future = completion.poll(waitNanos, TimeUnit.NANOSECONDS);
                if (future == null) {
                    break;
                }

                String result = future.get();
                if (result != null && !result.isEmpty()) {
                    return result;
                }
            }
        } finally {
            for (Future<String> future : futures) {
                future.cancel(true);
            }
            scanner.shutdownNow();
        }

        return "";
    }

    private String probeServer(String host) {
        String url = "http://" + host + ":45721";
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(url + "/api/auth/status").openConnection();
            connection.setConnectTimeout(280);
            connection.setReadTimeout(650);
            connection.setRequestProperty("Accept", "application/json");
            connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
            int status = connection.getResponseCode();
            if (status >= 200 && status < 300) {
                String body = readRemoteText(connection.getInputStream());
                if (body.contains("requiresSetup") || body.contains("isAuthenticated")) {
                    return url;
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
        return "";
    }

    private void showHome() {
        screen = Screen.HOME;
        currentDetail = null;
        selectedSeasonId = "";
        destroyPlayer();
        boolean hasVisibleLibrary = !libraryItems.isEmpty();
        if (!hasVisibleLibrary) {
            List<Models.LibraryItem> cachedItems = api.getCachedLibraryItems();
            if (!cachedItems.isEmpty()) {
                libraryItems = new ArrayList<>(cachedItems);
                hasVisibleLibrary = true;
            }
        }

        if (hasVisibleLibrary) {
            renderHomeContent();
        } else {
            showLoading("正在加载媒体库");
        }

        refreshHomeLibrary(!hasVisibleLibrary);
    }

    private void refreshHomeLibrary(boolean showErrorScreen) {
        runAsync(
                () -> api.getLibraryItems(),
                items -> {
                    if (screen != Screen.HOME) {
                        return;
                    }
                    libraryItems = new ArrayList<>(items);
                    renderHomeContent();
                },
                error -> {
                    if (showErrorScreen) {
                        showSetup(error.getMessage());
                    }
                });
    }

    private void renderHomeContent() {
        screen = Screen.HOME;
        root.removeAllViews();
        applyAppBackground();

        ScrollView scroll = new ScrollView(this);
        homeScrollView = scroll;
        scroll.setFillViewport(false);
        root.addView(scroll, match());

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(44), dp(28), dp(44), dp(36));
        scroll.addView(page, matchWidth());

        View firstFocusable = null;

        LinearLayout continueHeader = new LinearLayout(this);
        continueHeader.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(continueHeader, margin(matchWidth(), 0, 0, 0, dp(14)));
        TextView continueTitle = sectionTitle("继续播放");
        continueTitle.setPadding(0, 0, 0, 0);
        continueHeader.addView(continueTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        Button settings = compactButton("设置");
        continueHeader.addView(settings, new LinearLayout.LayoutParams(dp(96), dp(44)));
        settings.setOnClickListener(view -> {
            openSettingsOverlay();
        });

        List<Models.LibraryItem> continueItems = continueItems(libraryItems);
        if (continueItems.isEmpty()) {
            page.addView(emptyText("暂无继续播放"));
        } else {
            GridLayout continueGrid = posterGrid();
            page.addView(continueGrid, margin(matchWidth(), 0, 0, 0, dp(28)));
            for (Models.LibraryItem item : continueItems) {
                View card = posterCard(item, true);
                if (firstFocusable == null) {
                    firstFocusable = card;
                }
                continueGrid.addView(card);
            }
        }

        LinearLayout libraryHeader = new LinearLayout(this);
        libraryHeader.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(libraryHeader, margin(matchWidth(), 0, dp(6), 0, dp(14)));

        TextView libraryTitle = sectionTitle("所有影视");
        libraryTitle.setPadding(0, 0, 0, 0);
        libraryHeader.addView(libraryTitle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        Button search = compactButton("搜索");
        libraryHeader.addView(search, margin(new LinearLayout.LayoutParams(dp(96), dp(44)), dp(18), 0, 0, 0));
        search.setOnClickListener(view -> {
            isSearchOpen = !isSearchOpen;
            renderHomeContentKeepingPosition();
        });

        Button sort = compactButton("排序");
        libraryHeader.addView(sort, margin(new LinearLayout.LayoutParams(dp(96), dp(44)), dp(10), 0, 0, 0));
        sort.setOnClickListener(view -> {
            isSortOpen = !isSortOpen;
            renderHomeContentKeepingPosition();
        });

        Button direction = compactButton(sortDescending ? "降序" : "升序");
        libraryHeader.addView(direction, margin(new LinearLayout.LayoutParams(dp(96), dp(44)), dp(10), 0, 0, 0));
        direction.setOnClickListener(view -> {
            sortDescending = !sortDescending;
            renderHomeContentKeepingPosition();
        });

        EditText searchInput = null;
        if (isSearchOpen) {
            LinearLayout searchRow = new LinearLayout(this);
            searchRow.setGravity(Gravity.CENTER_VERTICAL);
            page.addView(searchRow, margin(matchWidth(), 0, 0, 0, dp(14)));
            searchInput = input("搜索", searchText);
            searchInput.setSelection(searchInput.getText().length());
            searchInput.addTextChangedListener(new TextWatcher() {
                @Override
                public void beforeTextChanged(CharSequence value, int start, int count, int after) {
                }

                @Override
                public void onTextChanged(CharSequence value, int start, int before, int count) {
                }

                @Override
                public void afterTextChanged(Editable editable) {
                    searchText = editable.toString();
                    if (pendingSearchRender != null) {
                        main.removeCallbacks(pendingSearchRender);
                    }
                    pendingSearchRender = () -> {
                        pendingSearchRender = null;
                        if (screen == Screen.HOME && isSearchOpen) {
                            renderHomeContentKeepingPosition();
                        }
                    };
                    main.postDelayed(pendingSearchRender, 220);
                }
            });
            searchRow.addView(searchInput, new LinearLayout.LayoutParams(0, dp(56), 1f));

            EditText finalSearchInput = searchInput;
            Button apply = compactButton("确定");
            searchRow.addView(apply, margin(new LinearLayout.LayoutParams(dp(96), dp(48)), dp(12), dp(4), 0, dp(4)));
            apply.setOnClickListener(view -> {
                searchText = finalSearchInput.getText().toString();
                renderHomeContentKeepingPosition();
            });
        }

        if (isSortOpen) {
            LinearLayout sortRow = new LinearLayout(this);
            sortRow.setGravity(Gravity.CENTER_VERTICAL);
            page.addView(sortRow, margin(matchWidth(), 0, 0, 0, dp(16)));
            addSortButton(sortRow, "名称", SortKey.TITLE);
            addSortButton(sortRow, "评分", SortKey.RATING);
            addSortButton(sortRow, "上映年份", SortKey.YEAR);
        }

        List<Models.LibraryItem> displayedItems = displayedItems();
        GridLayout grid = posterGrid();
        page.addView(grid);
        for (Models.LibraryItem item : displayedItems) {
            View card = posterCard(item, false);
            if (firstFocusable == null) {
                firstFocusable = card;
            }
            grid.addView(card);
        }

        if (displayedItems.isEmpty()) {
            page.addView(emptyText(libraryItems.isEmpty() ? "媒体库为空" : "没有匹配结果"));
        }

        if (restoreHomeScroll) {
            int targetScrollY = homeScrollY;
            restoreHomeScroll = false;
            main.post(() -> scroll.scrollTo(0, targetScrollY));
        } else {
            if (searchInput != null && isSearchOpen) {
                searchInput.requestFocus();
            } else if (firstFocusable != null) {
                firstFocusable.requestFocus();
            } else {
                search.requestFocus();
            }
        }

        if (isSettingsOpen) {
            renderSettingsOverlay();
        }
    }

    private void addSortButton(LinearLayout row, String label, SortKey key) {
        Button option = button(label, sortKey == key);
        row.addView(option, margin(new LinearLayout.LayoutParams(dp(132), dp(44)), 0, 0, dp(10), 0));
        option.setOnClickListener(view -> {
            sortKey = key;
            isSortOpen = false;
            renderHomeContentKeepingPosition();
        });
    }

    private void renderHomeContentKeepingPosition() {
        if (homeScrollView != null) {
            homeScrollY = homeScrollView.getScrollY();
            restoreHomeScroll = true;
        }
        renderHomeContent();
    }

    private void openSettingsOverlay() {
        if (isSettingsOpen) {
            return;
        }
        isSettingsOpen = true;
        renderSettingsOverlay();
    }

    private void closeSettingsOverlay() {
        isSettingsOpen = false;
        if (settingsPanel != null) {
            root.removeView(settingsPanel);
            settingsPanel = null;
        }
        settingsInitialFocus = null;
        if (settingsDim != null) {
            root.removeView(settingsDim);
            settingsDim = null;
        }
    }

    private void renderSettingsOverlay() {
        if (settingsPanel != null || settingsDim != null) {
            return;
        }
        FrameLayout dim = new FrameLayout(this);
        dim.setBackgroundColor(Color.argb(76, 15, 23, 42));
        dim.setFocusable(false);
        root.addView(dim, match());
        dim.setOnClickListener(view -> {
            closeSettingsOverlay();
        });
        settingsDim = dim;

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(22), dp(20), dp(22), dp(22));
        panel.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(12)));
        panel.setElevation(dp(18));
        panel.setFocusable(true);
        FrameLayout.LayoutParams panelParams = new FrameLayout.LayoutParams(dp(420), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.RIGHT);
        panelParams.setMargins(0, dp(84), dp(44), 0);
        root.addView(panel, panelParams);
        settingsPanel = panel;

        TextView title = text("设置", 24, Typeface.BOLD, COLOR_TEXT);
        panel.addView(title, margin(matchWidth(), 0, 0, 0, dp(18)));

        TextView version = text("版本 1.5", 14, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(version, margin(matchWidth(), 0, 0, 0, dp(14)));

        panel.addView(text("NAS 服务器", 14, Typeface.BOLD, COLOR_MUTED));
        TextView server = text(api.serverUrl(), 13, Typeface.NORMAL, COLOR_SUBTLE);
        server.setSingleLine(true);
        server.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        panel.addView(server, margin(matchWidth(), 0, dp(6), 0, dp(16)));

        panel.addView(text("分集详情显示", 14, Typeface.BOLD, COLOR_MUTED));
        LinearLayout episodeRow = new LinearLayout(this);
        episodeRow.setOrientation(LinearLayout.VERTICAL);
        panel.addView(episodeRow, margin(matchWidth(), 0, dp(8), 0, dp(18)));
        Button detailsOn = button("显示详情", showEpisodeDetails);
        detailsOn.setId(View.generateViewId());
        episodeRow.addView(detailsOn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)));
        detailsOn.setOnClickListener(view -> updateShowEpisodeDetails(true));
        Button detailsOff = button("只显示名称", !showEpisodeDetails);
        detailsOff.setId(View.generateViewId());
        episodeRow.addView(detailsOff, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)), 0, dp(8), 0, 0));
        detailsOff.setOnClickListener(view -> updateShowEpisodeDetails(false));

        panel.addView(text("4K直出硬解", 14, Typeface.BOLD, COLOR_MUTED), margin(matchWidth(), 0, 0, 0, dp(8)));
        LinearLayout directRow = new LinearLayout(this);
        directRow.setOrientation(LinearLayout.VERTICAL);
        panel.addView(directRow, margin(matchWidth(), 0, 0, 0, dp(18)));
        Button directOn = button("开启", direct4KPlayback);
        directOn.setId(View.generateViewId());
        directRow.addView(directOn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)));
        directOn.setOnClickListener(view -> updateDirect4KPlayback(true));
        Button directOff = button("关闭", !direct4KPlayback);
        directOff.setId(View.generateViewId());
        directRow.addView(directOff, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46)), 0, dp(8), 0, 0));
        directOff.setOnClickListener(view -> updateDirect4KPlayback(false));

        Button update = compactButton("软件更新");
        update.setId(View.generateViewId());
        panel.addView(update, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)));
        TextView updateStatus = text("", 13, Typeface.BOLD, COLOR_SUBTLE);
        panel.addView(updateStatus, margin(matchWidth(), 0, dp(10), 0, 0));
        update.setOnClickListener(view -> startUpdateInstall(update, updateStatus));

        Button disconnect = compactButton("退出连接");
        disconnect.setId(View.generateViewId());
        panel.addView(disconnect, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)), 0, dp(12), 0, 0));
        disconnect.setTextColor(COLOR_DANGER);
        disconnect.setOnClickListener(view -> disconnectFromServer());

        trapSettingsFocus(detailsOn, detailsOff, directOn, directOff, update, disconnect);
        panel.bringToFront();
        settingsInitialFocus = detailsOn;
        detailsOn.requestFocus();
    }

    private void updateShowEpisodeDetails(boolean value) {
        showEpisodeDetails = value;
        uiPreferences.edit().putBoolean(KEY_SHOW_EPISODE_DETAILS, value).apply();
        closeSettingsOverlay();
        renderHomeContentKeepingPosition();
        openSettingsOverlay();
    }

    private void updateDirect4KPlayback(boolean value) {
        direct4KPlayback = value;
        uiPreferences.edit().putBoolean(KEY_DIRECT_4K_PLAYBACK, value).apply();
        closeSettingsOverlay();
        renderHomeContentKeepingPosition();
        openSettingsOverlay();
    }

    private void trapSettingsFocus(Button detailsOn, Button detailsOff, Button directOn, Button directOff, Button update, Button disconnect) {
        detailsOn.setNextFocusLeftId(detailsOn.getId());
        detailsOn.setNextFocusRightId(detailsOn.getId());
        detailsOn.setNextFocusUpId(detailsOn.getId());
        detailsOn.setNextFocusDownId(detailsOff.getId());

        detailsOff.setNextFocusLeftId(detailsOff.getId());
        detailsOff.setNextFocusRightId(detailsOff.getId());
        detailsOff.setNextFocusUpId(detailsOn.getId());
        detailsOff.setNextFocusDownId(directOn.getId());

        directOn.setNextFocusLeftId(directOn.getId());
        directOn.setNextFocusRightId(directOn.getId());
        directOn.setNextFocusUpId(detailsOff.getId());
        directOn.setNextFocusDownId(directOff.getId());

        directOff.setNextFocusLeftId(directOff.getId());
        directOff.setNextFocusRightId(directOff.getId());
        directOff.setNextFocusUpId(directOn.getId());
        directOff.setNextFocusDownId(update.getId());

        update.setNextFocusLeftId(update.getId());
        update.setNextFocusRightId(update.getId());
        update.setNextFocusUpId(directOff.getId());
        update.setNextFocusDownId(disconnect.getId());

        disconnect.setNextFocusLeftId(disconnect.getId());
        disconnect.setNextFocusRightId(disconnect.getId());
        disconnect.setNextFocusUpId(update.getId());
        disconnect.setNextFocusDownId(disconnect.getId());
    }

    private void disconnectFromServer() {
        closeSettingsOverlay();
        showLoading("正在退出连接");
        runAsync(
                () -> {
                    api.disconnect();
                    return null;
                },
                ignored -> showSetup(null),
                error -> showSetup(null));
    }

    private void startUpdateInstall(Button updateButton, TextView updateStatus) {
        updateButton.setEnabled(false);
        updateStatus.setText("正在检查更新");
        runAsync(
                this::downloadLatestApk,
                apk -> {
                    updateButton.setEnabled(true);
                    updateStatus.setText("已下载，正在打开安装器");
                    installDownloadedApk(apk, updateStatus);
                },
                error -> {
                    updateButton.setEnabled(true);
                    updateStatus.setText("更新失败：" + error.getMessage());
                });
    }

    private File downloadLatestApk() throws IOException, JSONException {
        String downloadUrl = resolveLatestApkDownloadUrl();
        File apk = ApkUpdateProvider.apkFile(this);
        File directory = apk.getParentFile();
        if (directory != null && !directory.exists() && !directory.mkdirs()) {
            throw new IOException("无法创建更新缓存目录。");
        }

        HttpURLConnection connection = (HttpURLConnection) new URL(downloadUrl).openConnection();
        connection.setInstanceFollowRedirects(true);
        connection.setConnectTimeout(12000);
        connection.setReadTimeout(60000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
        int status = connection.getResponseCode();
        if (status < 200 || status >= 300) {
            connection.disconnect();
            throw new IOException("下载安装包失败：" + status);
        }

        try (InputStream input = connection.getInputStream();
             FileOutputStream output = new FileOutputStream(apk, false)) {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                output.write(buffer, 0, read);
            }
        } finally {
            connection.disconnect();
        }

        if (apk.length() <= 0) {
            throw new IOException("下载安装包为空。");
        }
        return apk;
    }

    private String resolveLatestApkDownloadUrl() throws IOException, JSONException {
        HttpURLConnection connection = (HttpURLConnection) new URL("https://api.github.com/repos/nandieling/OmniPlay/releases/latest").openConnection();
        connection.setConnectTimeout(12000);
        connection.setReadTimeout(30000);
        connection.setRequestProperty("Accept", "application/vnd.github+json");
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/1.5");
        int status = connection.getResponseCode();
        String body;
        try (InputStream input = status >= 400 ? connection.getErrorStream() : connection.getInputStream()) {
            body = readRemoteText(input);
        } finally {
            connection.disconnect();
        }

        if (status < 200 || status >= 300) {
            throw new IOException("检查 GitHub Release 失败：" + status);
        }

        JSONArray assets = new JSONObject(body).optJSONArray("assets");
        String bestUrl = "";
        int bestScore = Integer.MIN_VALUE;
        if (assets != null) {
            for (int index = 0; index < assets.length(); index++) {
                JSONObject asset = assets.optJSONObject(index);
                if (asset == null) {
                    continue;
                }

                String name = asset.optString("name");
                String url = asset.optString("browser_download_url");
                if (name == null || url == null || !name.toLowerCase(Locale.ROOT).endsWith(".apk")) {
                    continue;
                }

                int score = apkAssetScore(name);
                if (score > bestScore) {
                    bestScore = score;
                    bestUrl = url;
                }
            }
        }

        if (bestUrl.isEmpty()) {
            throw new IOException("GitHub 最新 Release 中未找到 APK 安装包。");
        }
        return bestUrl;
    }

    private int apkAssetScore(String name) {
        String normalized = name == null ? "" : name.toLowerCase(Locale.ROOT);
        int score = 0;
        if (normalized.contains("omniplay")) {
            score += 20;
        }
        if (normalized.contains("android")) {
            score += 20;
        }
        if (normalized.contains("tv") || normalized.contains("leanback")) {
            score += 30;
        }
        if (normalized.contains("arm64") || normalized.contains("universal")) {
            score += 10;
        }
        return score;
    }

    private void installDownloadedApk(File apk, TextView updateStatus) {
        if (apk == null || !apk.isFile()) {
            updateStatus.setText("安装包不存在。");
            return;
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !getPackageManager().canRequestPackageInstalls()) {
                updateStatus.setText("请允许 OmniPlay 安装未知应用后重试。");
                Intent settings = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName()));
                startActivity(settings);
                return;
            }

            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(ApkUpdateProvider.apkUri(this), "application/vnd.android.package-archive");
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(intent);
        } catch (Exception error) {
            updateStatus.setText("打开安装器失败：" + error.getMessage());
        }
    }

    private static String readRemoteText(InputStream input) throws IOException {
        if (input == null) {
            return "";
        }

        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    private View posterCard(Models.LibraryItem item, boolean showProgress) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setFocusable(true);
        card.setClickable(true);
        card.setPadding(dp(8), dp(8), dp(8), dp(10));
        card.setBackground(rounded(Color.TRANSPARENT, Color.TRANSPARENT, 0, dp(8)));
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = dp(204);
        params.height = ViewGroup.LayoutParams.WRAP_CONTENT;
        params.setMargins(dp(6), dp(6), dp(10), dp(22));
        card.setLayoutParams(params);
        applyCardFocus(card);

        FrameLayout posterFrame = new FrameLayout(this);
        posterFrame.setPadding(dp(1), dp(1), dp(1), dp(1));
        posterFrame.setClipToOutline(true);
        posterFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), dp(10)));
        card.addView(posterFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(282)));

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setClipToOutline(true);
        posterFrame.addView(poster, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        loadPoster(poster, item.posterAssetId);

        LinearLayout badges = new LinearLayout(this);
        badges.setGravity(Gravity.CENTER_VERTICAL);
        badges.setOrientation(LinearLayout.HORIZONTAL);
        FrameLayout.LayoutParams badgeParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.LEFT | Gravity.BOTTOM);
        badgeParams.setMargins(dp(10), 0, dp(10), dp(10));
        posterFrame.addView(badges, badgeParams);

        String rating = ratingText(item.voteAverage);
        if (rating != null) {
            badges.addView(badge(rating));
        }

        if (showProgress) {
            int progress = item.progressPercent();
            if (progress > 0 && progress < 95) {
                card.addView(progressBar(progress, dp(188), dp(5)), margin(new LinearLayout.LayoutParams(dp(188), dp(5)), 0, dp(8), 0, 0));
            }
        }

        TextView name = text(item.title, 16, Typeface.BOLD, COLOR_TEXT);
        name.setMaxLines(2);
        name.setEllipsize(TextUtils.TruncateAt.END);
        card.addView(name, margin(matchWidth(), 0, dp(10), 0, 0));

        LinearLayout yearRow = new LinearLayout(this);
        yearRow.setGravity(Gravity.CENTER_VERTICAL);
        card.addView(yearRow, margin(matchWidth(), 0, dp(2), 0, 0));
        String year = releaseYearText(item.releaseDate);
        yearRow.addView(text(year == null ? "" : year, 13, Typeface.NORMAL, COLOR_SUBTLE), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView watched = text(item.isWatched ? "✓" : "○", 17, Typeface.BOLD, item.isWatched ? COLOR_FOCUS : COLOR_SUBTLE);
        watched.setGravity(Gravity.CENTER);
        watched.setPadding(0, 0, 0, 0);
        watched.setMinHeight(dp(28));
        watched.setFocusable(true);
        watched.setClickable(true);
        watched.setBackgroundColor(Color.TRANSPARENT);
        watched.setOnClickListener(view -> toggleLibraryItemWatched(item));
        applyWatchedIconFocus(watched, item.isWatched);
        yearRow.addView(watched, new LinearLayout.LayoutParams(dp(30), dp(28)));

        card.setOnClickListener(view -> showDetail(item.id));
        return card;
    }

    private void showDetail(String itemId) {
        selectedSeasonId = "";
        loadDetail(itemId);
    }

    private void loadDetail(String itemId) {
        screen = Screen.DETAIL;
        showLoading("正在加载详情");
        runAsync(
                () -> api.getLibraryItemDetail(itemId),
                this::renderDetail,
                error -> renderError(error.getMessage(), this::showHome));
    }

    private void renderDetail(Models.LibraryDetail detail) {
        screen = Screen.DETAIL;
        currentDetail = detail;
        root.removeAllViews();
        applyAppBackground();

        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(44), dp(28), dp(44), dp(34));
        root.addView(page, match());

        ScrollView scroll = new ScrollView(this);
        page.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(content, matchWidth());

        LinearLayout hero = new LinearLayout(this);
        hero.setGravity(Gravity.TOP);
        content.addView(hero, margin(matchWidth(), 0, 0, 0, dp(34)));

        FrameLayout posterFrame = new FrameLayout(this);
        posterFrame.setPadding(0, 0, 0, 0);
        posterFrame.setClipToOutline(true);
        posterFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), dp(10)));
        hero.addView(posterFrame, new LinearLayout.LayoutParams(dp(230), dp(342)));

        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        poster.setClipToOutline(true);
        posterFrame.addView(poster, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        loadPoster(poster, detail.posterAssetId);

        LinearLayout info = new LinearLayout(this);
        info.setOrientation(LinearLayout.VERTICAL);
        hero.addView(info, margin(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f), dp(28), 0, 0, 0));
        info.addView(text(detail.title, 34, Typeface.BOLD, COLOR_TEXT));

        LinearLayout facts = new LinearLayout(this);
        facts.setGravity(Gravity.CENTER_VERTICAL);
        info.addView(facts, margin(matchWidth(), 0, dp(8), 0, dp(16)));
        String year = releaseYearText(detail.releaseDate);
        if (year != null) {
            facts.addView(metaPill(year));
        }
        String rating = ratingText(detail.voteAverage);
        if (rating != null) {
            facts.addView(metaPill("评分 " + rating), margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(32)), dp(8), 0, 0, 0));
        }

        if (detail.overview != null && !detail.overview.isEmpty()) {
            TextView overview = text(detail.overview, 17, Typeface.NORMAL, COLOR_TEXT);
            overview.setLineSpacing(dp(3), 1f);
            overview.setMaxLines(7);
            overview.setEllipsize(TextUtils.TruncateAt.END);
            info.addView(overview, margin(matchWidth(), 0, 0, 0, dp(22)));
        }

        Models.VideoFile mainFile = resolveMainFile(detail);
        if (mainFile != null) {
            LinearLayout actions = new LinearLayout(this);
            actions.setGravity(Gravity.CENTER_VERTICAL);
            info.addView(actions, margin(matchWidth(), 0, 0, 0, dp(8)));

            Button play = button(playButtonText(mainFile));
            actions.addView(play, new LinearLayout.LayoutParams(dp(220), dp(54)));
            play.setOnClickListener(view -> playFile(mainFile));
            play.requestFocus();

            Button watchStatus = button(watchedStatusLabel(mainFile), isEffectivelyWatched(mainFile));
            actions.addView(watchStatus, margin(new LinearLayout.LayoutParams(dp(104), dp(44)), dp(14), 0, 0, 0));
            watchStatus.setOnClickListener(view -> toggleVideoWatched(mainFile));

            LinearLayout timeline = timeline(detail, mainFile);
            actions.addView(timeline, margin(new LinearLayout.LayoutParams(dp(430), dp(44)), dp(14), 0, 0, 0));
        }

        if ("tv".equals(detail.itemKind) && !detail.seasons.isEmpty()) {
            renderEpisodes(content, detail, mainFile);
        }
    }

    private void renderEpisodes(LinearLayout content, Models.LibraryDetail detail, Models.VideoFile mainFile) {
        if (!hasSelectedSeason(detail)) {
            selectedSeasonId = defaultSeasonId(detail, mainFile);
        }

        Models.Season selectedSeason = selectedSeason(detail);
        if (selectedSeason == null) {
            return;
        }

        LinearLayout seasonHeader = new LinearLayout(this);
        seasonHeader.setGravity(Gravity.CENTER_VERTICAL);
        content.addView(seasonHeader, margin(matchWidth(), 0, dp(18), 0, dp(18)));

        for (Models.Season season : detail.seasons) {
            Button seasonButton = button(seasonDisplayLabel(season), season.id.equals(selectedSeason.id));
            seasonHeader.addView(seasonButton, margin(new LinearLayout.LayoutParams(dp(120), dp(44)), 0, 0, dp(10), 0));
            seasonButton.setOnClickListener(view -> {
                selectedSeasonId = season.id;
                renderDetail(detail);
            });
        }

        GridLayout episodeGrid = new GridLayout(this);
        episodeGrid.setColumnCount(4);
        content.addView(episodeGrid);

        for (Models.Episode episode : selectedSeason.episodes) {
            episodeGrid.addView(episodeCard(episode, detail.posterAssetId));
        }
    }

    private View episodeCard(Models.Episode episode, String posterAssetId) {
        LinearLayout card = new LinearLayout(this);
        boolean showDetails = showEpisodeDetails;
        card.setOrientation(LinearLayout.VERTICAL);
        card.setFocusable(episode.videoFile != null);
        card.setClickable(episode.videoFile != null);
        card.setPadding(0, 0, dp(8), dp(10));
        card.setBackground(rounded(showDetails ? COLOR_SURFACE : Color.TRANSPARENT, showDetails ? COLOR_BORDER : Color.TRANSPARENT, showDetails ? dp(1) : 0, dp(8)));
        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = dp(286);
        params.height = ViewGroup.LayoutParams.WRAP_CONTENT;
        params.setMargins(0, dp(6), dp(18), dp(20));
        card.setLayoutParams(params);
        applyCardFocus(card);

        FrameLayout stillFrame = new FrameLayout(this);
        stillFrame.setPadding(0, 0, 0, 0);
        stillFrame.setClipToOutline(true);
        stillFrame.setBackground(rounded(COLOR_SURFACE_ALT, COLOR_BORDER, dp(1), dp(10)));
        card.addView(stillFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(152)));

        ImageView still = new ImageView(this);
        still.setScaleType(ImageView.ScaleType.CENTER_CROP);
        still.setClipToOutline(true);
        stillFrame.addView(still, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (episode.stillAssetId != null) {
            imageLoader.load(still, api.thumbnailUrl(episode.stillAssetId), api.cookieHeader());
        } else {
            loadPoster(still, posterAssetId);
        }

        TextView title = text(episodeTitleDisplay(episode, showDetails), 15, Typeface.BOLD, COLOR_TEXT);
        title.setMaxLines(2);
        title.setEllipsize(TextUtils.TruncateAt.END);
        if (!showDetails) {
            title.setGravity(Gravity.CENTER);
        }
        card.addView(title, margin(matchWidth(), 0, dp(10), 0, 0));

        String facts = episodeFacts(episode);
        if (showDetails && !facts.isEmpty()) {
            TextView factView = text(facts, 13, Typeface.NORMAL, COLOR_SUBTLE);
            factView.setSingleLine(true);
            factView.setEllipsize(TextUtils.TruncateAt.END);
            card.addView(factView, margin(matchWidth(), 0, dp(4), 0, 0));
        }
        if (showDetails && episode.overview != null && !episode.overview.isEmpty()) {
            TextView overview = text(episode.overview, 13, Typeface.NORMAL, COLOR_SUBTLE);
            overview.setMaxLines(2);
            overview.setEllipsize(TextUtils.TruncateAt.END);
            card.addView(overview, margin(matchWidth(), 0, dp(6), 0, 0));
        }

        if (episode.videoFile != null) {
            int progress = fileProgressPercent(episode.videoFile);
            if (showDetails && progress > 0 && progress < 95) {
                card.addView(progressBar(progress, dp(258), dp(4)), margin(new LinearLayout.LayoutParams(dp(258), dp(4)), 0, dp(10), 0, 0));
            }
            card.setOnClickListener(view -> playFile(episode.videoFile.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber))));
        }

        return card;
    }

    private void playFile(Models.VideoFile file) {
        List<Models.VideoFile> queue = playbackQueueFor(file);
        int index = indexOfVideoFile(queue, file.id);
        playFile(file, queue, Math.max(0, index));
    }

    private void playFile(Models.VideoFile file, List<Models.VideoFile> queue, int queueIndex) {
        screen = Screen.PLAYER;
        root.removeAllViews();
        root.setBackgroundColor(Color.BLACK);
        root.setFocusable(true);
        root.setFocusableInTouchMode(false);
        root.setOnKeyListener((view, keyCode, event) ->
                event.getAction() == KeyEvent.ACTION_DOWN &&
                        screen == Screen.PLAYER &&
                        playerView != null &&
                        handlePlayerKeyDown(keyCode, event));
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        activeDirectPlayback = direct4KPlayback && isLikely4K(file);
        playerView = new MpvVideoView(this, activeDirectPlayback);
        root.addView(playerView, match());
        playerView.setOnKeyListener((view, keyCode, event) ->
                event.getAction() == KeyEvent.ACTION_DOWN &&
                        screen == Screen.PLAYER &&
                        playerView != null &&
                        handlePlayerKeyDown(keyCode, event));
        playerPaused = false;

        LinearLayout overlay = new LinearLayout(this);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.setPadding(dp(36), dp(28), dp(36), dp(28));
        overlay.setBackgroundColor(Color.argb(128, 0, 0, 0));
        overlay.addView(text(file.label(), 22, Typeface.BOLD, COLOR_PLAYER_TEXT));
        String summary = file.mediaSummary();
        if (!summary.isEmpty()) {
            overlay.addView(text(summary, 14, Typeface.NORMAL, COLOR_PLAYER_MUTED));
        }
        TextView status = text(activeDirectPlayback ? "正在准备4K直出硬解播放" : "正在准备安卓端硬解播放", 14, Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerStatusText = status;
        overlay.addView(status, margin(matchWidth(), 0, dp(8), 0, 0));
        FrameLayout.LayoutParams overlayParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP);
        root.addView(overlay, overlayParams);
        overlay.setElevation(dp(24));
        overlay.setTranslationZ(dp(24));
        overlay.bringToFront();
        playerTopOverlay = overlay;

        TextView subtitles = text("", 28, Typeface.BOLD, Color.WHITE);
        subtitles.setGravity(Gravity.CENTER);
        subtitles.setShadowLayer(dp(3), 0, dp(1), Color.BLACK);
        subtitles.setMaxLines(3);
        subtitles.setIncludeFontPadding(true);
        subtitles.setVisibility(View.GONE);
        FrameLayout.LayoutParams subtitleParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        subtitleParams.setMargins(dp(96), 0, dp(96), dp(132));
        root.addView(subtitles, subtitleParams);
        subtitles.setElevation(dp(30));
        subtitles.setTranslationZ(dp(30));
        subtitles.bringToFront();
        playerSubtitleOverlay = subtitles;

        LinearLayout controls = playerControls(file);
        FrameLayout.LayoutParams controlParams = new FrameLayout.LayoutParams(dp(920), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        controlParams.setMargins(0, 0, 0, dp(28));
        root.addView(controls, controlParams);
        controls.setElevation(dp(28));
        controls.setTranslationZ(dp(28));
        controls.bringToFront();
        playerControlsPanel = controls;

        activePlaybackFile = file;
        activePlaybackQueue = queue == null || queue.isEmpty() ? Collections.singletonList(file) : new ArrayList<>(queue);
        activePlaybackIndex = Math.max(0, Math.min(queueIndex, activePlaybackQueue.size() - 1));
        advancingPlaybackPart = false;
        runAsync(
                () -> resolvePlayback(file.id),
                playback -> startResolvedPlayback(file, playback, status),
                error -> renderPlayerError(error.getMessage()));
        if (playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
        }
    }

    private ResolvedPlayback resolvePlayback(String videoFileId) throws Exception {
        Models.AppSettings settings;
        try {
            settings = api.getSettings();
        } catch (Exception ignored) {
            settings = Models.AppSettings.defaults();
        }

        try {
            String ticket = api.createPlaybackTicket(videoFileId);
            if (ticket != null && !ticket.isEmpty()) {
                return new ResolvedPlayback(api.streamUrl(videoFileId, ticket), ticket, "", settings);
            }
        } catch (IOException error) {
            if (error.getMessage() == null || !error.getMessage().contains("404")) {
                throw error;
            }
        }

        return new ResolvedPlayback(api.streamUrl(videoFileId), "", api.cookieHeader(), settings);
    }

    private void startResolvedPlayback(Models.VideoFile file, ResolvedPlayback playback, TextView status) {
        if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
            return;
        }

        if (playback.url == null || playback.url.isEmpty()) {
            renderPlayerError("播放地址为空。");
            return;
        }

        status.setText("正在开始播放");
        activePlaybackTicket = playback.ticket;
        playerLoadStartedAtMs = System.currentTimeMillis();
        final boolean[] playbackErrorShown = {false};
        playerView.setPlaybackListener(new MpvVideoView.PlaybackListener() {
            private boolean didStart;

            @Override
            public void onLoaded() {
                if (didStart || screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                    return;
                }

                didStart = true;
                scheduleResumeSeek(file);
                status.setText(activeDirectPlayback ? "4K直出硬解播放" : "安卓端硬解播放");
                applyDefaultPlaybackTracks(file, playback.settings);
                startPlayerUiUpdates(file);
                startProgressUpdates(file);
                showPlayerChromeTemporarily();
            }

            @Override
            public void onError(String message) {
                if (screen == Screen.PLAYER && activePlaybackFile == file) {
                    playbackErrorShown[0] = true;
                    renderPlayerError(message);
                }
            }
        });
        boolean accepted = playerView.play(playback.url, playback.cookieHeader, file.positionSeconds);
        if (!accepted && !playbackErrorShown[0]) {
            renderPlayerError(playerView.lastError());
        }
    }

    private void scheduleResumeSeek(Models.VideoFile file) {
        if (file.positionSeconds <= 5) {
            return;
        }

        main.postDelayed(() -> {
            if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file) {
                playerView.seekTo(file.positionSeconds);
            }
        }, 350);
        main.postDelayed(() -> {
            if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file && playerView.currentTimeSeconds() < Math.max(0, file.positionSeconds - 3)) {
                playerView.seekTo(file.positionSeconds);
            }
        }, 1600);
    }

    private LinearLayout playerControls(Models.VideoFile file) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(18), dp(14), dp(18), dp(16));
        panel.setBackground(rounded(Color.argb(184, 0, 0, 0), Color.argb(90, 255, 255, 255), dp(1), dp(18)));

        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setGravity(Gravity.CENTER_VERTICAL);
        panel.addView(progressRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(34)));

        playerCurrentTime = text("0:00", 13, Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerCurrentTime.setGravity(Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        progressRow.addView(playerCurrentTime, new LinearLayout.LayoutParams(dp(70), ViewGroup.LayoutParams.MATCH_PARENT));

        playerProgressTrack = new FrameLayout(this);
        playerProgressTrack.setBackground(rounded(Color.argb(180, 255, 255, 255), Color.TRANSPARENT, 0, dp(3)));
        playerProgressFill = new View(this);
        playerProgressFill.setBackground(rounded(COLOR_ACCENT, Color.TRANSPARENT, 0, dp(3)));
        playerProgressTrack.addView(playerProgressFill, new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT));
        progressRow.addView(playerProgressTrack, margin(new LinearLayout.LayoutParams(0, dp(6), 1f), dp(12), 0, dp(12), 0));

        playerTotalTime = text("--:--", 13, Typeface.BOLD, COLOR_PLAYER_MUTED);
        playerTotalTime.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
        progressRow.addView(playerTotalTime, new LinearLayout.LayoutParams(dp(70), ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout buttons = new LinearLayout(this);
        buttons.setGravity(Gravity.CENTER);
        panel.addView(buttons, margin(matchWidth(), 0, dp(12), 0, 0));

        ImageButton pause = playerIconButton(R.drawable.ic_player_pause);
        pause.setContentDescription("播放/暂停");
        playerPlayPauseButton = pause;
        buttons.addView(pause, new LinearLayout.LayoutParams(dp(64), dp(48)));
        pause.setOnClickListener(view -> {
            togglePlayerPaused();
            showPlayerChromeTemporarily();
        });

        ImageButton audio = playerIconButton(R.drawable.ic_player_audio);
        audio.setContentDescription("音轨");
        buttons.addView(audio, margin(new LinearLayout.LayoutParams(dp(64), dp(48)), dp(18), 0, dp(8), 0));
        audio.setOnClickListener(view -> showAudioMenu(file, audio));

        ImageButton subtitle = playerIconButton(R.drawable.ic_player_subtitle);
        subtitle.setContentDescription("字幕");
        buttons.addView(subtitle, margin(new LinearLayout.LayoutParams(dp(64), dp(48)), dp(8), 0, 0, 0));
        subtitle.setOnClickListener(view -> showSubtitleMenu(file, subtitle));

        return panel;
    }

    private void startPlayerUiUpdates(Models.VideoFile file) {
        stopPlayerUiUpdates();
        playerUiUpdater = new Runnable() {
            @Override
            public void run() {
                if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                    return;
                }

                updatePlayerUi(file);
                main.postDelayed(this, 1000);
            }
        };
        playerUiUpdater.run();
    }

    private void stopPlayerUiUpdates() {
        if (playerUiUpdater != null) {
            main.removeCallbacks(playerUiUpdater);
            playerUiUpdater = null;
        }
    }

    private void updatePlayerUi(Models.VideoFile file) {
        double duration = playerView == null ? 0 : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        double position = resolvePlayerPosition(file, duration);
        PlaybackTimeline timeline = livePlaybackTimeline(file, position, duration);

        if (playerCurrentTime != null) {
            playerCurrentTime.setText(formatPlaybackTime(timeline.positionSeconds));
        }
        if (playerTotalTime != null) {
            playerTotalTime.setText(timeline.durationSeconds > 0 ? formatPlaybackTime(timeline.durationSeconds) : "--:--");
        }
        if (playerProgressTrack != null && playerProgressFill != null) {
            int width = playerProgressTrack.getWidth();
            int fillWidth = timeline.durationSeconds <= 0 || width <= 0 ? 0 : Math.max(0, Math.min(width, (int) Math.round(width * timeline.positionSeconds / timeline.durationSeconds)));
            ViewGroup.LayoutParams params = playerProgressFill.getLayoutParams();
            if (params.width != fillWidth) {
                params.width = fillWidth;
                playerProgressFill.setLayoutParams(params);
            }
            updatePlayerProgressSegments(timeline.durationSeconds, position, duration);
        }
        maybeAdvanceMoviePart(file, position, duration);
        if (playerStatusText != null) {
            if (position > 0 || duration > 0) {
                playerStatusText.setText(activeDirectPlayback ? "4K直出硬解播放" : "安卓端硬解播放");
            } else {
                long elapsed = playerLoadStartedAtMs <= 0 ? 0 : System.currentTimeMillis() - playerLoadStartedAtMs;
                playerStatusText.setText(elapsed >= 8000 ? "正在等待视频输出" : "正在打开视频流");
            }
        }
    }

    private double resolvePlayerPosition(Models.VideoFile file, double duration) {
        double position = playerView == null ? 0 : playerView.currentTimeSeconds();
        if (position > 0) {
            return position;
        }

        double percent = playerView == null ? -1 : playerView.percentPosition();
        if (percent > 0 && duration > 0) {
            return duration * percent / 100d;
        }

        double remaining = playerView == null ? -1 : playerView.remainingTimeSeconds();
        if (remaining >= 0 && duration > 0 && remaining <= duration) {
            double inferred = duration - remaining;
            if (inferred > 0) {
                return inferred;
            }
        }

        return Math.max(0, position);
    }

    private void updatePlayerProgressSegments(double totalDuration, double currentPosition, double currentDuration) {
        if (playerProgressTrack == null) {
            return;
        }

        for (View segmentView : playerProgressSegmentViews) {
            playerProgressTrack.removeView(segmentView);
        }
        playerProgressSegmentViews.clear();

        if (activePlaybackQueue == null || activePlaybackQueue.size() <= 1 || activePlaybackIndex < 0 || totalDuration <= 0) {
            return;
        }

        int trackWidth = playerProgressTrack.getWidth();
        if (trackWidth <= 0) {
            return;
        }

        double cursor = 0;
        for (int index = 0; index < activePlaybackQueue.size() - 1; index++) {
            cursor += playbackPartDurationSeconds(index, currentPosition, currentDuration);
            if (cursor <= 0 || cursor >= totalDuration) {
                continue;
            }

            View divider = new View(this);
            divider.setBackgroundColor(Color.argb(230, 255, 255, 255));
            int dividerWidth = dp(2);
            int left = Math.max(0, Math.min(trackWidth - dividerWidth, (int) Math.round(trackWidth * cursor / totalDuration) - dividerWidth / 2));
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dividerWidth, ViewGroup.LayoutParams.MATCH_PARENT);
            params.leftMargin = left;
            playerProgressTrack.addView(divider, params);
            playerProgressSegmentViews.add(divider);
        }
    }

    private double playbackPartDurationSeconds(int index, double currentPosition, double currentDuration) {
        if (activePlaybackQueue == null || index < 0 || index >= activePlaybackQueue.size()) {
            return 0;
        }

        Models.VideoFile part = activePlaybackQueue.get(index);
        return index == activePlaybackIndex
                ? Math.max(0, Math.max(part.durationSeconds, Math.max(currentDuration, currentPosition)))
                : Math.max(0, part.durationSeconds);
    }

    private PlaybackTimeline livePlaybackTimeline(Models.VideoFile file, double currentPosition, double currentDuration) {
        if (activePlaybackQueue == null || activePlaybackQueue.size() <= 1 || activePlaybackIndex < 0) {
            return new PlaybackTimeline(Math.max(0, currentPosition), Math.max(0, currentDuration));
        }

        double position = 0;
        double duration = 0;
        for (int index = 0; index < activePlaybackQueue.size(); index++) {
            double partDuration = playbackPartDurationSeconds(index, currentPosition, currentDuration);
            if (index < activePlaybackIndex) {
                position += partDuration;
            }
            duration += partDuration;
        }
        position += Math.max(0, currentPosition);
        return new PlaybackTimeline(Math.min(position, Math.max(duration, position)), duration);
    }

    private void maybeAdvanceMoviePart(Models.VideoFile file, double position, double duration) {
        if (advancingPlaybackPart ||
                activePlaybackQueue == null ||
                activePlaybackQueue.size() <= 1 ||
                activePlaybackIndex + 1 >= activePlaybackQueue.size() ||
                duration <= 0 ||
                position < Math.max(duration - 1.5, duration * 0.995)) {
            return;
        }

        advancingPlaybackPart = true;
        Models.VideoFile nextFile = activePlaybackQueue.get(activePlaybackIndex + 1);
        ArrayList<Models.VideoFile> queue = new ArrayList<>(activePlaybackQueue);
        int nextIndex = activePlaybackIndex + 1;
        double finalDuration = Math.max(duration, position);
        runAsync(
                () -> {
                    api.updatePlaybackProgress(file.id, finalDuration, finalDuration);
                    return null;
                },
                ignored -> transitionToPlaybackPart(nextFile, queue, nextIndex),
                ignored -> transitionToPlaybackPart(nextFile, queue, nextIndex));
    }

    private void transitionToPlaybackPart(Models.VideoFile nextFile, List<Models.VideoFile> queue, int nextIndex) {
        stopPlayerUiUpdates();
        stopProgressUpdates();
        stopSubtitleOverlayUpdates();
        closePlayerMenu(null);
        activePlaybackFile = null;
        if (playerView != null) {
            playerView.destroyPlayer();
            playerView = null;
        }
        playFile(nextFile, queue, nextIndex);
    }

    private void showAudioMenu(Models.VideoFile file, View returnFocus) {
        showPlayerChromeTemporarily();
        ArrayList<PlayerMenuOption> options = new ArrayList<>();
        options.add(new PlayerMenuOption("默认音轨", () -> {
            if (playerView != null) {
                playerView.setAudioTrack("auto");
            }
        }));
        for (int index = 0; index < file.audioTracks.size(); index++) {
            Models.VideoFileStream track = file.audioTracks.get(index);
            int trackOrdinal = index;
            options.add(new PlayerMenuOption(formatAudioTrack(track, index), () -> {
                if (playerView != null) {
                    playerView.setAudioTrack(String.valueOf(trackOrdinal + 1));
                }
            }));
        }
        showPlayerMenu("音轨", options, returnFocus);
    }

    private void showSubtitleMenu(Models.VideoFile file, View returnFocus) {
        showPlayerChromeTemporarily();
        ArrayList<PlayerMenuOption> options = new ArrayList<>();
        options.add(new PlayerMenuOption("关闭字幕", () -> {
            if (playerView != null) {
                playerView.setSubtitleTrack("no");
            }
            clearSubtitleOverlay();
        }));
        for (int index = 0; index < file.subtitleStreams.size(); index++) {
            Models.VideoFileStream stream = file.subtitleStreams.get(index);
            int ordinal = index;
            options.add(new PlayerMenuOption(formatSubtitleStream(stream, index), () -> selectEmbeddedSubtitle(file, ordinal)));
        }
        for (Models.SubtitleTrack track : activeExternalSubtitles) {
            options.add(new PlayerMenuOption(formatExternalSubtitle(track), () -> selectExternalSubtitle(track)));
        }
        showPlayerMenu("字幕", options, returnFocus);
    }

    private void showPlayerMenu(String title, List<PlayerMenuOption> options, View returnFocus) {
        showPlayerChromeTemporarily();
        closePlayerMenu(returnFocus);

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(18), dp(16), dp(18), dp(18));
        panel.setBackground(rounded(Color.argb(232, 0, 0, 0), Color.argb(100, 255, 255, 255), dp(1), dp(12)));
        panel.setElevation(dp(18));
        panel.addView(text(title, 18, Typeface.BOLD, COLOR_PLAYER_TEXT), margin(matchWidth(), 0, 0, 0, dp(10)));

        Button first = null;
        for (PlayerMenuOption option : options) {
            Button button = playerMenuButton(option.label);
            panel.addView(button, margin(new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)), 0, dp(6), 0, 0));
            button.setOnClickListener(view -> {
                option.action.run();
                closePlayerMenu(returnFocus);
                showPlayerChromeTemporarily();
            });
            if (first == null) {
                first = button;
            }
        }

        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dp(560), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.RIGHT | Gravity.BOTTOM);
        params.setMargins(0, 0, dp(44), dp(126));
        root.addView(panel, params);
        playerMenuPanel = panel;
        panel.setElevation(dp(34));
        panel.setTranslationZ(dp(34));
        panel.bringToFront();
        if (first != null) {
            first.requestFocus();
        }
    }

    private void closePlayerMenu(View returnFocus) {
        if (playerMenuPanel != null) {
            ViewParent parent = playerMenuPanel.getParent();
            if (parent instanceof ViewGroup) {
                ((ViewGroup) parent).removeView(playerMenuPanel);
            }
            playerMenuPanel = null;
        }
        if (returnFocus != null) {
            returnFocus.requestFocus();
        }
    }

    private void selectEmbeddedSubtitle(Models.VideoFile file, int ordinal) {
        if (playerView == null || ordinal < 0 || ordinal >= file.subtitleStreams.size()) {
            return;
        }
        String ticket = activePlaybackTicket;
        String subtitleUrl = api.embeddedSubtitleUrl(file.id, ordinal + 1, ticket);
        if (activeDirectPlayback && !subtitleUrl.isEmpty()) {
            loadSubtitleOverlay(subtitleUrl, ticket == null || ticket.isEmpty() ? api.cookieHeader() : "");
            playerView.setSubtitleTrack("no");
            return;
        }
        playerView.setSubtitleTrack(String.valueOf(ordinal + 1));
    }

    private void selectExternalSubtitle(Models.SubtitleTrack track) {
        if (playerView == null) {
            return;
        }
        String ticket = activePlaybackTicket;
        String cookieHeader = ticket == null || ticket.isEmpty() ? api.cookieHeader() : "";
        String subtitleUrl = api.subtitleUrl(track, ticket);
        if (!subtitleUrl.isEmpty()) {
            if (activeDirectPlayback) {
                loadSubtitleOverlay(subtitleUrl, cookieHeader);
                playerView.setSubtitleTrack("no");
                return;
            }
            playerView.addSubtitle(subtitleUrl, cookieHeader);
        }
    }

    private void loadSubtitleOverlay(String url, String cookieHeader) {
        if (url == null || url.isEmpty()) {
            clearSubtitleOverlay();
            return;
        }
        runAsync(
                () -> parseSubtitleCues(fetchText(url, cookieHeader)),
                cues -> {
                    if (screen != Screen.PLAYER || playerView == null || cues.isEmpty()) {
                        return;
                    }
                    activeSubtitleCues = cues;
                    startSubtitleOverlayUpdates();
                },
                ignored -> clearSubtitleOverlay());
    }

    private String fetchText(String url, String cookieHeader) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(20000);
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
        if (cookieHeader != null && !cookieHeader.isEmpty()) {
            connection.setRequestProperty("Cookie", cookieHeader);
        }
        try (InputStream input = connection.getInputStream();
             BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            StringBuilder builder = new StringBuilder();
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line).append('\n');
            }
            return builder.toString();
        } finally {
            connection.disconnect();
        }
    }

    private List<SubtitleCue> parseSubtitleCues(String text) {
        ArrayList<SubtitleCue> cues = new ArrayList<>();
        if (text == null || text.isEmpty()) {
            return cues;
        }

        String[] lines = text.replace("\r\n", "\n").replace('\r', '\n').split("\n");
        for (int index = 0; index < lines.length; index++) {
            String line = lines[index].trim();
            if (line.isEmpty() || line.equalsIgnoreCase("WEBVTT") || line.startsWith("NOTE")) {
                continue;
            }
            if (!line.contains("-->") && index + 1 < lines.length && lines[index + 1].contains("-->")) {
                index++;
                line = lines[index].trim();
            }
            if (!line.contains("-->")) {
                continue;
            }

            String[] parts = line.split("-->", 2);
            double start = parseSubtitleTime(parts[0]);
            double end = parseSubtitleTime(parts[1].trim().split("\\s+", 2)[0]);
            StringBuilder body = new StringBuilder();
            while (index + 1 < lines.length && !lines[index + 1].trim().isEmpty()) {
                String bodyLine = lines[++index].trim();
                if (!bodyLine.isEmpty()) {
                    if (body.length() > 0) {
                        body.append('\n');
                    }
                    body.append(bodyLine.replaceAll("<[^>]+>", ""));
                }
            }
            if (end > start && body.length() > 0) {
                cues.add(new SubtitleCue(start, end, body.toString()));
            }
        }
        return cues;
    }

    private double parseSubtitleTime(String value) {
        String clean = value == null ? "" : value.trim().replace(',', '.');
        String[] parts = clean.split(":");
        try {
            if (parts.length == 3) {
                return Integer.parseInt(parts[0]) * 3600d + Integer.parseInt(parts[1]) * 60d + Double.parseDouble(parts[2]);
            }
            if (parts.length == 2) {
                return Integer.parseInt(parts[0]) * 60d + Double.parseDouble(parts[1]);
            }
        } catch (NumberFormatException ignored) {
        }
        return 0;
    }

    private void startSubtitleOverlayUpdates() {
        stopSubtitleOverlayUpdates();
        subtitleOverlayUpdater = new Runnable() {
            @Override
            public void run() {
                updateSubtitleOverlay();
                if (screen == Screen.PLAYER && playerView != null) {
                    main.postDelayed(this, 250);
                }
            }
        };
        subtitleOverlayUpdater.run();
    }

    private void updateSubtitleOverlay() {
        if (playerSubtitleOverlay == null || activeSubtitleCues.isEmpty() || playerView == null) {
            return;
        }
        double position = playerView.currentTimeSeconds();
        String subtitle = "";
        for (SubtitleCue cue : activeSubtitleCues) {
            if (position >= cue.startSeconds && position <= cue.endSeconds) {
                subtitle = cue.text;
                break;
            }
        }
        playerSubtitleOverlay.setText(subtitle);
        playerSubtitleOverlay.setVisibility(subtitle.isEmpty() ? View.GONE : View.VISIBLE);
        if (!subtitle.isEmpty()) {
            playerSubtitleOverlay.bringToFront();
        }
    }

    private void clearSubtitleOverlay() {
        activeSubtitleCues = Collections.emptyList();
        stopSubtitleOverlayUpdates();
        if (playerSubtitleOverlay != null) {
            playerSubtitleOverlay.setText("");
            playerSubtitleOverlay.setVisibility(View.GONE);
        }
    }

    private void stopSubtitleOverlayUpdates() {
        if (subtitleOverlayUpdater != null) {
            main.removeCallbacks(subtitleOverlayUpdater);
            subtitleOverlayUpdater = null;
        }
    }

    private void applyDefaultPlaybackTracks(Models.VideoFile file, Models.AppSettings settings) {
        Models.PlaybackSettings playback = settings == null ? Models.PlaybackSettings.defaults() : settings.playback;
        Models.VideoFileStream audioTrack = resolvePreferredAudioTrack(file, playback.defaultAudioLanguage);
        if (playerView != null) {
            playerView.setAudioTrack(audioTrack == null ? "auto" : mpvAudioTrackId(file, audioTrack));
        }
        loadDefaultSubtitle(file, playback.defaultSubtitleLanguage);
    }

    private String mpvAudioTrackId(Models.VideoFile file, Models.VideoFileStream audioTrack) {
        for (int index = 0; index < file.audioTracks.size(); index++) {
            if (file.audioTracks.get(index) == audioTrack) {
                return String.valueOf(index + 1);
            }
        }
        return String.valueOf(audioTrack.index);
    }

    private void loadDefaultSubtitle(Models.VideoFile file, String languagePreference) {
        runAsync(
                () -> api.getSubtitles(file.id),
                tracks -> {
                    if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                        return;
                    }
                    activeExternalSubtitles = new ArrayList<>(tracks);

                    Models.SubtitleTrack externalTrack = matchingExternalSubtitle(tracks, languagePreference);
                    if (externalTrack != null) {
                        selectExternalSubtitle(externalTrack);
                        return;
                    }

                    int embeddedOrdinal = preferredEmbeddedSubtitleOrdinal(file, languagePreference);
                    if (embeddedOrdinal >= 0) {
                        selectEmbeddedSubtitle(file, embeddedOrdinal);
                        return;
                    }

                    Models.SubtitleTrack fallbackExternalTrack = firstPlayableExternalSubtitle(tracks);
                    if (fallbackExternalTrack != null) {
                        selectExternalSubtitle(fallbackExternalTrack);
                    }
                },
                ignored -> {
                    if (screen == Screen.PLAYER && playerView != null && activePlaybackFile == file) {
                        int embeddedOrdinal = preferredEmbeddedSubtitleOrdinal(file, languagePreference);
                        if (embeddedOrdinal >= 0) {
                            selectEmbeddedSubtitle(file, embeddedOrdinal);
                        }
                    }
                });
    }

    private void startProgressUpdates(Models.VideoFile file) {
        stopProgressUpdates();
        progressReporter = new Runnable() {
            @Override
            public void run() {
                if (screen != Screen.PLAYER || playerView == null || activePlaybackFile != file) {
                    return;
                }

                postPlaybackProgress(file);
                main.postDelayed(this, 10000);
            }
        };
        main.postDelayed(progressReporter, 8000);
    }

    private void stopProgressUpdates() {
        if (progressReporter != null) {
            main.removeCallbacks(progressReporter);
            progressReporter = null;
        }
    }

    private void flushPlaybackProgress() {
        if (activePlaybackFile != null && playerView != null) {
            postPlaybackProgress(activePlaybackFile);
        }
        activePlaybackFile = null;
        activePlaybackTicket = null;
        playerLoadStartedAtMs = 0;
    }

    private void postPlaybackProgress(Models.VideoFile file) {
        double duration = playerView == null ? 0 : playerView.durationSeconds();
        if (duration <= 0) {
            duration = file.durationSeconds;
        }
        double position = resolvePlayerPosition(file, duration);
        if (position <= 0 || duration <= 0) {
            return;
        }

        double finalDuration = duration;
        runAsync(
                () -> {
                    api.updatePlaybackProgress(file.id, position, finalDuration);
                    return null;
                },
                ignored -> {
                },
                ignored -> {
                });
    }

    private void toggleLibraryItemWatched(Models.LibraryItem item) {
        runAsync(
                () -> {
                    api.setLibraryItemWatchedStatus(item.id, !item.isWatched);
                    return null;
                },
                ignored -> showHome(),
                error -> renderError(error.getMessage(), this::showHome));
    }

    private void toggleVideoWatched(Models.VideoFile file) {
        boolean next = !isEffectivelyWatched(file);
        runAsync(
                () -> {
                    api.setWatchedStatus(file.id, next, file.durationSeconds);
                    return null;
                },
                ignored -> {
                    if (currentDetail != null) {
                        loadDetail(currentDetail.id);
                    }
                },
                error -> renderError(error.getMessage(), () -> {
                    if (currentDetail != null) {
                        renderDetail(currentDetail);
                    } else {
                        showHome();
                    }
                }));
    }

    private void renderPlayerError(String message) {
        TextView error = text(message == null ? "播放器初始化失败。" : message, 22, Typeface.BOLD, COLOR_DANGER);
        error.setGravity(Gravity.CENTER);
        error.setBackgroundColor(Color.argb(196, 0, 0, 0));
        root.addView(error, match());
    }

    private void showLoading(String message) {
        root.removeAllViews();
        applyAppBackground();
        TextView view = text(message, 24, Typeface.BOLD, COLOR_TEXT);
        view.setGravity(Gravity.CENTER);
        root.addView(view, match());
    }

    private void renderError(String message, Runnable backAction) {
        root.removeAllViews();
        applyAppBackground();
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setGravity(Gravity.CENTER);
        root.addView(page, match());
        TextView error = text(message == null ? "加载失败" : message, 22, Typeface.BOLD, COLOR_DANGER);
        page.addView(error, margin(matchWidth(), 0, 0, 0, dp(18)));
        Button back = button("返回");
        page.addView(back, new LinearLayout.LayoutParams(dp(160), dp(56)));
        back.setOnClickListener(view -> backAction.run());
        back.requestFocus();
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (event.getAction() == KeyEvent.ACTION_DOWN && screen == Screen.PLAYER && playerView != null) {
            if (handlePlayerKeyDown(event.getKeyCode(), event)) {
                return true;
            }
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_ESCAPE) {
            if (isSettingsOpen) {
                closeSettingsOverlay();
                return true;
            }
            if (screen == Screen.HOME && (isSearchOpen || isSortOpen)) {
                isSearchOpen = false;
                isSortOpen = false;
                renderHomeContentKeepingPosition();
                return true;
            }
            if (screen == Screen.PLAYER) {
                if (playerMenuPanel != null) {
                    closePlayerMenu(playerPlayPauseButton);
                    return true;
                }
                closePlayer();
                return true;
            }
            if (screen == Screen.DETAIL) {
                showHome();
                return true;
            }
        }

        if (isSettingsOpen && isDpadNavigationKey(keyCode) && settingsPanel != null && !isDescendant(settingsPanel, getCurrentFocus())) {
            if (settingsInitialFocus != null) {
                settingsInitialFocus.requestFocus();
            } else {
                settingsPanel.requestFocus();
            }
            return true;
        }

        return super.onKeyDown(keyCode, event);
    }

    @Override
    public void onBackPressed() {
        if (isSettingsOpen) {
            closeSettingsOverlay();
            return;
        }
        if (screen == Screen.HOME && (isSearchOpen || isSortOpen)) {
            isSearchOpen = false;
            isSortOpen = false;
            renderHomeContentKeepingPosition();
            return;
        }
        if (screen == Screen.PLAYER) {
            if (playerMenuPanel != null) {
                closePlayerMenu(playerPlayPauseButton);
                return;
            }
            closePlayer();
            return;
        }
        if (screen == Screen.DETAIL) {
            showHome();
            return;
        }
        super.onBackPressed();
    }

    private boolean isDpadNavigationKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_UP ||
                keyCode == KeyEvent.KEYCODE_DPAD_DOWN ||
                keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT ||
                keyCode == KeyEvent.KEYCODE_DPAD_CENTER ||
                keyCode == KeyEvent.KEYCODE_ENTER;
    }

    private boolean isDescendant(View parent, View child) {
        View current = child;
        while (current != null) {
            if (current == parent) {
                return true;
            }
            ViewParent parentView = current.getParent();
            current = parentView instanceof View ? (View) parentView : null;
        }
        return false;
    }

    private boolean handlePlayerKeyDown(int keyCode, KeyEvent event) {
        boolean wasChromeHidden = playerChromeHidden;
        showPlayerChromeTemporarily();
        if (isPlayerSeekKey(keyCode)) {
            if (keyCode == KeyEvent.KEYCODE_MEDIA_REWIND ||
                    keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD ||
                    event.getRepeatCount() > 0) {
                playerView.seek(playerSeekSeconds(keyCode));
                return true;
            }
            if (wasChromeHidden) {
                if (playerPlayPauseButton != null) {
                    playerPlayPauseButton.requestFocus();
                }
                return true;
            }
            return false;
        }
        if (keyCode == KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE || keyCode == KeyEvent.KEYCODE_SPACE) {
            togglePlayerPaused();
            return true;
        }
        if ((keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER) &&
                (wasChromeHidden || getCurrentFocus() == playerView)) {
            togglePlayerPaused();
            if (wasChromeHidden && playerPlayPauseButton != null) {
                playerPlayPauseButton.requestFocus();
            }
            return true;
        }
        if (wasChromeHidden &&
                (keyCode == KeyEvent.KEYCODE_DPAD_UP || keyCode == KeyEvent.KEYCODE_DPAD_DOWN) &&
                playerPlayPauseButton != null) {
            playerPlayPauseButton.requestFocus();
            return true;
        }
        return false;
    }

    private boolean isPlayerSeekKey(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT ||
                keyCode == KeyEvent.KEYCODE_MEDIA_REWIND ||
                keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD;
    }

    private double playerSeekSeconds(int keyCode) {
        return keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_MEDIA_REWIND ? -10 : 10;
    }

    private boolean isLikely4K(Models.VideoFile file) {
        if (file == null) {
            return false;
        }
        if (file.videoHeight >= 2000 || file.videoWidth >= 3500) {
            return true;
        }
        String name = joinNonEmptyValues(" ", file.fileName, file.relativePath, file.label()).toLowerCase(Locale.ROOT);
        return name.contains("2160p") ||
                name.contains("4k") ||
                name.contains("uhd") ||
                name.contains("ultra hd");
    }

    private void togglePlayerPaused() {
        if (playerView == null) {
            return;
        }
        playerView.togglePaused();
        playerPaused = !playerPaused;
        updatePlayerPlayPauseIcon();
    }

    private void updatePlayerPlayPauseIcon() {
        if (playerPlayPauseButton != null) {
            playerPlayPauseButton.setImageResource(playerPaused ? R.drawable.ic_player_play : R.drawable.ic_player_pause);
        }
    }

    private void showPlayerChromeTemporarily() {
        if (screen != Screen.PLAYER) {
            return;
        }
        showPlayerChromeView(playerTopOverlay);
        showPlayerChromeView(playerControlsPanel);
        if (playerTopOverlay != null) {
            playerTopOverlay.bringToFront();
        }
        if (playerControlsPanel != null) {
            playerControlsPanel.bringToFront();
        }
        if (playerMenuPanel != null) {
            playerMenuPanel.bringToFront();
        }
        playerChromeHidden = false;
        schedulePlayerChromeHide();
    }

    private void showPlayerChromeView(View view) {
        if (view == null) {
            return;
        }
        view.setVisibility(View.VISIBLE);
        view.setAlpha(1f);
    }

    private void schedulePlayerChromeHide() {
        if (playerChromeHider != null) {
            main.removeCallbacks(playerChromeHider);
        }
        playerChromeHider = () -> {
            if (screen != Screen.PLAYER) {
                return;
            }
            if (playerMenuPanel != null) {
                schedulePlayerChromeHide();
                return;
            }
            hidePlayerChrome();
        };
        main.postDelayed(playerChromeHider, 3000);
    }

    private void hidePlayerChrome() {
        if (screen != Screen.PLAYER || playerMenuPanel != null) {
            return;
        }
        if (playerView != null) {
            root.requestFocus();
        }
        hidePlayerChromeView(playerTopOverlay);
        hidePlayerChromeView(playerControlsPanel);
        playerChromeHidden = true;
    }

    private void hidePlayerChromeView(View view) {
        if (view == null) {
            return;
        }
        view.setAlpha(0f);
        view.setVisibility(View.INVISIBLE);
    }

    private void stopPlayerChromeHide() {
        if (playerChromeHider != null) {
            main.removeCallbacks(playerChromeHider);
            playerChromeHider = null;
        }
    }

    private void closePlayer() {
        destroyPlayer();
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (currentDetail != null) {
            renderDetail(currentDetail);
        } else {
            showHome();
        }
    }

    private void destroyPlayer() {
        stopPlayerChromeHide();
        stopSubtitleOverlayUpdates();
        if (playerView != null) {
            closePlayerMenu(null);
            stopPlayerUiUpdates();
            stopProgressUpdates();
            flushPlaybackProgress();
            playerView.destroyPlayer();
            playerView = null;
        }
        playerCurrentTime = null;
        playerTotalTime = null;
        playerStatusText = null;
        playerSubtitleOverlay = null;
        playerPlayPauseButton = null;
        playerTopOverlay = null;
        playerControlsPanel = null;
        playerProgressFill = null;
        playerProgressTrack = null;
        playerProgressSegmentViews.clear();
        playerChromeHidden = false;
        playerPaused = false;
        activeDirectPlayback = false;
        activePlaybackQueue = Collections.emptyList();
        activePlaybackIndex = 0;
        advancingPlaybackPart = false;
        activeExternalSubtitles = Collections.emptyList();
        activeSubtitleCues = Collections.emptyList();
        root.setOnKeyListener(null);
        root.setFocusable(false);
        root.setFocusableInTouchMode(false);
    }

    @Override
    protected void onDestroy() {
        destroyPlayer();
        io.shutdownNow();
        super.onDestroy();
    }

    private List<Models.LibraryItem> continueItems(List<Models.LibraryItem> items) {
        ArrayList<Models.LibraryItem> result = new ArrayList<>();
        for (Models.LibraryItem item : items) {
            if (!item.isWatched && item.maxProgressSeconds > 0 && item.maxDurationSeconds > 0) {
                result.add(item);
                if (result.size() >= 12) {
                    break;
                }
            }
        }
        return result;
    }

    private List<Models.LibraryItem> displayedItems() {
        String normalizedSearch = searchText == null ? "" : searchText.trim().toLowerCase(Locale.ROOT);
        ArrayList<Models.LibraryItem> result = new ArrayList<>();
        for (Models.LibraryItem item : libraryItems) {
            if (normalizedSearch.isEmpty() || item.title.toLowerCase(Locale.ROOT).contains(normalizedSearch)) {
                result.add(item);
            }
        }
        result.sort(libraryComparator());
        return result;
    }

    private Comparator<Models.LibraryItem> libraryComparator() {
        return (a, b) -> {
            int direction = sortDescending ? -1 : 1;
            int result;
            if (sortKey == SortKey.TITLE) {
                result = titleCollator.compare(a.title, b.title) * direction;
            } else if (sortKey == SortKey.RATING) {
                result = compareNullableNumber(ratingValue(a.voteAverage), ratingValue(b.voteAverage), direction);
            } else {
                result = compareNullableNumber(yearValue(a.releaseDate), yearValue(b.releaseDate), direction);
            }

            if (result == 0) {
                result = titleCollator.compare(a.title, b.title);
            }
            return result;
        };
    }

    private int compareNullableNumber(Double a, Double b, int direction) {
        if (a == null && b == null) {
            return 0;
        }
        if (a == null) {
            return 1;
        }
        if (b == null) {
            return -1;
        }
        return Double.compare(a, b) * direction;
    }

    private Double ratingValue(double value) {
        return value > 0 && Double.isFinite(value) ? value : null;
    }

    private Double yearValue(String releaseDate) {
        if (releaseDate == null || releaseDate.length() < 4) {
            return null;
        }
        try {
            return (double) Integer.parseInt(releaseDate.substring(0, 4));
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    private Models.VideoFile resolveMainFile(Models.LibraryDetail detail) {
        if ("tv".equals(detail.itemKind)) {
            ArrayList<Models.VideoFile> files = episodeFiles(detail);
            for (Models.VideoFile file : files) {
                if (hasUnfinishedProgress(file)) {
                    return file;
                }
            }
            for (Models.VideoFile file : files) {
                if (!isEffectivelyWatched(file)) {
                    return file;
                }
            }
            if (!files.isEmpty()) {
                return files.get(0);
            }
        }

        ArrayList<Models.VideoFile> movieFiles = moviePlaybackFiles(detail);
        for (Models.VideoFile file : movieFiles) {
            if (hasUnfinishedProgress(file)) {
                return file;
            }
        }
        for (Models.VideoFile file : movieFiles) {
            if (!isEffectivelyWatched(file)) {
                return file;
            }
        }
        if (!movieFiles.isEmpty()) {
            return movieFiles.get(0);
        }

        if (!detail.videoFiles.isEmpty()) {
            return detail.videoFiles.get(0);
        }
        List<Models.VideoFile> playable = detail.playableFiles();
        return playable.isEmpty() ? null : playable.get(0);
    }

    private ArrayList<Models.VideoFile> moviePlaybackFiles(Models.LibraryDetail detail) {
        ArrayList<Models.VideoFile> files = new ArrayList<>();
        if (detail == null || !"movie".equals(detail.itemKind)) {
            return files;
        }
        files.addAll(detail.videoFiles);
        files.sort((left, right) -> {
            int leftPart = moviePartSortIndex(left);
            int rightPart = moviePartSortIndex(right);
            if (leftPart != rightPart) {
                return Integer.compare(leftPart, rightPart);
            }

            int path = compareNaturalText(left.relativePath, right.relativePath);
            if (path != 0) {
                return path;
            }
            int name = compareNaturalText(left.fileName, right.fileName);
            return name != 0 ? name : left.id.compareTo(right.id);
        });
        return files;
    }

    private int moviePartSortIndex(Models.VideoFile file) {
        String raw = (file.relativePath == null ? "" : file.relativePath) + "/" + (file.fileName == null ? "" : file.fileName);
        String[] tokens = raw.split("[\\\\/\\s._\\-\\[\\]\\(\\)\\{\\}【】（）,:：]+");
        for (int index = 0; index < tokens.length; index++) {
            String rawToken = tokens[index];
            if (rawToken == null || rawToken.isEmpty()) {
                continue;
            }
            Integer chinesePart = parseChineseMoviePartToken(rawToken);
            if (chinesePart != null) {
                return chinesePart;
            }

            String token = rawToken.toLowerCase(Locale.ROOT);
            for (String prefix : MOVIE_PART_PREFIXES) {
                if (token.equals(prefix)) {
                    if (index + 1 < tokens.length) {
                        Integer separatedPart = parseMoviePartToken(tokens[index + 1]);
                        if (separatedPart != null) {
                            return separatedPart;
                        }
                    }
                } else if (token.startsWith(prefix)) {
                    Integer joinedPart = parseMoviePartToken(token.substring(prefix.length()));
                    if (joinedPart != null) {
                        return joinedPart;
                    }
                }
            }
        }
        return UNKNOWN_MOVIE_PART_INDEX;
    }

    private Integer parseMoviePartToken(String token) {
        if (token == null) {
            return null;
        }
        String trimmed = token.trim();
        if (trimmed.isEmpty()) {
            return null;
        }
        try {
            int numeric = Integer.parseInt(trimmed);
            return numeric > 0 ? numeric : null;
        } catch (NumberFormatException ignored) {
            // Continue with non-decimal part markers.
        }

        Integer chinesePart = parseChineseMoviePartToken(trimmed);
        if (chinesePart != null) {
            return chinesePart;
        }
        return parseRomanNumber(trimmed);
    }

    private Integer parseChineseMoviePartToken(String token) {
        switch (token) {
            case "上":
            case "上半":
            case "上篇":
            case "前篇":
                return 1;
            case "下":
            case "下半":
            case "下篇":
            case "后篇":
            case "後篇":
                return 2;
            default:
                return null;
        }
    }

    private Integer parseRomanNumber(String token) {
        String roman = token.trim().toUpperCase(Locale.ROOT);
        if (roman.isEmpty()) {
            return null;
        }

        int total = 0;
        for (int index = 0; index < roman.length(); index++) {
            int current = romanDigitValue(roman.charAt(index));
            if (current <= 0) {
                return null;
            }
            int next = index + 1 < roman.length() ? romanDigitValue(roman.charAt(index + 1)) : 0;
            total += current < next ? -current : current;
        }
        return total > 0 ? total : null;
    }

    private int romanDigitValue(char ch) {
        switch (ch) {
            case 'I':
                return 1;
            case 'V':
                return 5;
            case 'X':
                return 10;
            case 'L':
                return 50;
            case 'C':
                return 100;
            case 'D':
                return 500;
            case 'M':
                return 1000;
            default:
                return 0;
        }
    }

    private List<Models.VideoFile> playbackQueueFor(Models.VideoFile file) {
        if (currentDetail == null || file == null || !"movie".equals(currentDetail.itemKind)) {
            return Collections.singletonList(file);
        }

        ArrayList<Models.VideoFile> files = moviePlaybackFiles(currentDetail);
        return files.size() > 1 && indexOfVideoFile(files, file.id) >= 0 ? files : Collections.singletonList(file);
    }

    private int indexOfVideoFile(List<Models.VideoFile> files, String videoFileId) {
        if (files == null || videoFileId == null) {
            return -1;
        }
        for (int index = 0; index < files.size(); index++) {
            if (videoFileId.equals(files.get(index).id)) {
                return index;
            }
        }
        return -1;
    }

    private int compareNaturalText(String left, String right) {
        return Collator.getInstance(Locale.CHINA).compare(left == null ? "" : left, right == null ? "" : right);
    }

    private ArrayList<Models.VideoFile> episodeFiles(Models.LibraryDetail detail) {
        ArrayList<Models.VideoFile> files = new ArrayList<>();
        for (Models.Season season : detail.seasons) {
            for (Models.Episode episode : season.episodes) {
                if (episode.videoFile != null) {
                    files.add(episode.videoFile.withEpisodeLabel(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber)));
                }
            }
        }
        return files;
    }

    private boolean hasUnfinishedProgress(Models.VideoFile file) {
        if (file == null || file.positionSeconds <= 5) {
            return false;
        }
        return file.durationSeconds <= 0 || fileProgressPercent(file) < 95;
    }

    private boolean isEffectivelyWatched(Models.VideoFile file) {
        return file != null && (file.isWatched || fileProgressPercent(file) >= 95);
    }

    private int fileProgressPercent(Models.VideoFile file) {
        if (file == null || file.durationSeconds <= 0 || file.positionSeconds <= 0) {
            return 0;
        }
        return Math.max(0, Math.min(100, (int) Math.round(Math.min(file.positionSeconds, file.durationSeconds) / file.durationSeconds * 100)));
    }

    private String watchedStatusLabel(Models.VideoFile file) {
        if (file == null) {
            return "未播";
        }
        if (isEffectivelyWatched(file)) {
            return "已播";
        }
        return file.positionSeconds > 5 ? "未播完" : "未播";
    }

    private String playButtonText(Models.VideoFile file) {
        String action = file.positionSeconds > 5 ? "继续播放" : "开始播放";
        if (file.seasonNumber != null && file.episodeNumber != null) {
            return action + " " + episodeDisplayLabel(file.seasonNumber, file.episodeNumber);
        }
        return action;
    }

    private boolean hasSelectedSeason(Models.LibraryDetail detail) {
        for (Models.Season season : detail.seasons) {
            if (season.id.equals(selectedSeasonId)) {
                return true;
            }
        }
        return false;
    }

    private String defaultSeasonId(Models.LibraryDetail detail, Models.VideoFile mainFile) {
        if (mainFile != null && mainFile.seasonNumber != null) {
            for (Models.Season season : detail.seasons) {
                if (season.seasonNumber == mainFile.seasonNumber) {
                    return season.id;
                }
            }
        }
        return detail.seasons.isEmpty() ? "" : detail.seasons.get(0).id;
    }

    private Models.Season selectedSeason(Models.LibraryDetail detail) {
        for (Models.Season season : detail.seasons) {
            if (season.id.equals(selectedSeasonId)) {
                return season;
            }
        }
        return detail.seasons.isEmpty() ? null : detail.seasons.get(0);
    }

    private String seasonDisplayLabel(Models.Season season) {
        if (season.title != null && !season.title.isEmpty()) {
            return season.title;
        }
        return season.seasonNumber == 0 ? "特别篇" : "第 " + season.seasonNumber + " 季";
    }

    private String episodeTitleDisplay(Models.Episode episode, boolean showDetails) {
        String label = episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber);
        if (showDetails) {
            return episode.title == null || episode.title.isEmpty() ? label : episode.title;
        }

        String subtitle = extractEpisodeSubtitle(episode.title);
        return subtitle.isEmpty() ? label : label + "·" + subtitle;
    }

    private String episodeFacts(Models.Episode episode) {
        ArrayList<String> facts = new ArrayList<>();
        facts.add(episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber));
        if (episode.airDate != null && !episode.airDate.isEmpty()) {
            facts.add(episode.airDate);
        }
        return join(facts, " · ");
    }

    private String episodeDisplayLabel(int seasonNumber, int episodeNumber) {
        return seasonNumber == 0 ? "特别篇第 " + episodeNumber + " 集" : "第 " + seasonNumber + " 季第 " + episodeNumber + " 集";
    }

    private String extractEpisodeSubtitle(String title) {
        if (title == null || title.isEmpty()) {
            return "";
        }

        int separatorIndex = title.indexOf("·");
        return separatorIndex < 0 ? "" : title.substring(separatorIndex + 1).trim();
    }

    private String formatAudioTrack(Models.VideoFileStream track, int ordinal) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("音轨 " + (ordinal + 1));
        parts.add(displayLanguage(track.language, track.title));
        String audioFormat = displayAudioFormat(track);
        if (!audioFormat.isEmpty()) {
            parts.add(audioFormat);
        }
        if (track.title != null && !track.title.isEmpty()) {
            parts.add(track.title);
        }
        if (track.isDefault) {
            parts.add("默认");
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatSubtitleStream(Models.VideoFileStream stream, int ordinal) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("内嵌字幕 " + (ordinal + 1));
        parts.add(displayLanguage(stream.language, stream.title));
        String subtitleFormat = displaySubtitleFormat(stream.codec);
        if (!subtitleFormat.isEmpty()) {
            parts.add(subtitleFormat);
        }
        if (stream.title != null && !stream.title.isEmpty()) {
            parts.add(stream.title);
        }
        if (stream.isDefault) {
            parts.add("默认");
        }
        if (stream.isForced) {
            parts.add("强制");
        }
        return joinNonEmpty(parts, " · ");
    }

    private String formatExternalSubtitle(Models.SubtitleTrack track) {
        ArrayList<String> parts = new ArrayList<>();
        parts.add("外挂字幕");
        parts.add(displayLanguage(track.language, track.fileName));
        String format = displaySubtitleFormat(track.format);
        if (!format.isEmpty()) {
            parts.add(format);
        }
        if (track.fileName != null && !track.fileName.isEmpty()) {
            parts.add(track.fileName);
        }
        return joinNonEmpty(parts, " · ");
    }

    private Models.VideoFileStream resolvePreferredAudioTrack(Models.VideoFile file, String preference) {
        if (file.audioTracks.isEmpty()) {
            return null;
        }

        Models.VideoFileStream defaultTrack = null;
        for (Models.VideoFileStream track : file.audioTracks) {
            if (defaultTrack == null && track.isDefault) {
                defaultTrack = track;
            }
        }

        String requestedLanguage = "smart".equalsIgnoreCase(preference)
                ? resolveSmartAudioLanguage(file)
                : preference;
        if (requestedLanguage != null && !requestedLanguage.isEmpty()) {
            for (Models.VideoFileStream track : file.audioTracks) {
                ArrayList<String> values = new ArrayList<>();
                values.add(track.language);
                values.add(track.title);
                values.add(track.codec);
                values.add(track.channelLayout);
                values.add(track.channels == null ? null : track.channels + "ch");
                if (matchesLanguagePreference(values, requestedLanguage)) {
                    return track;
                }
            }
        }

        return defaultTrack == null ? file.audioTracks.get(0) : defaultTrack;
    }

    private Models.SubtitleTrack matchingExternalSubtitle(List<Models.SubtitleTrack> tracks, String preference) {
        for (Models.SubtitleTrack track : tracks) {
            if (api.subtitleUrl(track, activePlaybackTicket).isEmpty()) {
                continue;
            }
            ArrayList<String> values = new ArrayList<>();
            values.add(track.language);
            values.add(track.fileName);
            values.add(track.format);
            if (matchesLanguagePreference(values, preference)) {
                return track;
            }
        }
        return null;
    }

    private Models.SubtitleTrack firstPlayableExternalSubtitle(List<Models.SubtitleTrack> tracks) {
        for (Models.SubtitleTrack track : tracks) {
            if (!api.subtitleUrl(track, activePlaybackTicket).isEmpty()) {
                return track;
            }
        }
        return null;
    }

    private int preferredEmbeddedSubtitleOrdinal(Models.VideoFile file, String preference) {
        int firstSubtitle = -1;
        int firstDefaultSubtitle = -1;
        int firstLanguageSubtitle = -1;
        for (int index = 0; index < file.subtitleStreams.size(); index++) {
            Models.VideoFileStream stream = file.subtitleStreams.get(index);
            if (firstSubtitle < 0) {
                firstSubtitle = index;
            }
            if (firstDefaultSubtitle < 0 && stream.isDefault) {
                firstDefaultSubtitle = index;
            }
            ArrayList<String> values = new ArrayList<>();
            values.add(stream.language);
            values.add(stream.title);
            values.add(stream.codec);
            if (firstLanguageSubtitle < 0 && matchesLanguagePreference(values, preference)) {
                firstLanguageSubtitle = index;
            }
        }

        if (firstLanguageSubtitle >= 0) {
            return firstLanguageSubtitle;
        }
        if (firstDefaultSubtitle >= 0) {
            return firstDefaultSubtitle;
        }
        return firstSubtitle;
    }

    private String resolveSmartAudioLanguage(Models.VideoFile file) {
        ArrayList<String> values = new ArrayList<>();
        if (currentDetail != null) {
            values.add(currentDetail.title);
        }
        values.add(file.fileName);
        values.add(file.relativePath);
        values.add(file.episodeTitle);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        if (tokens.contains("japan") ||
                tokens.contains("japanese") ||
                tokens.contains("jpn") ||
                tokens.contains("jp") ||
                tokens.contains("anime") ||
                containsJapaneseKana(text)) {
            return "ja";
        }
        if (tokens.contains("china") ||
                tokens.contains("chinese") ||
                tokens.contains("mandarin") ||
                tokens.contains("cantonese") ||
                tokens.contains("chn") ||
                tokens.contains("cn") ||
                tokens.contains("hk") ||
                tokens.contains("tw")) {
            return "zh";
        }
        if (tokens.contains("usa") ||
                tokens.contains("us") ||
                tokens.contains("uk") ||
                tokens.contains("gb") ||
                tokens.contains("english") ||
                tokens.contains("eng") ||
                tokens.contains("america") ||
                tokens.contains("britain")) {
            return "en";
        }
        return null;
    }

    private boolean matchesLanguagePreference(List<String> values, String preference) {
        String language = normalizeLanguagePreference(preference);
        String text = normalizeSearchText(values);
        Set<String> tokens = tokenizeSearchText(text);
        if ("en".equals(language)) {
            return tokens.contains("en") ||
                    tokens.contains("eng") ||
                    tokens.contains("english") ||
                    text.contains("英语") ||
                    text.contains("英文") ||
                    text.contains("英語");
        }
        if ("ja".equals(language)) {
            return tokens.contains("ja") ||
                    tokens.contains("jp") ||
                    tokens.contains("jpn") ||
                    tokens.contains("japanese") ||
                    text.contains("日本語") ||
                    text.contains("日语") ||
                    text.contains("日語") ||
                    text.contains("日文");
        }
        return tokens.contains("zh") ||
                tokens.contains("zho") ||
                tokens.contains("chi") ||
                tokens.contains("chs") ||
                tokens.contains("cht") ||
                tokens.contains("cmn") ||
                tokens.contains("yue") ||
                tokens.contains("cn") ||
                tokens.contains("chinese") ||
                text.contains("chinese") ||
                text.contains("mandarin") ||
                text.contains("cantonese") ||
                text.contains("中文") ||
                text.contains("国语") ||
                text.contains("國語") ||
                text.contains("普通话") ||
                text.contains("普通話") ||
                text.contains("粤语") ||
                text.contains("粵語") ||
                text.contains("简体") ||
                text.contains("簡體") ||
                text.contains("繁体") ||
                text.contains("繁體");
    }

    private String normalizeLanguagePreference(String preference) {
        String value = preference == null ? "" : preference.trim().toLowerCase(Locale.ROOT);
        if (value.equals("en") || value.equals("eng") || value.equals("english")) {
            return "en";
        }
        if (value.equals("ja") || value.equals("jp") || value.equals("jpn") || value.equals("japanese")) {
            return "ja";
        }
        return "zh";
    }

    private String normalizeSearchText(List<String> values) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (value == null || value.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append(' ');
            }
            builder.append(value);
        }
        return Normalizer.normalize(builder.toString(), Normalizer.Form.NFKC).toLowerCase(Locale.ROOT);
    }

    private Set<String> tokenizeSearchText(String value) {
        HashSet<String> tokens = new HashSet<>();
        if (value == null || value.isEmpty()) {
            return tokens;
        }
        String[] parts = value.split("[^a-z0-9]+");
        for (String part : parts) {
            if (!part.isEmpty()) {
                tokens.add(part);
            }
        }
        return tokens;
    }

    private boolean containsJapaneseKana(String value) {
        if (value == null) {
            return false;
        }
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            if ((character >= '\u3040' && character <= '\u30ff')) {
                return true;
            }
        }
        return false;
    }

    private String displayLanguage(String language, String fallbackText) {
        String value = language == null ? "" : language.trim().toLowerCase(Locale.ROOT);
        String text = ((fallbackText == null ? "" : fallbackText) + " " + value).toLowerCase(Locale.ROOT);
        if (value.equals("zh") || value.equals("zho") || value.equals("chi") || value.equals("chs") || value.equals("cht") ||
                value.equals("cmn") || value.equals("yue") || text.contains("中文") || text.contains("chinese") ||
                text.contains("mandarin") || text.contains("cantonese") || text.contains("国语") || text.contains("國語") ||
                text.contains("普通话") || text.contains("普通話") || text.contains("粤语") || text.contains("粵語") ||
                text.contains("简体") || text.contains("簡體") || text.contains("繁体") || text.contains("繁體")) {
            return "中文";
        }
        if (value.equals("en") || value.equals("eng") || text.contains("english") || text.contains("英语") || text.contains("英文")) {
            return "英语";
        }
        if (value.equals("ja") || value.equals("jpn") || value.equals("jp") || text.contains("japanese") || text.contains("日语") || text.contains("日文")) {
            return "日语";
        }
        if (value.equals("ko") || value.equals("kor") || value.equals("kr") || text.contains("korean") || text.contains("韩语") || text.contains("韓語")) {
            return "韩语";
        }
        if (value.equals("und") || value.isEmpty()) {
            return "";
        }
        return language;
    }

    private String displayAudioFormat(Models.VideoFileStream track) {
        ArrayList<String> parts = new ArrayList<>();
        String codec = displayAudioCodec(track.codec);
        if (!codec.isEmpty()) {
            parts.add(codec);
        }
        String channels = displayChannels(track);
        if (!channels.isEmpty()) {
            parts.add(channels);
        }
        return joinNonEmpty(parts, " ");
    }

    private String displayAudioCodec(String codec) {
        String value = codec == null ? "" : codec.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        if (value.isEmpty()) {
            return "";
        }
        if (value.equals("truehd")) {
            return "TrueHD";
        }
        if (value.equals("eac3") || value.equals("eac-3")) {
            return "DD+";
        }
        if (value.equals("ac3") || value.equals("ac-3")) {
            return "AC-3";
        }
        if (value.equals("dts")) {
            return "DTS";
        }
        if (value.equals("aac")) {
            return "AAC";
        }
        if (value.equals("flac")) {
            return "FLAC";
        }
        if (value.equals("opus")) {
            return "Opus";
        }
        return codec.toUpperCase(Locale.ROOT);
    }

    private String displayChannels(Models.VideoFileStream track) {
        if (track.channelLayout != null && !track.channelLayout.isEmpty()) {
            String layout = track.channelLayout.trim().toLowerCase(Locale.ROOT);
            if (layout.equals("7.1") || layout.contains("7.1")) {
                return "7.1";
            }
            if (layout.equals("5.1") || layout.contains("5.1")) {
                return "5.1";
            }
            if (layout.equals("stereo")) {
                return "2.0";
            }
            if (layout.equals("mono")) {
                return "1.0";
            }
            if (layout.contains("side") && track.channels != null && track.channels == 8) {
                return "7.1";
            }
            if (layout.contains("5.1")) {
                return "5.1";
            }
        }
        if (track.channels == null || track.channels <= 0) {
            return "";
        }
        if (track.channels == 8) {
            return "7.1";
        }
        if (track.channels == 6) {
            return "5.1";
        }
        if (track.channels == 2) {
            return "2.0";
        }
        if (track.channels == 1) {
            return "1.0";
        }
        return track.channels + "ch";
    }

    private String displaySubtitleFormat(String format) {
        String value = format == null ? "" : format.trim().toLowerCase(Locale.ROOT).replace('_', '-');
        if (value.isEmpty()) {
            return "";
        }
        if (value.equals("hdmv-pgs-subtitle") || value.equals("pgssub") || value.equals("sup") || value.equals("pgs")) {
            return "PGS";
        }
        if (value.equals("subrip") || value.equals("srt")) {
            return "SRT";
        }
        if (value.equals("webvtt") || value.equals("vtt")) {
            return "VTT";
        }
        if (value.equals("ass")) {
            return "ASS";
        }
        if (value.equals("ssa")) {
            return "SSA";
        }
        if (value.equals("mov-text")) {
            return "MOV_TEXT";
        }
        return format.toUpperCase(Locale.ROOT);
    }

    private boolean canUseEmbeddedSubtitleAsWebTrack(Models.VideoFileStream stream) {
        String codec = stream.codec == null ? "" : stream.codec.trim().toLowerCase(Locale.ROOT);
        return codec.equals("ass") ||
                codec.equals("ssa") ||
                codec.equals("subrip") ||
                codec.equals("srt") ||
                codec.equals("webvtt") ||
                codec.equals("mov_text") ||
                codec.equals("text");
    }

    private boolean isChineseSubtitle(Models.VideoFileStream stream) {
        String value = ((stream.language == null ? "" : stream.language) + " " + (stream.title == null ? "" : stream.title)).toLowerCase(Locale.ROOT);
        return value.contains("zh") ||
                value.contains("zho") ||
                value.contains("chi") ||
                value.contains("chs") ||
                value.contains("cht") ||
                value.contains("cmn") ||
                value.contains("yue") ||
                value.contains("cn") ||
                value.contains("chinese") ||
                value.contains("中文") ||
                value.contains("简体") ||
                value.contains("繁体");
    }

    private String releaseYearText(String releaseDate) {
        return releaseDate == null || releaseDate.length() < 4 ? null : releaseDate.substring(0, 4);
    }

    private String ratingText(double voteAverage) {
        return voteAverage > 0 && Double.isFinite(voteAverage) ? String.format(Locale.CHINA, "%.1f", voteAverage) : null;
    }

    private String formatPlaybackTime(double seconds) {
        int total = Math.max(0, (int) Math.round(seconds));
        int hours = total / 3600;
        int minutes = (total % 3600) / 60;
        int remainingSeconds = total % 60;
        if (hours > 0) {
            return String.format(Locale.CHINA, "%d:%02d:%02d", hours, minutes, remainingSeconds);
        }
        return String.format(Locale.CHINA, "%d:%02d", minutes, remainingSeconds);
    }

    private LinearLayout timeline(Models.LibraryDetail detail, Models.VideoFile file) {
        PlaybackTimeline summary = "movie".equals(detail.itemKind)
                ? savedMovieTimeline(moviePlaybackFiles(detail))
                : new PlaybackTimeline(file == null ? 0 : Math.max(0, file.positionSeconds), file == null ? 0 : Math.max(0, file.durationSeconds));
        LinearLayout timeline = new LinearLayout(this);
        timeline.setGravity(Gravity.CENTER_VERTICAL);
        TextView elapsed = text(formatPlaybackTime(summary.positionSeconds), 12, Typeface.BOLD, COLOR_MUTED);
        elapsed.setGravity(Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        timeline.addView(elapsed, new LinearLayout.LayoutParams(dp(58), ViewGroup.LayoutParams.MATCH_PARENT));
        int percent = summary.durationSeconds > 0
                ? Math.max(0, Math.min(100, (int) Math.round(Math.min(summary.positionSeconds, summary.durationSeconds) / summary.durationSeconds * 100)))
                : fileProgressPercent(file);
        timeline.addView(progressBar(percent, dp(278), dp(6)), margin(new LinearLayout.LayoutParams(dp(278), dp(6)), dp(8), 0, dp(8), 0));
        TextView total = text(summary.durationSeconds > 0 ? formatPlaybackTime(summary.durationSeconds) : "--:--", 12, Typeface.BOLD, COLOR_MUTED);
        total.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL);
        timeline.addView(total, new LinearLayout.LayoutParams(dp(72), ViewGroup.LayoutParams.MATCH_PARENT));
        return timeline;
    }

    private PlaybackTimeline savedMovieTimeline(List<Models.VideoFile> files) {
        double position = 0;
        double duration = 0;
        for (Models.VideoFile file : files) {
            if (file.durationSeconds <= 0) {
                continue;
            }
            duration += file.durationSeconds;
            position += isEffectivelyWatched(file)
                    ? file.durationSeconds
                    : Math.min(Math.max(0, file.positionSeconds), file.durationSeconds);
        }
        return new PlaybackTimeline(position, duration);
    }

    private FrameLayout progressBar(int percent, int width, int height) {
        FrameLayout track = new FrameLayout(this);
        track.setBackground(rounded(Color.rgb(229, 231, 235), Color.TRANSPARENT, 0, height / 2));
        View fill = new View(this);
        fill.setBackground(rounded(COLOR_FOCUS, Color.TRANSPARENT, 0, height / 2));
        track.addView(fill, new FrameLayout.LayoutParams(percent <= 0 ? 0 : Math.max(dp(2), Math.round(width * percent / 100f)), ViewGroup.LayoutParams.MATCH_PARENT));
        return track;
    }

    private GridLayout posterGrid() {
        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(5);
        return grid;
    }

    private void loadPoster(ImageView image, String posterAssetId) {
        if (posterAssetId != null) {
            imageLoader.load(image, api.posterUrl(posterAssetId), api.cookieHeader());
        } else {
            imageLoader.load(image, null, api.cookieHeader());
        }
    }

    private <T> void runAsync(Task<T> task, Success<T> success, Failure failure) {
        io.execute(() -> {
            try {
                T result = task.run();
                main.post(() -> success.accept(result));
            } catch (Exception error) {
                main.post(() -> failure.accept(error));
            }
        });
    }

    private EditText input(String hint, String value) {
        EditText editText = new EditText(this);
        editText.setText(value);
        editText.setHint(hint);
        editText.setSingleLine(true);
        editText.setTextSize(18);
        editText.setTextColor(COLOR_TEXT);
        editText.setHintTextColor(COLOR_SUBTLE);
        editText.setPadding(dp(18), 0, dp(18), 0);
        editText.setSelectAllOnFocus(false);
        editText.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(8)));
        editText.setMinHeight(dp(58));
        editText.setFocusable(true);
        editText.setFocusableInTouchMode(true);
        applyInputFocus(editText);
        return editText;
    }

    private Button compactButton(String label) {
        Button button = button(label);
        button.setTextSize(15);
        return button;
    }

    private Button playerButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(14);
        button.setAllCaps(false);
        button.setTextColor(Color.WHITE);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(10), 0, dp(10), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(96, 255, 255, 255), Color.argb(90, 255, 255, 255), dp(2), dp(24)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.04f : 1f).scaleY(hasFocus ? 1.04f : 1f).setDuration(90).start();
            button.setTextColor(hasFocus ? COLOR_FOCUS : Color.WHITE);
            focusedView.setBackground(rounded(Color.argb(hasFocus ? 126 : 96, 255, 255, 255), Color.argb(90, 255, 255, 255), dp(2), dp(24)));
            focusedView.setElevation(0);
        });
        return button;
    }

    private ImageButton playerIconButton(int iconResId) {
        ImageButton button = new ImageButton(this);
        button.setImageResource(iconResId);
        button.setColorFilter(Color.WHITE);
        button.setScaleType(ImageView.ScaleType.CENTER);
        button.setPadding(dp(12), dp(10), dp(12), dp(10));
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(96, 255, 255, 255), Color.argb(90, 255, 255, 255), dp(2), dp(24)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.04f : 1f).scaleY(hasFocus ? 1.04f : 1f).setDuration(90).start();
            button.setColorFilter(hasFocus ? COLOR_FOCUS : Color.WHITE);
            focusedView.setBackground(rounded(Color.argb(hasFocus ? 126 : 96, 255, 255, 255), Color.argb(90, 255, 255, 255), dp(2), dp(24)));
            focusedView.setElevation(0);
        });
        return button;
    }

    private Button playerMenuButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(14);
        button.setAllCaps(false);
        button.setTextColor(Color.WHITE);
        button.setGravity(Gravity.CENTER_VERTICAL);
        button.setPadding(dp(14), 0, dp(14), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setSingleLine(true);
        button.setEllipsize(TextUtils.TruncateAt.END);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(Color.argb(72, 255, 255, 255), Color.argb(80, 255, 255, 255), dp(1), dp(10)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            button.setTextColor(hasFocus ? COLOR_FOCUS : Color.WHITE);
            focusedView.setBackground(rounded(Color.argb(hasFocus ? 126 : 72, 255, 255, 255), Color.argb(90, 255, 255, 255), dp(1), dp(10)));
            focusedView.setElevation(0);
        });
        return button;
    }

    private Button button(String label) {
        return button(label, false);
    }

    private Button button(String label, boolean active) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(17);
        button.setAllCaps(false);
        button.setTextColor(active ? COLOR_FOCUS : COLOR_TEXT);
        button.setGravity(Gravity.CENTER);
        button.setPadding(dp(12), 0, dp(12), 0);
        button.setMinWidth(0);
        button.setMinHeight(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(0);
        button.setIncludeFontPadding(false);
        button.setBackgroundTintList(null);
        button.setStateListAnimator(null);
        button.setClipToOutline(true);
        button.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(10)));
        button.setFocusable(true);
        button.setFocusableInTouchMode(false);
        applyButtonFocus(button, active);
        return button;
    }

    private TextView title(String value) {
        return text(value, 32, Typeface.BOLD, COLOR_TEXT);
    }

    private TextView sectionTitle(String value) {
        TextView title = text(value, 24, Typeface.BOLD, COLOR_TEXT);
        title.setPadding(0, dp(10), 0, dp(14));
        return title;
    }

    private TextView body(String value) {
        TextView text = text(value, 16, Typeface.NORMAL, COLOR_MUTED);
        text.setPadding(0, dp(8), 0, dp(18));
        return text;
    }

    private TextView emptyText(String value) {
        TextView text = text(value, 16, Typeface.NORMAL, COLOR_SUBTLE);
        text.setPadding(0, 0, 0, dp(24));
        return text;
    }

    private TextView metaPill(String value) {
        TextView pill = text(value, 14, Typeface.BOLD, COLOR_MUTED);
        pill.setGravity(Gravity.CENTER);
        pill.setPadding(dp(12), 0, dp(12), 0);
        pill.setBackground(rounded(Color.argb(184, 255, 255, 255), COLOR_BORDER, dp(1), dp(999)));
        pill.setMinHeight(dp(32));
        return pill;
    }

    private TextView badge(String value) {
        TextView badge = text(value, 13, Typeface.BOLD, COLOR_TEXT);
        badge.setGravity(Gravity.CENTER);
        badge.setPadding(dp(8), 0, dp(8), 0);
        badge.setMinHeight(dp(30));
        badge.setBackground(rounded(Color.argb(230, 255, 255, 255), Color.TRANSPARENT, 0, dp(6)));
        return badge;
    }

    private TextView text(String value, int sp, int style, int color) {
        TextView textView = new TextView(this);
        textView.setText(value == null ? "" : value);
        textView.setTextSize(sp);
        textView.setTypeface(Typeface.DEFAULT, style);
        textView.setTextColor(color);
        textView.setIncludeFontPadding(true);
        return textView;
    }

    private void applyButtonFocus(Button button, boolean active) {
        button.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.035f : 1f).scaleY(hasFocus ? 1.035f : 1f).setDuration(90).start();
            button.setTextColor(hasFocus || active ? COLOR_FOCUS : COLOR_TEXT);
            focusedView.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(10)));
            focusedView.setElevation(0);
        });
    }

    private void applyInputFocus(EditText editText) {
        editText.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.setBackground(rounded(COLOR_SURFACE, COLOR_BORDER, dp(2), dp(8)));
            focusedView.setElevation(0);
        });
    }

    private void applyCardFocus(View view) {
        view.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.035f : 1f).scaleY(hasFocus ? 1.035f : 1f).setDuration(90).start();
            focusedView.setBackground(rounded(hasFocus ? COLOR_SURFACE : Color.TRANSPARENT, hasFocus ? COLOR_FOCUS : Color.TRANSPARENT, hasFocus ? dp(2) : 0, dp(8)));
            focusedView.setElevation(hasFocus ? dp(10) : 0);
        });
    }

    private void applyBadgeFocus(TextView badge) {
        badge.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.08f : 1f).scaleY(hasFocus ? 1.08f : 1f).setDuration(90).start();
            focusedView.setBackground(rounded(Color.argb(235, 255, 255, 255), hasFocus ? COLOR_FOCUS : Color.TRANSPARENT, hasFocus ? dp(2) : 0, dp(6)));
            focusedView.setElevation(hasFocus ? dp(8) : 0);
        });
    }

    private void applyWatchedIconFocus(TextView icon, boolean watched) {
        icon.setOnFocusChangeListener((focusedView, hasFocus) -> {
            focusedView.animate().scaleX(hasFocus ? 1.18f : 1f).scaleY(hasFocus ? 1.18f : 1f).setDuration(90).start();
            icon.setTextColor(hasFocus || watched ? COLOR_FOCUS : COLOR_SUBTLE);
            focusedView.setBackgroundColor(Color.TRANSPARENT);
            focusedView.setElevation(0);
        });
    }

    private void applyAppBackground() {
        GradientDrawable background = new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{
                        Color.rgb(248, 250, 252),
                        Color.rgb(239, 246, 255),
                        blend(COLOR_ACCENT, Color.WHITE, 0.90f)
                });
        root.setBackground(background);
    }

    private GradientDrawable rounded(int fillColor, int strokeColor, int strokeWidth, int radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fillColor);
        drawable.setCornerRadius(radius);
        if (strokeWidth > 0) {
            drawable.setStroke(strokeWidth, strokeColor);
        }
        return drawable;
    }

    private int blend(int from, int to, float amount) {
        float clamped = Math.max(0f, Math.min(1f, amount));
        int red = Math.round(Color.red(from) + (Color.red(to) - Color.red(from)) * clamped);
        int green = Math.round(Color.green(from) + (Color.green(to) - Color.green(from)) * clamped);
        int blue = Math.round(Color.blue(from) + (Color.blue(to) - Color.blue(from)) * clamped);
        return Color.rgb(red, green, blue);
    }

    private FrameLayout.LayoutParams match() {
        return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
    }

    private LinearLayout.LayoutParams matchWidth() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams margin(LinearLayout.LayoutParams params, int left, int top, int right, int bottom) {
        params.setMargins(left, top, right, bottom);
        return params;
    }

    private int dp(float value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private String join(List<String> values, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(value);
        }
        return builder.toString();
    }

    private String joinNonEmpty(List<String> values, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (value == null || value.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(value);
        }
        return builder.toString();
    }

    private String joinNonEmptyValues(String separator, String... values) {
        ArrayList<String> list = new ArrayList<>();
        if (values != null) {
            Collections.addAll(list, values);
        }
        return joinNonEmpty(list, separator);
    }

    private enum Screen {
        SETUP,
        HOME,
        DETAIL,
        PLAYER
    }

    private enum SortKey {
        TITLE,
        RATING,
        YEAR
    }

    private static final class ResolvedPlayback {
        final String url;
        final String ticket;
        final String cookieHeader;
        final Models.AppSettings settings;

        ResolvedPlayback(String url, String ticket, String cookieHeader, Models.AppSettings settings) {
            this.url = url;
            this.ticket = ticket == null ? "" : ticket;
            this.cookieHeader = cookieHeader == null ? "" : cookieHeader;
            this.settings = settings == null ? Models.AppSettings.defaults() : settings;
        }
    }

    private static final class PlayerMenuOption {
        final String label;
        final Runnable action;

        PlayerMenuOption(String label, Runnable action) {
            this.label = label;
            this.action = action;
        }
    }

    private static final class PlaybackTimeline {
        final double positionSeconds;
        final double durationSeconds;

        PlaybackTimeline(double positionSeconds, double durationSeconds) {
            this.positionSeconds = Math.max(0, positionSeconds);
            this.durationSeconds = Math.max(0, durationSeconds);
        }
    }

    private static final class SubtitleCue {
        final double startSeconds;
        final double endSeconds;
        final String text;

        SubtitleCue(double startSeconds, double endSeconds, String text) {
            this.startSeconds = startSeconds;
            this.endSeconds = endSeconds;
            this.text = text == null ? "" : text;
        }
    }

    private interface Task<T> {
        T run() throws Exception;
    }

    private interface Success<T> {
        void accept(T result);
    }

    private interface Failure {
        void accept(Exception error);
    }
}
