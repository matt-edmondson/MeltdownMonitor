# Getting MeltdownMonitor onto an iPhone — TestFlight runbook

> **Status — 2026-05-31:** The repo is **TestFlight-ready**. The macOS CI build
> on **PR #45** is green, which verified the icon/asset-catalog/Info.plist
> wiring that can't be compiled on Linux. **Phase B (repo changes) is done.**
> Your remaining work is **A → C → D → E**, all of which need your Apple/GitHub
> accounts and can't be automated from here.

The repo can't be built for iOS on Linux (Xcode is macOS-only). This runbook
uses the **existing `.github/workflows/ios.yml` `testflight` job** to build on
GitHub's macOS runners and ship to TestFlight — **no local Mac required** —
using a **paid Apple Developer Program** account. Internal TestFlight testing
has **no Beta App Review**, so a build can reach your phone the same day.

> External TestFlight testers *do* require Beta App Review (a day or more).
> Stay on the **internal** track for same-day testing.

---

## ✅ Your action checklist (this is all that's left)

- [ ] **A.** Apple portal — App ID + HealthKit, distribution cert, provisioning
  profile, App Store Connect app record + API key, add yourself as an internal tester
- [ ] **C.** GitHub secrets — `IOS_SIGNING_AVAILABLE` (repo) + 9 signing secrets (`ios-release` environment)
- [ ] **D.** Merge PR #45, then push the `ios-v1.0.0` tag to trigger the build/upload
- [ ] **E.** Install via TestFlight on the iPhone and verify the live RR stream

Already done for you (no action needed): **Phase B** — see the checked list below.

---

## Pipeline at a glance

```
You, in Apple's portals        Repo (✅ DONE)               You, in GitHub          CI (Mac runner)
─────────────────────────      ────────────────             ──────────────         ─────────────────
App ID + HealthKit         ┐    AppIcon.appiconset      ┐    10 secrets        ┐    workload install
Distribution cert (.p12)   ├──▶ ITSAppUsesNonExempt...  ├──▶ create env        ├──▶ sign + archive .ipa
App Store profile          │    Xcode pin in CI job     │    push tag ios-v*   │    altool → TestFlight
ASC app record + API key   ┘    modern launch screen    ┘                      ┘    (~15–25 min)
                                                                                         │
                                                                          App Store Connect processing
                                                                          (~10–30 min) ─▶ TestFlight app
                                                                                            on your iPhone
```

Key facts from the project: bundle ID **`com.thethreethousands.meltdownmonitor`**,
**iOS 17.0+**, iPhone-only, HealthKit entitlement enabled, BLE background modes
set (no special provisioning needed).

---

## ⬜ Phase A — Apple side (your action; ~45 min)

At <https://developer.apple.com> and <https://appstoreconnect.apple.com>:

- [ ] **1. App ID** — Identifiers → **+** → App IDs → App. Explicit Bundle ID
  `com.thethreethousands.meltdownmonitor`. Enable the **HealthKit** capability
  (the app's `Entitlements.plist` declares it, so the profile must grant it).
- [ ] **2. Distribution certificate — no Mac needed**, via OpenSSL:
  ```bash
  openssl genrsa -out dist.key 2048
  openssl req -new -key dist.key -out dist.csr \
    -subj "/CN=MeltdownMonitor Distribution/emailAddress=matt@thethreethousands.com/C=US"
  # Portal → Certificates → + → "Apple Distribution" → upload dist.csr → download distribution.cer
  openssl x509 -inform DER -in distribution.cer -out dist.pem
  openssl pkcs12 -export -inkey dist.key -in dist.pem -out dist.p12 \
    -name "Apple Distribution" -passout pass:CHOOSE_A_PASSWORD
  # Read the exact name for IOS_CODESIGN_KEY (the certificate Common Name):
  openssl pkcs12 -in dist.p12 -nodes -passin pass:CHOOSE_A_PASSWORD | openssl x509 -noout -subject
  #   → e.g. Apple Distribution: The Three Thousands (TEAMID)
  ```
- [ ] **3. Provisioning profile** — Profiles → **+** → **App Store** distribution
  → select the App ID + the distribution cert → name it e.g.
  `MeltdownMonitor App Store` → download the `.mobileprovision`. *(That name
  becomes `IOS_PROVISION_NAME`.)*
- [ ] **4. App Store Connect → app record** — My Apps → **+** → New App → iOS,
  the bundle ID, a name, SKU, primary language.
- [ ] **5. App Store Connect → API key** — Users and Access → Integrations → App
  Store Connect API → generate a key with **App Manager** access. **Download the
  `.p8` once** (can't be re-downloaded); note the **Key ID** and **Issuer ID**.
- [ ] **6. Add yourself as an internal tester** — your app → TestFlight →
  Internal Testing → new group → add your Apple ID. No review; the build arrives
  minutes after it finishes processing.

---

## ✅ Phase B — Repo changes (DONE — PR #45, verified green on macOS CI)

These were the gaps that would otherwise have failed the upload or made the app
render wrong on device. The macOS `build-test` job on PR #45 compiled them
successfully, which is the verification that couldn't be done on Linux:

- [x] **App icon** — `MeltdownMonitor.iOS/Assets.xcassets/AppIcon.appiconset/`
  with a full-bleed, opaque **1024×1024** rendered from
  `assets/branding/app-icon.svg`, designated via `XSAppIconAssets` in
  `Info.plist`. CI confirmed the .NET iOS SDK auto-includes the `.xcassets`
  (no csproj change needed, no duplicate-item error).
- [x] **`ITSAppUsesNonExemptEncryption = false`** in `Info.plist` — skips the
  per-build export-compliance prompt (the app uses only exempt crypto).
- [x] **Modern launch screen** — replaced the dangling `UILaunchStoryboardName`
  (which referenced a nonexistent storyboard and would letterbox the app) with
  an empty `UILaunchScreen` dict for full-screen native layout.
- [x] **CI toolchain pin** — the `testflight` job now selects **Xcode 26.0** to
  match the `build-test` job (the toolchain PR CI validates) instead of "latest".
- [x] **This runbook.**

HealthKit entitlement is **kept** — the paid account signs it fine.

---

## ⬜ Phase C — GitHub secrets (your action; ~15 min)

> **Critical:** the `check-secrets` job runs on Ubuntu with **no** environment,
> so `IOS_SIGNING_AVAILABLE` must be a **repository** secret. The `testflight`
> job uses `environment: ios-release`, so the signing secrets go in a GitHub
> **Environment** named exactly `ios-release` (Settings → Environments → New).

- [ ] **Add the repository secret** (Settings → Secrets and variables → Actions):

  | Secret | Value |
  |---|---|
  | `IOS_SIGNING_AVAILABLE` | `true` |

- [ ] **Create the `ios-release` environment and add its 9 secrets:**

  | Secret | Value |
  |---|---|
  | `IOS_CERT_P12_BASE64` | `base64 -w0 dist.p12` |
  | `IOS_CERT_P12_PASSWORD` | the `.p12` password you chose |
  | `IOS_KEYCHAIN_PASSWORD` | any throwaway string |
  | `IOS_PROVISIONING_PROFILE_BASE64` | `base64 -w0 profile.mobileprovision` |
  | `IOS_CODESIGN_KEY` | the cert CN, e.g. `Apple Distribution: The Three Thousands (TEAMID)` |
  | `IOS_PROVISION_NAME` | the profile name, e.g. `MeltdownMonitor App Store` |
  | `APP_STORE_CONNECT_KEY_BASE64` | `base64 -w0 AuthKey_XXXXXX.p8` |
  | `APP_STORE_CONNECT_KEY_ID` | the Key ID |
  | `APP_STORE_CONNECT_ISSUER_ID` | the Issuer ID |

(`base64 -w0` is Linux; on Windows use `certutil -encode` or PowerShell. None of
these are committed — paste directly into GitHub.)

---

## ⬜ Phase D — Ship it (your action)

The `testflight` job is gated on a **tag matching `ios-v*`** *and*
`IOS_SIGNING_AVAILABLE == 'true'`. Once Phase C is set:

- [ ] **Merge PR #45.**
- [ ] **Tag and push:**
  ```bash
  git tag ios-v1.0.0
  git push origin ios-v1.0.0
  ```
  That fires `ios.yml`: `build-test` (the canary) + `testflight` (installs the
  iOS workload, signs, archives the `.ipa`, and `altool` uploads to TestFlight).

---

## ⬜ Phase E — On your iPhone (your action)

- [ ] **1.** Install **TestFlight** from the App Store; sign in with the **same
  Apple ID** that you added as an internal tester.
- [ ] **2.** After the build finishes *Processing* in App Store Connect
  (~10–30 min; the first one can take longer), open TestFlight → **Install**.
- [ ] **3.** Launch → accept the first-run **disclaimer gate** → grant
  **Bluetooth** (and **HealthKit** when prompted) → pair the **Polar H10 /
  Verity Sense** → confirm the RR stream and that the regulation field moves.

---

## Known limitations / gotchas

- **Live Activity won't appear.** The Lock-Screen widget is Swift, built in
  Xcode, and is *not* part of the .NET solution (README), so the TestFlight
  build omits it. Everything else works: BLE, HRV, baseline, detection, alerts,
  notifications, history, HealthKit.
- **Internal only today.** External TestFlight needs Beta App Review.
- **First processing is the slow part**, not the build.
- **Build expiry:** TestFlight builds last 90 days.
