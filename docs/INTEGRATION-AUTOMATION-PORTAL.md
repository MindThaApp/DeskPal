# Desk Pal x Automation Portal: integration plan

## 1. Verdict

**Worth it, but as a step 5b item (about 3–4 days Desk Pal, 2–3 days AP), not earlier.** The verbs it exposes (focus, pause) don't exist until step 5, and an integration on a half-built scheduler would freeze the wrong contract.

- **Step 1: one piece of pre-investment, no transport.** Build the scheduler around holds (leases): `{source, reason, until, seated}`. Toast Snooze, full-screen, Focus Assist, Pomodoro and a later external "pause" are all the same primitive. Route every tray, toast and card button through one internal command router, and raise internal events.
- **Step 3:** define "away" once (see Pitfalls), including seated holds.
- **Step 5b:** pipe server, `dp.exe`, `deskpal://`, the settings page and templates. Do it before the first MSIX submission so the protocol and alias declarations go through certification once.
- **AP side** ships after Desk Pal's portable beta has the pipe, hidden unless Desk Pal is detected (AP's existing pattern).

The biggest gain is not the AP team's list. AP knows about calls, calendar and screen sharing, which Desk Pal can't detect cheaply, and those are the moments a wellbeing app is most annoying or embarrassing.

## 2. Integration contract v1

**Use the pipe for machines and the URI for humans.** This deliberately differs from the Power Plan Manager pattern (URI plus status pipe): a URI has no caller identity, so it must never change state silently. AP's action group talks to the pipe.

### Pipe `DeskPal-<user SID>`
- Same conventions as AP: `PipeOptions.CurrentUserOnly`, one UTF-8 JSON line in and one out, camelCase.
- Copy AP's `InstanceNames.For(dataRoot)` suffix trick so test and beta data roots never talk to the real instance.
- Request: `{"v":1,"cmd":"pause","args":{"minutes":60,"source":"call","reason":"In a call"}}`
- Reply: `{"ok":true,"result":"held","status":{...}}`. Every reply carries fresh status.
- Errors: `{"ok":false,"error":"not-allowed|bad-args|unknown-command|busy|rate-limited"}`.

| cmd | args (defaults, limits) | result | notes |
|---|---|---|---|
| `ping` | none | `{app:"DeskPal",version,contract:1}` | always allowed |
| `status` | none | status JSON | always allowed, read-only |
| `pause` | `minutes` (30, 1–240), `source` (`"manual"`), `reason` (40 chars max) | `held` | creates or renews the hold for that `source`; expires by itself |
| `resume` | `source`, or `all:true` | `resumed` or `still-held` | releases only that source's hold |
| `focus.start` | `minutes` (25, 5–180) | `started` | same as the tray Pomodoro |
| `focus.stop` | none | `stopped` | triggers the normal merged catch-up |
| `water` | `ml` (250, 50–1000) | `logged` | at most 12 external logs a day; stored with `source`; undoable in Today |
| `nudge` | `kind` (`stretch`, `eyes`, `water`, `walk`, `breathe`), `when` (`now` or `soon`), `reason` | `shown`, `queued`, `merged`, `held` or `off-hours` | goes through the scheduler, never stacks; 1 per 5 min |
| `cheer` (v1.1) | `text` (60 chars max) | `shown` | Sprout or Bloop reaction; cosmetic, no streak credit; 1 per 10 min |

- **No `snooze` verb.** "Snooze nudges" in AP's editor maps to `pause`. One concept is easier to keep stable.
- **Reserved sources `call`, `meeting`, `presenting` are seated holds.** The sit timer keeps counting and no-input time is not credited as a break. Without this, a 60-minute call with no typing is recorded as a 60-minute break.
- **Leases beat a global pause.** Overlapping causes (a call inside a calendar block) don't cancel each other, and a crashed AP can't leave nudges off forever.

### Status JSON (additive only; `contract` bumps on breaking change)
```json
{ "contract":1, "app":"DeskPal", "version":"0.5.0", "edition":"portable|store",
  "allowExternalControl":true,
  "state":"active|break|paused|focus|dnd|away|offHours", "stateSince":"2026-11-11T10:42:00+01:00",
  "holds":[{"source":"call","reason":"In a call","until":"2026-11-11T11:30:00+01:00","seated":true}],
  "focus":{"active":true,"phase":"work|rest","round":2,"endsAt":"..."},
  "nextNudge":{"kind":"stretch","dueAt":"...","held":true},
  "today":{"waterMl":1250,"waterGoalMl":2000,"caffeineMg":95,"stretches":4,"eyeBreaks":6,
           "walks":1,"focusMinutes":50,"sittingMinutes":47},
  "streakDays":12, "sprout":{"mood":"happy","stage":"young"} }
```
- State precedence: `offHours` > `away` > `dnd` > `focus` > `paused` > `break` > `active`.
- `sittingMinutes` is the most useful AP condition ("if sitting more than 50").

### CLI `dp.exe`
A thin pipe client next to `DeskPal.exe`; in MSIX it is an AppExecutionAlias `dp`, mirroring `ap`.
```
dp status [--json]
dp pause [--minutes N] [--source id] [--reason text]
dp resume [--source id | --all]
dp focus start [--minutes N] | dp focus stop
dp water [--ml N]
dp nudge <kind> [--soon] [--reason text]
dp open [today|hydration|stats|sprout|settings]
```
- Exit codes: 0 done, 2 bad arguments, 3 Desk Pal not running, 4 not allowed, 5 busy or rate-limited.
- Only `dp open` starts the app. It doubles as the test harness.

### URI `deskpal://`
Use `deskpal`, not `desk-pal`, to match `automationportal`.
- `deskpal://open?page=today|hydration|stats|sprout|settings|connect` is the only verb that acts without a click.
- `deskpal://water?ml=250`, `deskpal://pause?minutes=30`, `deskpal://focus?minutes=25` and `deskpal://nudge?kind=stretch` open a Bloop confirm card with the action pre-filled and one button. They need a click every time, with no "always allow" option.
- Copy AP's `LaunchArguments.Trusted` rule: on a link launch, honour only the first `deskpal:` argument and ignore every other switch. Windows passes links unescaped, so a quote can inject arguments.

### Event → shortcut mapping
- **Where:** Desk Pal Settings › Automation Portal. One row per event with a dropdown filled from AP's pipe `list`, default "None", plus a Test button. Store the shortcut **id** and show the name, so renames don't break it.
- **Events in v1:** `focus.started`, `focus.ended`, `break.started` (kind), `break.ended`, `goal.reached` (hydration), `day.summary`. In v1.1: `sprout.changed`.
- **How it runs:** Desk Pal sends `run` on `AutomationPortal-<SID>`, fire-and-forget.
  - This avoids a console flash, doesn't depend on the `ap` alias (users can switch aliases off), and skips the per-link allow prompt.
  - If AP isn't running, skip and count it ("3 events skipped today"). Never start AP in the background.
  - AP must document `ping|list|run|import` as a frozen sibling contract and offer a no-wait run on the pipe. Until then, fall back to `ap run "<id>" --no-wait --input ...` with no window.
- **Input:** a plain sentence by default ("Focus ended after 25 min, round 2"), usable directly in SpeakText, notes or a spreadsheet row. A per-row toggle "Send details as JSON" sends `{"event":"focus.ended","at":"...","minutes":25,"round":2,"today":{...}}`. Structured data is otherwise available through AP's "Get Desk Pal status" action.
- **Loop guard:** events caused by an external command are tagged `cause:"external"` and don't fire mappings. Each event fires at most once per 60 s.

### Security and abuse
- **Confirm once per calling app, on the pipe.**
  - On the first state-changing command, Desk Pal resolves the caller (`GetNamedPipeClientProcessId`, then package family or exe path).
  - Bloop asks once: "Automation Portal wants to pause nudges and log water. Allow / Not now".
  - The answer is remembered per package family, or per exe path when unpackaged, and can be revoked in Settings.
  - `allowExternalControl` in status lets AP show "Turn on ... in Desk Pal" instead of failing blindly, as `PpmPipe.EnsureLinksAllowedAsync` does.
- **Nothing is silent.** An active hold changes the tray icon and tooltip ("Held by Automation Portal: In a call, until 11:30"), and the day summary reports "nudges held 1 h 40". There is no extra toast.
- **Caps.** Pause is 240 minutes at most and always expires. External water is limited by count and volume, tagged and undoable, so links and scripts can't farm streaks or Sprout growth. `reason` and `text` are length-capped and shown as plain text.
- **AP trusts the server** the same way it does for Power Plan Manager (`IsTrustedServer`: package family or WindowsApps path). It should accept the unpackaged beta, labelled "(portable)", and parse replies strictly with a size cap.

### Detection and graceful absence
- **Running:** pipe `ping` with a 150 ms connect timeout. This is the only check needed for actions.
- **Installed but not running:** `Launcher.FindUriSchemeHandlersAsync("automationportal" / "deskpal")`. It covers both Store and unpackaged registrations and returns the handler's package family. Don't probe `HKCU\Software\Classes` alone, because it misses packaged apps.
- **Absent:**
  - Desk Pal's settings page shows a single "Get Automation Portal" card (a Store link opened by the user's click) and no dead dropdowns.
  - AP hides the Desk Pal group in the picker.
  - Existing shortcuts fail with "Desk Pal isn't running. Open it and try again."

## 3. Feature ideas, ranked

| # | Scenario | Value | Effort | Builds |
|---|---|---|---|---|
| 1 | **Meeting-aware hold:** CallStarted → `pause source=call`; CallEnded → `resume` | Removes the worst annoyance | S | DP verbs, AP template |
| 2 | **Screen-share guard:** PowerPoint slideshow, a sharing toolbar or OBS in focus → `pause source=presenting`. Desk Pal's full-screen check misses windowed sharing | Stops Bloop appearing on a shared screen | S | AP template |
| 3 | **"That was a 50-minute call. Stretch?"** `nudge stretch --soon` on CallEnded, with a seated hold so sitting time is correct | Best-timed nudge in the product | S | DP |
| 4 | **Focus drives the PC:** `focus.started` runs a shortcut (mute, close chat apps, power plan); `focus.ended` restores | Pomodoro that actually protects focus | S | DP events |
| 5 | **"Desk Pal is in focus / on break / sitting > N" condition** plus a "Get Desk Pal status" action | Other shortcuts stop interrupting | M | AP |
| 6 | **Pre-meeting top-up:** CalendarEventSoon (10 min) → `nudge water` "Fill your glass before your next meeting" | Useful and non-naggy | S | AP template |
| 7 | **Sunny lunch walk:** weekdays 12:15, if weather is good and `today.walks == 0` → `nudge walk` "14° and dry, 10 minutes outside?" | Delight, daylight | M | AP (needs weather read) |
| 8 | **Water hotkey:** KeyShortcut, Stream Deck or NFC → `water 250`; Sprout perks up | No-friction logging | S | AP template |
| 9 | **Headphones on = focus:** Bluetooth headset connects → `focus.start` | Delight, no UI | S | AP template |
| 10 | **Sprout cheers your automations:** a long chain finishing ("Backup done") → `cheer` | Delight, cross-app personality | M | DP animation |
| 11 | **Walk means walk:** `break.started kind=walk` → pause music and lock the PC after 30 s (opt-in) | Makes the walk happen | S | AP template |
| 12 | **Wind-down ritual:** `day.summary` → close work apps, read out tomorrow's first event | Clean end of day | S | AP template |
| 13 | **Day log:** `day.summary` → spreadsheet row or note (water, stretches, walks, focus minutes) | Self-tracking users | S | AP template |
| 14 | **Office or home mode:** WifiJoined or a dock via UsbDevice → `mode quiet|normal` (toasts only at the office) | Context fit | M, v2 | Both |
| 15 | **Sprout milestones:** `sprout.changed` to bloom → wallpaper change or spoken "Sprout bloomed" | Delight | S, v1.1 | DP event |

Don't build Lock or Unlock as an integration. Desk Pal should take session lock natively (WTS session notifications) as "away immediately".

## 4. Bundled starter shortcuts

Desk Pal sends the template text over AP's pipe `import`, and AP shows its own import review. No file paths are involved, so MSIX redirection can't break it.
1. **Calls hold Desk Pal:** a pair. CallStarted → pause (call, 90 min). CallEnded → resume, then stretch soon.
2. **Deep focus:** mapped to `focus.started` and `focus.ended`. Mute, close a user-picked app list, then restore.
3. **Sunny lunch walk:** Daily 12:15 on weekdays, with weather and `walks == 0` conditions, then a walk nudge.
4. **Water hotkey:** Ctrl+Alt+W → log 250 ml. No AP notification; Desk Pal confirms.
5. **Day log:** mapped to `day.summary`. Appends a spreadsheet row.

No template includes HTTP, AskAi or email steps.

## 5. Pitfalls

- **Double nudges.**
  - Desk Pal owns every human-facing wellbeing notification. Templates never pair a Desk Pal action with ShowNotification or SpeakText for the same moment.
  - `nudge` returns `shown|queued|merged|held` so a shortcut can branch.
  - External nudges obey the same minimum gap and merge logic as internal ones.
  - AP's "When I've been away" trigger and Desk Pal's welcome-back can both greet; document that one should be chosen.
- **Idle definitions.**
  - AP's Idle trigger is raw `GetLastInputInfo` with a user threshold. Leave it alone.
  - Desk Pal's "away" is: no input for 5 minutes or more, with no seated hold and no full-screen video; or session locked.
  - Agree the vocabulary, not shared code: lock means away at once; a call means present and seated. Desk Pal publishes `state:"away"`, so AP users who want Desk Pal's meaning use the condition.
- **Focus Assist.**
  - Both apps should read `SHQueryUserNotificationState` (documented), not undocumented WNF state.
  - Windows suppresses toasts under Do Not Disturb, but not Bloop's custom card, so Desk Pal must check before showing it.
  - Neither app toggles Do Not Disturb for the other.
- **Store certification.**
  - Declare `windows.protocol` (`deskpal`) and the `dp` alias in the manifest.
  - Desk Pal must be fully functional without AP. "Works with Automation Portal" in the listing is fine; requiring it is not.
  - Launch the other app only from an explicit user click, never at startup.
  - The Store link uses `ms-windows-store://pdp/?ProductId=...`.
- **Portable and Store builds side by side.**
  - Both register `deskpal:` and the same pipe name.
  - The second instance should detect the first by pipe and exit with a message.
  - The portable build needs a `--unregister` like AP's.
- **Elevation.** `CurrentUserOnly` pipes refuse connections between elevated and non-elevated processes. Both apps run non-elevated, and tests launched from an elevated shell will give false "not running" results.
- **Privacy.**
  - Status and event payloads carry wellbeing counts only: no window titles, app names or calendar subjects.
  - `reason` strings from AP are shown and never written to stats, logs or diagnostics.
  - Templates pass generic text ("In a call"), not `{EventTitle}`.
  - Desk Pal's privacy text gets one sentence: data is shared with Automation Portal on the same PC only after the user connects them.

## 6. Next steps

**Desk Pal**
1. Now (step 1): the hold/lease model in the scheduler, the command router and internal events. No transport.
2. Step 3: the "away" definition with seated holds, and session lock.
3. Step 5b: pipe server (`ping`, `status`, `pause`, `resume`, `focus.*`, `water`, `nudge`) and the confirm-once card.
4. Then `dp.exe`, `deskpal://open` with confirm cards, and the settings page with event mapping and template import.
5. `cheer` and `sprout.changed` in v1.1.

**Automation Portal**
1. Document and freeze pipe `ping|list|run|import` as the sibling contract, and add no-wait run.
2. Generalise `IsTrustedServer` and the status client beyond Power Plan Manager.
3. Add the Desk Pal action group: Pause, Resume, Start/Stop focus, Log water, Nudge, Get status, plus the state condition.
4. Add the five gallery templates.
5. Confirm or add a current-weather read for the lunch walk.

**Shared:** a one-page contract document copied into both repos, and a fake pipe server fixture in each test suite (AP already does this with `IPpmStatusClient`).

## Open questions

- Does Automation Portal have a "read current weather" action? The sunny lunch walk (idea 7, template 3) needs one; the WeatherAlert trigger alone is not enough.
- Does Automation Portal have a JSON-parse action? If not, the "Send details as JSON" toggle on event mappings has little use and can wait.
- What is Automation Portal's Store product ID, for the "Get Automation Portal" card?
- How does MSIX redirect `%TEMP%` and `%APPDATA%` for the packaged Desk Pal? The contract avoids shared files for this reason; confirm before adding any file hand-off between the two apps.
