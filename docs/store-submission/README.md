# App Store / TestFlight submission bundle

Reference material for App Store Connect submissions of MeltdownMonitor for
iOS. Lives here so a future submission run doesn't have to re-derive any of
the questionnaire copy, privacy answers, or screenshot plan — bumping the
TestFlight build is `git tag ios-vX.Y.Z && git push --tags`, and the
`ios-release` workflow (`.github/workflows/ios.yml`) takes it from there.

## Contents

- `disclaimer.md` — first-run disclaimer and privacy copy, kept in sync with
  `MeltdownMonitor.Mobile/ViewModels/DisclaimerViewModel.cs`. This is the
  text App Review will see on first launch and the wording referenced in
  the app's marketing description.
- `privacy-nutrition.md` — answers for the App Store Connect "Privacy
  Nutrition" questionnaire. Drives the on-store privacy label.
- `screenshots.md` — required screenshot sizes, the in-app surfaces each
  one captures, and how to take them from the simulator artifact produced
  by CI.

## Workflow

1. Cut a release tag matching `ios-v*` (e.g. `ios-v1.0.0`).
2. The `iOS Workflow` GitHub Actions run archives an `.ipa` and uploads it
   to TestFlight via `xcrun altool`. This step is skipped — with the
   workflow still green — when the `IOS_SIGNING_AVAILABLE` secret is
   absent, so contributors without the Apple Developer Program key can
   still get build & test coverage.
3. In App Store Connect, attach the screenshots from `screenshots.md` and
   paste the marketing copy from `disclaimer.md` (long description) when
   promoting from TestFlight to public release.

## Required repository secrets (TestFlight only)

These are set on the `ios-release` environment so PRs from forks cannot
read them.

| Secret | What it is |
|---|---|
| `IOS_SIGNING_AVAILABLE` | `true` to enable the TestFlight job. Acts as the master gate. |
| `IOS_CERT_P12_BASE64` | Distribution `.p12` certificate, base64-encoded. |
| `IOS_CERT_P12_PASSWORD` | Password for the `.p12`. |
| `IOS_KEYCHAIN_PASSWORD` | Throwaway password for the ephemeral CI keychain. |
| `IOS_PROVISIONING_PROFILE_BASE64` | App Store distribution profile, base64. |
| `IOS_CODESIGN_KEY` | Certificate common name (e.g. `Apple Distribution: …`). |
| `IOS_PROVISION_NAME` | Provisioning profile name (matches the profile installed above). |
| `APP_STORE_CONNECT_KEY_BASE64` | App Store Connect API key `.p8`, base64. |
| `APP_STORE_CONNECT_KEY_ID` | The 10-char key identifier. |
| `APP_STORE_CONNECT_ISSUER_ID` | App Store Connect issuer UUID. |

Resolves §13 question 4 ("paid developer account?"): the workflow assumes
the maintainer is enrolled in the Apple Developer Program. Without the
secrets above, the iOS job stays at build & test only.
