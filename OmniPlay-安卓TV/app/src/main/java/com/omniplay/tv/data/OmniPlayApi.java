package com.omniplay.tv.data;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.UnsupportedEncodingException;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;

public final class OmniPlayApi {
    private static final String PREFS = "omniplay_tv";
    private static final String KEY_SERVER_URL = "server_url";
    private static final String KEY_COOKIE = "session_cookie";
    private static final String KEY_LIBRARY_ITEMS_CACHE = "library_items_cache";

    private final SharedPreferences preferences;
    private String serverUrl;
    private String sessionCookie;

    public OmniPlayApi(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        serverUrl = normalizeServerUrl(preferences.getString(KEY_SERVER_URL, ""));
        sessionCookie = preferences.getString(KEY_COOKIE, "");
    }

    public boolean hasServerUrl() {
        return serverUrl != null && !serverUrl.isEmpty();
    }

    public String serverUrl() {
        return serverUrl == null ? "" : serverUrl;
    }

    public String cookieHeader() {
        return sessionCookie == null ? "" : sessionCookie;
    }

    public void setServerUrl(String value) {
        serverUrl = normalizeServerUrl(value);
        preferences.edit().putString(KEY_SERVER_URL, serverUrl).apply();
    }

    public Models.AuthStatus authStatus() throws IOException, JSONException {
        return Models.AuthStatus.fromJson(new JSONObject(request("GET", "/api/auth/status", null)));
    }

    public Models.AuthStatus login(String baseUrl, String username, String password) throws IOException, JSONException {
        setServerUrl(baseUrl);
        sessionCookie = "";
        preferences.edit().remove(KEY_COOKIE).apply();
        JSONObject body = new JSONObject();
        body.put("username", trimOuterWhitespace(username));
        body.put("password", trimOuterWhitespace(password));
        Models.AuthStatus status = Models.AuthStatus.fromJson(new JSONObject(request("POST", "/api/auth/login", body.toString())));
        preferences.edit().putString(KEY_COOKIE, sessionCookie).apply();
        return status;
    }

    public void disconnect() {
        if (hasServerUrl()) {
            try {
                request("POST", "/api/auth/logout", "{}");
            } catch (Exception ignored) {
            }
        }

        serverUrl = "";
        sessionCookie = "";
        preferences.edit()
                .remove(KEY_SERVER_URL)
                .remove(KEY_COOKIE)
                .remove(KEY_LIBRARY_ITEMS_CACHE)
                .apply();
    }

    public List<Models.LibraryItem> getLibraryItems() throws IOException, JSONException {
        String response = request("GET", "/api/library/items", null);
        preferences.edit().putString(KEY_LIBRARY_ITEMS_CACHE, response).apply();
        return Models.libraryItemsFromJson(new JSONArray(response));
    }

    public List<Models.LibraryItem> getCachedLibraryItems() {
        String cached = preferences.getString(KEY_LIBRARY_ITEMS_CACHE, "");
        if (cached == null || cached.isEmpty()) {
            return java.util.Collections.emptyList();
        }

        try {
            return Models.libraryItemsFromJson(new JSONArray(cached));
        } catch (JSONException ignored) {
            return java.util.Collections.emptyList();
        }
    }

    public Models.AppSettings getSettings() throws IOException, JSONException {
        return Models.AppSettings.fromJson(new JSONObject(request("GET", "/api/settings", null)));
    }

    public Models.LibraryDetail getLibraryItemDetail(String id) throws IOException, JSONException {
        return Models.LibraryDetail.fromJson(new JSONObject(request("GET", "/api/library/items/" + encode(id), null)));
    }

    public List<Models.SubtitleTrack> getSubtitles(String videoFileId) throws IOException, JSONException {
        return Models.subtitleTracksFromJson(new JSONArray(request("GET", "/api/playback/files/" + encode(videoFileId) + "/subtitles", null)));
    }

    public void updatePlaybackProgress(String videoFileId, double positionSeconds, double durationSeconds) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("videoFileId", videoFileId);
        body.put("positionSeconds", Math.max(0, positionSeconds));
        body.put("durationSeconds", Math.max(0, durationSeconds));
        request("POST", "/api/playback/progress", body.toString());
    }

    public void setWatchedStatus(String videoFileId, boolean isWatched, double durationSeconds) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("videoFileId", videoFileId);
        body.put("isWatched", isWatched);
        body.put("durationSeconds", Math.max(0, durationSeconds));
        request("POST", "/api/playback/watched", body.toString());
    }

    public Models.LibraryDetail setLibraryItemWatchedStatus(String libraryItemId, boolean isWatched) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("libraryItemId", libraryItemId);
        body.put("isWatched", isWatched);
        return Models.LibraryDetail.fromJson(new JSONObject(request("POST", "/api/library/items/" + encode(libraryItemId) + "/watched", body.toString())));
    }

    public String posterUrl(String posterAssetId) {
        return absoluteUrl("/api/assets/posters/" + encode(posterAssetId));
    }

    public String thumbnailUrl(String thumbnailAssetId) {
        return absoluteUrl("/api/assets/thumbnails/" + encode(thumbnailAssetId));
    }

    public String streamUrl(String videoFileId) {
        return absoluteUrl("/api/playback/files/" + encode(videoFileId) + "/stream");
    }

    public String streamUrl(String videoFileId, String ticket) {
        return withTicket(streamUrl(videoFileId), ticket);
    }

    public String createPlaybackTicket(String videoFileId) throws IOException, JSONException {
        String response = request("POST", "/api/playback/files/" + encode(videoFileId) + "/ticket", "{}");
        JSONObject json = new JSONObject(response);
        String token = json.optString("token");
        return token == null || token.isEmpty() ? json.optString("ticket") : token;
    }

    public String subtitleUrl(Models.SubtitleTrack track) {
        if (track == null || track.webVttUrl == null || track.webVttUrl.isEmpty()) {
            return "";
        }

        if (track.webVttUrl.startsWith("http://") || track.webVttUrl.startsWith("https://")) {
            return track.webVttUrl;
        }

        return absoluteUrl(track.webVttUrl);
    }

    public String subtitleUrl(Models.SubtitleTrack track, String ticket) {
        return withTicket(subtitleUrl(track), ticket);
    }

    public String embeddedSubtitleUrl(String videoFileId, int subtitleOrdinal, String ticket) {
        return withTicket(absoluteUrl("/api/playback/files/" + encode(videoFileId) + "/embedded-subtitles/" + subtitleOrdinal + ".vtt"), ticket);
    }

    private String request(String method, String path, String body) throws IOException {
        ensureServerUrl();
        HttpURLConnection connection = (HttpURLConnection) new URL(absoluteUrl(path)).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(30000);
        connection.setRequestMethod(method);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("User-Agent", "OmniPlay-Android/0.1");
        if (sessionCookie != null && !sessionCookie.isEmpty()) {
            connection.setRequestProperty("Cookie", sessionCookie);
        }

        if (body != null) {
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
        }

        int status = connection.getResponseCode();
        captureSessionCookie(connection.getHeaderFields());
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String text = readText(stream);
        connection.disconnect();
        if (status < 200 || status >= 300) {
            throw new IOException(errorMessage(status, text));
        }
        return text;
    }

    private void captureSessionCookie(Map<String, List<String>> headers) {
        for (Map.Entry<String, List<String>> entry : headers.entrySet()) {
            if (entry.getKey() == null || !"Set-Cookie".equalsIgnoreCase(entry.getKey())) {
                continue;
            }

            for (String value : entry.getValue()) {
                if (value == null || !value.startsWith("omniplay_session=")) {
                    continue;
                }

                int end = value.indexOf(';');
                sessionCookie = end > 0 ? value.substring(0, end) : value;
                preferences.edit().putString(KEY_COOKIE, sessionCookie).apply();
                return;
            }
        }
    }

    private String absoluteUrl(String path) {
        String normalizedPath = path.startsWith("/") ? path : "/" + path;
        return serverUrl() + normalizedPath;
    }

    private static String withTicket(String url, String ticket) {
        if (url == null || url.isEmpty() || ticket == null || ticket.isEmpty()) {
            return url;
        }

        return url + (url.contains("?") ? "&" : "?") + "ticket=" + encode(ticket);
    }

    private void ensureServerUrl() {
        if (!hasServerUrl()) {
            throw new IllegalStateException("请先设置 OmniPlay 服务端地址。");
        }
    }

    private static String normalizeServerUrl(String value) {
        String trimmed = value == null ? "" : value.trim();
        if (!trimmed.isEmpty() && !trimmed.contains("://")) {
            trimmed = "http://" + trimmed;
        }
        while (trimmed.endsWith("/")) {
            trimmed = trimmed.substring(0, trimmed.length() - 1);
        }
        return trimmed;
    }

    private static String trimOuterWhitespace(String value) {
        if (value == null || value.isEmpty()) {
            return "";
        }

        int start = 0;
        int end = value.length();
        while (start < end && Character.isWhitespace(value.charAt(start))) {
            start++;
        }
        while (end > start && Character.isWhitespace(value.charAt(end - 1))) {
            end--;
        }
        return value.substring(start, end);
    }

    private static String encode(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8").replace("+", "%20");
        } catch (UnsupportedEncodingException error) {
            throw new IllegalStateException(error);
        }
    }

    private static String readText(InputStream stream) throws IOException {
        if (stream == null) {
            return "";
        }

        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    private static String errorMessage(int status, String body) {
        try {
            JSONObject json = new JSONObject(body);
            String error = json.optString("error");
            if (error != null && !error.isEmpty()) {
                return error;
            }
        } catch (JSONException ignored) {
        }

        return "请求失败：" + status;
    }
}
