// C-callable bridge between the managed LiveActivityController (C#) and
// ActivityKit. Each function is exported with @_cdecl so the .NET side can
// reach it via [DllImport("__Internal", EntryPoint = "mm_live_activity_*")].
//
// The C# parameter marshalling is:
//   [MarshalAs(LPUTF8Str)] string  -> UnsafePointer<CChar>
//   int                            -> Int32
//   double                         -> Double
//   [MarshalAs(I1)] bool           -> Bool
//
// Part of a native Xcode-managed target; the .NET build does not compile this
// file. See docs/live-activity.md for the wiring steps.

import ActivityKit
import Foundation

@available(iOS 16.2, *)
private enum MeltdownLiveActivityStore {
    static var current: Activity<MeltdownActivityAttributes>?
}

private func makeState(
    _ label: UnsafePointer<CChar>,
    _ colorHex: UnsafePointer<CChar>,
    _ heartRate: Int32,
    _ rmssdRatio: Double,
    _ isPaused: Bool
) -> Any? {
    guard #available(iOS 16.2, *) else { return nil }
    return MeltdownActivityAttributes.ContentState(
        label: String(cString: label),
        colorHex: String(cString: colorHex),
        heartRate: Int(heartRate),
        rmssdRatio: rmssdRatio,
        isPaused: isPaused)
}

@_cdecl("mm_live_activity_start")
public func mm_live_activity_start(
    _ label: UnsafePointer<CChar>,
    _ colorHex: UnsafePointer<CChar>,
    _ heartRate: Int32,
    _ rmssdRatio: Double,
    _ isPaused: Bool
) {
    guard #available(iOS 16.2, *) else { return }
    guard ActivityAuthorizationInfo().areActivitiesEnabled else { return }
    guard MeltdownLiveActivityStore.current == nil,
          let state = makeState(label, colorHex, heartRate, rmssdRatio, isPaused)
            as? MeltdownActivityAttributes.ContentState else { return }

    let attributes = MeltdownActivityAttributes(title: "MeltdownMonitor")
    do {
        MeltdownLiveActivityStore.current = try Activity.request(
            attributes: attributes,
            content: .init(state: state, staleDate: nil))
    } catch {
        // Activity request can fail if the user disabled Live Activities or the
        // per-app budget is exhausted — nothing to do but skip it.
    }
}

@_cdecl("mm_live_activity_update")
public func mm_live_activity_update(
    _ label: UnsafePointer<CChar>,
    _ colorHex: UnsafePointer<CChar>,
    _ heartRate: Int32,
    _ rmssdRatio: Double,
    _ isPaused: Bool
) {
    guard #available(iOS 16.2, *),
          let activity = MeltdownLiveActivityStore.current,
          let state = makeState(label, colorHex, heartRate, rmssdRatio, isPaused)
            as? MeltdownActivityAttributes.ContentState else { return }

    Task {
        await activity.update(.init(state: state, staleDate: nil))
    }
}

@_cdecl("mm_live_activity_end")
public func mm_live_activity_end() {
    guard #available(iOS 16.2, *), let activity = MeltdownLiveActivityStore.current else { return }
    MeltdownLiveActivityStore.current = nil

    Task {
        await activity.end(nil, dismissalPolicy: .immediate)
    }
}
