import { type FormEvent, type KeyboardEvent, type MouseEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import Hls from "hls.js";
import {
  ArrowLeft,
  Bug,
  CheckCircle2,
  Circle,
  CircleStop,
  FolderOpen,
  FolderPlus,
  LogOut,
  Captions,
  Music2,
  Pause,
  Pencil,
  Play,
  Save,
  Search,
  Settings,
  SlidersHorizontal,
  SkipBack,
  SkipForward,
  Star,
  RefreshCw,
  Trash2,
  X,
} from "lucide-react";
import {
  addLocalMediaSource,
  applyLibraryItemMetadata,
  AppSettingsSnapshot,
  AuthStatus,
  BackgroundTaskSnapshot,
  BackgroundTaskStatus,
  browseLocalDirectories,
  CacheUsageSummary,
  CacheSettings,
  cancelLibraryScan,
  cancelMetadataEnrichment,
  cancelPlaybackCache,
  cleanupAssetCache,
  cleanupHlsCache,
  EpisodeDetail,
  FfmpegTranscodeCapabilities,
  getBackgroundTasks,
  getAppSettings,
  getAuthStatus,
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
  LibraryItemCustomMetadataUpdateRequest,
  LibraryItemSummary,
  LibraryMetadataEnrichmentStatus,
  LibraryScanStatus,
  login,
  LocalDirectoryBrowseResult,
  logout,
  MediaSourceSummary,
  PlaybackCacheStatus,
  PlaybackDiagnostics,
  PlaybackSubtitleTrack,
  ProxyConnectionTestResult,
  ProxySettings,
  RuntimeSelfCheckSnapshot,
  posterUrl,
  preparePlaybackCache,
  registerAdmin,
  removeMediaSource,
  searchLibraryItemMetadata,
  setLibraryItemWatchedStatus,
  scanLibrary,
  stopHlsSession,
  TmdbMetadataMatch,
  thumbnailUrl,
  tmdbPosterUrl,
  updateAppSettings,
  updateMediaSource,
  updatePlaybackProgress,
  testTmdbConnection,
  testProxyConnection,
  TmdbConnectionTestResult,
  updateLibraryItemCustomMetadata,
  VideoFileSummary,
} from "../shared/api/client";

type MetadataSearchState = {
  item: LibraryItemSummary;
  detail: LibraryItemDetail | null;
  query: string;
  year: string;
  candidates: TmdbMetadataMatch[];
  isLoadingDetail: boolean;
  isSearching: boolean;
  isApplying: boolean;
  error: string;
};

type CustomMetadataEditState = {
  detail: LibraryItemDetail;
  isSaving: boolean;
  error: string;
};

export function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [isAuthLoading, setIsAuthLoading] = useState(true);
  const [authError, setAuthError] = useState("");
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
  const [isSearchOpen, setIsSearchOpen] = useState(false);
  const [isSortOpen, setIsSortOpen] = useState(false);
  const [sortKey, setSortKey] = useState<"title" | "rating" | "year">("year");
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("desc");
  const [isLoading, setIsLoading] = useState(true);
  const [isScanning, setIsScanning] = useState(false);
  const [scanStatus, setScanStatus] = useState<LibraryScanStatus | null>(null);
  const [isScraping, setIsScraping] = useState(false);
  const [scrapeStatus, setScrapeStatus] = useState<LibraryMetadataEnrichmentStatus | null>(null);
  const [selectedDetail, setSelectedDetail] = useState<LibraryItemDetail | null>(null);
  const [metadataSearch, setMetadataSearch] = useState<MetadataSearchState | null>(null);
  const [customMetadataEdit, setCustomMetadataEdit] = useState<CustomMetadataEditState | null>(null);
  const [playingFile, setPlayingFile] = useState<VideoFileSummary | null>(null);
  const [isDetailLoading, setIsDetailLoading] = useState(false);
  const [statusText, setStatusText] = useState("");
  const [errorText, setErrorText] = useState("");
  const scanWasRunningRef = useRef(false);
  const scrapeWasRunningRef = useRef(false);
  const scrapeLoadedUpdatedItemsRef = useRef(0);
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
      if (nextStatus.isRunning && nextStatusText) {
        setStatusText(nextStatusText);
      } else if (!nextStatus.isRunning && !nextStatus.wasCanceled) {
        setStatusText("");
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
      if (nextStatus.isRunning && nextStatusText) {
        setStatusText(nextStatusText);
      } else if (!nextStatus.isRunning && !nextStatus.wasCanceled) {
        setStatusText("");
      }

      const updatedItems = nextStatus.progress?.updatedItemCount ?? 0;
      if (nextStatus.isRunning && updatedItems > scrapeLoadedUpdatedItemsRef.current) {
        scrapeLoadedUpdatedItemsRef.current = updatedItems;
        void loadData().catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)));
      }

      if (scrapeWasRunningRef.current && !nextStatus.isRunning && !nextStatus.wasCanceled) {
        scrapeLoadedUpdatedItemsRef.current = 0;
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

      if (!nextStatus.isRunning) {
        scrapeLoadedUpdatedItemsRef.current = 0;
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
    void getAuthStatus()
      .then(setAuthStatus)
      .catch((error: unknown) => setAuthError(error instanceof Error ? error.message : String(error)))
      .finally(() => setIsAuthLoading(false));
  }, []);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      setIsLoading(false);
      return;
    }

    setIsLoading(true);
    void loadData()
      .catch((error: unknown) => setErrorText(error instanceof Error ? error.message : String(error)))
      .finally(() => setIsLoading(false));
  }, [authStatus?.isAuthenticated, loadData]);

  useEffect(() => {
    selectedDetailRef.current = selectedDetail;
  }, [selectedDetail]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

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
    pollHandle = window.setInterval(() => void pollStatus(), 1500);
    if (typeof EventSource === "undefined") {
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
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScanStatus, authStatus?.isAuthenticated]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

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
    pollHandle = window.setInterval(() => void pollStatus(), 1500);
    if (typeof EventSource === "undefined") {
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
    };

    return () => {
      disposed = true;
      events.close();
      if (pollHandle !== 0) {
        window.clearInterval(pollHandle);
      }
    };
  }, [applyScrapeStatus, authStatus?.isAuthenticated]);

  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return;
    }

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
	  }, [authStatus?.isAuthenticated]);

  const displayedItems = useMemo(() => {
    const normalizedSearch = searchText.trim().toLowerCase();
    return items
      .filter((item) => item.title.toLowerCase().includes(normalizedSearch))
      .sort((a, b) => compareLibraryItems(a, b, sortKey, sortDirection));
  }, [items, searchText, sortDirection, sortKey]);

  const continueItems = useMemo(() => {
    return items
      .filter((item) => !item.isWatched && item.maxProgressSeconds > 0 && item.maxDurationSeconds > 0)
      .slice(0, 12);
  }, [items]);
  const scanPercent = scanProgressPercent(scanStatus);
  const scrapePercent = scrapeProgressPercent(scrapeStatus);
  const topStatusText =
    errorText ||
    (scrapeStatus?.isRunning ? formatScrapeStatus(scrapeStatus) : "") ||
    (scanStatus?.isRunning ? formatScanStatus(scanStatus) : "") ||
    statusText;

  async function handleAuthenticate(username: string, password: string) {
    setAuthError("");
    const nextStatus = authStatus?.isSetupRequired
      ? await registerAdmin(username, password)
      : await login(username, password);
    setAuthStatus(nextStatus);
  }

  async function handleLogout() {
    setErrorText("");
    setStatusText("");
    await logout();
    setAuthStatus(await getAuthStatus());
    setItems([]);
    setSources([]);
    setTaskSnapshot(null);
    setCacheStatus(null);
    setSelectedDetail(null);
    setPlayingFile(null);
  }

  async function handleCreateSources(paths: string[]): Promise<boolean> {
    const normalizedPaths = Array.from(new Set(paths.map((path) => path.trim()).filter(Boolean)));
    if (normalizedPaths.length === 0) {
      return false;
    }

    setIsSavingSource(true);
    setErrorText("");
    setStatusText(normalizedPaths.length === 1 ? "正在添加媒体源" : `正在添加 ${normalizedPaths.length} 个媒体源`);
    try {
      for (const path of normalizedPaths) {
        await addLocalMediaSource(path);
      }
      setSources(await getMediaSources());
      if (isScanning || isScraping) {
        setStatusText("媒体源已添加，当前扫描/刮削结束后可再次扫描");
        return true;
      }

      setStatusText("媒体源已添加，正在自动扫描并刮削");
      applyScanStatus(await scanLibrary(buildRefreshRequest()));
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
      const nextItems = await getLibraryItems();
      setSources((current) => current.map((source) => (source.id === updated.id ? updated : source)));
      setItems(nextItems);
      setSelectedDetail((current) => (current && nextItems.some((item) => item.id === current.id) ? current : null));
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
      await loadData();
      setStatusText(task.isRunning ? "媒体源已移除，正在清理索引" : "媒体源已移除");
      return true;
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setIsSavingSource(false);
    }
  }

  function buildRefreshRequest() {
    return { sortKey, sortDirection };
  }

  async function handleRefreshLibrary() {
    setIsScanning(true);
    setErrorText("");
    setStatusText("正在扫描并刮削");
    try {
      applyScanStatus(await scanLibrary(buildRefreshRequest()));
      setStatusText("扫描并刮削已提交");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
      setIsScanning(false);
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

  async function handleCancelScrape() {
    setErrorText("");
    setStatusText("正在取消刮削");
    try {
      applyScrapeStatus(await cancelMetadataEnrichment());
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleCancelRefresh() {
    if (isScanning) {
      await handleCancelScan();
      return;
    }

    if (isScraping) {
      await handleCancelScrape();
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
    proxy: ProxySettings,
  ) {
    setErrorText("");
    setStatusText("正在保存设置");
    setIsSavingSettings(true);
    try {
      const nextSettings = await updateAppSettings({ tmdb, cache, playback, proxy });
      setSettings(nextSettings);
      setStatusText("设置已保存");
      setIsSettingsOpen(false);
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSavingSettings(false);
    }
  }

  function syncOpenDetail(detail: LibraryItemDetail) {
    if (selectedDetailRef.current?.id === detail.id) {
      setSelectedDetail(detail);
      selectedDetailRef.current = detail;
    }
  }

  async function handleOpenMetadataSearch(item: LibraryItemSummary) {
    setErrorText("");
    setStatusText("");
    setMetadataSearch({
      item,
      detail: null,
      query: item.title,
      year: item.releaseDate?.slice(0, 4) ?? "",
      candidates: [],
      isLoadingDetail: true,
      isSearching: false,
      isApplying: false,
      error: "",
    });

    try {
      const detail = await getLibraryItemDetail(item.id);
      setMetadataSearch((current) =>
        current?.item.id === item.id
          ? {
              ...current,
              detail,
              isLoadingDetail: false,
              year: current.year || detail.releaseDate?.slice(0, 4) || "",
            }
          : current,
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === item.id ? { ...current, isLoadingDetail: false, error: message } : current,
      );
    }
  }

  async function handleSearchMetadataFromCard() {
    if (!metadataSearch) {
      return;
    }

    const query = metadataSearch.query.trim();
    if (!query) {
      setMetadataSearch({ ...metadataSearch, error: "请输入影视名称。" });
      return;
    }

    setMetadataSearch({ ...metadataSearch, isSearching: true, error: "", candidates: [] });
    try {
      const candidates = await searchLibraryItemMetadata(
        metadataSearch.item.id,
        query,
        "all",
        metadataSearch.year.trim(),
      );
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id
          ? {
              ...current,
              candidates,
              isSearching: false,
              error: candidates.length === 0 ? `未找到与「${query}」相关的影视。` : "",
            }
          : current,
      );
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id ? { ...current, isSearching: false, error: message } : current,
      );
    }
  }

  async function handleApplyMetadataSearchCandidate(match: TmdbMetadataMatch) {
    if (!metadataSearch) {
      return;
    }

    setMetadataSearch({ ...metadataSearch, isApplying: true, error: "" });
    try {
      const detail = await applyLibraryItemMetadata(metadataSearch.item.id, match);
      syncOpenDetail(detail);
      setMetadataSearch(null);
      setStatusText("已应用匹配项并锁定元数据");
      await loadData();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setMetadataSearch((current) =>
        current?.item.id === metadataSearch.item.id ? { ...current, isApplying: false, error: message } : current,
      );
    }
  }

  async function handleOpenCustomMetadataEdit(item: LibraryItemSummary) {
    setErrorText("");
    setStatusText("正在读取条目资料");
    try {
      const detail = await getLibraryItemDetail(item.id);
      setCustomMetadataEdit({ detail, isSaving: false, error: "" });
      setStatusText("");
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function handleSaveCustomMetadata(request: LibraryItemCustomMetadataUpdateRequest) {
    if (!customMetadataEdit) {
      return;
    }

    setCustomMetadataEdit({ ...customMetadataEdit, isSaving: true, error: "" });
    try {
      const detail = await updateLibraryItemCustomMetadata(customMetadataEdit.detail.id, request);
      syncOpenDetail(detail);
      setCustomMetadataEdit(null);
      setStatusText("已保存自定义资料并锁定元数据");
      await loadData();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setCustomMetadataEdit((current) => (current ? { ...current, isSaving: false, error: message } : current));
    }
  }

  async function toggleLibraryItemWatched(item: LibraryItemSummary) {
    const nextWatched = !item.isWatched;
    setErrorText("");
    try {
      const detail = await setLibraryItemWatchedStatus(item.id, nextWatched);
      syncOpenDetail(detail);
      setStatusText(nextWatched ? "已标记为已看" : "已标记为未看");
      await loadData();
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    }
  }

  async function openDetail(item: LibraryItemSummary) {
    setIsDetailLoading(true);
    setErrorText("");
    setPlayingFile(null);
    try {
      setSelectedDetail(await getLibraryItemDetail(item.id));
    } catch (error) {
      setErrorText(error instanceof Error ? error.message : String(error));
    } finally {
      setIsDetailLoading(false);
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

  if (isAuthLoading || !authStatus?.isAuthenticated) {
    return (
      <AuthGate
        errorText={authError}
        isLoading={isAuthLoading}
        isSetupRequired={authStatus?.isSetupRequired ?? false}
        onSubmit={(username, password) => handleAuthenticate(username, password)}
      />
    );
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
        showEpisodeDetails={settings?.playback.showEpisodeDetails ?? true}
        onBack={() => {
          setPlayingFile(null);
          setSelectedDetail(null);
        }}
        onPlay={(file) => setPlayingFile(file)}
      />
    );
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div className="topStatus" aria-live="polite">
          {topStatusText ? <strong className={errorText ? "error" : ""}>{topStatusText}</strong> : null}
          <div>
            {scanPercent !== null ? (
              <progress aria-label="scan progress" className="scanProgress" max={100} value={scanPercent} />
            ) : null}
            {scrapePercent !== null ? (
              <progress aria-label="scrape progress" className="scanProgress" max={100} value={scrapePercent} />
            ) : null}
          </div>
        </div>
        <div className="toolbar" aria-label="library tools">
          <button
            aria-label="media sources"
            onClick={() => setIsSourceManagerOpen(true)}
            title="媒体源"
          >
            <FolderPlus size={20} />
          </button>
          <button
            aria-label="scan and scrape"
            disabled={isScanning || isScraping || sources.length === 0}
            onClick={handleRefreshLibrary}
            title="扫描并刮削"
          >
            <RefreshCw size={19} className={isScanning || isScraping ? "spin" : ""} />
          </button>
          {isScanning || isScraping ? (
            <button aria-label="cancel refresh" onClick={() => void handleCancelRefresh()} title="取消">
              <CircleStop size={19} />
            </button>
          ) : null}
          <button aria-label="settings" disabled={!settings} onClick={() => setIsSettingsOpen(true)} title="设置">
            <Settings size={19} />
          </button>
          <button aria-label="logout" onClick={() => void handleLogout()} title="退出登录">
            <LogOut size={18} />
          </button>
        </div>
      </header>

      {isSourceManagerOpen ? (
        <SourceManagerPanel
          disabled={isSavingSource}
          isSaving={isSavingSource}
          onAdd={(paths) => handleCreateSources(paths)}
          onClose={() => setIsSourceManagerOpen(false)}
          onRemove={(sourceId) => handleRemoveSource(sourceId)}
          onUpdate={(sourceId, name, isEnabled) => handleUpdateSource(sourceId, name, isEnabled)}
          scanStatus={scanStatus}
          sources={sources}
        />
      ) : null}

      {isSettingsOpen && settings ? (
        <SettingsPanel
          isSaving={isSavingSettings}
          onClose={() => setIsSettingsOpen(false)}
          onSave={(tmdb, cache, playback, proxy) => void handleSaveAppSettings(tmdb, cache, playback, proxy)}
          settings={settings}
        />
      ) : null}

      {metadataSearch ? (
        <MetadataSearchModal
          state={metadataSearch}
          onApply={(match) => void handleApplyMetadataSearchCandidate(match)}
          onChangeQuery={(query) => setMetadataSearch((current) => (current ? { ...current, query } : current))}
          onChangeYear={(year) => setMetadataSearch((current) => (current ? { ...current, year } : current))}
          onClose={() => setMetadataSearch(null)}
          onSearch={() => void handleSearchMetadataFromCard()}
        />
      ) : null}

      {customMetadataEdit ? (
        <CustomMetadataEditModal
          errorText={customMetadataEdit.error}
          isSaving={customMetadataEdit.isSaving}
          detail={customMetadataEdit.detail}
          onClose={() => setCustomMetadataEdit(null)}
          onSave={(request) => void handleSaveCustomMetadata(request)}
        />
      ) : null}

      <section className="continue">
        <h1>继续播放</h1>
        {continueItems.length > 0 ? (
          <div className="posterGrid">
            {continueItems.map((item) => (
              <PosterCard
                item={item}
                key={item.id}
                onEdit={(target) => void handleOpenCustomMetadataEdit(target)}
                onOpen={openDetail}
                onSearchMetadata={(target) => void handleOpenMetadataSearch(target)}
                onToggleWatched={(target) => void toggleLibraryItemWatched(target)}
                showProgress
              />
            ))}
          </div>
        ) : null}
      </section>

      <section className="library">
        <div className="sectionHeader">
          <h2>所有影视</h2>
          <div className="libraryHeaderActions">
            <button aria-label="search library" onClick={() => setIsSearchOpen((current) => !current)} title="搜索">
              <Search size={19} />
            </button>
            <div className="sortMenu">
              <button aria-label="sort library" onClick={() => setIsSortOpen((current) => !current)} title="排序">
                <SlidersHorizontal size={18} />
              </button>
              {isSortOpen ? (
                <div className="sortOptions">
                  {[
                    ["title", "名称"],
                    ["rating", "评分"],
                    ["year", "上映年份"],
                  ].map(([value, label]) => (
                    <button
                      className={sortKey === value ? "active" : ""}
                      key={value}
                      onClick={() => {
                        setSortKey(value as "title" | "rating" | "year");
                        setIsSortOpen(false);
                      }}
                      type="button"
                    >
                      <span>{label}</span>
                    </button>
                  ))}
                </div>
              ) : null}
            </div>
            <button
              aria-label="toggle sort direction"
              className="directionButton"
              onClick={() => setSortDirection((current) => (current === "desc" ? "asc" : "desc"))}
              title={sortDirection === "desc" ? "降序" : "升序"}
            >
              {sortDirection === "desc" ? "↓" : "↑"}
            </button>
          </div>
        </div>
        {isSearchOpen ? (
          <div className="librarySearchRow">
            <input
              aria-label="search library"
              onChange={(event) => setSearchText(event.target.value)}
              placeholder="搜索"
              value={searchText}
            />
          </div>
        ) : null}
        <div className="posterGrid">
          {isLoading ? <div className="emptyRow">加载中</div> : null}
          {!isLoading && displayedItems.length === 0 ? <div className="emptyRow">媒体库为空</div> : null}
          {displayedItems.map((item) => (
            <PosterCard
              item={item}
              key={item.id}
              onEdit={(target) => void handleOpenCustomMetadataEdit(target)}
              onOpen={openDetail}
              onSearchMetadata={(target) => void handleOpenMetadataSearch(target)}
              onToggleWatched={(target) => void toggleLibraryItemWatched(target)}
            />
          ))}
        </div>
      </section>
    </main>
  );
}

function AuthGate({
  errorText,
  isLoading,
  isSetupRequired,
  onSubmit,
}: {
  errorText: string;
  isLoading: boolean;
  isSetupRequired: boolean;
  onSubmit: (username: string, password: string) => Promise<void>;
}) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [localError, setLocalError] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLocalError("");
    setIsSubmitting(true);
    try {
      await onSubmit(username, password);
    } catch (error) {
      setLocalError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <main className="authShell">
      <form className="authPanel" onSubmit={handleSubmit}>
        <header>
          <h1>OmniPlay</h1>
          <span>{isSetupRequired ? "注册管理员" : "登录"}</span>
        </header>
        <label>
          <span>用户名</span>
          <input
            autoComplete="username"
            disabled={isLoading || isSubmitting}
            onChange={(event) => setUsername(event.target.value)}
            value={username}
          />
        </label>
        <label>
          <span>密码</span>
          <input
            autoComplete={isSetupRequired ? "new-password" : "current-password"}
            disabled={isLoading || isSubmitting}
            onChange={(event) => setPassword(event.target.value)}
            type="password"
            value={password}
          />
        </label>
        {errorText || localError ? <strong>{localError || errorText}</strong> : null}
        <button disabled={isLoading || isSubmitting} type="submit">
          <span>{isSubmitting ? "处理中" : isSetupRequired ? "注册" : "登录"}</span>
        </button>
      </form>
    </main>
  );
}

function compareLibraryItems(
  a: LibraryItemSummary,
  b: LibraryItemSummary,
  sortKey: "title" | "rating" | "year",
  sortDirection: "asc" | "desc",
): number {
  const direction = sortDirection === "desc" ? -1 : 1;
  let result = 0;

  if (sortKey === "title") {
    result = a.title.localeCompare(b.title, "zh-Hans");
  } else if (sortKey === "rating") {
    result = compareNullableNumber(a.voteAverage, b.voteAverage, direction);
  } else {
    result = compareNullableNumber(readReleaseYear(a.releaseDate), readReleaseYear(b.releaseDate), direction);
  }

  if (result === 0) {
    result = a.title.localeCompare(b.title, "zh-Hans");
  }

  return sortKey === "title" ? result * direction : result;
}

function compareNullableNumber(a: number | null, b: number | null, direction: 1 | -1): number {
  if (a === null && b === null) {
    return 0;
  }

  if (a === null) {
    return 1;
  }

  if (b === null) {
    return -1;
  }

  return (a - b) * direction;
}

function readReleaseYear(releaseDate: string | null): number | null {
  if (!releaseDate) {
    return null;
  }

  const year = Number(releaseDate.slice(0, 4));
  return Number.isFinite(year) ? year : null;
}

function formatUserVisibleError(message: string): string {
  if (
    message.includes("Received an unexpected EOF or 0 bytes from the transport stream") ||
    message.toLowerCase().includes("unexpected eof")
  ) {
    return "网络连接提前断开（EOF）：通常是代理、TMDB/WebDAV 目标服务或中间网络中断导致，请检查代理、DNS/Host 和目标服务后重试。";
  }

  return message;
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
        starting: "正在启动扫描",
        discovering: "扫描文件",
        probing: "探测媒体",
        indexing: "写入媒体库",
        "source-completed": "媒体源完成",
      }[progress.phase] ?? "正在扫描";
    const fileCount =
      progress.phase !== "probing" && progress.totalVideoFileCount > 0
        ? `${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`
        : progress.phase === "discovering" && progress.processedVideoFileCount > 0
          ? `已发现 ${progress.processedVideoFileCount} 个视频`
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
    return formatUserVisibleError(status.lastError);
  }

  if (status.lastSummary) {
    return `刷新完成：${status.lastSummary.newVideoFileCount} 个新视频`;
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
        "fetching-episodes": "刷新分集信息和剧照",
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
    return formatUserVisibleError(status.lastError);
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
  onClose,
  onRemove,
  onUpdate,
  scanStatus,
  sources,
}: {
  disabled: boolean;
  isSaving: boolean;
  onAdd: (paths: string[]) => Promise<boolean>;
  onClose: () => void;
  onRemove: (sourceId: number) => Promise<boolean>;
  onUpdate: (sourceId: number, name: string, isEnabled: boolean) => Promise<boolean>;
  scanStatus: LibraryScanStatus | null;
  sources: MediaSourceSummary[];
}) {
  const [browsePath, setBrowsePath] = useState("");
  const [browseResult, setBrowseResult] = useState<LocalDirectoryBrowseResult | null>(null);
  const [browseError, setBrowseError] = useState("");
  const [isBrowsing, setIsBrowsing] = useState(false);
  const [draftNames, setDraftNames] = useState<Record<number, string>>({});
  const [selectedLocalPaths, setSelectedLocalPaths] = useState<string[]>([]);
  const [removedSourceIds, setRemovedSourceIds] = useState<number[]>([]);

  useEffect(() => {
    setDraftNames(Object.fromEntries(sources.map((source) => [source.id, source.name])));
    setRemovedSourceIds((current) => current.filter((sourceId) => sources.some((source) => source.id === sourceId)));
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

  async function handleRemove(source: MediaSourceSummary) {
    if (!window.confirm(`移除媒体源“${formatMediaSourceDisplayName(source)}”？`)) {
      return;
    }

    if (await onRemove(source.id)) {
      setRemovedSourceIds((current) => (current.includes(source.id) ? current : [...current, source.id]));
    }
  }

  function toggleLocalPath(path: string) {
    setSelectedLocalPaths((current) =>
      current.includes(path) ? current.filter((item) => item !== path) : [...current, path],
    );
  }

  async function handleMountSelected() {
    const paths = selectedLocalPaths;
    if (paths.length === 0) {
      return;
    }

    const added = await onAdd(paths);
    if (added) {
      setSelectedLocalPaths([]);
    }
  }

  const canMountSelected = selectedLocalPaths.length > 0;
  const directoryEntries = (browseResult?.entries ?? [])
    .slice()
    .sort((left, right) => {
      if (left.isReadable !== right.isReadable) {
        return left.isReadable ? -1 : 1;
      }

      return left.name.localeCompare(right.name, "zh-CN", { sensitivity: "base", numeric: true });
    });

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

        <section className="sourceList">
          {sources.filter((source) => !removedSourceIds.includes(source.id)).length === 0 ? (
            <div className="sourceEmpty">还没有媒体源</div>
          ) : null}
          {sources.filter((source) => !removedSourceIds.includes(source.id)).map((source) => {
            const draftName = draftNames[source.id] ?? source.name;
            const isSourceScanning = isScanningSource(scanStatus, source);
            const displayName = formatMediaSourceDisplayName(source);
            return (
              <article className="sourceRow compactSourceRow" key={source.id}>
                <div className="sourceMeta">
                  <strong>{displayName}</strong>
                  {isSourceScanning ? <span>{formatSourceScanStatus(scanStatus)}</span> : null}
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
                    aria-label={`remove ${displayName}`}
                    disabled={disabled}
                    onClick={() => void handleRemove(source)}
                    type="button"
                  >
                    <Trash2 size={15} />
                  </button>
                </div>
              </article>
            );
          })}
        </section>

        <section className="directoryBrowser" aria-label="local directory browser">
          <div className="directoryPath">
            <span className="currentDirectoryPath">{browseResult?.currentPath ?? (browsePath || "/")}</span>
            <button
              aria-label="mount selected paths"
              disabled={disabled || !canMountSelected}
              onClick={() => void handleMountSelected()}
              type="button"
            >
              <FolderPlus size={15} />
              <span>
                {isSaving ? "挂载中" : selectedLocalPaths.length > 0 ? `挂载 ${selectedLocalPaths.length}` : "挂载"}
              </span>
            </button>
          </div>
          {browseError ? <strong>{browseError}</strong> : null}
          <div className="directoryList">
            {browseResult?.parentPath ? (
              <div
                className="directoryRow"
                key="parent"
              >
                <button
                  className="directoryOpenButton"
                  disabled={disabled || isBrowsing}
                  onClick={() => void loadDirectory(browseResult.parentPath ?? "")}
                  type="button"
                >
                  <ArrowLeft size={15} />
                  <span>上级目录</span>
                </button>
              </div>
            ) : null}
            {directoryEntries.map((entry) => (
              <div
                className="directoryRow"
                key={entry.path}
                title={entry.path}
              >
                <button
                  className="directoryOpenButton"
                  disabled={disabled || isBrowsing || !entry.isReadable}
                  onClick={() => void loadDirectory(entry.path)}
                  type="button"
                >
                  <FolderOpen size={15} />
                  <span>{entry.name}</span>
                </button>
                {!entry.isReadable ? <em>不可访问</em> : null}
                <button
                  aria-label={selectedLocalPaths.includes(entry.path) ? `取消标星 ${entry.name}` : `标星 ${entry.name}`}
                  className={selectedLocalPaths.includes(entry.path) ? "starButton selected" : "starButton"}
                  disabled={disabled || !entry.isReadable}
                  onClick={() => toggleLocalPath(entry.path)}
                  title={selectedLocalPaths.includes(entry.path) ? "取消选择" : "选择挂载"}
                  type="button"
                >
                  <Star size={15} />
                </button>
              </div>
            ))}
            {!browseResult || browseResult.entries.length > 0 || browseResult.parentPath ? null : (
              <div className="sourceEmpty">没有子目录</div>
            )}
          </div>
        </section>
      </aside>
    </div>
  );
}

function formatMediaSourceDisplayName(source: MediaSourceSummary): string {
  const path = source.baseUrl.replace(/\\/g, "/").replace(/\/+$/g, "");
  const name = path.split("/").filter(Boolean).pop();
  return name || source.name;
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
    return `刷新 ${Math.min(progress.processedVideoFileCount, progress.totalVideoFileCount)}/${progress.totalVideoFileCount}`;
  }

  return "刷新中";
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
    proxy: ProxySettings,
  ) => void;
  settings: AppSettingsSnapshot;
}) {
  const [tmdb, setTmdb] = useState(settings.tmdb);
  const [tmdbCredential, setTmdbCredential] = useState(readTmdbCredential(settings.tmdb));
  const [tmdbTest, setTmdbTest] = useState<TmdbConnectionTestResult | null>(null);
  const [tmdbTestError, setTmdbTestError] = useState("");
  const [isTestingTmdb, setIsTestingTmdb] = useState(false);
  const [cache, setCache] = useState(settings.cache);
  const [playback, setPlayback] = useState(normalizePlaybackSettings(settings.playback));
  const [proxy, setProxy] = useState(settings.proxy);
  const [proxyTest, setProxyTest] = useState<ProxyConnectionTestResult | null>(null);
  const [proxyTestError, setProxyTestError] = useState("");
  const [isTestingProxy, setIsTestingProxy] = useState(false);
  const [selfCheck, setSelfCheck] = useState<RuntimeSelfCheckSnapshot | null>(null);
  const [selfCheckError, setSelfCheckError] = useState("");
  const [isCheckingRuntime, setIsCheckingRuntime] = useState(false);

  useEffect(() => {
    setTmdb(settings.tmdb);
    setTmdbCredential(readTmdbCredential(settings.tmdb));
    setTmdbTest(null);
    setTmdbTestError("");
    setCache(settings.cache);
    setPlayback(normalizePlaybackSettings(settings.playback));
    setProxy(settings.proxy);
    setProxyTest(null);
    setProxyTestError("");
  }, [settings]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSave(
      {
        ...applyTmdbCredential(tmdb, tmdbCredential),
        enableMetadataEnrichment: true,
        enablePosterDownloads: true,
        language: "zh-CN",
      },
      {
        ...cache,
        hlsRetentionHours: Math.min(720, Math.max(1, Math.round(cache.hlsRetentionHours || 24))),
        webDavRetentionHours: Math.min(720, Math.max(1, Math.round(cache.webDavRetentionHours || 72))),
        webDavMaxGb: Math.min(1024, Math.max(1, Math.round(cache.webDavMaxGb || 20))),
      },
      playback,
      normalizeProxySettings(proxy),
    );
  }

  async function handleTmdbTest() {
    setTmdbTest(null);
    setTmdbTestError("");
    setIsTestingTmdb(true);
    try {
      setTmdbTest(
        await testTmdbConnection({
          ...applyTmdbCredential(tmdb, tmdbCredential),
          enableMetadataEnrichment: true,
          enablePosterDownloads: true,
          language: "zh-CN",
        }),
      );
    } catch (error) {
      setTmdbTestError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsTestingTmdb(false);
    }
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

  async function handleProxyTest() {
    setProxyTest(null);
    setProxyTestError("");
    setIsTestingProxy(true);
    try {
      setProxyTest(await testProxyConnection(normalizeProxySettings(proxy)));
    } catch (error) {
      setProxyTestError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsTestingProxy(false);
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
          </div>
          <div className="settingsHeaderActions">
            <button disabled={isSaving || !hasPlaybackStrategy} form="app-settings-form" type="submit">
              <Save size={15} />
              <span>{isSaving ? "保存中" : "保存"}</span>
            </button>
            <button aria-label="close settings" onClick={onClose} type="button">
              <X size={18} />
            </button>
          </div>
        </header>

        <form className="settingsForm" id="app-settings-form" onSubmit={handleSubmit}>
          <section className="settingsSection">
            <div className="settingsSectionHeader">
              <h3>TMDB</h3>
              <button disabled={isTestingTmdb} onClick={() => void handleTmdbTest()} type="button">
                <RefreshCw size={15} className={isTestingTmdb ? "spin" : ""} />
                <span>{isTestingTmdb ? "检测中" : "检测"}</span>
              </button>
            </div>
            <label className="settingsToggle">
              <input
                checked={tmdb.enableBuiltInPublicSource}
                onChange={(event) => setTmdb({ ...tmdb, enableBuiltInPublicSource: event.target.checked })}
                type="checkbox"
              />
              <span>内置公开源</span>
            </label>
            <label className="settingsField">
              <span>API 凭据</span>
              <input
                autoComplete="off"
                onChange={(event) => setTmdbCredential(event.target.value)}
                placeholder="API Key 或 Bearer Token"
                type="password"
                value={tmdbCredential}
              />
            </label>
            {tmdbTest ? (
              <div className={`tmdbTestResult ${tmdbTest.isReachable ? "ok" : "error"}`}>
                <strong>{tmdbTest.isReachable ? "连通" : "失败"}</strong>
                <span>{[tmdbTest.source, tmdbTest.statusCode ? `HTTP ${tmdbTest.statusCode}` : null, tmdbTest.message].filter(Boolean).join(" · ")}</span>
              </div>
            ) : null}
            {tmdbTestError ? <p className="runtimeCheckError">{tmdbTestError}</p> : null}
          </section>

          <section className="settingsSection">
            <div className="settingsSectionHeader">
              <h3>代理</h3>
              <button disabled={isTestingProxy} onClick={() => void handleProxyTest()} type="button">
                <RefreshCw size={15} className={isTestingProxy ? "spin" : ""} />
                <span>{isTestingProxy ? "检测中" : "检测"}</span>
              </button>
            </div>
            <label className="settingsToggle">
              <input
                checked={proxy.isEnabled}
                onChange={(event) => setProxy({ ...proxy, isEnabled: event.target.checked })}
                type="checkbox"
              />
              <span>启用代理</span>
            </label>
            <label className="settingsField">
              <span>代理地址</span>
              <input
                autoComplete="off"
                onChange={(event) => setProxy({ ...proxy, url: event.target.value })}
                placeholder="http://192.168.1.2:7890"
                value={proxy.url}
              />
            </label>
            {proxyTest ? (
              <div className={`tmdbTestResult ${proxyTest.isReachable ? "ok" : "error"}`}>
                <strong>{proxyTest.isReachable ? "可用" : "失败"}</strong>
                <span>{[proxyTest.proxyUrl, proxyTest.statusCode ? `HTTP ${proxyTest.statusCode}` : null, proxyTest.message].filter(Boolean).join(" · ")}</span>
              </div>
            ) : null}
            {proxyTestError ? <p className="runtimeCheckError">{proxyTestError}</p> : null}
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
              <span>HLS 硬件转码</span>
            </label>
            <label className="settingsToggle">
              <input
                checked={playback.showEpisodeDetails}
                onChange={(event) => setPlayback({ ...playback, showEpisodeDetails: event.target.checked })}
                type="checkbox"
              />
              <span>分集详情</span>
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
              <p className="runtimeCheckHint">检查 FFmpeg、监听端口、缓存目录、SQLite 和硬件解码/编码。</p>
            )}
          </section>
        </form>
      </aside>
    </div>
  );
}

function readTmdbCredential(settings: AppSettingsSnapshot["tmdb"]): string {
  return settings.customAccessToken || settings.customApiKey || "";
}

function applyTmdbCredential(
  settings: AppSettingsSnapshot["tmdb"],
  credential: string,
): AppSettingsSnapshot["tmdb"] {
  const normalizedCredential = credential.trim();
  if (!normalizedCredential) {
    return { ...settings, customApiKey: "", customAccessToken: "" };
  }

  if (looksLikeTmdbAccessToken(normalizedCredential)) {
    return {
      ...settings,
      customApiKey: "",
      customAccessToken: normalizedCredential.replace(/^Bearer\s+/i, "").trim(),
    };
  }

  return { ...settings, customApiKey: normalizedCredential, customAccessToken: "" };
}

function looksLikeTmdbAccessToken(value: string): boolean {
  const token = value.replace(/^Bearer\s+/i, "").trim();
  return token.startsWith("eyJ") || token.length > 80;
}

function normalizePlaybackSettings(settings: AppSettingsSnapshot["playback"]): AppSettingsSnapshot["playback"] {
  return {
    ...settings,
    showEpisodeDetails: settings.showEpisodeDetails ?? true,
  };
}

function normalizeProxySettings(settings: ProxySettings): ProxySettings {
  return {
    ...settings,
    url: settings.url.trim(),
    username: "",
    password: "",
    bypassList: "",
  };
}

function CacheMaintenance({
  cacheStatus,
  cacheSettings,
  disabled,
  isSaving,
  onCleanupAssets,
  onCleanupTranscode,
  onSaveSettings,
}: {
  cacheStatus: CacheUsageSummary | null;
  cacheSettings: CacheSettings | null;
  disabled: boolean;
  isSaving: boolean;
  onCleanupAssets: () => void;
  onCleanupTranscode: () => void;
  onSaveSettings: (
    hlsRetentionHours: number,
    imageCleanupScope: string,
    webDavRetentionHours: number,
    webDavMaxGb: number,
  ) => void;
}) {
  const [hlsRetentionHours, setHlsRetentionHours] = useState(24);
  const [imageCleanupScope, setImageCleanupScope] = useState("orphans-and-untracked");

  useEffect(() => {
    if (!cacheSettings) {
      return;
    }

    setHlsRetentionHours(cacheSettings.hlsRetentionHours);
    setImageCleanupScope(cacheSettings.imageCleanupScope);
  }, [
    cacheSettings?.hlsRetentionHours,
    cacheSettings?.imageCleanupScope,
  ]);

  if (!cacheStatus) {
    return null;
  }

  const imageBytes = sumCacheBuckets(cacheStatus, ["posters", "thumbnails"]);
  const transcodeBytes = sumCacheBuckets(cacheStatus, ["transcode"]);

  return (
    <section className="cacheMaintenance" aria-label="cache maintenance">
      <div>
        <strong>{formatBytes(cacheStatus.totalBytes)}</strong>
        <span>
          图片 {formatBytes(imageBytes)} · HLS {formatBytes(transcodeBytes)} · {cacheStatus.totalFileCount} 文件
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
          onClick={() => onSaveSettings(
            hlsRetentionHours,
            imageCleanupScope,
            cacheSettings?.webDavRetentionHours ?? 72,
            cacheSettings?.webDavMaxGb ?? 20,
          )}
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
    return task.errorMessage ? formatUserVisibleError(task.errorMessage) : "执行失败";
  }

  return task.progressText ?? task.state;
}

function MetadataSearchModal({
  state,
  onApply,
  onChangeQuery,
  onChangeYear,
  onClose,
  onSearch,
}: {
  state: MetadataSearchState;
  onApply: (match: TmdbMetadataMatch) => void;
  onChangeQuery: (query: string) => void;
  onChangeYear: (year: string) => void;
  onClose: () => void;
  onSearch: () => void;
}) {
  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSearch();
  }

  return (
    <div className="settingsOverlay modalOverlay" role="dialog" aria-label="metadata search" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close metadata search" onClick={onClose} type="button" />
      <section className="metadataDialog">
        <header className="settingsHeader">
          <div>
            <h2>重新匹配 TMDB</h2>
            <span>{state.item.title}</span>
          </div>
          <button aria-label="close metadata search" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <div className="metadataDialogBody">
          <div className="sourceInfoBox">
            <span>源文件名称</span>
            <strong>{state.isLoadingDetail ? "正在读取源文件信息" : sourceFileLabel(state.detail)}</strong>
          </div>

          <form className="metadataSearchForm" onSubmit={handleSubmit}>
            <input
              aria-label="TMDB search query"
              onChange={(event) => onChangeQuery(event.target.value)}
              placeholder="输入正确的影视名称"
              value={state.query}
            />
            <input
              aria-label="TMDB search year"
              inputMode="numeric"
              onChange={(event) => onChangeYear(event.target.value)}
              placeholder="年份"
              value={state.year}
            />
            <button disabled={state.isSearching || state.isApplying || !state.query.trim()} type="submit">
              <Search size={16} />
              <span>{state.isSearching ? "搜索中" : "搜索"}</span>
            </button>
          </form>

          {state.error ? <strong className="metadataDialogError">{state.error}</strong> : null}
          {!state.error && state.candidates.length === 0 && !state.isSearching ? (
            <div className="metadataDialogEmpty">暂无搜索结果，请输入关键字后回车开始匹配</div>
          ) : null}
          {state.isSearching ? <div className="metadataDialogEmpty">正在向 TMDB 请求数据</div> : null}

          {state.candidates.length > 0 ? (
            <div className="metadataDialogResults">
              {state.candidates.map((candidate) => (
                <article className="metadataCandidate" key={`${candidate.mediaType}-${candidate.id}`}>
                  <div className="metadataPoster">
                    {tmdbPosterUrl(candidate.posterPath) ? (
                      <img alt="" src={tmdbPosterUrl(candidate.posterPath)!} />
                    ) : (
                      <span>TMDB</span>
                    )}
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
                  <button disabled={state.isApplying} onClick={() => onApply(candidate)} type="button">
                    关联此项
                  </button>
                </article>
              ))}
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}

function CustomMetadataEditModal({
  detail,
  errorText,
  isSaving,
  onClose,
  onSave,
}: {
  detail: LibraryItemDetail;
  errorText: string;
  isSaving: boolean;
  onClose: () => void;
  onSave: (request: LibraryItemCustomMetadataUpdateRequest) => void;
}) {
  const [title, setTitle] = useState(detail.title);
  const [releaseDate, setReleaseDate] = useState(detail.releaseDate ?? "");
  const [voteAverage, setVoteAverage] = useState(
    typeof detail.voteAverage === "number" && Number.isFinite(detail.voteAverage) ? detail.voteAverage.toFixed(1) : "",
  );
  const [overview, setOverview] = useState(detail.overview ?? "");
  const [posterFile, setPosterFile] = useState<File | null>(null);
  const [posterPreview, setPosterPreview] = useState<string | null>(null);
  const [localError, setLocalError] = useState("");

  useEffect(() => {
    if (!posterFile) {
      setPosterPreview(null);
      return;
    }

    const previewUrl = URL.createObjectURL(posterFile);
    setPosterPreview(previewUrl);
    return () => URL.revokeObjectURL(previewUrl);
  }, [posterFile]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLocalError("");

    const trimmedTitle = title.trim();
    if (!trimmedTitle) {
      setLocalError("请填写影视名称。");
      return;
    }

    const trimmedVote = voteAverage.trim();
    const parsedVote = trimmedVote ? Number(trimmedVote) : null;
    if (parsedVote !== null && (!Number.isFinite(parsedVote) || parsedVote < 0 || parsedVote > 10)) {
      setLocalError("评分需要在 0 到 10 之间。");
      return;
    }

    onSave({
      title: trimmedTitle,
      releaseDate: releaseDate.trim() || null,
      overview: overview.trim() || null,
      voteAverage: parsedVote,
      posterFile,
    });
  }

  const currentPoster = detail.posterAssetId ? posterUrl(detail.posterAssetId) : null;

  return (
    <div className="settingsOverlay modalOverlay" role="dialog" aria-label="custom metadata editor" aria-modal="true">
      <button className="settingsBackdrop" aria-label="close custom editor" onClick={onClose} type="button" />
      <form className="metadataDialog editDialog" onSubmit={handleSubmit}>
        <header className="settingsHeader">
          <div>
            <h2>手动编辑资料</h2>
            <span>{detail.title}</span>
          </div>
          <button aria-label="close custom editor" onClick={onClose} type="button">
            <X size={18} />
          </button>
        </header>

        <div className="metadataDialogBody editDialogBody">
          <section className="editSection">
            <h3>源文件信息</h3>
            <div className="sourceInfoBox compact">
              <span>文件</span>
              <strong>{sourceFileLabel(detail)}</strong>
            </div>
          </section>

          <section className="editSection">
            <h3>基础信息</h3>
            <label className="settingsField">
              <span>影视名称</span>
              <input disabled={isSaving} onChange={(event) => setTitle(event.target.value)} value={title} />
            </label>
            <label className="settingsField">
              <span>上映时间</span>
              <input
                disabled={isSaving}
                onChange={(event) => setReleaseDate(event.target.value)}
                placeholder="2025-01-01"
                value={releaseDate}
              />
            </label>
            <label className="settingsField">
              <span>评分</span>
              <input
                disabled={isSaving}
                inputMode="decimal"
                onChange={(event) => setVoteAverage(event.target.value)}
                placeholder="0.0 - 10.0"
                value={voteAverage}
              />
            </label>
          </section>

          <section className="editSection">
            <h3>剧情简介</h3>
            <textarea disabled={isSaving} onChange={(event) => setOverview(event.target.value)} value={overview} />
          </section>

          <section className="editSection">
            <h3>海报管理</h3>
            <div className="posterEditRow">
              <div className="posterPreview">
                {posterPreview || currentPoster ? <img alt="" src={posterPreview ?? currentPoster!} /> : <span>暂无海报</span>}
              </div>
              <label className="settingsField">
                <span>选择本地图片</span>
                <input
                  accept="image/png,image/jpeg,image/webp"
                  disabled={isSaving}
                  onChange={(event) => setPosterFile(event.target.files?.[0] ?? null)}
                  type="file"
                />
              </label>
            </div>
          </section>

          {localError || errorText ? <strong className="metadataDialogError">{localError || errorText}</strong> : null}
        </div>

        <footer className="settingsFooter">
          <button disabled={isSaving} onClick={onClose} type="button">
            取消
          </button>
          <button disabled={isSaving} type="submit">
            <Save size={16} />
            <span>{isSaving ? "保存中" : "保存修改并锁定"}</span>
          </button>
        </footer>
      </form>
    </div>
  );
}

function sourceFileLabel(detail: LibraryItemDetail | null): string {
  const file = detail?.videoFiles[0] ?? null;
  if (!file) {
    return "未找到关联的视频文件";
  }

  return file.relativePath || file.fileName || "未知文件名";
}

function PosterCard({
  item,
  onEdit,
  onOpen,
  onSearchMetadata,
  onToggleWatched,
  showProgress = false,
}: {
  item: LibraryItemSummary;
  onEdit?: (item: LibraryItemSummary) => void;
  onOpen?: (item: LibraryItemSummary) => void;
  onSearchMetadata?: (item: LibraryItemSummary) => void;
  onToggleWatched?: (item: LibraryItemSummary) => void;
  showProgress?: boolean;
}) {
  const year = item.releaseDate?.slice(0, 4) ?? "";
  const rating =
    typeof item.voteAverage === "number" && Number.isFinite(item.voteAverage) ? item.voteAverage.toFixed(1) : null;
  const progressPercent = playbackProgressPercent(item);

  function handleCardAction(event: MouseEvent<HTMLButtonElement>, action?: (target: LibraryItemSummary) => void) {
    event.preventDefault();
    event.stopPropagation();
    action?.(item);
  }

  return (
    <article className="posterCard" onClick={() => onOpen?.(item)}>
      <div className="posterArt">
        {item.posterAssetId ? <img alt="" src={posterUrl(item.posterAssetId)} /> : null}
        {rating ? <span>{rating}</span> : null}
        <div className="posterArtActions">
          <button
            aria-label={`搜索刮削 ${item.title}`}
            onClick={(event) => handleCardAction(event, onSearchMetadata)}
            title="搜索刮削"
            type="button"
          >
            <Search size={16} />
          </button>
          <button
            aria-label={`自定义编辑 ${item.title}`}
            onClick={(event) => handleCardAction(event, onEdit)}
            title="自定义编辑"
            type="button"
          >
            <Pencil size={15} />
          </button>
        </div>
      </div>
      {showProgress ? (
        <div
          aria-label="播放进度"
          aria-valuemax={100}
          aria-valuemin={0}
          aria-valuenow={progressPercent}
          className="posterProgress"
          role="progressbar"
        >
          <div className="posterProgressFill" style={{ width: `${progressPercent}%` }} />
        </div>
      ) : null}
      <div className="posterTitleRow">
        <h3>{item.title}</h3>
        <button
          aria-label={item.isWatched ? `标为未看 ${item.title}` : `标为已看 ${item.title}`}
          className={item.isWatched ? "watchedButton watched" : "watchedButton"}
          onClick={(event) => handleCardAction(event, onToggleWatched)}
          title={item.isWatched ? "标为未看" : "标为已看"}
          type="button"
        >
          {item.isWatched ? <CheckCircle2 size={15} /> : <Circle size={15} />}
        </button>
      </div>
      {year ? <p>{year}</p> : null}
    </article>
  );
}

function playbackProgressPercent(item: LibraryItemSummary): number {
  if (item.maxDurationSeconds <= 0 || item.maxProgressSeconds <= 0) {
    return 0;
  }

  return Math.round((Math.min(item.maxProgressSeconds, item.maxDurationSeconds) / item.maxDurationSeconds) * 100);
}

function DetailView({
  detail,
  errorText,
  showEpisodeDetails,
  onBack,
  onPlay,
}: {
  detail: LibraryItemDetail;
  errorText: string;
  showEpisodeDetails: boolean;
  onBack: () => void;
  onPlay: (file: VideoFileSummary) => void;
}) {
  const poster = detail.posterAssetId ? posterUrl(detail.posterAssetId) : null;
  const year = detail.releaseDate?.slice(0, 4);
  const playableEpisodes = detail.seasons.flatMap((season) => season.episodes).filter((episode) => episode.videoFile);
  const mainEpisode =
    detail.itemKind === "tv"
      ? playableEpisodes.find((episode) => hasUnfinishedProgress(episode.videoFile)) ??
        playableEpisodes.find((episode) => episode.videoFile && !episode.videoFile.isWatched) ??
        playableEpisodes[0] ??
        null
      : null;
  const mainFile = mainEpisode?.videoFile ?? detail.videoFiles[0] ?? null;
  const mainProgressPercent = fileProgressPercent(mainFile);
  const mainStatusLabel = watchedStatusLabel(mainFile);
  const mainElapsedLabel = mainFile ? formatPlaybackTime(mainFile.positionSeconds) : "0:00";
  const mainDurationLabel = mainFile && mainFile.durationSeconds > 0 ? formatPlaybackTime(mainFile.durationSeconds) : "--:--";
  const playButtonText = [
    mainFile && mainFile.positionSeconds > 5 ? "继续播放" : "开始播放",
    mainEpisode ? episodeDisplayLabel(mainEpisode.seasonNumber, mainEpisode.episodeNumber) : null,
  ]
    .filter(Boolean)
    .join(" ");
  const [selectedSeasonId, setSelectedSeasonId] = useState(detail.seasons[0]?.id ?? "");
  const selectedSeason = detail.seasons.find((season) => season.id === selectedSeasonId) ?? detail.seasons[0] ?? null;

  useEffect(() => {
    if (detail.seasons.length === 0) {
      setSelectedSeasonId("");
      return;
    }

    if (!detail.seasons.some((season) => season.id === selectedSeasonId)) {
      setSelectedSeasonId(detail.seasons[0].id);
    }
  }, [detail.id, detail.seasons, selectedSeasonId]);

  return (
    <main className="detailShell">
      {poster ? <img alt="" className="detailBackdrop" src={poster} /> : null}
      <header className="detailTopbar">
        <button aria-label="back" onClick={onBack}>
          <ArrowLeft size={19} />
        </button>
      </header>

      <section className="detailHero">
        <div className="detailPoster">
          {poster ? <img alt="" src={poster} /> : null}
        </div>
        <div className="detailMeta">
          <h1>{detail.title}</h1>
          <div className="detailFacts">
            {year ? <span>{year}</span> : null}
            {detail.voteAverage ? <span>评分 {detail.voteAverage.toFixed(1)}</span> : null}
          </div>
          <p>{detail.overview || "暂无简介"}</p>
          {errorText ? <strong className="detailError">{errorText}</strong> : null}
          {mainFile ? (
            <div className="detailActions">
              <button aria-label="play" onClick={() => onPlay(mainFile)}>
                <Play size={18} />
                <span>{playButtonText}</span>
              </button>
              <span className={mainStatusLabel === "已播" ? "detailWatchStatus watched" : "detailWatchStatus"}>
                {mainStatusLabel}
              </span>
              <div className="detailTimelineAxis">
                <span>{mainElapsedLabel}</span>
                <div
                  aria-label="播放进度"
                  aria-valuemax={100}
                  aria-valuemin={0}
                  aria-valuenow={mainProgressPercent}
                  className="detailTimeline"
                  role="progressbar"
                >
                  <div style={{ width: `${mainProgressPercent}%` }} />
                </div>
                <span>{mainDurationLabel}</span>
              </div>
            </div>
          ) : null}
        </div>
      </section>

      {detail.itemKind === "tv" && selectedSeason ? (
        <section className="episodeSection">
          <div className="seasonHeader">
            <select
              aria-label="season"
              onChange={(event) => setSelectedSeasonId(event.target.value)}
              value={selectedSeason.id}
            >
              {detail.seasons.map((season) => (
                <option key={season.id} value={season.id}>
                  {season.title ?? (season.seasonNumber === 0 ? "特别篇" : `第 ${season.seasonNumber} 季`)}
                </option>
              ))}
            </select>
          </div>
          <div className="episodeGrid">
            {selectedSeason.episodes.map((episode) => (
              <EpisodeCard
                episode={episode}
                key={episode.id}
                onPlay={onPlay}
                poster={poster}
                showDetails={showEpisodeDetails}
              />
            ))}
          </div>
        </section>
      ) : null}
    </main>
  );
}

function EpisodeCard({
  episode,
  onPlay,
  poster,
  showDetails,
}: {
  episode: EpisodeDetail;
  onPlay: (file: VideoFileSummary) => void;
  poster: string | null;
  showDetails: boolean;
}) {
  const file = episode.videoFile;
  const episodeLabel = episodeDisplayLabel(episode.seasonNumber, episode.episodeNumber);
  const facts = [episode.title ? episodeLabel : null, episode.airDate].filter(Boolean).join(" · ");
  const progressPercent = fileProgressPercent(file);
  const showProgress = progressPercent > 0 && progressPercent < 95;
  const handleKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (!file || (event.key !== "Enter" && event.key !== " ")) {
      return;
    }

    event.preventDefault();
    onPlay(file);
  };

  return (
    <article
      className={[file ? "episodeCard clickableEpisode" : "episodeCard", showDetails ? "" : "simpleEpisode"]
        .filter(Boolean)
        .join(" ")}
      onClick={file ? () => onPlay(file) : undefined}
      onKeyDown={handleKeyDown}
      role={file ? "button" : undefined}
      tabIndex={file ? 0 : undefined}
    >
      {episode.stillAssetId ? (
        <img alt="" className="episodeStill" src={thumbnailUrl(episode.stillAssetId)} />
      ) : (
        <div className="episodeStill episodeStillPlaceholder">
          {poster ? <img alt="" src={poster} /> : null}
        </div>
      )}
      <div className="episodeBody">
        <h3>{showDetails ? episode.title ?? episodeLabel : episodeLabel}</h3>
        {showDetails && facts ? <p>{facts}</p> : null}
        {showDetails && episode.overview ? <span>{episode.overview}</span> : null}
        {showDetails && showProgress ? (
          <div
            aria-label="播放进度"
            aria-valuemax={100}
            aria-valuemin={0}
            aria-valuenow={progressPercent}
            className="episodeProgress"
            role="progressbar"
          >
            <div style={{ width: `${progressPercent}%` }} />
          </div>
        ) : null}
      </div>
    </article>
  );
}

function episodeDisplayLabel(seasonNumber: number, episodeNumber: number): string {
  return seasonNumber === 0 ? `特别篇第 ${episodeNumber} 集` : `第 ${seasonNumber} 季第 ${episodeNumber} 集`;
}

function fileProgressPercent(file: VideoFileSummary | null): number {
  if (!file || file.durationSeconds <= 0 || file.positionSeconds <= 0) {
    return 0;
  }

  return Math.round((Math.min(file.positionSeconds, file.durationSeconds) / file.durationSeconds) * 100);
}

function hasUnfinishedProgress(file: VideoFileSummary | null | undefined): boolean {
  if (!file || file.positionSeconds <= 5) {
    return false;
  }

  return file.durationSeconds <= 0 || fileProgressPercent(file) < 95;
}

function watchedStatusLabel(file: VideoFileSummary | null): string {
  if (!file) {
    return "未播";
  }

  if (file.isWatched || fileProgressPercent(file) >= 95) {
    return "已播";
  }

  return file.positionSeconds > 5 ? "未播完" : "未播";
}

function FileRow({
  file,
  onPlay,
}: {
  file: VideoFileSummary;
  onPlay: (file: VideoFileSummary) => void;
}) {
  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "Enter" && event.key !== " ") {
      return;
    }

    event.preventDefault();
    onPlay(file);
  };

  return (
    <div className="fileRow" onClick={() => onPlay(file)} onKeyDown={handleKeyDown} role="button" tabIndex={0}>
      <div>
        <strong>{file.fileName}</strong>
        <span>{file.relativePath}</span>
        <span>{formatMediaProbe(file)}</span>
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
  const controlsHideTimerRef = useRef(0);
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [playbackMode, setPlaybackMode] = useState<"direct" | "hls" | null>(null);
  const [playerStatus, setPlayerStatus] = useState("正在准备播放");
  const [playerError, setPlayerError] = useState("");
  const [quality, setQuality] = useState("original");
  const [audioTrack, setAudioTrack] = useState("auto");
  const [subtitleMode, setSubtitleMode] = useState("off");
  const [selectedSubtitleId, setSelectedSubtitleId] = useState("");
  const [useHardware, setUseHardware] = useState(true);
  const [capabilities, setCapabilities] = useState<FfmpegTranscodeCapabilities | null>(null);
  const [subtitles, setSubtitles] = useState<PlaybackSubtitleTrack[]>([]);
  const [cacheStatus, setCacheStatus] = useState<PlaybackCacheStatus | null>(null);
  const [diagnostics, setDiagnostics] = useState<PlaybackDiagnostics | null>(null);
  const [diagnosticsError, setDiagnosticsError] = useState("");
  const [isDiagnosticsOpen, setIsDiagnosticsOpen] = useState(false);
  const [cleanupText, setCleanupText] = useState("");
  const [showPlayerControls, setShowPlayerControls] = useState(true);
  const [isPaused, setIsPaused] = useState(true);
  const [currentTime, setCurrentTime] = useState(file.positionSeconds);
  const [duration, setDuration] = useState(file.durationSeconds);
  const title = formatPlayerTitle(detail, file);
  const subTitle = playerSubTitle(file);
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
  const seekSeconds = 10;
  const progressValue = duration > 0 ? Math.min(100, Math.max(0, (currentTime / duration) * 100)) : 0;

  const resetControlTimer = useCallback((forceVisible = true) => {
    window.clearTimeout(controlsHideTimerRef.current);
    if (forceVisible) {
      setShowPlayerControls(true);
    }

    const video = videoRef.current;
    if (!video || video.paused || playerStatus || playerError) {
      return;
    }

    controlsHideTimerRef.current = window.setTimeout(() => {
      setShowPlayerControls(false);
    }, 3000);
  }, [playerError, playerStatus]);

  useEffect(() => {
    return () => window.clearTimeout(controlsHideTimerRef.current);
  }, []);

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

  useEffect(() => {
    function handleKeyDown(event: globalThis.KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target?.closest("input, select, textarea, button")) {
        return;
      }

      if (event.code === "Space") {
        event.preventDefault();
        togglePlayPause();
        return;
      }

      if (event.key === "ArrowLeft") {
        event.preventDefault();
        seekRelative(-seekSeconds);
        return;
      }

      if (event.key === "ArrowRight") {
        event.preventDefault();
        seekRelative(seekSeconds);
      }
    }

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  });

  function handleLoadedMetadata() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const duration = Number.isFinite(video.duration) ? video.duration : file.durationSeconds;
    setDuration(duration);
    setIsPaused(video.paused);
    if (duration <= 0 || file.positionSeconds < duration - 8) {
      video.currentTime = file.positionSeconds;
      setCurrentTime(file.positionSeconds);
    }
  }

  function handleTimeUpdate() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    setCurrentTime(Number.isFinite(video.currentTime) ? video.currentTime : 0);
    if (Number.isFinite(video.duration) && video.duration > 0) {
      setDuration(video.duration);
    }
    void saveProgress(false);
  }

  function handlePlayStateChange(paused: boolean) {
    setIsPaused(paused);
    resetControlTimer(true);
  }

  function togglePlayPause() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (video.paused) {
      void video.play().catch((error: unknown) => {
        setPlayerError(error instanceof Error ? error.message : String(error));
      });
    } else {
      video.pause();
    }
    resetControlTimer(true);
  }

  function seekRelative(seconds: number) {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const nextTime = Math.max(0, Math.min(duration || video.duration || 0, video.currentTime + seconds));
    video.currentTime = nextTime;
    setCurrentTime(nextTime);
    resetControlTimer(true);
  }

  function handleSeekPercent(value: string) {
    const video = videoRef.current;
    if (!video || duration <= 0) {
      return;
    }

    const nextTime = (Number(value) / 100) * duration;
    video.currentTime = nextTime;
    setCurrentTime(nextTime);
    resetControlTimer(true);
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
    <main className="playerShell" onMouseMove={() => resetControlTimer(true)}>
      <section className="playerStage" onClick={() => setShowPlayerControls((current) => !current)}>
        <video
          autoPlay
          onClick={(event) => event.stopPropagation()}
          onEnded={() => void saveProgress(true)}
          onLoadedMetadata={handleLoadedMetadata}
          onPause={() => {
            handlePlayStateChange(true);
            void saveProgress(true);
          }}
          onPlay={() => handlePlayStateChange(false)}
          onTimeUpdate={handleTimeUpdate}
          ref={videoRef}
        >
          {subtitleMode === "web" && selectedSubtitle?.webVttUrl ? (
            <track default kind="subtitles" label={selectedSubtitle.language ?? selectedSubtitle.fileName} src={selectedSubtitle.webVttUrl} />
          ) : null}
        </video>

        <div className={showPlayerControls ? "playerChrome visible" : "playerChrome"}>
          <header className="playerTopbar">
            <button aria-label="back" onClick={() => void handleBack()} type="button">
              <ArrowLeft size={22} />
            </button>
            <div>
              <h1>{title}</h1>
              {subTitle ? <p>{subTitle}</p> : null}
            </div>
          </header>

          <div className="playerBottomBar" onClick={(event) => event.stopPropagation()}>
            {cleanupText ? <div className="playerNotice">{cleanupText}</div> : null}
            <div className="playerTransport">
              <button aria-label="backward" onClick={() => seekRelative(-seekSeconds)} type="button">
                <SkipBack size={34} />
              </button>
              <button className="playerPlayButton" aria-label={isPaused ? "play" : "pause"} onClick={togglePlayPause} type="button">
                {isPaused ? <Play size={58} /> : <Pause size={58} />}
              </button>
              <button aria-label="forward" onClick={() => seekRelative(seekSeconds)} type="button">
                <SkipForward size={34} />
              </button>
            </div>

            <div className="playerTimeline">
              <span>{formatPlaybackTime(currentTime)}</span>
              <input
                aria-label="播放进度"
                max={100}
                min={0}
                onChange={(event) => handleSeekPercent(event.target.value)}
                step={0.1}
                type="range"
                value={progressValue}
              />
              <span>{duration > 0 ? `-${formatPlaybackTime(Math.max(0, duration - currentTime))}` : "--:--"}</span>
            </div>

            <div className="playerMenuBar">
              <label title="清晰度">
                <SlidersHorizontal size={16} />
                <select aria-label="quality" onChange={(event) => setQuality(event.target.value)} value={quality}>
                  <option value="original">原始</option>
                  <option value="1080p">1080p</option>
                  <option value="720p">720p</option>
                  <option value="480p">480p</option>
                  <option value="360p">360p</option>
                </select>
              </label>
              <label title="音轨">
                <Music2 size={16} />
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
              </label>
              <label title="字幕">
                <Captions size={16} />
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
                  <option value="">关闭字幕</option>
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
              </label>
              <label title="字幕方式">
                <Settings size={16} />
                <select
                  aria-label="subtitle mode"
                  disabled={!selectedSubtitleId}
                  onChange={(event) => setSubtitleMode(event.target.value)}
                  value={subtitleMode}
                >
                  <option value="off">关闭</option>
                  <option disabled={!!selectedEmbeddedSubtitle} value="web">外挂</option>
                  <option value="burn">烧录</option>
                </select>
              </label>
              <button aria-label="cleanup cache" onClick={() => void handleCleanupCache()} title="清理缓存" type="button">
                <Trash2 size={16} />
              </button>
              <button aria-label="playback diagnostics" onClick={() => void handleLoadDiagnostics()} title="播放诊断" type="button">
                <Bug size={16} />
              </button>
            </div>
          </div>
        </div>

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

        {isDiagnosticsOpen ? (
          <section className="playbackDiagnostics" aria-label="playback diagnostics" onClick={(event) => event.stopPropagation()}>
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
      </section>
    </main>
  );
}

function formatPlayerTitle(detail: LibraryItemDetail, file: VideoFileSummary): string {
  if (file.seasonNumber === null || file.episodeNumber === null) {
    return detail.title;
  }

  return `${detail.title} ${episodeDisplayLabel(file.seasonNumber, file.episodeNumber)}`;
}

function playerSubTitle(file: VideoFileSummary): string {
  return [file.container, file.videoCodec, file.audioCodec].filter(Boolean).join(" · ");
}

function formatPlaybackTime(seconds: number): string {
  const safeSeconds = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  const remainingSeconds = safeSeconds % 60;
  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainingSeconds).padStart(2, "0")}`;
  }

  return `${minutes}:${String(remainingSeconds).padStart(2, "0")}`;
}
