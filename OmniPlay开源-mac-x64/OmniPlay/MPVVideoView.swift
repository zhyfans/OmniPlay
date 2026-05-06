import SwiftUI
import AppKit
import QuartzCore
import Libmpv

private final class MPVContainerView: NSView {
    weak var playerManager: MPVPlayerManager?
    private var lastLayoutSize: CGSize = .zero
    private var windowObservationTokens: [NSObjectProtocol] = []

    deinit {
        removeWindowObservers()
    }

    override func layout() {
        super.layout()
        refreshDrawableSize()

        let size = bounds.size
        if size.width > 0, size.height > 0, size != lastLayoutSize {
            lastLayoutSize = size
            playerManager?.setDrawable(self, force: true)
            scheduleDrawableRefresh(rebuildLayer: false)
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        installWindowObservers()
        scheduleDrawableRefresh(rebuildLayer: false)
    }

    func installFreshMetalLayer(rebind: Bool) {
        let metalLayer = CAMetalLayer()
        configure(metalLayer)
        layer = metalLayer
        lastLayoutSize = bounds.size
        if rebind {
            playerManager?.setDrawable(self, force: true)
        }
    }

    private func installWindowObservers() {
        removeWindowObservers()
        guard let window else { return }
        let center = NotificationCenter.default
        let events: [(Notification.Name, Bool)] = [
            (NSWindow.didResizeNotification, false),
            (NSWindow.didEndLiveResizeNotification, false),
            (NSWindow.didEnterFullScreenNotification, false),
            (NSWindow.didExitFullScreenNotification, true)
        ]
        windowObservationTokens = events.map { eventName, shouldRebuild in
            center.addObserver(forName: eventName, object: window, queue: .main) { [weak self] _ in
                self?.scheduleDrawableRefresh(rebuildLayer: shouldRebuild)
            }
        }
    }

    private func removeWindowObservers() {
        let center = NotificationCenter.default
        for token in windowObservationTokens {
            center.removeObserver(token)
        }
        windowObservationTokens = []
    }

    private func configure(_ metalLayer: CAMetalLayer) {
        let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2.0
        metalLayer.backgroundColor = NSColor.black.cgColor
        metalLayer.frame = bounds
        metalLayer.contentsScale = scale
        metalLayer.drawableSize = NSSize(width: bounds.width * scale, height: bounds.height * scale)
        metalLayer.needsDisplayOnBoundsChange = true
        metalLayer.setNeedsDisplay()
    }

    private func refreshDrawableSize() {
        layer?.frame = bounds
        if let metalLayer = layer as? CAMetalLayer {
            configure(metalLayer)
        }
    }

    private func scheduleDrawableRefresh(rebuildLayer: Bool) {
        let delays: [TimeInterval] = rebuildLayer ? [0.0, 0.08, 0.22, 0.45] : [0.0, 0.12]
        for (index, delay) in delays.enumerated() {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, self.window != nil else { return }
                self.layoutSubtreeIfNeeded()
                if rebuildLayer && index == 1 {
                    self.installFreshMetalLayer(rebind: true)
                } else {
                    self.refreshDrawableSize()
                    self.playerManager?.setDrawable(self, force: true)
                }
            }
        }
    }
}

struct MPVVideoView: NSViewRepresentable {
    let playerManager: MPVPlayerManager

    final class Coordinator {
        var lastSize: CGSize = .zero
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = MPVContainerView(frame: .zero)
        view.playerManager = playerManager
        view.wantsLayer = true
        view.autoresizingMask = [.width, .height]

        view.installFreshMetalLayer(rebind: false)
        context.coordinator.lastSize = view.bounds.size

        print("[MPVVideoView] makeNSView created")
        // Bind immediately so PlayerScreen can wait for drawable-ready before loadFiles.
        playerManager.setDrawable(view)

        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        let newSize = nsView.bounds.size
        if newSize != context.coordinator.lastSize {
            context.coordinator.lastSize = newSize
            // Rebind on fullscreen/resize to avoid stale drawable region (black area on right/bottom).
            playerManager.setDrawable(nsView, force: true)
        }
    }
}
