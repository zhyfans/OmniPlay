package com.omniplay.tv.data;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class Models {
    private Models() {
    }

    public static final class AuthStatus {
        public final boolean requiresSetup;
        public final boolean isAuthenticated;
        public final String username;
        public final String role;

        private AuthStatus(boolean requiresSetup, boolean isAuthenticated, String username, String role) {
            this.requiresSetup = requiresSetup;
            this.isAuthenticated = isAuthenticated;
            this.username = username;
            this.role = role;
        }

        public static AuthStatus fromJson(JSONObject json) {
            return new AuthStatus(
                    json.optBoolean("requiresSetup"),
                    json.optBoolean("isAuthenticated"),
                    optString(json, "username"),
                    optString(json, "role"));
        }
    }

    public static final class AppSettings {
        public final PlaybackSettings playback;

        private AppSettings(PlaybackSettings playback) {
            this.playback = playback == null ? PlaybackSettings.defaults() : playback;
        }

        public static AppSettings defaults() {
            return new AppSettings(PlaybackSettings.defaults());
        }

        public static AppSettings fromJson(JSONObject json) {
            return new AppSettings(PlaybackSettings.fromJson(json == null ? null : json.optJSONObject("playback")));
        }
    }

    public static final class PlaybackSettings {
        public final boolean showEpisodeDetails;
        public final String defaultAudioLanguage;
        public final String defaultSubtitleLanguage;

        private PlaybackSettings(boolean showEpisodeDetails, String defaultAudioLanguage, String defaultSubtitleLanguage) {
            this.showEpisodeDetails = showEpisodeDetails;
            this.defaultAudioLanguage = defaultAudioLanguage == null || defaultAudioLanguage.isEmpty() ? "smart" : defaultAudioLanguage;
            this.defaultSubtitleLanguage = defaultSubtitleLanguage == null || defaultSubtitleLanguage.isEmpty() ? "zh" : defaultSubtitleLanguage;
        }

        public static PlaybackSettings defaults() {
            return new PlaybackSettings(true, "smart", "zh");
        }

        public static PlaybackSettings fromJson(JSONObject json) {
            if (json == null) {
                return defaults();
            }

            return new PlaybackSettings(
                    json.optBoolean("showEpisodeDetails", true),
                    optString(json, "defaultAudioLanguage"),
                    optString(json, "defaultSubtitleLanguage"));
        }
    }

    public static class LibraryItem {
        public final String id;
        public final String itemKind;
        public final String title;
        public final String releaseDate;
        public final String overview;
        public final String posterAssetId;
        public final double voteAverage;
        public final boolean isWatched;
        public final int videoFileCount;
        public final double maxProgressSeconds;
        public final double maxDurationSeconds;

        LibraryItem(JSONObject json) {
            id = json.optString("id");
            itemKind = json.optString("itemKind");
            title = json.optString("title");
            releaseDate = optString(json, "releaseDate");
            overview = optString(json, "overview");
            posterAssetId = optString(json, "posterAssetId");
            voteAverage = json.optDouble("voteAverage", 0);
            isWatched = json.optBoolean("isWatched");
            videoFileCount = json.optInt("videoFileCount");
            maxProgressSeconds = json.optDouble("maxProgressSeconds", 0);
            maxDurationSeconds = json.optDouble("maxDurationSeconds", 0);
        }

        public int progressPercent() {
            if (maxDurationSeconds <= 0 || maxProgressSeconds <= 0) {
                return 0;
            }

            return Math.max(0, Math.min(100, (int) Math.round(maxProgressSeconds / maxDurationSeconds * 100)));
        }
    }

    public static final class LibraryDetail extends LibraryItem {
        public final List<VideoFile> videoFiles;
        public final List<Season> seasons;

        private LibraryDetail(JSONObject json) {
            super(json);
            videoFiles = parseVideoFiles(json.optJSONArray("videoFiles"));
            seasons = parseSeasons(json.optJSONArray("seasons"));
        }

        public static LibraryDetail fromJson(JSONObject json) {
            return new LibraryDetail(json);
        }

        public List<VideoFile> playableFiles() {
            ArrayList<VideoFile> files = new ArrayList<>();
            for (Season season : seasons) {
                for (Episode episode : season.episodes) {
                    if (episode.videoFile != null) {
                        files.add(episode.videoFile.withEpisodeLabel(episode.label()));
                    }
                }
            }

            if (!files.isEmpty()) {
                return files;
            }

            return videoFiles;
        }
    }

    public static final class Season {
        public final String id;
        public final int seasonNumber;
        public final String title;
        public final String posterAssetId;
        public final List<Episode> episodes;

        private Season(JSONObject json) {
            id = json.optString("id");
            seasonNumber = json.optInt("seasonNumber");
            title = optString(json, "title");
            posterAssetId = optString(json, "posterAssetId");
            episodes = parseEpisodes(json.optJSONArray("episodes"));
        }
    }

    public static final class Episode {
        public final String id;
        public final int seasonNumber;
        public final int episodeNumber;
        public final String title;
        public final String overview;
        public final String stillAssetId;
        public final String airDate;
        public final VideoFile videoFile;

        private Episode(JSONObject json) {
            id = json.optString("id");
            seasonNumber = json.optInt("seasonNumber");
            episodeNumber = json.optInt("episodeNumber");
            title = optString(json, "title");
            overview = optString(json, "overview");
            stillAssetId = optString(json, "stillAssetId");
            airDate = optString(json, "airDate");
            JSONObject file = json.optJSONObject("videoFile");
            videoFile = file == null ? null : new VideoFile(file);
        }

        public String label() {
            String base = "S" + pad2(seasonNumber) + "E" + pad2(episodeNumber);
            return title == null || title.isEmpty() ? base : base + "  " + title;
        }
    }

    public static final class VideoFile {
        public final String id;
        public final String relativePath;
        public final String fileName;
        public final String mediaKind;
        public final double durationSeconds;
        public final double positionSeconds;
        public final boolean isWatched;
        public final Integer seasonNumber;
        public final Integer episodeNumber;
        public final String episodeTitle;
        public final String container;
        public final String videoCodec;
        public final int videoWidth;
        public final int videoHeight;
        public final String audioCodec;
        public final String subtitleSummary;
        public final List<VideoFileStream> audioTracks;
        public final List<VideoFileStream> subtitleStreams;
        public final String displayLabel;

        private VideoFile(JSONObject json) {
            this(
                    json.optString("id"),
                    json.optString("relativePath"),
                    json.optString("fileName"),
                    json.optString("mediaKind"),
                    json.optDouble("durationSeconds", 0),
                    json.optDouble("positionSeconds", 0),
                    json.optBoolean("isWatched"),
                    optInt(json, "seasonNumber"),
                    optInt(json, "episodeNumber"),
                    optString(json, "episodeTitle"),
                    optString(json, "container"),
                    optString(json, "videoCodec"),
                    optDimension(json, "videoWidth", "width"),
                    optDimension(json, "videoHeight", "height"),
                    optString(json, "audioCodec"),
                    optString(json, "subtitleSummary"),
                    parseStreams(json.optJSONArray("audioTracks")),
                    parseStreams(json.optJSONArray("subtitleStreams")),
                    null);
        }

        private VideoFile(
                String id,
                String relativePath,
                String fileName,
                String mediaKind,
                double durationSeconds,
                double positionSeconds,
                boolean isWatched,
                Integer seasonNumber,
                Integer episodeNumber,
                String episodeTitle,
                String container,
                String videoCodec,
                int videoWidth,
                int videoHeight,
                String audioCodec,
                String subtitleSummary,
                List<VideoFileStream> audioTracks,
                List<VideoFileStream> subtitleStreams,
                String displayLabel) {
            this.id = id;
            this.relativePath = relativePath;
            this.fileName = fileName;
            this.mediaKind = mediaKind;
            this.durationSeconds = durationSeconds;
            this.positionSeconds = positionSeconds;
            this.isWatched = isWatched;
            this.seasonNumber = seasonNumber;
            this.episodeNumber = episodeNumber;
            this.episodeTitle = episodeTitle;
            this.container = container;
            this.videoCodec = videoCodec;
            this.videoWidth = videoWidth;
            this.videoHeight = videoHeight;
            this.audioCodec = audioCodec;
            this.subtitleSummary = subtitleSummary;
            this.audioTracks = audioTracks == null ? Collections.emptyList() : audioTracks;
            this.subtitleStreams = subtitleStreams == null ? Collections.emptyList() : subtitleStreams;
            this.displayLabel = displayLabel;
        }

        public VideoFile withEpisodeLabel(String label) {
            return new VideoFile(
                    id,
                    relativePath,
                    fileName,
                    mediaKind,
                    durationSeconds,
                    positionSeconds,
                    isWatched,
                    seasonNumber,
                    episodeNumber,
                    episodeTitle,
                    container,
                    videoCodec,
                    videoWidth,
                    videoHeight,
                    audioCodec,
                    subtitleSummary,
                    audioTracks,
                    subtitleStreams,
                    label);
        }

        public String label() {
            if (displayLabel != null && !displayLabel.isEmpty()) {
                return displayLabel;
            }

            if (seasonNumber != null && episodeNumber != null) {
                String base = "S" + pad2(seasonNumber) + "E" + pad2(episodeNumber);
                return episodeTitle == null || episodeTitle.isEmpty() ? base : base + "  " + episodeTitle;
            }

            return fileName;
        }

        public String mediaSummary() {
            ArrayList<String> parts = new ArrayList<>();
            if (container != null && !container.isEmpty()) {
                parts.add(container);
            }
            if (videoCodec != null && !videoCodec.isEmpty()) {
                parts.add(videoCodec);
            }
            if (audioCodec != null && !audioCodec.isEmpty()) {
                parts.add(audioCodec);
            }
            if (subtitleSummary != null && !subtitleSummary.isEmpty()) {
                parts.add("字幕 " + subtitleSummary);
            }
            return join(parts, " · ");
        }
    }

    public static final class VideoFileStream {
        public final int index;
        public final String kind;
        public final String codec;
        public final String language;
        public final String title;
        public final Integer channels;
        public final String channelLayout;
        public final boolean isDefault;
        public final boolean isForced;

        private VideoFileStream(JSONObject json) {
            index = json.optInt("index");
            kind = optString(json, "kind");
            codec = optString(json, "codec");
            language = optString(json, "language");
            title = optString(json, "title");
            channels = optInt(json, "channels");
            channelLayout = optString(json, "channelLayout");
            isDefault = json.optBoolean("isDefault");
            isForced = json.optBoolean("isForced");
        }
    }

    public static final class SubtitleTrack {
        public final String id;
        public final String fileName;
        public final String format;
        public final String language;
        public final String webVttUrl;
        public final boolean canBurn;

        private SubtitleTrack(JSONObject json) {
            id = json.optString("id");
            fileName = optString(json, "fileName");
            format = optString(json, "format");
            language = optString(json, "language");
            webVttUrl = optString(json, "webVttUrl");
            canBurn = json.optBoolean("canBurn");
        }
    }

    public static List<LibraryItem> libraryItemsFromJson(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<LibraryItem> items = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                items.add(new LibraryItem(item));
            }
        }
        return items;
    }

    public static List<SubtitleTrack> subtitleTracksFromJson(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<SubtitleTrack> tracks = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                tracks.add(new SubtitleTrack(item));
            }
        }
        return tracks;
    }

    private static List<Season> parseSeasons(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<Season> seasons = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                seasons.add(new Season(item));
            }
        }
        return seasons;
    }

    private static List<Episode> parseEpisodes(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<Episode> episodes = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                episodes.add(new Episode(item));
            }
        }
        return episodes;
    }

    private static List<VideoFile> parseVideoFiles(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<VideoFile> files = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                files.add(new VideoFile(item));
            }
        }
        return files;
    }

    private static List<VideoFileStream> parseStreams(JSONArray array) {
        if (array == null) {
            return Collections.emptyList();
        }

        ArrayList<VideoFileStream> streams = new ArrayList<>(array.length());
        for (int index = 0; index < array.length(); index++) {
            JSONObject item = array.optJSONObject(index);
            if (item != null) {
                streams.add(new VideoFileStream(item));
            }
        }
        return streams;
    }

    private static String optString(JSONObject json, String key) {
        return json.has(key) && !json.isNull(key) ? json.optString(key) : null;
    }

    private static Integer optInt(JSONObject json, String key) {
        return json.has(key) && !json.isNull(key) ? json.optInt(key) : null;
    }

    private static int optDimension(JSONObject json, String primaryKey, String fallbackKey) {
        if (json.has(primaryKey) && !json.isNull(primaryKey)) {
            return Math.max(0, json.optInt(primaryKey));
        }
        if (json.has(fallbackKey) && !json.isNull(fallbackKey)) {
            return Math.max(0, json.optInt(fallbackKey));
        }
        return 0;
    }

    private static String pad2(int value) {
        return value < 10 ? "0" + value : String.valueOf(value);
    }

    private static String join(List<String> parts, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String part : parts) {
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(part);
        }
        return builder.toString();
    }
}
