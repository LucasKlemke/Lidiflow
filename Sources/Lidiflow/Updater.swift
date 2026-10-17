import AppKit

/// Checks GitHub for a newer release and installs it in place: downloads the .zip, checks that it's
/// signed by the same Developer ID as this app, swaps the bundle and relaunches.
@MainActor
final class Updater: ObservableObject {
    struct Release: Equatable {
        let version: String
        let notesURL: URL
        let zipURL: URL
    }

    enum Phase: Equatable {
        case idle, downloading(Double), installing
        case failed(String)
    }

    static let shared = Updater()

    @Published private(set) var available: Release?
    @Published private(set) var phase = Phase.idle

    static let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"

    private static let latestURL = URL(string: "https://api.github.com/repos/LucasKlemke/Lidiflow/releases/latest")!
    /// Only this app (its bundle id), signed by this team (build.sh's Developer ID), is installed.
    private static let requirement =
        #"identifier "com.lidiflow.app" and anchor apple generic and certificate leaf[subject.OU] = "V78C7X77T3""#
    private var timer: Timer?

    /// Checks now and every 6 hours.
    func start() {
        Task { await check() }
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { await self?.check() }
        }
    }

    func check() async {
        var request = URLRequest(url: Self.latestURL)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 15
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String,
              let page = (json["html_url"] as? String).flatMap(URL.init(string:)) else { return }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        let assets = json["assets"] as? [[String: Any]] ?? []
        guard Self.isNewer(version, than: Self.current),
              let zip = assets.compactMap({ $0["browser_download_url"] as? String })
                  .first(where: { $0.hasSuffix(".zip") }).flatMap(URL.init(string:)) else { return }
        available = Release(version: version, notesURL: page, zipURL: zip)
    }

    /// Downloads and installs `available`, then relaunches. Where the app can't replace itself (a build
    /// outside Applications, or a folder it can't write), it opens the download page instead.
    func install() async {
        guard let release = available, phase == .idle || phase.isFailure else { return }
        let bundle = Bundle.main.bundleURL
        guard Self.canReplace(bundle) else {
            NSWorkspace.shared.open(release.notesURL)
            return
        }
        phase = .downloading(0)
        do {
            let work = FileManager.default.temporaryDirectory.appendingPathComponent("lidiflow-update-\(UUID().uuidString)")
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
            let zip = try await Self.download(release.zipURL, to: work.appendingPathComponent("update.zip")) { value in
                Task { @MainActor in if case .downloading = self.phase { self.phase = .downloading(value) } }
            }
            phase = .installing
            try await Self.run("/usr/bin/ditto", ["-x", "-k", zip.path, work.path])
            let fresh = work.appendingPathComponent("Lidiflow.app")
            // Never install something that isn't this app, signed by this developer.
            try await Self.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "-R=\(Self.requirement)", fresh.path])
            try Self.replace(bundle, with: fresh)
            Self.relaunch(bundle)
        } catch {
            phase = .failed("Não foi possível atualizar. Baixe pelo site.")
        }
    }

    // MARK: - Helpers

    /// "0.10.0" is newer than "0.9.2": compares the numbers, not the text.
    static func isNewer(_ version: String, than current: String) -> Bool {
        let a = version.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for index in 0..<max(a.count, b.count) {
            let (x, y) = (index < a.count ? a[index] : 0, index < b.count ? b[index] : 0)
            if x != y { return x > y }
        }
        return false
    }

    private static func canReplace(_ bundle: URL) -> Bool {
        let folder = bundle.deletingLastPathComponent()
        let applications = ["/Applications", NSHomeDirectory() + "/Applications"]
        return applications.contains(folder.path) && FileManager.default.isWritableFile(atPath: folder.path)
    }

    /// Moves the running app aside, puts the new one in its place, and deletes the old one. If the swap
    /// fails halfway, the old app goes back.
    private static func replace(_ bundle: URL, with fresh: URL) throws {
        let manager = FileManager.default
        let old = fresh.deletingLastPathComponent().appendingPathComponent("Lidiflow-old.app")
        try manager.moveItem(at: bundle, to: old)
        do {
            try manager.moveItem(at: fresh, to: bundle)
        } catch {
            try? manager.moveItem(at: old, to: bundle)
            throw error
        }
        try? manager.removeItem(at: old)
    }

    /// Opens the new bundle once this process has quit.
    private static func relaunch(_ bundle: URL) {
        let pid = ProcessInfo.processInfo.processIdentifier
        let script = "while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done; open \"\(bundle.path)\""
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", script]
        try? process.run()
        NSApp.terminate(nil)
    }

    private static func run(_ tool: String, _ arguments: [String]) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            process.terminationHandler = { finished in
                if finished.terminationStatus == 0 {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: CocoaError(.executableLoad))
                }
            }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
    }

    private static func download(_ url: URL, to destination: URL, progress: @escaping (Double) -> Void) async throws -> URL {
        let (temporary, response) = try await URLSession.shared.download(from: url, delegate: DownloadProgress(progress))
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        try FileManager.default.moveItem(at: temporary, to: destination)
        return destination
    }

    private final class DownloadProgress: NSObject, URLSessionDownloadDelegate {
        let report: (Double) -> Void

        init(_ report: @escaping (Double) -> Void) {
            self.report = report
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                        totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
            guard totalBytesExpectedToWrite > 0 else { return }
            report(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
        }

        // The async `download(from:)` delivers the file itself; this is only here to satisfy the protocol.
        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
    }
}

private extension Updater.Phase {
    var isFailure: Bool {
        if case .failed = self { return true }
        return false
    }
}
