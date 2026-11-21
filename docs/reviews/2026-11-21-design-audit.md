# Desk Pal design audit, 2026-11-21

Static review of `src/DeskPal` and `src/DeskPal.Core` against the Portal design guide (sections 2, 3, 4.3, 4.4, 4.5, 4.11, 7, 8). The app was not launched. The pet's own art and rendering are exempt.

## Owner decisions

| Decision | Result |
|---|---|
| Follow the Windows accent | Pass. No hard-coded brand accent. Chrome uses `AccentFillColorDefaultBrush`, `AccentButtonStyle`, `TextOnAccent...`. The only literal colours are the pet customiser swatches (content) and neutral alpha strokes (`CustomisePanel.cs:212`, `QuickPanelWindow.xaml.cs:114`). |
| Pro mark gradient | Gap. `Pro.cs:62-76` draws the Pro pill as a solid accent fill. No violet/pink/orange gradient anywhere. |
| Trial is 30 days | Gap. It is 7 days (see "Trial locations"). |
| Tier word "Pro" | Pass. UI strings say "Pro" only (no "Premium" or "Plus"). Code comments say "Evolution", which is internal. |
| Cards radius 8, 20 only in tray pop-ups | Mostly pass. Cards use 8 (`HomePage.xaml:35`, `Pro.cs:62`) or `OverlayCornerRadius` (8). Gap: `TrayMenuWindow.xaml.cs:155` rows use 6 (controls are 4). No tile-20 pop-up exists. |
| Every visual default adjustable in Appearance | Gap. See G2. |

## Pre-build checklist (guide section 8)

| # | Row | Status | Note |
|---|---|---|---|
| 1 | Visual changes seen on the running app | Needs live check | |
| 2 | Light/dark/high contrast, 100/150/200 %, 200 % text, materials | Needs live check | Static: no material choice exists (G2), no high-contrast branch in custom code. |
| 3 | Empty/loading/error/long-name states | Needs live check | |
| 4 | No unexplained truncation | Gap (minor) | `NudgeCardWindow.xaml:23`, `EyeCuePillWindow.xaml:10`, `ApprovalCardWindow.cs:20` use `CharacterEllipsis` with no way to see the full text. `QuickPanelWindow.xaml:26` caps at 2 lines. |
| 5 | Keyboard and Narrator walk | Needs live check | Static: Esc is handled only in `TrayPanelHost.cs:196-200` (G5). Tray menu rows set automation names (`TrayMenuWindow.xaml.cs:157`). |
| 6 | One accent primary per surface; accent never status | Gap (minor) | `ProBadge` is a filled accent pill (`Pro.cs:62`) next to `AccentButtonStyle` buttons (`ProPage.cs:73`, `WelcomeWizardWindow.cs:36`). |
| 7 | Commands show keys, palette, no needless modal | Gap | No palette, no `KeyboardAccelerator` or access keys anywhere, no `Ctrl+,` or `Ctrl+1..9` (G5). |
| 8 | Undo for recoverable destructive acts | Not relevant | No user-file deletes in UI code. Remove in Settings (`SettingsPage.xaml.cs:210`) only drops an approved-app entry. |
| 9 | Destructive-code gate | Not relevant | Audit only, no code changed. |
| 10 | File-change badge or preview | Not relevant | The app does not change user files. |
| 11 | Strings in resources; pseudo-loc; RTL | Pass / needs live check | No literal `Text =`/`Content =` strings found; all UI text uses `Loc.Get`; 10 locales. Pseudo-loc and RTL need a live check. |
| 12 | Free/Pro: core free, pill, one word, no upsell | Pass, one gap | Core is free (`ProLicence.cs:3-6`), Pro pill on Pro items, no upsell in front of a task. Gaps in trial wording (G1) and lifecycle (G7). |
| 13 | One screen at launch; What's new updated | Gap | No What's new surface exists (G8). Launch screen count needs a live check. |
| 14 | Tray pop-up: inward, whole tiles, Esc/focus loss | Partial | Esc and focus-loss watch (100 ms) exist (`TrayPanelHost.cs:177-200`); placement is edge-aware. No tiles, edit mode or resize (G11). Edge behaviour needs a live check. |
| 15 | Hover/press <= 83 ms; nothing > 333 ms; reduced motion | Gap | `EyeCuePillWindow.xaml.cs:31` fades over 700 ms. Nudge in/out 200 ms and Home 150 ms are within limits. No Windows animation setting is read anywhere (G3). |
| 16 | Test windows off-screen and closed | Not relevant | Process rule, not a design item. |

## Other guide rows checked

| Area | Status | Note |
|---|---|---|
| 4.1 Title bar, placement | Needs live check | Window position and size are stored (`Settings.cs:158-161`). Title-bar extension not verified. |
| 4.2 Navigation | Pass | `NavigationView` left pane, Settings built in, 5 items plus Pro and About in footer (`MainWindow.cs:39-47`). No `Ctrl+,`. |
| 4.3 Settings layout | Gap | One long page with 7 sections (guide: left list above 6); no search; no per-section reset (one reset-all at `SettingsPage.xaml.cs:85`, one per-card link at `:292`); no profile export. Cards use 8 (pass). |
| 4.5 Welcome | Gap | `WelcomeWizardWindow.cs`: 3 steps, Back/Next only. No "Skip for now", "Start with recommended settings", Esc, "try it now" step, tray-icon location or trial info. |
| 4.6 About | Gap | Version only (`MainPages.cs:235-253`). No What's new, feedback, privacy link, data folder, licence state in words; no rate/suggest card. |
| 4.11 Offer | Gap | Offer is a page, not a right panel (acceptable here). Missing: "No card needed. The trial starts when you press the button" line, footer chip in the last 7 days, toast before the end, expiry summary. |
| Type ramp | Gap (minor) | 11 px below the 12 px floor: `Pro.cs:68`, `MainPages.cs:56`, `QuickPanelWindow.xaml.cs:116`, `NudgeCardWindow.xaml:45`. 15 px off-ramp: `QuickPanelWindow.xaml:25`. Fixed sizes ignore any in-app text step (none exists). |
| Materials | Partial | Mica on windows (`MainWindow.cs:35`, `WelcomeWizardWindow.cs:32`); thin acrylic on nudge and pill (`CardChrome.cs:50`). No Solid fallback for transparency off, battery saver or high contrast found. |
| Tray right-click menu | Pass | State first (`TrayMenuWindow.xaml:18-21`), rows 36 px. |

## Trial locations (7 days)

- `src/DeskPal.Core/ProLicence.cs:43`: `public const int TrialDays = 7;` is the single source of truth (used at `:59` and `:61`).
- `src/DeskPal.Core/ProLicence.cs:3`: header comment "one-time 7-day trial".
- Strings: `Pro_TryFree` (en-US `:858`, "Try Pro free for 7 days") and `Pro_Msg_TrialStarted` (en-US `:874`, "Your 7-day Pro trial has started") in all 10 locales (de, es, fr, it, nl, pt-BR at `:858`/`:874`; ja, ko, pl at `:880`/`:896`).
- Comment "{0} = days left in the trial (2 to 7)" on `Pro_Status_Trial` in all locales (`:828`; ja/ko/pl `:850`).
- Not trial, leave alone: "7 days of history" free limit (`Resources.resw:855`, `ProLicence.cs:6`) and "Last 7 days" (`:107`).
- Check `tests/DeskPal.Core.Tests` for assertions on 7 before changing.
- The Store add-on's trial setting in Partner Center must also match 30 days if it is configured there.

## Ranked gaps

1. **G1 Trial is 7 days, not 30.** Guide 4.11, section 7.3. Evidence in "Trial locations". Fix: `TrialDays = 30`, update the comment, the two strings in every locale (`Pro_TryFree`, `Pro_Msg_TrialStarted`) and the "2 to 7" comments, update tests; add "No card needed. The trial starts when you press the button, not before." Size S (M with translations).
2. **G2 Settings > Appearance lacks the required controls.** Guide 4.3, principle 11. Evidence: `SettingsPage.xaml.cs:97-111` offers only nudge style, hide time, avatar style, tray animation and sounds; `Settings.cs` has no theme, accent, material, corners, density, text size, motion or blur fields. Fix: add the full Appearance group with live preview and "Reset appearance" (theme via `RequestedTheme`, accent System or presets, material Mica/Acrylic/Solid, corners, density, text step, motion, nudge-card blur). Size L.
3. **G3 Reduced motion is not honoured.** Guide section 3 motion rules, checklist row 15. Evidence: `UISettings.AnimationsEnabled` is never read; `EyeCuePillWindow.xaml.cs:31` fades over 700 ms; other animations at `NudgeCardWindow.xaml.cs:203,277`, `HomePage.xaml.cs:188`. Fix: one motion helper (Follow Windows / Reduced / Off) used by all storyboards, fades <= 100 ms when off, cap the pill fade at 333 ms. Size M.
4. **G4 Pro mark is a solid accent pill, not the shared gradient.** Guide section 3 (Pro mark), 4.11, decision 7.2. Evidence: `Pro.cs:62-76`; `ProPage.cs:73` makes Buy the accent button. Fix: pill with accent text on a card fill; "Get Pro" button with a 1.5 px `#7B5CFF` to `#E0457B` (55 %) to `#FF9A3D` gradient border, once per screen. Size S.
5. **G5 Keyboard and Esc coverage is thin.** Guide 4.5, 4.9, section 8 rows 5 and 7. Evidence: only `TrayPanelHost.cs:196` handles Esc; no accelerators anywhere. Fix: Esc and Enter in the welcome window, approval card, language-keep window and quick panel; `Ctrl+,` for Settings and `Ctrl+1..7` for pages in `MainWindow.cs`; keys in tooltips. Size M.
6. **G6 Welcome window misses the standard pattern.** Guide 4.5. Evidence: `WelcomeWizardWindow.cs:36-52`. Fix: add "Skip for now" and "Start with recommended settings", apply choices as made so Skip keeps them, a "try it now" step, tray-icon location and trial info, Esc = skip. Size M.
7. **G7 Trial lifecycle UI is thin.** Guide 4.11, 4.6. Evidence: trial days appear only on the Pro page (`ProPage.cs:85-87`). Fix: licence state in words on About, footer chip in the last 7 days, one toast before the end, one expiry summary of what stopped and what stays free. Size M.
8. **G8 About has no What's new, feedback, privacy or data-folder links.** Guide 4.5, 4.6. Evidence: `MainPages.cs:235-253`. Fix: per-release JSON with 3-4 cards shown once after an update and from About; add feedback, privacy and "Open data folder" links. Size M.
9. **G9 Settings is one long page with no search or per-section reset.** Guide 4.3. Evidence: `SettingsPage.xaml.cs:45-128`. Fix: left section list, settings search, per-section reset, 720 px max form width. Size M.
10. **G10 Small token deviations.** Evidence: font sizes under the floor or off-ramp (see Type ramp row); tray menu row radius 6 instead of 4 (`TrayMenuWindow.xaml.cs:155`); ellipsis without a full-value path (`NudgeCardWindow.xaml:23`, `EyeCuePillWindow.xaml:10`, `ApprovalCardWindow.cs:20`); no Solid or high-contrast fallback for acrylic (`CardChrome.cs:56-66`). Fix: map to ramp tokens, radius 4, tooltip with full text, fallback brush. Size S.
11. **G11 Tray pop-up is a menu plus quick panel, not the 4.4 tile pop-up.** Guide 4.4. Evidence: `TrayMenuWindow.xaml`, `QuickPanelWindow.xaml`. A state-first header exists; radius-20 tiles, edit mode and resize are absent. Optional. Size L.
