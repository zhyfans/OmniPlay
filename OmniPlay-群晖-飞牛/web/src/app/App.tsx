import { type FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import Hls from "hls.js";
import {
  ArrowLeft,
  CircleStop,
  Cloud,
  FolderOpen,
  FolderPlus,
  Lock,
  Play,
  Save,
  Search,
  Settings,
  SlidersHorizontal,
  RefreshCw,
  Sparkles,
  Trash2,
  Unlock,
  X,
} from "lucide-react";
import {
  addLocalMediaSource,
  addWebDavMediaSource,
  applyLibraryItemMetadata,
  AppSettingsSnapshot,
  BackgroundTaskSnapshot,
  BackgroundTaskStatus,
  browseLocalDirectories,
  browseWebDavDirectories,
  CacheUsageSummary,
  CacheSettings,
  cancelLibraryScan,
  cancelBackgroundTask,
  cancelMetadataEnrichment,
  cancelPlaybackCache,
  cleanupAssetCache,
  cleanupHlsCache,
  cleanupWebDavCache,
  EpisodeDetail,
  FfmpegTranscodeCapabilities,
  getBackgroundTasks,
  getAppSettings,
  getCacheStatus,
  getLibraryScanStatus,
  getMetadataEnrichmentStatus,
  getPlaybackDecision,
  getPlaybackDiagnostics,
  getPlaybackCapabilities,
  getPlaybackCacheStatus,
  getPlaybackSubtitles,
  getRuntimeSelfCheck,
  getLibraryItemDetail,
  getLibraryItems,
  getMediaSources,
  LibraryItemDetail,
  LibraryItemSummary,
  LibraryMetadataEnrichmentStatus,
  LibraryScanStatus,
  LocalDirectoryBrowseResult,
  MediaSourceSummary,
  PlaybackCacheStatus,
  PlaybackDiagnostics,
  PlaybackSubtitleTrack,
  RuntimeSelfCheckSnapshot,
  posterUrl,
  preparePlaybackCache,
  removeMediaSource,
  rescrapeLibraryItem,
  scrapeLibrary,
  scanMediaSource,
  searchLibraryItemMetadata,
  setWatchedStatus,
  setLibraryItemLocked,
  scanLibrary,
  stopHlsSession,
  TmdbMetadataMatch,
  thumbnailUrl,
  tmdbPosterUrl,
  updateAppSettings,
  updateMediaSource,
  updatePlaybackProgress,
  testWebDavConnection,
  VideoFileSummary,
  WebDavDirectoryBrowseResult,
} from "../shared/api/client";

export function App() {
  const [items, setItems] = useState<LibraryItemSummary[]>([]);
  const [sources, setSources] = useState<MediaSourceSummary[]>([]);
  const [taskSnapshot, setTaskSnapshot] = useState<BackgroundTaskSnapshot | null>(null);
  const [cacheStatus, setCacheStatus] = useState<CacheUsageSummary | null>(null);
  const [settings, setSettings] = useState<AppSettingsSnapshot | null>(null);
  const [isSavingCacheSettings, setIsSavingCacheSettings] = useState(false);
  const [isSourceManagerOpen, setIsSourceManagerOpen] = useState(false);
  const [isSavingSource, setIsSavingSource] = useState(false);
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const [isSavingSettings, setIsSavingSettings] = useState(false);
  const [searchText, setSearchText] = useState("");
  const [isLoading, setIsLoading] = useState(true);
  const [isScanning, setIsScanning] = useState(false);
  const [scanStatus, setScanStatus] = useState<LibraryScanStatus | null>(null);
  const [isScraping, setIsScraping] = useState(false);
  const [scrapeStatus, setScrapeStatus] = useState<LibraryMetadataEnrichmentStatus | null>(null);
  const [selectedDetail, setSelectedDetail] = useState<LibraryItemDetail | null>(null);
  const [metadataCandidates, setMetadataCandidates] = useState<TmdbMetadataMatch[]>([]);
  const [playingFile, setPlayingFile] = useState<VideoFileSummary | null>(null);
  const [isDetailLoading, setIsDetailLoading] = useState(false);
  const [statusText, setStatusText] = useState("");
  const [errorText, setErrorText] = useState("");
  const scanWasRunningRef = useRef(false);
  const scrapeWasRunningRef = useRef(false);
  const cacheCleanupWasRunningRef = useRef(false);
  const sourceCleanupWasRunningRef = useRef(false);
  const selectedDetailRef = useRef<LibraryItemDetail | null>(null);

  const loadData = useCallback(async () => {
    setErrorText("");
    const [nextItems, nextSources, nextCacheStatus, nextSettings] = await Promise.all([
      getLibraryItems(),
      getMediaSources(),
      getCacheStatus(),
      getAppSettings(),
    ]);
    setItems(nextItems);
    setSources(nextSources);
    setCacheStatus(nextCacheStatus);
    setSettings(nextSettings);
  }, []);

  const applyScanStatus = useCallback(
    (nextStatus: LibraryScanStatus) => {
      setScanStatus(nextStatus);
      setIsScanning(nextStatus.isRunning);
      const nextStatusText = formatScanStatus(nextStatus);
      if (nextStatusText) {
        setStatusText(nextStatusText);
      }

      if (scanWasRunningRef.current && !nextStatus.isRunning && !nextStatus.wasCanceled) {
        void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
      }

      scanWasRunningRef.current = nextStatus.isRunning;
    },
    [loadData],
  );

	  const applyScrapeStatus = useCallback(
	    (nextStatus: LibraryMetadataEnrichmentStatus) => {
      setScrapeStatus(nextStatus);
      setIsScraping(nextStatus.isRunning);
      const nextStatusText = formatScrapeStatus(nextStatus);
      if (nextStatusText) {
        setStatusText(nextStatusText);
      }

      if (scrapeWasRunningRef.current && !nextStatus.isRunning && !nextStatus.wasCanceled) {
        void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
        const detailId = selectedDetailRef.current?.id;
        if (!detailId || (nextStatus.targetLibraryItemId && nextStatus.targetLibraryItemId !== detailId)) {
          scrapeWasRunningRef.current = nextStatus.isRunning;
          return;
        }

        void getLibraryItemDetail(detailId)
          .then(setSelectedDetail)
          .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
      }

      scrapeWasRunningRef.current = nextStatus.isRunning;
    },
	    [loadData],
	  );

  function refreshCacheStatusAfterCleanup(snapshot: BackgroundTaskSnapshot) {
    const cleanupIsRunning =
      snapshot.activeTask?.kind === "asset-cache-cleanup" ||
      snapshot.activeTask?.kind === "hls-cache-cleanup" ||
      snapshot.activeTask?.kind === "webdav-cache-cleanup";
    if (cacheCleanupWasRunningRef.current && !cleanupIsRunning) {
      void getCacheStatus()
        .then(setCacheStatus)
        .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
    }

    cacheCleanupWasRunningRef.current = cleanupIsRunning;

    const sourceCleanupIsRunning = snapshot.activeTask?.kind === "media-source-cleanup";
    if (sourceCleanupWasRunningRef.current && !sourceCleanupIsRunning) {
      void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
    }

    sourceCleanupWasRunningRef.current = sourceCleanupIsRunning;
  }

  useEffect(() => {
    void loadData()
      .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)))
      .finally(() => setIsLoading(false));
  }, [loadData]);

  useEffect(() => {
    selectedDetailRef.current = selectedDetail;
  }, [selectedDetail]);

  useEffect(() => {
    let disposed = false;
    let pollHandle = 0;

    async function pollStatus() {
      try {
        const nextStatus = await getLibraryScanStatus();
        if (!disposed) {
          applyScanStatus(nextStatus);
        }
      } catch {
        // Status polling is only a fallback for scan progress; keep the main UI usable.
      }
    }

    void pollStatus();
    if (typeof EventSource === "undefined") {
      pollHandle = window.setInterval(() => void pollStatus(), 1500);
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

    const events = new EventSource("/api/library/scan/events");
    events.addEventListener("status", (event) => {
      try {
        applyScanStatus(JSON.parse((event as MessageEvent<string>).data) as LibraryScanStatus);
      } catch {
        // Ignore malformed progress frames.
      }
    });
    events.onerror = () => {
      events.close();
      if (!disposed && pollHandle === 0) {
        pollHandle = window.setInterval(() => void pollStatus(), 1500);
      }
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScanStatus]);

  useEffect(() => {
    let disposed = false;
    let pollHandle = 0;

    async function pollStatus() {
      try {
        const nextStatus = await getMetadataEnrichmentStatus();
        if (!disposed) {
          applyScrapeStatus(nextStatus);
        }
      } catch {
        // Status polling is only a fallback for metadata progress.
      }
    }

    void pollStatus();
    if (typeof EventSource === "undefined") {
      pollHandle = window.setInterval(() => void pollStatus(), 1500);
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

    const events = new EventSource("/api/library/scrape/events");
    events.addEventListener("status", (event) => {
      try {
        applyScrapeStatus(JSON.parse((event as MessageEvent<string>).data) as LibraryMetadataEnrichmentStatus);
      } catch {
        // Ignore malformed progress frames.
      }
    });
    events.onerror = () => {
      events.close();
      if (!disposed && pollHandle === 0) {
        pollHandle = window.setInterval(() => void pollStatus(), 1500);
      }
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScrapeStatus]);

  useEffect(() => {
    let disposed = false;
    let pollHandle = 0;

	    async function pollTasks() {
	      try {
	        const nextSnapshot = await getBackgroundTasks();
	        if (!disposed) {
	          setTaskSnapshot(nextSnapshot);
	          refreshCacheStatusAfterCleanup(nextSnapshot);
	        }
	      } catch {
	        // Task center status is supplementary; keep the library usable if it fails.
      }
    }

    void pollTasks();
    if (typeof EventSource === "undefined") {
      pollHandle = window.setInterval(() => void pollTasks(), 1500);
      return () => {
        disposed = true;
        window.clearInterval(pollHandle);
      };
    }

	    const events = new EventSource("/api/tasks/events");
	    events.addEventListener("status", (event) => {
	      try {
	        const nextSnapshot = JSON.parse((event as MessageEvent<string>).data) as BackgroundTaskSnapshot;
	        setTaskSnapshot(nextSnapshot);
	        refreshCacheStatusAfterCleanup(nextSnapshot);
	      } catch {
	        // Ignore malformed task frames.
	      }
    });
    events.onerror = () => {
      events.close();
      if (!disposed && pollHandle === 0) {
        pollHandle = window.setInterval(() => void pollTasks(), 1500);
      }
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
	  }, []);

  const displayedItems = useMemo(() => {
    const normalizedSearch = searchText.trim().toLowerCase();
    return items
      .filter((item) => item.title.toLowerCase().includes(normalizedSearch))
      .sort((a, b) => a.title.localeCompare(b.title, "zh-Hans"));
  }, [items, searchText]);

  const continueItems = useMemo(() => {
    return items.filter((item) => item.maxProgressSeconds > 0 && item.maxDurationSeconds > 0).slice(0, 12);
  }, [items]);
  const scanPercent = scanProgressPercent(scanStatus);
  const scrapePercent = scrapeProgressPercent(scrapeStatus);

  async function handleCreateSource(path: string, name: string): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在添加媒体源");
    try {
      await addLocalMediaSource(path.trim(), name.trim() || undefined);
      setSources(await getMediaSources());
      setStatusText("媒体源已添加，可开始扫描");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleCreateWebDavSource(
    url: string,
    name: string,
    username: string,
    password: string,
  ): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在添加 WebDAV 媒体源");
    try {
      await addWebDavMediaSource(
        url.trim(),
        name.trim() || undefined,
        username.trim() || undefined,
        password || undefined,
      );
      setSources(await getMediaSources());
      setStatusText("WebDAV 媒体源已添加");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleUpdateSource(sourceId: number, name: string, isEnabled: boolean): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在更新媒体源");
    try {
      const updated = await updateMediaSource(sourceId, { name: name.trim(), isEnabled });
      setSources((current) => current.map((source) => (source.id === updated.id ? updated : source)));
      setStatusText("媒体源已更新");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleRemoveSource(sourceId: number): Promise<boolean> {
    setIsSavingSource(true);
    setErrorText("");
    setStatusText("正在移除媒体源");
    try {
      const task = await removeMediaSource(sourceId);
      setSources((current) => current.filter((source) => source.id !== sourceId));
      setTaskSnapshot((snapshot) => ({
        tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
        activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
      }));
      setStatusText("媒体源清理已提交");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  async function handleScan() {
    setIsScanning(true);
    setErrorText("");
    setStatusText("正在扫描");
    try {
      applyScanStatus(await scanLibrary());
      setStatusText("扫描已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScanning(false);
    }
  }

  async function handleScanSource(sourceId: number): Promise<boolean> {
    setIsScanning(true);
    setErrorText("");
    setStatusText("正在扫描媒体源");
    try {
      applyScanStatus(await scanMediaSource(sourceId));
      setStatusText("媒体源扫描已提交");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScanning(false);
      return false;
    }
  }

  async function handleCancelScan() {
    setErrorText("");
    setStatusText("正在取消扫描");
    try {
      applyScanStatus(await cancelLibraryScan());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleScrape() {
    setIsScraping(true);
    setErrorText("");
    setStatusText("正在刮削");
    try {
      applyScrapeStatus(await scrapeLibrary());
      setStatusText("刮削已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScraping(false);
    }
  }

  async function handleCancelScrape() {
    setErrorText("");
    setStatusText("正在取消刮削");
    try {
      applyScrapeStatus(await cancelMetadataEnrichment());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCleanupAssetCache() {
    setErrorText("");
    setStatusText("正在提交图片缓存清理");
    try {
      const task = await cleanupAssetCache();
      setTaskSnapshot((snapshot) => ({
        tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
        activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
      }));
      setStatusText("图片缓存清理已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  function pushBackgroundTask(task: BackgroundTaskStatus) {
    setTaskSnapshot((snapshot) => ({
      tasks: [task, ...(snapshot?.tasks.filter((item) => item.id !== task.id) ?? [])],
      activeTask: task.isRunning ? task : snapshot?.activeTask ?? null,
    }));
  }

  async function handleCleanupTranscodeCache() {
    setErrorText("");
    setStatusText("正在提交 HLS 缓存清理");
    try {
      const task = await cleanupHlsCache();
      pushBackgroundTask(task);
      setStatusText("HLS 缓存清理已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCleanupWebDavCache() {
    setErrorText("");
    setStatusText("正在提交 WebDAV 缓存清理");
    try {
      const task = await cleanupWebDavCache();
      pushBackgroundTask(task);
      setStatusText("WebDAV 缓存清理已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleSaveCacheSettings(
    hlsRetentionHours: number,
    imageCleanupScope: string,
    webDavRetentionHours: number,
    webDavMaxGb: number,
  ) {
    setErrorText("");
    setStatusText("正在保存缓存策略");
    setIsSavingCacheSettings(true);
    try {
      const nextSettings = await updateAppSettings({
        cache: {
          hlsRetentionHours,
          imageCleanupScope,
          webDavRetentionHours,
          webDavMaxGb,
        },
      });
      setSettings(nextSettings);
      setStatusText("缓存策略已保存");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSavingCacheSettings(false);
    }
  }

  async function handleSaveAppSettings(
    tmdb: AppSettingsSnapshot["tmdb"],
    cache: CacheSettings,
    playback: AppSettingsSnapshot["playback"],
  ) {
    setErrorText("");
    setStatusText("正在保存设置");
    setIsSavingSettings(true);
    try {
      const nextSettings = await updateAppSettings({ tmdb, cache, playback });
      setSettings(nextSettings);
      setStatusText("设置已保存");
      setIsSettingsOpen(false);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSavingSettings(false);
    }
  }

  async function handleRescrapeCurrentDetail() {
    if (!selectedDetail) {
      return;
    }

    setIsScraping(true);
    setErrorText("");
    setStatusText("正在提交重刮削");
    try {
      applyScrapeStatus(await rescrapeLibraryItem(selectedDetail.id));
      setStatusText("重刮削已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScraping(false);
    }
  }

  async function handleSearchCurrentDetailMetadata() {
    if (!selectedDetail) {
      return;
    }

    const query = window.prompt("搜索 TMDB", selectedDetail.title);
    if (!query?.trim()) {
      return;
    }

    setErrorText("");
    setStatusText("正在搜索匹配项");
    try {
      const candidates = await searchLibraryItemMetadata(
        selectedDetail.id,
        query.trim(),
        selectedDetail.itemKind,
        selectedDetail.releaseDate?.slice(0, 4),
      );
      setMetadataCandidates(candidates);
      setStatusText(candidates.length === 0 ? "未找到匹配项" : `找到 ${candidates.length} 个匹配项`);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleApplyMetadataCandidate(match: TmdbMetadataMatch) {
    if (!selectedDetail) {
      return;
    }

    setIsDetailLoading(true);
    setErrorText("");
    setStatusText("正在应用匹配项");
    try {
      const detail = await applyLibraryItemMetadata(selectedDetail.id, match);
      setSelectedDetail(detail);
      selectedDetailRef.current = detail;
      setMetadataCandidates([]);
      setStatusText("已应用匹配项并锁定元数据");
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsDetailLoading(false);
    }
  }

  async function handleToggleCurrentDetailLock() {
    if (!selectedDetail) {
      return;
    }

    setIsDetailLoading(true);
    setErrorText("");
    try {
      const detail = await setLibraryItemLocked(selectedDetail.id, !selectedDetail.isLocked);
      setSelectedDetail(detail);
      selectedDetailRef.current = detail;
      setStatusText(detail.isLocked ? "已锁定元数据" : "已解锁元数据");
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsDetailLoading(false);
    }
  }

  async function handleCancelTask(taskId: string) {
    setErrorText("");
    try {
      await cancelBackgroundTask(taskId);
      setTaskSnapshot(await getBackgroundTasks());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function openDetail(item: LibraryItemSummary) {
    setIsDetailLoading(true);
    setErrorText("");
    setPlayingFile(null);
    setMetadataCandidates([]);
    try {
      setSelectedDetail(await getLibraryItemDetail(item.id));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsDetailLoading(false);
    }
  }

  async function toggleWatched(file: VideoFileSummary) {
    try {
      await setWatchedStatus(file.id, !file.isWatched, file.durationSeconds);
      if (selectedDetail) {
        setSelectedDetail(await getLibraryItemDetail(selectedDetail.id));
      }
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function refreshCurrentDetail() {
    if (!selectedDetail) {
      return;
    }

    try {
      setSelectedDetail(await getLibraryItemDetail(selectedDetail.id));
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  if (selectedDetail && playingFile) {
    return (
      <PlayerView
        detail={selectedDetail}
        file={playingFile}
        onBack={() => {
          setPlayingFile(null);
          void refreshCurrentDetail();
        }}
      />
    );
  }

  if (selectedDetail) {
    return (
      <DetailView
        detail={selectedDetail}
        errorText={errorText}
        isRescraping={isScraping}
        isRefreshing={isDetailLoading}
        metadataCandidates={metadataCandidates}
        onBack={() => {
          setPlayingFile(null);
          setSelectedDetail(null);
          setMetadataCandidates([]);
        }}
        onApplyMetadataCandidate={(match) => void handleApplyMetadataCandidate(match)}
        onCancelTask={(taskId) => void handleCancelTask(taskId)}
        onSearchMetadata={() => void handleSearchCurrentDetailMetadata()}
        onPlay={(file) => setPlayingFile(file)}
        onRescrape={() => void handleRescrapeCurrentDetail()}
        onToggleMetadataLock={() => void handleToggleCurrentDetailLock()}
        onToggleWatched={toggleWatched}
        statusText={statusText}
        taskSnapshot={taskSnapshot}
      />
    );
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div className="brand">OmniPlay NAS</div>
        <div className="toolbar" aria-label="library tools">
          <button aria-label="search">
            <Search size={19} />
          </button>
          <button aria-label="sort">
            <SlidersHorizontal size={19} />
          </button>
          <button aria-label="scan" disabled={isScanning || isScraping || sources.length === 0} onClick={handleScan}>
            <RefreshCw size={19} className={isScanning ? "spin" : ""} />
          </button>
          {isScanning ? (
            <button aria-label="cancel scan" disabled={isScraping} onClick={handleCancelScan}>
              <CircleStop size={19} />
            </button>
          ) : null}
          <button aria-label="scrape" disabled={isScanning || isScraping || items.length === 0} onClick={handleScrape}>
            <Sparkles size={19} className={isScraping ? "pulse" : ""} />
          </button>
          {isScraping ? (
            <button aria-label="cancel scrape" disabled={isScanning} onClick={handleCancelScrape}>
              <CircleStop size={19} />
            </button>
          ) : null}
	          <button
	            aria-label="media sources"
	            disabled={isScanning || isScraping}
	            onClick={() => setIsSourceManagerOpen(true)}
	            title="媒体源"
	          >
	            <FolderPlus size={20} />
	          </button>
	          <button aria-label="settings" disabled={!settings} onClick={() => setIsSettingsOpen(true)} title="设置">
	            <Settings size={19} />
	          </button>
        </div>
      </header>

      {isSourceManagerOpen ? (
        <SourceManagerPanel
          disabled={isScanning || isScraping || isSavingSource}
          isSaving={isSavingSource}
          onAdd={(path, name) => handleCreateSource(path, name)}
          onAddWebDav={(url, name, username, password) => handleCreateWebDavSource(url, name, username, password)}
          onClose={() => setIsSourceManagerOpen(false)}
          onRemove={(sourceId) => handleRemoveSource(sourceId)}
          onScan={(sourceId) => handleScanSource(sourceId)}
          onUpdate={(sourceId, name, isEnabled) => handleUpdateSource(sourceId, name, isEnabled)}
          scanStatus={scanStatus}
          sources={sources}
        />
      ) : null}

      {isSettingsOpen && settings ? (
        <SettingsPanel
          isSaving={isSavingSettings}
          onClose={() => setIsSettingsOpen(false)}
          onSave={(tmdb, cache, playback) => void handleSaveAppSettings(tmdb, cache, playback)}
          settings={settings}
        />
      ) : null}

      <section className="continue">
        <h1>继续播放</h1>
        {continueItems.length === 0 ? (
          <div className="emptyRow">
            <Play size={22} />
            <span>暂无续播</span>
          </div>
        ) : (
          <div className="continueRail">
            {continueItems.map((item) => (
              <PosterCard item={item} key={item.id} compact onOpen={openDetail} />
            ))}
          </div>
        )}
      </section>

      <section className="library">
        <div className="sectionHeader">
          <h2>所有影视</h2>
          <span>{displayedItems.length} items</span>
        </div>
	        <div className="libraryControls">
	          <input
	            aria-label="search library"
            onChange={(event) => setSearchText(event.target.value)}
            placeholder="搜索"
            value={searchText}
          />
          {statusText ? <span>{statusText}</span> : null}
          {scanPercent !== null ? (
            <progress aria-label="scan progress" className="scanProgress" max={100} value={scanPercent} />
          ) : null}
          {scrapePercent !== null ? (
            <progress aria-label="scrape progress" className="scanProgress" max={100} value={scrapePercent} />
	          ) : null}
	          {errorText ? <strong>{errorText}</strong> : null}
	        </div>
	        <CacheMaintenance
	          cacheStatus={cacheStatus}
	          cacheSettings={settings?.cache ?? null}
	          disabled={Boolean(taskSnapshot?.activeTask?.isRunning)}
	          isSaving={isSavingCacheSettings}
	          onCleanupAssets={() => void handleCleanupAssetCache()}
	          onCleanupTranscode={() => void handleCleanupTranscodeCache()}
	          onCleanupWebDav={() => void handleCleanupWebDavCache()}
	          onSaveSettings={(hlsRetentionHours, imageCleanupScope, webDavRetentionHours, webDavMaxGb) =>
	            void handleSaveCacheSettings(hlsRetentionHours, imageCleanupScope, webDavRetentionHours, webDavMaxGb)
	          }
	        />
	        <TaskCenter snapshot={taskSnapshot} onCancel={(taskId) => void handleCancelTask(taskId)} />
        <div className="posterGrid">
          {isLoading ? <div className="emptyRow">加载中</div> : null}
          {!isLoading && displayedItems.length === 0 ? <div className="emptyRow">媒体库为空</div> : null}
          {displayedItems.map((item) => (
            <PosterCard item={item} key={item.id} onOpen={openDetail} />
          ))}
        </div>
      </section>
    </main>
  );
}

function formatScanStatus(status: LibraryScanStatus): string {
  if (status.isRunning) {
    if (status.isCancellationRequested) {
      return "正在取消扫描";
    }

    const progress = status.progress;
    if (!progress) {
      return "正在扫描";
    }

    const phaseLabel =
      {
        starting: "准备扫描",
        probing: "探测媒体",
        indexing: "写入媒体库",
        "source-completed": "媒体源完成",
      }[progress.phase] ?? "正在扫描";
    const fileCount =
      progress.totalVideoFileCount > 0
        ? `${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`
        : "";
    const probeCount =
      progress.phase === "probing" && progress.probeCandidateCount > 0
        ? `，探测 ${progress.probedVideoFileCount}/${progress.probeCandidateCount}`
        : "";
    const source = progress.currentSourceName ? ` · ${progress.currentSourceName}` : "";
    return `${phaseLabel}${fileCount ? ` ${fileCount}` : ""}${probeCount}${source}`;
  }

  if (status.wasCanceled) {
    return "扫描已取消";
  }

  if (status.lastError) {
    return status.lastError;
  }

  if (status.lastSummary) {
    return `扫描完成：${status.lastSummary.newVideoFileCount} 个新视频`;
  }

  return "";
}

function scanProgressPercent(status: LibraryScanStatus | null): number | null {
  const progress = status?.progress;
  if (!status?.isRunning || !progress || progress.totalVideoFileCount <= 0) {
    return null;
  }

  if (progress.phase === "probing" && progress.probeCandidateCount > 0) {
    return Math.round((progress.probedVideoFileCount / progress.probeCandidateCount) * 100);
  }

  return Math.round((progress.processedVideoFileCount / progress.totalVideoFileCount) * 100);
}

function formatScrapeStatus(status: LibraryMetadataEnrichmentStatus): string {
  if (status.isRunning) {
    if (status.isCancellationRequested) {
      return "正在取消刮削";
    }

    const progress = status.progress;
    if (!progress) {
      return "正在刮削";
    }

    const phaseLabel =
      {
        starting: "准备刮削",
        searching: "匹配元数据",
        "fetching-details": "精确刷新 TMDB",
        "fetching-episodes": "刷新分集信息",
        "downloading-poster": "下载海报",
        updating: "写入元数据",
      }[progress.phase] ?? "正在刮削";
    const itemCount =
      progress.targetItemCount > 0
        ? `${Math.min(progress.processedItemCount, progress.targetItemCount)}/${progress.targetItemCount}`
        : "";
    const title = progress.currentTitle ? ` · ${progress.currentTitle}` : "";
    return `${phaseLabel}${itemCount ? ` ${itemCount}` : ""}${title}`;
  }

  if (status.wasCanceled) {
    return "刮削已取消";
  }

  if (status.lastError) {
    return status.lastError;
  }

  if (status.lastSummary) {
    return `刮削完成：${status.lastSummary.updatedItems} 个条目，${status.lastSummary.downloadedPosters} 张海报`;
  }

  return "";
}

function scrapeProgressPercent(status: LibraryMetadataEnrichmentStatus | null): number | null {
  const progress = status?.progress;
  if (!status?.isRunning || !progress || progress.targetItemCount <= 0) {
    return null;
  }

  return Math.round((progress.processedItemCount / progress.targetItemCount) * 100);
}

function SourceManagerPanel({
  disabled,
  isSaving,
  onAdd,
  onAddWebDav,
  onClose,
  onRemove,
  onScan,
  onUpdate,
  scanStatus,
  sources,
}: {
  disabled: boolean;
  isSaving: boolean;
  onAdd: (path: string, name: string) => Promise<boolean>;
  onAddWebDav: (url: string, name: string, username: string, password: string) => Promise<boolean>;
  onClose: () => void;
  onRemove: (sourceId: number) => Promise<boolean>;
  onScan: (sourceId: number) => Promise<boolean>;
  onUpdate: (sourceId: number, name: string, isEnabled: boolean) => Promise<boolean>;
  scanStatus: LibraryScanStatus | null;
  sources: MediaSourceSummary[];
}) {
  const [newName, setNewName] = useState("");
  const [newPath, setNewPath] = useState("");
  const [webDavName, setWebDavName] = useState("");
  const [webDavUrl, setWebDavUrl] = useState("");
  const [webDavUsername, setWebDavUsername] = useState("");
  const [webDavPassword, setWebDavPassword] = useState("");
  const [webDavBrowseResult, setWebDavBrowseResult] = useState<WebDavDirectoryBrowseResult | null>(null);
  const [webDavStatus, setWebDavStatus] = useState("");
  const [webDavError, setWebDavError] = useState("");
  const [isWebDavBusy, setIsWebDavBusy] = useState(false);
  const [browsePath, setBrowsePath] = useState("");
  const [browseResult, setBrowseResult] = useState<LocalDirectoryBrowseResult | null>(null);
  const [browseError, setBrowseError] = useState("");
  const [isBrowsing, setIsBrowsing] = useState(false);
  const [draftNames, setDraftNames] = useState<Record<number, string>>({});

  useEffect(() => {
    setDraftNames(Object.fromEntries(sources.map((source) => [source.id, source.name])));
  }, [sources]);

  useEffect(() => {
    let disposed = false;
    void loadDirectory(browsePath, () => disposed);

    return () => {
      disposed = true;
    };
  }, []);

  async function loadDirectory(path: string, isDisposed: () => boolean = () => false) {
    setIsBrowsing(true);
    setBrowseError("");
    try {
      const result = await browseLocalDirectories(path);
      if (!isDisposed()) {
        setBrowseResult(result);
        setBrowsePath(result.currentPath);
      }
    } catch (error) {
      if (!isDisposed()) {
        setBrowseError(error instanceof Error ? error.message : String(error));
      }
    } finally {
      if (!isDisposed()) {
        setIsBrowsing(false);
      }
    }
  }

  async function handleAdd(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!newPath.trim()) {
      return;
    }

    const added = await onAdd(newPath, newName);
    if (added) {
      setNewName("");
      setNewPath("");
    }
  }

  async function handleAddWebDav(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!webDavUrl.trim()) {
      return;
    }

    const added = await onAddWebDav(webDavUrl, webDavName, webDavUsername, webDavPassword);
    if (added) {
      setWebDavName("");
      setWebDavUrl("");
      setWebDavUsername("");
      setWebDavPassword("");
      setWebDavBrowseResult(null);
      setWebDavStatus("");
      setWebDavError("");
    }
  }

  async function handleTestWebDav() {
    if (!webDavUrl.trim()) {
      return;
    }

    setIsWebDavBusy(true);
    setWebDavStatus("正在测试 WebDAV");
    setWebDavError("");
    try {
      const result = await testWebDavConnection(
        webDavUrl.trim(),
        webDavUsername.trim() || undefined,
        webDavPassword || undefined,
      );
      setWebDavUrl(result.url);
      setWebDavStatus(result.message);
      if (!result.isReachable) {
        setWebDavError(result.message);
      }
    } catch (error) {
      setWebDavStatus("");
      setWebDavError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsWebDavBusy(false);
    }
  }

  async function handleBrowseWebDav(url = webDavUrl) {
    if (!url.trim()) {
      return;
    }

    setIsWebDavBusy(true);
    setWebDavStatus("正在浏览 WebDAV");
    setWebDavError("");
    try {
      const result = await browseWebDavDirectories(
        url.trim(),
        webDavUsername.trim() || undefined,
        webDavPassword || undefined,
      );
      setWebDavBrowseResult(result);
      setWebDavUrl(result.currentUrl);
      setWebDavStatus(result.entries.length === 0 ? "当前目录没有子目录" : `发现 ${result.entries.length} 个子目录`);
    } catch (error) {
      setWebDavStatus("");
      setWebDavError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsWebDavBusy(false);
    }
  }

  async function handleRemove(source: MediaSourceSummary) {
    if (!window.confirm(`移除媒体源“${source.name}”？`)) {
      return;
    }

    await onRemove(source.id);
  }

  return (
    <div className="settingsOverlay" role="dialog" aria-label="media source manager" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close media sources" onClick={onClose} type="button" />
      <aside className="settingsDrawer sourceDrawer">
        <header className="settingsHeader">
          <div>
            <h2>媒体源</h2>
            <span>{sources.length} 个来源</span>
          </div>
          <button aria-label="close media sources" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <form className="sourceForm" onSubmit={(event) => void handleAdd(event)}>
          <label className="settingsField">
            <span>本地目录</span>
            <input
              disabled={disabled}
              onChange={(event) => setNewPath(event.target.value)}
              placeholder="/volume1/video"
              value={newPath}
            />
          </label>
          <label className="settingsField">
            <span>名称</span>
            <input
              disabled={disabled}
              onChange={(event) => setNewName(event.target.value)}
              placeholder="自动使用目录名"
              value={newName}
            />
          </label>
          <button disabled={disabled || !newPath.trim()} type="submit">
            <FolderPlus size={16} />
            <span>{isSaving ? "添加中" : "添加"}</span>
          </button>
        </form>

        <section className="directoryBrowser" aria-label="local directory browser">
          <div className="directoryPath">
            <input
              aria-label="browse path"
              disabled={disabled || isBrowsing}
              onChange={(event) => setBrowsePath(event.target.value)}
              value={browsePath}
            />
            <button
              aria-label="open path"
              disabled={disabled || isBrowsing || !browsePath.trim()}
              onClick={() => void loadDirectory(browsePath)}
              type="button"
            >
              <FolderOpen size={15} />
            </button>
            <button
              aria-label="select current path"
              disabled={disabled || !browseResult}
              onClick={() => browseResult ? setNewPath(browseResult.currentPath) : undefined}
              type="button"
            >
              <span>选择</span>
            </button>
          </div>
          {browseError ? <strong>{browseError}</strong> : null}
          <div className="directoryList">
            {browseResult?.parentPath ? (
              <button
                className="directoryRow"
                disabled={disabled || isBrowsing}
                onClick={() => void loadDirectory(browseResult.parentPath ?? "")}
                type="button"
              >
                <ArrowLeft size={15} />
                <span>上级目录</span>
              </button>
            ) : null}
            {browseResult?.entries.map((entry) => (
              <button
                className="directoryRow"
                disabled={disabled || isBrowsing || !entry.isReadable}
                key={entry.path}
                onClick={() => void loadDirectory(entry.path)}
                title={entry.path}
                type="button"
              >
                <FolderOpen size={15} />
                <span>{entry.name}</span>
                {!entry.isReadable ? <em>不可访问</em> : null}
              </button>
            ))}
            {!browseResult || browseResult.entries.length > 0 || browseResult.parentPath ? null : (
              <div className="sourceEmpty">没有子目录</div>
            )}
          </div>
        </section>

        <form className="sourceForm webDavForm" onSubmit={(event) => void handleAddWebDav(event)}>
          <label className="settingsField webDavUrlField">
            <span>WebDAV 地址</span>
            <input
              disabled={disabled}
              onChange={(event) => setWebDavUrl(event.target.value)}
              placeholder="https://nas.example.com/dav"
              value={webDavUrl}
            />
          </label>
          <label className="settingsField">
            <span>名称</span>
            <input
              disabled={disabled}
              onChange={(event) => setWebDavName(event.target.value)}
              placeholder="自动使用主机名"
              value={webDavName}
            />
          </label>
          <label className="settingsField">
            <span>用户名</span>
            <input
              autoComplete="username"
              disabled={disabled}
              onChange={(event) => setWebDavUsername(event.target.value)}
              placeholder="可留空"
              value={webDavUsername}
            />
          </label>
          <label className="settingsField">
            <span>密码</span>
            <input
              autoComplete="current-password"
              disabled={disabled}
              onChange={(event) => setWebDavPassword(event.target.value)}
              placeholder="可留空"
              type="password"
              value={webDavPassword}
            />
          </label>
          <div className="webDavActions">
            <button
              disabled={disabled || isWebDavBusy || !webDavUrl.trim()}
              onClick={() => void handleTestWebDav()}
              type="button"
            >
              <Cloud size={16} />
              <span>{isWebDavBusy ? "测试中" : "测试"}</span>
            </button>
            <button
              disabled={disabled || isWebDavBusy || !webDavUrl.trim()}
              onClick={() => void handleBrowseWebDav()}
              type="button"
            >
              <FolderOpen size={16} />
              <span>{isWebDavBusy ? "浏览中" : "浏览"}</span>
            </button>
            <button disabled={disabled || !webDavUrl.trim()} type="submit">
              <Cloud size={16} />
              <span>{isSaving ? "添加中" : "添加 WebDAV"}</span>
            </button>
          </div>
        </form>

        {webDavStatus || webDavError || webDavBrowseResult ? (
          <section className="directoryBrowser webDavBrowser" aria-label="webdav directory browser">
            {webDavStatus ? <span>{webDavStatus}</span> : null}
            {webDavError ? <strong>{webDavError}</strong> : null}
            {webDavBrowseResult ? (
              <div className="directoryList">
                {webDavBrowseResult.parentUrl ? (
                  <button
                    className="directoryRow"
                    disabled={disabled || isWebDavBusy}
                    onClick={() => void handleBrowseWebDav(webDavBrowseResult.parentUrl ?? "")}
                    type="button"
                  >
                    <ArrowLeft size={15} />
                    <span>上级目录</span>
                  </button>
                ) : null}
                {webDavBrowseResult.entries.map((entry) => (
                  <button
                    className="directoryRow"
                    disabled={disabled || isWebDavBusy || !entry.isReadable}
                    key={entry.url}
                    onClick={() => void handleBrowseWebDav(entry.url)}
                    title={entry.url}
                    type="button"
                  >
                    <FolderOpen size={15} />
                    <span>{entry.name}</span>
                  </button>
                ))}
              </div>
            ) : null}
          </section>
        ) : null}

        <section className="sourceList">
          {sources.length === 0 ? <div className="sourceEmpty">还没有媒体源</div> : null}
          {sources.map((source) => {
            const draftName = draftNames[source.id] ?? source.name;
            const nameChanged = draftName.trim() !== source.name;
            const isSourceScanning = isScanningSource(scanStatus, source);
            const normalizedKind = source.kind.toLowerCase();
            const canScanSource = normalizedKind === "local" || normalizedKind === "webdav";
            return (
              <article className="sourceRow" key={source.id}>
                <div className="sourceMeta">
                  <div>
                    <strong>{source.name}</strong>
                    <span>
                      {[
                        formatSourceKind(source.kind),
                        source.isEnabled ? "已启用" : "已停用",
                        isSourceScanning ? formatSourceScanStatus(scanStatus) : null,
                        canScanSource
                          ? source.lastScannedAt
                            ? `上次扫描 ${formatDateTime(source.lastScannedAt)}`
                            : "未扫描"
                          : "待接入扫描",
                      ]
                        .filter(Boolean)
                        .join(" · ")}
                    </span>
                  </div>
                  <p>{source.baseUrl}</p>
                </div>
                <div className="sourceEdit">
                  <input
                    aria-label={`rename ${source.name}`}
                    disabled={disabled}
                    onChange={(event) =>
                      setDraftNames((current) => ({ ...current, [source.id]: event.target.value }))
                    }
                    value={draftName}
                  />
                  <button
                    aria-label={`save ${source.name}`}
                    disabled={disabled || !nameChanged || !draftName.trim()}
                    onClick={() => void onUpdate(source.id, draftName, source.isEnabled)}
                    type="button"
                  >
                    <Save size={15} />
                  </button>
                </div>
                <div className="sourceActions">
                  <label className="sourceToggle">
                    <input
                      checked={source.isEnabled}
                      disabled={disabled}
                      onChange={(event) => void onUpdate(source.id, draftName, event.target.checked)}
                      type="checkbox"
                    />
                    <span>{source.isEnabled ? "启用" : "停用"}</span>
                  </label>
                  <button
                    aria-label={`remove ${source.name}`}
                    disabled={disabled}
                    onClick={() => void handleRemove(source)}
                    type="button"
                  >
                    <Trash2 size={15} />
                  </button>
                  <button
                    aria-label={`scan ${source.name}`}
                    disabled={disabled || !source.isEnabled || !canScanSource}
                    onClick={() => void onScan(source.id)}
                    title={canScanSource ? "扫描媒体源" : "该媒体源类型暂不支持扫描"}
                    type="button"
                  >
                    <RefreshCw size={15} className={isSourceScanning ? "spin" : ""} />
                  </button>
                </div>
              </article>
            );
          })}
        </section>
      </aside>
    </div>
  );
}

function formatSourceKind(kind: string): string {
  const normalized = kind.toLowerCase();
  if (normalized === "local") {
    return "本地目录";
  }

  if (normalized === "webdav") {
    return "WebDAV";
  }

  return kind;
}

function isScanningSource(scanStatus: LibraryScanStatus | null, source: MediaSourceSummary): boolean {
  if (!scanStatus?.isRunning) {
    return false;
  }

  const currentSourceName = scanStatus.progress?.currentSourceName;
  return currentSourceName ? currentSourceName === source.name : false;
}

function formatSourceScanStatus(scanStatus: LibraryScanStatus | null): string | null {
  const progress = scanStatus?.progress;
  if (!scanStatus?.isRunning || !progress) {
    return null;
  }

  if (progress.totalVideoFileCount > 0) {
    return `扫描 ${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`;
  }

  return "扫描中";
}

function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function SettingsPanel({
  isSaving,
  onClose,
  onSave,
  settings,
}: {
  isSaving: boolean;
  onClose: () => void;
  onSave: (
    tmdb: AppSettingsSnapshot["tmdb"],
    cache: CacheSettings,
    playback: AppSettingsSnapshot["playback"],
  ) => void;
  settings: AppSettingsSnapshot;
}) {
  const [tmdb, setTmdb] = useState(settings.tmdb);
  const [cache, setCache] = useState(settings.cache);
  const [playback, setPlayback] = useState(settings.playback);
  const [selfCheck, setSelfCheck] = useState<RuntimeSelfCheckSnapshot | null>(null);
  const [selfCheckError, setSelfCheckError] = useState("");
  const [isCheckingRuntime, setIsCheckingRuntime] = useState(false);

  useEffect(() => {
    setTmdb(settings.tmdb);
    setCache(settings.cache);
    setPlayback(settings.playback);
  }, [settings]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSave(
      {
        ...tmdb,
        customApiKey: tmdb.customApiKey.trim(),
        customAccessToken: tmdb.customAccessToken.trim(),
        language: tmdb.language.trim() || "zh-CN",
      },
      {
        ...cache,
        hlsRetentionHours: Math.min(720, Math.max(1, Math.round(cache.hlsRetentionHours || 24))),
        webDavRetentionHours: Math.min(720, Math.max(1, Math.round(cache.webDavRetentionHours || 72))),
        webDavMaxGb: Math.min(1024, Math.max(1, Math.round(cache.webDavMaxGb || 20))),
      },
      playback,
    );
  }

  async function handleRuntimeSelfCheck() {
    setSelfCheckError("");
    setIsCheckingRuntime(true);
    try {
      setSelfCheck(await getRuntimeSelfCheck());
    } catch (error) {
      setSelfCheckError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsCheckingRuntime(false);
    }
  }

  const hasPlaybackStrategy = playback.directStream || playback.hlsRemux || playback.transcode;

  return (
    <div className="settingsOverlay" role="dialog" aria-label="settings panel" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close settings" onClick={onClose} type="button" />
      <aside className="settingsDrawer">
        <header className="settingsHeader">
          <div>
            <h2>设置</h2>
            <span>{settings.phase}</span>
          </div>
          <button aria-label="close settings" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <form className="settingsForm" onSubmit={handleSubmit}>
          <section className="settingsSection">
            <h3>TMDB</h3>
            <label className="settingsToggle">
              <input
                checked={tmdb.enableMetadataEnrichment}
                onChange={(event) => setTmdb({ ...tmdb, enableMetadataEnrichment: event.target.checked })}
                type="checkbox"
              />
              <span>元数据刮削</span>
            </label>
            <label className="settingsToggle">
              <input
                checked={tmdb.enablePosterDownloads}
                onChange={(event) => setTmdb({ ...tmdb, enablePosterDownloads: event.target.checked })}
                type="checkbox"
              />
              <span>下载海报</span>
            </label>
            <label className="settingsToggle">
              <input
                checked={tmdb.enableBuiltInPublicSource}
                onChange={(event) => setTmdb({ ...tmdb, enableBuiltInPublicSource: event.target.checked })}
                type="checkbox"
              />
              <span>内置公开源</span>
            </label>
            <label className="settingsField">
              <span>语言</span>
              <input
                onChange={(event) => setTmdb({ ...tmdb, language: event.target.value })}
                placeholder="zh-CN"
                value={tmdb.language}
              />
            </label>
            <label className="settingsField">
              <span>API Key</span>
              <input
                autoComplete="off"
                onChange={(event) => setTmdb({ ...tmdb, customApiKey: event.target.value })}
                type="password"
                value={tmdb.customApiKey}
              />
            </label>
            <label className="settingsField">
              <span>Access Token</span>
              <input
                autoComplete="off"
                onChange={(event) => setTmdb({ ...tmdb, customAccessToken: event.target.value })}
                type="password"
                value={tmdb.customAccessToken}
              />
            </label>
          </section>

          <section className="settingsSection">
            <h3>缓存</h3>
            <label className="settingsField">
              <span>HLS 保留小时</span>
              <input
                max={720}
                min={1}
                onChange={(event) => setCache({ ...cache, hlsRetentionHours: Number(event.target.value) })}
                type="number"
                value={cache.hlsRetentionHours}
              />
            </label>
            <label className="settingsField">
              <span>WebDAV 保留小时</span>
              <input
                max={720}
                min={1}
                onChange={(event) => setCache({ ...cache, webDavRetentionHours: Number(event.target.value) })}
                type="number"
                value={cache.webDavRetentionHours}
              />
            </label>
            <label className="settingsField">
              <span>WebDAV 上限 GB</span>
              <input
                max={1024}
                min={1}
                onChange={(event) => setCache({ ...cache, webDavMaxGb: Number(event.target.value) })}
                type="number"
                value={cache.webDavMaxGb}
              />
            </label>
            <label className="settingsField">
              <span>图片清理范围</span>
              <select
                onChange={(event) => setCache({ ...cache, imageCleanupScope: event.target.value })}
                value={cache.imageCleanupScope}
              >
                <option value="orphans-and-untracked">孤儿记录和残留文件</option>
                <option value="orphans-only">仅孤儿记录</option>
              </select>
            </label>
          </section>

          <section className="settingsSection">
            <h3>播放</h3>
            <label className="settingsToggle">
              <input
                checked={playback.directStream}
                onChange={(event) => setPlayback({ ...playback, directStream: event.target.checked })}
                type="checkbox"
              />
              <span>Range 直出</span>
            </label>
            <label className="settingsToggle">
              <input
                checked={playback.hlsRemux}
                onChange={(event) => setPlayback({ ...playback, hlsRemux: event.target.checked })}
                type="checkbox"
              />
              <span>HLS 转封装</span>
            </label>
            <label className="settingsToggle">
              <input
                checked={playback.transcode}
                onChange={(event) => setPlayback({ ...playback, transcode: event.target.checked })}
                type="checkbox"
              />
              <span>HLS 转码</span>
            </label>
            {!hasPlaybackStrategy ? <strong className="settingsWarning">至少保留一种播放策略</strong> : null}
          </section>

          <section className="settingsSection runtimeCheckSection">
            <div className="settingsSectionHeader">
              <h3>运行时自检</h3>
              <button disabled={isCheckingRuntime} onClick={() => void handleRuntimeSelfCheck()} type="button">
                <RefreshCw size={15} />
                <span>{isCheckingRuntime ? "检查中" : "检查"}</span>
              </button>
            </div>
            {selfCheck ? (
              <div className={`runtimeCheckSummary ${selfCheck.status}`}>
                <strong>{selfCheck.status}</strong>
                <span>{formatDateTime(selfCheck.checkedAt)}</span>
              </div>
            ) : null}
            {selfCheckError ? <p className="runtimeCheckError">{selfCheckError}</p> : null}
            {selfCheck ? (
              <div className="runtimeCheckList">
                {selfCheck.items.map((item) => (
                  <article className={`runtimeCheckItem ${item.status}`} key={item.key}>
                    <span>{item.label}</span>
                    <p>{item.detail}</p>
                  </article>
                ))}
              </div>
            ) : (
              <p className="runtimeCheckHint">检查 FFmpeg、监听端口、缓存目录、SQLite、WebDAV Range 和硬件编码。</p>
            )}
          </section>

          <footer className="settingsFooter">
            <button onClick={onClose} type="button">
              <span>取消</span>
            </button>
            <button disabled={isSaving || !hasPlaybackStrategy} type="submit">
              <span>{isSaving ? "保存中" : "保存"}</span>
            </button>
          </footer>
        </form>
      </aside>
    </div>
  );
}

function CacheMaintenance({
  cacheStatus,
  cacheSettings,
  disabled,
  isSaving,
  onCleanupAssets,
  onCleanupTranscode,
  onCleanupWebDav,
  onSaveSettings,
}: {
  cacheStatus: CacheUsageSummary | null;
  cacheSettings: CacheSettings | null;
  disabled: boolean;
  isSaving: boolean;
  onCleanupAssets: () => void;
  onCleanupTranscode: () => void;
  onCleanupWebDav: () => void;
  onSaveSettings: (
    hlsRetentionHours: number,
    imageCleanupScope: string,
    webDavRetentionHours: number,
    webDavMaxGb: number,
  ) => void;
}) {
  const [hlsRetentionHours, setHlsRetentionHours] = useState(24);
  const [imageCleanupScope, setImageCleanupScope] = useState("orphans-and-untracked");
  const [webDavRetentionHours, setWebDavRetentionHours] = useState(72);
  const [webDavMaxGb, setWebDavMaxGb] = useState(20);

  useEffect(() => {
    if (!cacheSettings) {
      return;
    }

    setHlsRetentionHours(cacheSettings.hlsRetentionHours);
    setImageCleanupScope(cacheSettings.imageCleanupScope);
    setWebDavRetentionHours(cacheSettings.webDavRetentionHours);
    setWebDavMaxGb(cacheSettings.webDavMaxGb);
  }, [
    cacheSettings?.hlsRetentionHours,
    cacheSettings?.imageCleanupScope,
    cacheSettings?.webDavMaxGb,
    cacheSettings?.webDavRetentionHours,
  ]);

  if (!cacheStatus) {
    return null;
  }

  const imageBytes = sumCacheBuckets(cacheStatus, ["posters", "thumbnails"]);
  const transcodeBytes = sumCacheBuckets(cacheStatus, ["transcode"]);
  const webDavBytes = sumCacheBuckets(cacheStatus, ["webdav"]);

  return (
    <section className="cacheMaintenance" aria-label="cache maintenance">
      <div>
        <strong>{formatBytes(cacheStatus.totalBytes)}</strong>
        <span>
          图片 {formatBytes(imageBytes)} · HLS {formatBytes(transcodeBytes)} · WebDAV {formatBytes(webDavBytes)} ·{" "}
          WebDAV 上限 {formatBytes((cacheSettings?.webDavMaxGb ?? 20) * 1024 ** 3)} · {cacheStatus.totalFileCount} 文件
        </span>
      </div>
      <div className="cacheActions">
        <input
          aria-label="hls retention hours"
          min={1}
          max={720}
          onChange={(event) => setHlsRetentionHours(Number(event.target.value))}
          type="number"
          value={hlsRetentionHours}
        />
        <input
          aria-label="webdav retention hours"
          min={1}
          max={720}
          onChange={(event) => setWebDavRetentionHours(Number(event.target.value))}
          type="number"
          value={webDavRetentionHours}
        />
        <input
          aria-label="webdav max gb"
          min={1}
          max={1024}
          onChange={(event) => setWebDavMaxGb(Number(event.target.value))}
          type="number"
          value={webDavMaxGb}
        />
        <select
          aria-label="image cleanup scope"
          onChange={(event) => setImageCleanupScope(event.target.value)}
          value={imageCleanupScope}
        >
          <option value="orphans-and-untracked">孤儿+残留</option>
          <option value="orphans-only">仅孤儿</option>
        </select>
        <button
          disabled={disabled || isSaving || !cacheSettings}
          onClick={() => onSaveSettings(hlsRetentionHours, imageCleanupScope, webDavRetentionHours, webDavMaxGb)}
        >
          <span>{isSaving ? "保存中" : "保存"}</span>
        </button>
        <button disabled={disabled} onClick={onCleanupAssets}>
          <Trash2 size={15} />
          <span>图片</span>
        </button>
        <button disabled={disabled} onClick={onCleanupTranscode}>
          <Trash2 size={15} />
          <span>HLS</span>
        </button>
        <button disabled={disabled} onClick={onCleanupWebDav}>
          <Trash2 size={15} />
          <span>WebDAV</span>
        </button>
      </div>
    </section>
  );
}

function sumCacheBuckets(cacheStatus: CacheUsageSummary, keys: string[]): number {
  return cacheStatus.buckets
    .filter((bucket) => keys.includes(bucket.key))
    .reduce((total, bucket) => total + bucket.bytes, 0);
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }

  return `${value >= 10 ? value.toFixed(1) : value.toFixed(2)} ${units[unitIndex]}`;
}

function TaskCenter({
  snapshot,
  onCancel,
}: {
  snapshot: BackgroundTaskSnapshot | null;
  onCancel: (taskId: string) => void;
}) {
  const tasks = snapshot?.tasks.slice(0, 4) ?? [];
  if (tasks.length === 0) {
    return null;
  }

  return (
    <section className="taskCenter" aria-label="task center">
      {tasks.map((task) => (
        <div className="taskRow" key={task.id}>
          <div>
            <strong>{task.title}</strong>
            <span>{formatBackgroundTask(task)}</span>
            {task.progressPercent !== null && task.isRunning ? (
              <progress max={100} value={Math.round(task.progressPercent)} />
            ) : null}
          </div>
          {task.canCancel ? (
            <button aria-label={`cancel ${task.title}`} onClick={() => onCancel(task.id)}>
              <CircleStop size={16} />
            </button>
          ) : null}
        </div>
      ))}
    </section>
  );
}

function formatBackgroundTask(task: BackgroundTaskSnapshot["tasks"][number]): string {
  if (task.isCancellationRequested) {
    return "正在取消";
  }

  if (task.isRunning) {
    return task.progressText ?? "正在执行";
  }

  if (task.state === "completed") {
    return task.resultText ?? "已完成";
  }

  if (task.state === "canceled") {
    return "已取消";
  }

  if (task.state === "failed") {
    return task.errorMessage ?? "执行失败";
  }

  return task.progressText ?? task.state;
}

function PosterCard({
  item,
  compact = false,
  onOpen,
}: {
  item: LibraryItemSummary;
  compact?: boolean;
  onOpen?: (item: LibraryItemSummary) => void;
}) {
  const year = item.releaseDate?.slice(0, 4) ?? "";
  const badge = item.voteAverage ? item.voteAverage.toFixed(1) : item.itemKind === "tv" ? "TV" : "MOV";

  return (
    <article className={compact ? "posterCard compact" : "posterCard"} onClick={() => onOpen?.(item)}>
      <div className="posterArt">
        {item.posterAssetId ? <img alt="" src={posterUrl(item.posterAssetId)} /> : null}
        <span>{badge}</span>
      </div>
      <h3>{item.title}</h3>
      <p>{[year, `${item.videoFileCount} 个视频`].filter(Boolean).join(" · ")}</p>
    </article>
  );
}

function DetailView({
  detail,
  errorText,
  isRescraping,
  isRefreshing,
  metadataCandidates,
  onBack,
  onApplyMetadataCandidate,
  onCancelTask,
  onSearchMetadata,
  onPlay,
  onRescrape,
  onToggleMetadataLock,
  onToggleWatched,
  statusText,
  taskSnapshot,
}: {
  detail: LibraryItemDetail;
  errorText: string;
  isRescraping: boolean;
  isRefreshing: boolean;
  metadataCandidates: TmdbMetadataMatch[];
  onBack: () => void;
  onApplyMetadataCandidate: (match: TmdbMetadataMatch) => void;
  onCancelTask: (taskId: string) => void;
  onSearchMetadata: () => void;
  onPlay: (file: VideoFileSummary) => void;
  onRescrape: () => void;
  onToggleMetadataLock: () => void;
  onToggleWatched: (file: VideoFileSummary) => void;
  statusText: string;
  taskSnapshot: BackgroundTaskSnapshot | null;
}) {
  const poster = detail.posterAssetId ? posterUrl(detail.posterAssetId) : null;
  const year = detail.releaseDate?.slice(0, 4);
  const mainFile = detail.videoFiles[0] ?? null;

  return (
    <main className="detailShell">
      {poster ? <img alt="" className="detailBackdrop" src={poster} /> : null}
      <header className="detailTopbar">
        <button aria-label="back" onClick={onBack}>
          <ArrowLeft size={19} />
        </button>
        <span>{isRefreshing ? "刷新中" : "详情"}</span>
        <button aria-label="manual match" disabled={isRefreshing} onClick={onSearchMetadata} title="手动匹配">
          <Search size={17} />
        </button>
        <button aria-label="rescrape" disabled={isRefreshing || isRescraping} onClick={onRescrape} title="重刮削">
          <Sparkles size={17} className={isRescraping ? "pulse" : ""} />
        </button>
        <button
          aria-label="toggle metadata lock"
          disabled={isRefreshing}
          onClick={onToggleMetadataLock}
          title={detail.isLocked ? "解锁元数据" : "锁定元数据"}
        >
          {detail.isLocked ? <Lock size={17} /> : <Unlock size={17} />}
        </button>
      </header>

      <section className="detailHero">
        <div className="detailPoster">
          {poster ? <img alt="" src={poster} /> : <span>{detail.itemKind === "tv" ? "TV" : "MOV"}</span>}
        </div>
        <div className="detailMeta">
          <h1>{detail.title}</h1>
          <div className="detailFacts">
            {year ? <span>{year}</span> : null}
            {detail.voteAverage ? <span>{detail.voteAverage.toFixed(1)}</span> : null}
            {detail.isLocked ? <span>已锁定</span> : null}
            {detail.tmdbId ? <span>TMDB {detail.tmdbId}</span> : null}
            <span>{detail.videoFileCount} 个视频</span>
          </div>
          <p>{detail.overview || "暂无简介"}</p>
          {mainFile ? (
            <div className="detailActions">
              <button aria-label="play" onClick={() => onPlay(mainFile)}>
                <Play size={18} />
              </button>
              <button onClick={() => onToggleWatched(mainFile)}>{mainFile.isWatched ? "已播" : "未播"}</button>
            </div>
          ) : null}
        </div>
      </section>

      {statusText || errorText || taskSnapshot?.tasks.length ? (
        <section className="detailTasks">
          {statusText ? <span>{statusText}</span> : null}
          {errorText ? <strong>{errorText}</strong> : null}
          <TaskCenter snapshot={taskSnapshot} onCancel={onCancelTask} />
        </section>
      ) : null}

      {metadataCandidates.length > 0 ? (
        <section className="metadataCandidates">
          {metadataCandidates.map((candidate) => (
            <article className="metadataCandidate" key={`${candidate.mediaType}-${candidate.id}`}>
              <div className="metadataPoster">
                {tmdbPosterUrl(candidate.posterPath) ? <img alt="" src={tmdbPosterUrl(candidate.posterPath)!} /> : <span>TMDB</span>}
              </div>
              <div>
                <h3>{candidate.title}</h3>
                <p>
                  {[candidate.releaseDate?.slice(0, 4), candidate.voteAverage ? candidate.voteAverage.toFixed(1) : null]
                    .filter(Boolean)
                    .join(" · ") || "TMDB"}
                </p>
                <span>{candidate.overview || "暂无简介"}</span>
              </div>
              <button onClick={() => onApplyMetadataCandidate(candidate)}>应用</button>
            </article>
          ))}
        </section>
      ) : null}

      {detail.itemKind === "tv" && detail.seasons.length > 0 ? (
        <section className="episodeSection">
          {detail.seasons.map((season) => (
            <div className="seasonBlock" key={season.id}>
              <div className="seasonHeader">
                {season.posterAssetId ? <img alt="" src={posterUrl(season.posterAssetId)} /> : null}
                <h2>{season.title ?? (season.seasonNumber === 0 ? "特别篇" : `第 ${season.seasonNumber} 季`)}</h2>
              </div>
              <div className="episodeGrid">
                {season.episodes.map((episode) => (
                  <EpisodeCard
                    episode={episode}
                    key={episode.id}
                    onPlay={onPlay}
                    onToggleWatched={onToggleWatched}
                  />
                ))}
              </div>
            </div>
          ))}
        </section>
      ) : (
        <section className="fileSection">
          <h2>视频文件</h2>
          {detail.videoFiles.map((file) => (
            <FileRow file={file} key={file.id} onPlay={onPlay} onToggleWatched={onToggleWatched} />
          ))}
        </section>
      )}
    </main>
  );
}

function EpisodeCard({
  episode,
  onPlay,
  onToggleWatched,
}: {
  episode: EpisodeDetail;
  onPlay: (file: VideoFileSummary) => void;
  onToggleWatched: (file: VideoFileSummary) => void;
}) {
  const file = episode.videoFile;
  const facts = [episode.airDate, file?.fileName].filter(Boolean).join(" · ");
  return (
    <article className="episodeCard">
      {episode.stillAssetId ? <img alt="" className="episodeStill" src={thumbnailUrl(episode.stillAssetId)} /> : null}
      <div>
        <h3>{episode.title ?? `第 ${episode.episodeNumber} 集`}</h3>
        <p>{facts || "暂无文件"}</p>
        {episode.overview ? <span>{episode.overview}</span> : null}
      </div>
      {file ? (
        <div className="rowActions">
          <button aria-label="play episode" onClick={() => onPlay(file)}>
            <Play size={16} />
          </button>
          <button onClick={() => onToggleWatched(file)}>{file.isWatched ? "已播" : "未播"}</button>
        </div>
      ) : null}
    </article>
  );
}

function FileRow({
  file,
  onPlay,
  onToggleWatched,
}: {
  file: VideoFileSummary;
  onPlay: (file: VideoFileSummary) => void;
  onToggleWatched: (file: VideoFileSummary) => void;
}) {
  return (
    <div className="fileRow">
      <div>
        <strong>{file.fileName}</strong>
        <span>{file.relativePath}</span>
        <span>{formatMediaProbe(file)}</span>
      </div>
      <div className="rowActions">
        <button aria-label="play file" onClick={() => onPlay(file)}>
          <Play size={16} />
        </button>
        <button onClick={() => onToggleWatched(file)}>{file.isWatched ? "已播" : "未播"}</button>
      </div>
    </div>
  );
}

function formatMediaProbe(file: VideoFileSummary): string {
  return [
    file.container,
    file.videoCodec ? `V:${file.videoCodec}` : null,
    file.audioCodec ? `A:${file.audioCodec}` : null,
    file.audioTracks.length > 0 ? `${file.audioTracks.length} 音轨` : null,
    file.subtitleStreams.length > 0 ? `${file.subtitleStreams.length} 内嵌字幕` : null,
    file.subtitleSummary ? `S:${file.subtitleSummary}` : null,
  ]
    .filter(Boolean)
    .join(" · ") || "未探测";
}

function formatAudioTrack(track: VideoFileSummary["audioTracks"][number]): string {
  return [
    `音轨 ${track.index + 1}`,
    track.language,
    track.title,
    track.codec,
    track.channelLayout ?? (track.channels ? `${track.channels}ch` : null),
    track.isDefault ? "默认" : null,
  ]
    .filter(Boolean)
    .join(" · ");
}

function formatSubtitleStream(stream: VideoFileSummary["subtitleStreams"][number]): string {
  return [
    `字幕 ${stream.index + 1}`,
    stream.language,
    stream.title,
    stream.codec,
    stream.isForced ? "强制" : null,
    stream.isDefault ? "默认" : null,
  ]
    .filter(Boolean)
    .join(" · ");
}

function formatPlaybackCacheStatus(status: PlaybackCacheStatus): string {
  if (!status.isRemote) {
    return "正在准备播放";
  }

  const percent = status.percent === null ? null : `${Math.floor(status.percent)}%`;
  const bytes =
    status.totalBytes && status.totalBytes > 0
      ? `${formatBytes(status.downloadedBytes)} / ${formatBytes(status.totalBytes)}`
      : formatBytes(status.downloadedBytes);
  return percent ? `正在缓存 WebDAV ${percent} · ${bytes}` : `正在缓存 WebDAV · ${bytes}`;
}

function PlayerView({
  detail,
  file,
  onBack,
}: {
  detail: LibraryItemDetail;
  file: VideoFileSummary;
  onBack: () => void;
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const lastSavedAtRef = useRef(0);
  const sessionIdRef = useRef<string | null>(null);
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [playbackMode, setPlaybackMode] = useState<"direct" | "hls" | null>(null);
  const [playerStatus, setPlayerStatus] = useState("正在准备播放");
  const [playerError, setPlayerError] = useState("");
  const [quality, setQuality] = useState("original");
  const [audioTrack, setAudioTrack] = useState("auto");
  const [subtitleMode, setSubtitleMode] = useState("off");
  const [selectedSubtitleId, setSelectedSubtitleId] = useState("");
  const [useHardware, setUseHardware] = useState(false);
  const [capabilities, setCapabilities] = useState<FfmpegTranscodeCapabilities | null>(null);
  const [subtitles, setSubtitles] = useState<PlaybackSubtitleTrack[]>([]);
  const [cacheStatus, setCacheStatus] = useState<PlaybackCacheStatus | null>(null);
  const [diagnostics, setDiagnostics] = useState<PlaybackDiagnostics | null>(null);
  const [diagnosticsError, setDiagnosticsError] = useState("");
  const [isDiagnosticsOpen, setIsDiagnosticsOpen] = useState(false);
  const [cleanupText, setCleanupText] = useState("");
  const title = file.episodeTitle ?? detail.title;
  const subTitle = [formatEpisode(file), file.fileName].filter(Boolean).join(" · ");
  const selectedSubtitle = subtitles.find((subtitle) => subtitle.id === selectedSubtitleId) ?? null;
  const selectedEmbeddedSubtitle = selectedSubtitleId.startsWith("embedded_")
    ? file.subtitleStreams.find((stream) => `embedded_${stream.index}` === selectedSubtitleId) ?? null
    : null;
  const requiresFullCacheForPlayback =
    quality !== "original" ||
    audioTrack !== "auto" ||
    (!!selectedSubtitleId && subtitleMode === "burn");
  const isPlaybackCachePlayable =
    cacheStatus?.isReady || (!!cacheStatus?.canStreamDirect && !requiresFullCacheForPlayback) || false;

  const saveProgress = useCallback(
    async (force = false) => {
      const video = videoRef.current;
      if (!video) {
        return;
      }

      const positionSeconds = Number.isFinite(video.currentTime) ? video.currentTime : file.positionSeconds;
      const durationSeconds =
        Number.isFinite(video.duration) && video.duration > 0 ? video.duration : file.durationSeconds;
      if (!force && Math.abs(positionSeconds - lastSavedAtRef.current) < 10) {
        return;
      }

      lastSavedAtRef.current = positionSeconds;
      try {
        await updatePlaybackProgress(file.id, positionSeconds, durationSeconds);
      } catch {
        // Playback must keep running even if a transient progress save fails.
      }
    },
    [file.id, file.durationSeconds, file.positionSeconds],
  );

  useEffect(() => {
    lastSavedAtRef.current = file.positionSeconds;
  }, [file.id, file.positionSeconds]);

  useEffect(() => {
    if (!isPlaybackCachePlayable) {
      return;
    }

    let isCancelled = false;
    void getPlaybackCapabilities()
      .then((nextCapabilities) => {
        if (isCancelled) {
          return;
        }

        setCapabilities(nextCapabilities);
      })
      .catch((error: unknown) => {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      });

    return () => {
      isCancelled = true;
    };
  }, [file.id, isPlaybackCachePlayable]);

  useEffect(() => {
    let isCancelled = false;
    let pollHandle = 0;
    const canUseCacheStatus = (status: PlaybackCacheStatus) =>
      status.isReady || (status.canStreamDirect && !requiresFullCacheForPlayback);

    async function pollCache(start: boolean) {
      try {
        const nextStatus = start ? await getPlaybackCacheStatus(file.id) : await getPlaybackCacheStatus(file.id);
        if (isCancelled) {
          return;
        }

        setCacheStatus(nextStatus);
        if (nextStatus.errorMessage) {
          setPlayerError(nextStatus.errorMessage);
          setPlayerStatus("");
          return;
        }

        if (canUseCacheStatus(nextStatus)) {
          setPlayerStatus("正在准备播放");
          return;
        }

        const preparedStatus = start ? await preparePlaybackCache(file.id) : nextStatus;
        if (isCancelled) {
          return;
        }

        setCacheStatus(preparedStatus);
        if (preparedStatus.errorMessage) {
          setPlayerError(preparedStatus.errorMessage);
          setPlayerStatus("");
          return;
        }

        if (canUseCacheStatus(preparedStatus)) {
          setPlayerStatus("正在准备播放");
          return;
        }

        setPlayerStatus(formatPlaybackCacheStatus(preparedStatus));
        pollHandle = window.setTimeout(() => void pollCache(false), 800);
      } catch (error) {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
          setPlayerStatus("");
        }
      }
    }

    setCacheStatus(null);
    setPlaybackMode(null);
    setPlaybackUrl(null);
    setPlayerStatus("正在准备缓存");
    setPlayerError("");
    void pollCache(true);

    return () => {
      isCancelled = true;
      window.clearTimeout(pollHandle);
    };
  }, [file.id, requiresFullCacheForPlayback]);

  useEffect(() => {
    if (!isPlaybackCachePlayable) {
      return;
    }

    let isCancelled = false;
    void getPlaybackSubtitles(file.id)
      .then((nextSubtitles) => {
        if (isCancelled) {
          return;
        }

        setSubtitles(nextSubtitles);
        setSelectedSubtitleId((current) =>
          current &&
          (nextSubtitles.some((subtitle) => subtitle.id === current) ||
            file.subtitleStreams.some((stream) => `embedded_${stream.index}` === current))
            ? current
            : "",
        );
      })
      .catch((error: unknown) => {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      });

    return () => {
      isCancelled = true;
    };
  }, [file.id, file.subtitleStreams, isPlaybackCachePlayable]);

  useEffect(() => {
    if (!isPlaybackCachePlayable) {
      return;
    }

    let isCancelled = false;
    let retryHandle = 0;
    let activeSessionId: string | null = null;

    async function resolvePlayback(attempt: number) {
      try {
        const effectiveSubtitleMode = selectedSubtitleId ? subtitleMode : "off";
        const decision = await getPlaybackDecision(file.id, {
          quality,
          audioTrackIndex: audioTrack === "auto" ? null : Number(audioTrack),
          subtitleMode: selectedEmbeddedSubtitle && effectiveSubtitleMode === "web" ? "burn" : effectiveSubtitleMode,
          subtitleId: selectedSubtitleId || null,
          hardware: useHardware,
        });
        if (isCancelled) {
          return;
        }

        sessionIdRef.current = decision.sessionId;
        activeSessionId = decision.sessionId;
        if (decision.mode === "direct" && decision.streamUrl) {
          setPlaybackMode("direct");
          setPlaybackUrl(decision.streamUrl);
          setPlayerStatus("");
          setPlayerError("");
          return;
        }

        if (decision.mode.startsWith("hls") && decision.manifestUrl) {
          if (decision.isReady || attempt >= 10) {
            setPlaybackMode("hls");
            setPlaybackUrl(decision.manifestUrl);
            setPlayerStatus("");
            setPlayerError("");
            return;
          }

          setPlayerStatus(decision.reason ?? "正在准备 HLS");
          retryHandle = window.setTimeout(() => void resolvePlayback(attempt + 1), 1200);
          return;
        }

        setPlayerError(decision.reason ?? "当前文件暂时无法播放");
      } catch (error) {
        if (!isCancelled) {
          setPlayerError(error instanceof Error ? error.message : String(error));
        }
      }
    }

    setPlaybackMode(null);
    setPlaybackUrl(null);
    setPlayerStatus("正在准备播放");
    setPlayerError("");
    void resolvePlayback(0);

    return () => {
      isCancelled = true;
      window.clearTimeout(retryHandle);
      if (activeSessionId) {
        void stopHlsSession(activeSessionId).catch(() => undefined);
      }
    };
  }, [
    audioTrack,
    file.id,
    file.subtitleStreams,
    isPlaybackCachePlayable,
    quality,
    selectedEmbeddedSubtitle,
    selectedSubtitleId,
    subtitleMode,
    useHardware,
  ]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !playbackUrl || !playbackMode) {
      return;
    }

    let hls: Hls | null = null;
    if (playbackMode === "hls" && Hls.isSupported()) {
      hls = new Hls({ enableWorker: true });
      hls.loadSource(playbackUrl);
      hls.attachMedia(video);
    } else if (
      playbackMode === "hls" &&
      !video.canPlayType("application/vnd.apple.mpegurl")
    ) {
      setPlayerError("当前浏览器不支持 HLS 播放。");
      return;
    } else {
      video.src = playbackUrl;
    }

    return () => {
      hls?.destroy();
      video.removeAttribute("src");
      video.load();
    };
  }, [playbackMode, playbackUrl]);

  function handleLoadedMetadata() {
    const video = videoRef.current;
    if (!video || file.positionSeconds <= 0) {
      return;
    }

    const duration = Number.isFinite(video.duration) ? video.duration : file.durationSeconds;
    if (duration <= 0 || file.positionSeconds < duration - 8) {
      video.currentTime = file.positionSeconds;
    }
  }

  async function handleBack() {
    await saveProgress(true);
    if (sessionIdRef.current) {
      await stopHlsSession(sessionIdRef.current).catch(() => undefined);
    }
    if (cacheStatus?.canCancel) {
      await cancelPlaybackCache(file.id).catch(() => undefined);
    }
    onBack();
  }

  async function handleCancelCache() {
    try {
      const nextStatus = await cancelPlaybackCache(file.id);
      setCacheStatus(nextStatus);
      setPlayerStatus("");
      setPlayerError(nextStatus.errorMessage ?? "缓存已取消。");
    } catch (error) {
      setPlayerError(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCleanupCache() {
    const task = await cleanupHlsCache();
    setCleanupText(task.isRunning ? "缓存清理已提交" : (task.resultText ?? "缓存清理已提交"));
  }

  async function handleLoadDiagnostics() {
    const nextOpen = !isDiagnosticsOpen;
    setIsDiagnosticsOpen(nextOpen);
    if (!nextOpen) {
      return;
    }

    setDiagnosticsError("");
    setDiagnostics(null);
    try {
      const effectiveSubtitleMode = selectedSubtitleId ? subtitleMode : "off";
      const nextDiagnostics = await getPlaybackDiagnostics(file.id, {
        quality,
        audioTrackIndex: audioTrack === "auto" ? null : Number(audioTrack),
        subtitleMode: selectedEmbeddedSubtitle && effectiveSubtitleMode === "web" ? "burn" : effectiveSubtitleMode,
        subtitleId: selectedSubtitleId || null,
        hardware: useHardware,
      });
      setDiagnostics(nextDiagnostics);
    } catch (error) {
      setDiagnosticsError(error instanceof Error ? error.message : String(error));
    }
  }

  return (
    <main className="playerShell">
      <header className="playerTopbar">
        <button aria-label="back" onClick={() => void handleBack()}>
          <ArrowLeft size={19} />
        </button>
        <div>
          <h1>{title}</h1>
          <p>{subTitle}</p>
        </div>
        <div className="playerControls">
          <select aria-label="quality" onChange={(event) => setQuality(event.target.value)} value={quality}>
            <option value="original">原始</option>
            <option value="1080p">1080p</option>
            <option value="720p">720p</option>
            <option value="480p">480p</option>
            <option value="360p">360p</option>
          </select>
          <select aria-label="audio track" onChange={(event) => setAudioTrack(event.target.value)} value={audioTrack}>
            <option value="auto">默认音轨</option>
            {file.audioTracks.length > 0 ? (
              file.audioTracks.map((track) => (
                <option key={track.index} value={String(track.index)}>
                  {formatAudioTrack(track)}
                </option>
              ))
            ) : (
              <>
                <option value="0">音轨 1</option>
                <option value="1">音轨 2</option>
                <option value="2">音轨 3</option>
                <option value="3">音轨 4</option>
              </>
            )}
          </select>
          <select
            aria-label="subtitle"
            onChange={(event) => {
              setSelectedSubtitleId(event.target.value);
              if (event.target.value.startsWith("embedded_")) {
                setSubtitleMode("burn");
              }
            }}
            value={selectedSubtitleId}
          >
            <option value="">无字幕</option>
            {file.subtitleStreams.length > 0 ? (
              <optgroup label="内嵌字幕">
                {file.subtitleStreams.map((stream) => (
                  <option key={stream.index} value={`embedded_${stream.index}`}>
                    {formatSubtitleStream(stream)}
                  </option>
                ))}
              </optgroup>
            ) : null}
            {subtitles.map((subtitle) => (
              <option key={subtitle.id} value={subtitle.id}>
                {subtitle.language ?? subtitle.fileName}
              </option>
            ))}
          </select>
          <select
            aria-label="subtitle mode"
            disabled={!selectedSubtitleId}
            onChange={(event) => setSubtitleMode(event.target.value)}
            value={subtitleMode}
          >
            <option value="off">字幕关</option>
            <option disabled={!!selectedEmbeddedSubtitle} value="web">外挂</option>
            <option value="burn">烧录</option>
          </select>
          <label className="playerToggle">
            <input
              checked={useHardware}
              disabled={!capabilities?.preferredHardwareEncoder}
              onChange={(event) => setUseHardware(event.target.checked)}
              type="checkbox"
            />
            硬件
          </label>
          <button aria-label="cleanup cache" onClick={() => void handleCleanupCache()}>
            清理
          </button>
          <button aria-label="playback diagnostics" onClick={() => void handleLoadDiagnostics()}>
            诊断
          </button>
        </div>
      </header>
      {cleanupText ? <div className="playerNotice">{cleanupText}</div> : null}
      {isDiagnosticsOpen ? (
        <section className="playbackDiagnostics" aria-label="playback diagnostics">
          {diagnosticsError ? <p className="diagnosticsError">{diagnosticsError}</p> : null}
          {diagnostics ? (
            <>
              <div className="diagnosticsSummary">
                <strong>{diagnostics.effectiveMode}</strong>
                <span>{diagnostics.reason}</span>
              </div>
              <div className="diagnosticsFlags">
                <span>{diagnostics.sourceKind}</span>
                <span>{diagnostics.usesWebDavRangeProxy ? "Range 代理" : diagnostics.usesHls ? "HLS" : "直出"}</span>
                <span>{diagnostics.requiresFullCache ? "完整缓存" : "无需完整缓存"}</span>
                {diagnostics.usesTranscode ? <span>转码</span> : null}
                {diagnostics.burnsSubtitle ? <span>字幕烧录</span> : null}
                {diagnostics.capabilities?.preferredHardwareEncoder ? <span>{diagnostics.capabilities.preferredHardwareEncoder}</span> : null}
              </div>
              <div className="diagnosticsSteps">
                {diagnostics.steps.map((step) => (
                  <article className={`diagnosticStep ${step.status}`} key={step.key}>
                    <span>{step.label}</span>
                    <p>{step.detail}</p>
                  </article>
                ))}
              </div>
              {diagnostics.ffmpegCommandPreview ? (
                <pre className="diagnosticsCommand">{diagnostics.ffmpegCommandPreview}</pre>
              ) : null}
            </>
          ) : diagnosticsError ? null : (
            <p className="diagnosticsLoading">正在生成诊断</p>
          )}
        </section>
      ) : null}

      <section className="playerStage">
        {playerStatus ? (
          <div className="playerOverlay">
            <span>{playerStatus}</span>
            {cacheStatus?.canCancel ? (
              <button onClick={() => void handleCancelCache()} type="button">
                取消
              </button>
            ) : null}
          </div>
        ) : null}
        {playerError ? <div className="playerOverlay error">{playerError}</div> : null}
        <video
          autoPlay
          controls
          onEnded={() => void saveProgress(true)}
          onLoadedMetadata={handleLoadedMetadata}
          onPause={() => void saveProgress(true)}
          onTimeUpdate={() => void saveProgress(false)}
          ref={videoRef}
        >
          {subtitleMode === "web" && selectedSubtitle?.webVttUrl ? (
            <track default kind="subtitles" label={selectedSubtitle.language ?? selectedSubtitle.fileName} src={selectedSubtitle.webVttUrl} />
          ) : null}
        </video>
      </section>
    </main>
  );
}

function formatEpisode(file: VideoFileSummary): string | null {
  if (file.seasonNumber === null || file.episodeNumber === null) {
    return null;
  }

  return `S${String(file.seasonNumber).padStart(2, "0")}E${String(file.episodeNumber).padStart(2, "0")}`;
}
