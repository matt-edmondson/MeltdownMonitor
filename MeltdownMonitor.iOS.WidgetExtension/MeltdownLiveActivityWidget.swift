// SwiftUI presentation for the MeltdownMonitor Live Activity — Lock Screen
// banner, Dynamic Island expanded/compact/minimal. Lives in a native widget
// extension target (MeltdownMonitor.iOS.WidgetExtension); the .NET build does
// not compile it. See docs/live-activity.md for how to add the target in Xcode.

import ActivityKit
import SwiftUI
import WidgetKit

// MeltdownActivityAttributes.swift is shared with the app target (add it to
// both targets' membership in Xcode), so the model isn't redeclared here.

@available(iOS 16.2, *)
private extension Color {
    /// Parse a "#RRGGBB" string produced by StateColors.HexFor (C#). Falls back
    /// to grey so a malformed string never renders as an invisible swatch.
    init(hex: String) {
        let trimmed = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        var value: UInt64 = 0
        guard Scanner(string: trimmed).scanHexInt64(&value), trimmed.count == 6 else {
            self = Color(.sRGB, red: 0.56, green: 0.56, blue: 0.58)
            return
        }
        self = Color(
            .sRGB,
            red: Double((value >> 16) & 0xFF) / 255.0,
            green: Double((value >> 8) & 0xFF) / 255.0,
            blue: Double(value & 0xFF) / 255.0)
    }
}

@available(iOS 16.2, *)
private struct BalanceGlyph: View {
    let ratio: Double

    // ratio >= 1 means RMSSD at/above baseline (calmer); < 1 is suppressed.
    private var symbol: String {
        switch ratio {
        case ..<0.6: return "arrow.down.heart.fill"
        case ..<0.85: return "arrow.down.heart"
        case ...1.15: return "heart.fill"
        default: return "arrow.up.heart.fill"
        }
    }

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 16, weight: .semibold))
    }
}

@available(iOS 16.2, *)
private struct LockScreenView: View {
    let state: MeltdownActivityAttributes.ContentState

    var body: some View {
        HStack(spacing: 14) {
            Circle()
                .fill(Color(hex: state.colorHex))
                .frame(width: 14, height: 14)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(state.label)
                    .font(.headline)
                Text(state.heartRate > 0 ? "\(state.heartRate) bpm" : "— bpm")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            BalanceGlyph(ratio: state.rmssdRatio)
                .foregroundStyle(Color(hex: state.colorHex))
        }
        .padding(16)
        .activityBackgroundTint(Color.black.opacity(0.4))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(state.label), \(state.heartRate) beats per minute")
    }
}

@available(iOS 16.2, *)
struct MeltdownLiveActivityWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: MeltdownActivityAttributes.self) { context in
            LockScreenView(state: context.state)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Circle()
                        .fill(Color(hex: context.state.colorHex))
                        .frame(width: 12, height: 12)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.state.label).font(.headline)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(context.state.heartRate > 0 ? "\(context.state.heartRate) bpm" : "—")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    BalanceGlyph(ratio: context.state.rmssdRatio)
                        .foregroundStyle(Color(hex: context.state.colorHex))
                }
            } compactLeading: {
                Circle()
                    .fill(Color(hex: context.state.colorHex))
                    .frame(width: 10, height: 10)
            } compactTrailing: {
                BalanceGlyph(ratio: context.state.rmssdRatio)
                    .foregroundStyle(Color(hex: context.state.colorHex))
            } minimal: {
                Circle()
                    .fill(Color(hex: context.state.colorHex))
                    .frame(width: 10, height: 10)
            }
        }
    }
}
