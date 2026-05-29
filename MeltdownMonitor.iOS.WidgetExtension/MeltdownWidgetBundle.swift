// Entry point for the widget extension. Registers the Live Activity widget.
// Native Xcode-managed target — not compiled by the .NET build.

import SwiftUI
import WidgetKit

@main
@available(iOS 16.2, *)
struct MeltdownWidgetBundle: WidgetBundle {
    var body: some Widget {
        MeltdownLiveActivityWidget()
    }
}
