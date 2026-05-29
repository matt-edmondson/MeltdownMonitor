// Live Activity data model shared between the app target (which starts and
// updates the activity through LiveActivityBridge.swift) and the widget
// extension (which presents it). Part of a native Xcode-managed target — the
// .NET build does not compile this file. See docs/live-activity.md.

import ActivityKit
import Foundation

@available(iOS 16.2, *)
public struct MeltdownActivityAttributes: ActivityAttributes {
    /// Live, per-update content. Mirrors MeltdownMonitor.Mobile.Services.LiveActivityContent;
    /// the colour is already resolved to a #RRGGBB string by the C# palette so the
    /// presentation layer never re-derives it.
    public struct ContentState: Codable, Hashable {
        public var label: String      // "Watching", "Paused", …
        public var colorHex: String   // "#RRGGBB"
        public var heartRate: Int     // bpm, 0 when unknown
        public var rmssdRatio: Double // RMSSD ÷ baseline; 1.0 = at baseline
        public var isPaused: Bool

        public init(label: String, colorHex: String, heartRate: Int, rmssdRatio: Double, isPaused: Bool) {
            self.label = label
            self.colorHex = colorHex
            self.heartRate = heartRate
            self.rmssdRatio = rmssdRatio
            self.isPaused = isPaused
        }
    }

    /// Fixed for the activity's lifetime.
    public var title: String

    public init(title: String) {
        self.title = title
    }
}
