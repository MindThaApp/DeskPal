# Product review: Desk Pal and Automation Portal (2026-11-11)

**Basis.** Desk Pal: `docs/BRIEF.md`, `docs/INTEGRATION-AUTOMATION-PORTAL.md`, `src/DeskPal/App.xaml.cs` (milestone 1), the 14 flat SVGs in `Assets/Characters/` and the 3 soft-3D samples. Automation Portal: `docs/SPEC.md`, `docs/CLI.md`, `docs/research/feature-survey.md`, the XAML pages, tray, editor and trigger providers, read only. Neither app was launched, so every visual and timing remark is from code and docs (see "Not verified" at the end).

---

## 1. Desk Pal

### Top 3 problems or risks

1. **The default schedule interrupts every 15 minutes, and merging barely helps.**
   - Movement every 30 min is 16 nudges in an 8-hour day; 20-20-20 is 24 more. The two grids only coincide on the hour, so "one scheduler merges nudges" removes 8 of 40. Add hydration and the default is one interruption every 12–15 minutes.
   - Each Windows toast makes a sound, slides over the bottom-right corner and lands in the notification centre. At that rate the app is uninstalled in week 1, however charming Bloop is.
   - Fix it in the brief, before the scheduler is written: a hard **interruption budget** (default 2 visible nudges an hour), intervals on a shared 20-minute grid (movement 40 or 60, not 30) so merges are real, and eye breaks delivered as a silent ambient cue, never a toast.
2. **The gamification list is bigger than the product and half of it pulls against "never guilt-trippy".**
   - Planned: daily score, streaks, seeds currency, 3 daily quests, achievements, combos, garden shelf, outfits, two art styles, weekly recap. That is a second app on top of an unproven core loop.
   - **Bloop combos for quick responses** reward reacting fast to an interruption. That trains people to break concentration at once and to press Done without stretching. It contradicts focus mode and should be cut or redefined (see Improvements).
   - Sprout's growth curve (Day 1, Week 1, Month 1, Month 3) has nothing new between day 8 and day 30, which is exactly the week-2 retention cliff, and nothing at all after month 3.
   - Two switchable art styles double every pose, outfit and animation for a solo developer. Today there are 14 flat poses, 3 soft-3D samples and 5 of the 8 listed stretches still undrawn.
3. **The app is blind in the moments where it embarrasses the user.**
   - The brief covers Focus Assist and full-screen apps. It does not cover windowed calls or windowed screen sharing, and the integration plan hands both to Automation Portal. Most Desk Pal users will never install a second app.
   - A stretch card appearing on a shared screen in front of a client is the one failure users will not forgive. "In a call" detection must be native (see New feature ideas, row 2).

### Improvements to existing features

**Onboarding and first day (nothing is specified yet)**
- First run is one window, three steps, under a minute: (1) working days and hours, pre-filled Mon–Fri 9:00–17:30; (2) a pace preset: **Gentle / Balanced / Active**, which sets every interval at once; (3) "Plant your Sprout" with a name field. Naming is the cheapest attachment mechanic available.
- End the flow with a live 20-second overhead reach on the real card. The user must feel the core loop in the first minute, not after waiting 30 for the first timer. `App.xaml.cs` already fires a nudge 3 s after launch; turn that test into this moment.
- Ask "Start with Windows" in step 1, default on, in plain words. A tray wellbeing app without autostart is gone on day 2. In MSIX this is a `windows.startupTask` the user must enable from inside the app, so the first-run switch is the only natural place.
- **Gentle first week.** Day 1–2: movement and water only. Day 3: Bloop offers eye breaks. Day 5: offers focus mode. Each offer is a card on the Today flyout, not a toast. This halves day-1 interruptions and gives week 1 three small reveals for free.
- Confirm toasts work. Unpackaged apps often have notifications off or show the wrong app name. After the first test nudge ask "Did you see Bloop's message?" and link to `ms-settings:notifications` if not.

**Toast and nudge card: give each a single job**
- **Toast is the invitation, card is the activity.** Toasts cannot animate and do not take SVG, so Bloop "demonstrating" a stretch needs the custom card. Pre-render each pose to PNG for the toast image.
- Buttons today are `Done / Snooze 5 min / Skip`. Change to **Start** (opens the 20 s card with a progress ring, and completion counts by itself) and **In 10 min**. Put "Skip this one" and "Pause for 1 hour" in the toast context menu. Dismissing the toast is a skip with no penalty.
- "Done" as the first button invites honour-system clicking and makes every stat meaningless. Keep a small "Already did it" link on the card instead.
- Snooze 5 min is too short. A nudge that returns five minutes later, mid-sentence, is the definition of nagging. Offer 10 min and "after this task" (next typing pause or app switch).
- Give every toast a tag and group so a new nudge replaces the old one, and an expiry equal to the snooze length. `ShowNudge()` sets neither, so stale "Stretch time" toasts will pile up in the notification centre.
- A click on the toast body is currently logged as `(body click)` and does nothing. It should open the card.
- The card is bottom-right above the taskbar, about 320 px wide, never takes focus (`WS_EX_NOACTIVATE`), never shows over full-screen or Do Not Disturb, closes on Esc and remembers which monitor the user works on.
- Toast text must stand alone. "Reach for the sky with Bloop, 20 seconds" means nothing to a screen-reader user or in the notification centre. Use "Raise both arms overhead and hold for 20 seconds".
- Sound is off by default. One soft chime is a setting.

**Annoyance rules to write into the brief**
- "Adapt timing if a reminder is always skipped" must ask, never act silently: "You've skipped eye breaks 6 times this week. Every 40 minutes instead, or off?" Silent adaptation reads as a bug.
- Sprout's thirsty and sleepy moods are visible only when the user looks (tray flyout, Today page). Never send "Sprout is thirsty" as a notification; that is guilt delivered by push.
- Time held by calls, focus or presenting never counts against the day. A three-hour meeting block must not produce a sleepy Sprout.
- Drop the caffeine log from v1. It adds a setting, a stat and a medical flavour for no retention gain.

**Tray**
- Left-click does nothing today and the menu is "Test nudge / Exit". Left-click should open a **Today flyout**: Sprout, water ring with "+250 ml", next nudge time, Pause (30 min / 1 h / until tomorrow), Start focus.
- Tooltip should carry state: "Next stretch in 12 min · 750 of 2000 ml". Today it is the static text "Desk Pal".
- A paused or held state changes the icon itself, not only the tooltip.

**Settings surface**
- Use a WinUI `NavigationView` with four pages and `SettingsCard`/`SettingsExpander` rows, so it looks like Windows 11 Settings:
  - **Reminders:** one expander per nudge type (toggle, interval, delivery: toast / card only / quiet cue).
  - **Schedule:** working days and hours, lunch window, and "Stay quiet when": in a call, full-screen, Do Not Disturb, presenting.
  - **Sprout and Bloop:** name, message tone, cosmetics.
  - **General:** start with Windows, sound, units, export or reset data, connected apps, about.
- The pace preset sits at the top of Reminders. Most users should never open an expander.
- Units (ml or fl oz) follow the Windows region. The eye break says "about 6 m" in metric regions.

**Gamification balance**
- **Redefine combos** as "one break, several good things": stretch plus water plus eyes in the same break. That rewards merging, the thing the scheduler wants, instead of speed.
- **Replace the daily score** with a short checklist ("3 of 4 today"). A 0–100 score invites judgement, a checklist invites completion.
- **Streaks:** count configured working days only. A day with no PC use never breaks the streak. Show the weekly rest day as a visible "rest leaf" that is used automatically. Add "Away until…" for holidays. Lead with a cumulative, unbreakable number ("Sprout tended 41 days") and show the streak second.
- **Quests:** all three must be met by normal behaviour at the user's chosen pace ("walk before lunch", "finish 3 eye breaks"), tick themselves, and live only on the Today flyout. No quest toasts, no penalty, no red.
- **Seeds:** defer. A currency needs a shop, prices and a sink. Launch with milestone unlocks ("10 walks: watering can") and add seeds only once there are 15 or more cosmetics.
- **Outfits:** design them as accessories on fixed anchor points (hat, pot skin, flower colour) so one asset fits every pose. Per-pose outfits are an asset explosion.
- **Art styles:** do not ship a switch at 1.0. Use soft 3D for stills (Store art, onboarding, Sprout on Today, toast image; it matches Windows 11's own emoji) and flat for everything that moves (stretch demos, tray icon). Offer the style switch later as a long-term unlock if users ask.

**Accessibility**
- Respect Windows "Animation effects": with it off, show the end pose and a text countdown.
- Moods must not rely on colour. Thirsty already has a droop and a water drop; check sleepy and happy are distinct in greyscale and at 16 px.
- High contrast: flat shapes with fixed fills vanish on some themes. Add an outline variant or a plate behind the character.
- Mobility: a "Seated only" switch and "Avoid: neck / wrists / hips" checkboxes. Neck rolls and hip flexor stretches are not for everyone, and "wellbeing tips" wording does not remove the duty to let people opt out.
- The card is fully keyboard operable, announces through a UI Automation live region, and scales to 200% text size without clipping.
- Celebrations never flash. Sparkles fade.

**Retention past week 2**
- Eight stretches at 12 or more nudges a day repeat by Tuesday afternoon. Target 24 by 1.0, in rotating weekly sets.
- Shorten Sprout to a **4-week cycle** with visible beats at days 3, 7, 14, 21 and 28, then start a new species and put the grown plant on the garden shelf. One plant every three months is too slow to collect.
- Weekly recap on Monday morning with one real insight from local data ("Your longest sitting block is Tuesday 14:00–16:10"). Insight keeps people; totals do not.
- Lapsed users (3 or more working days of ignored nudges) get one card: "Too much? Switch to Gentle." Not a streak-loss message.

**Store listing**
- Lead with what is unusual: free, no account, no ads, everything stays on the PC. Then the mascot.
- Screenshot 1 is the nudge card on a real desktop. Screenshot 2 is the garden shelf. Add a 15-second clip of Bloop stretching.
- Search terms people actually type: stretch reminder, break reminder, water reminder, eye strain, 20-20-20, posture, pomodoro.
- Check "Desk Pal" is free in Partner Center before more art is drawn around the name.

### New feature ideas, ranked

| # | Idea | Value | Effort | Why |
|---|---|---|---|---|
| 1 | **Natural-break delivery:** hold a due nudge up to 5 min for a typing pause or app switch | High | M | The largest drop in annoyance for the least UI. Only needs last-input polling. |
| 2 | **Native "in a call" hold:** microphone or camera in use → seated hold | High | S | Removes the worst failure for every user, not only those with Automation Portal. |
| 3 | **Pace presets + gentle first week** | High | S | One choice replaces eight settings and halves day-1 interruptions. |
| 4 | **Quiet eye cue:** tray icon pulse plus a small 20 s pill, no sound, no toast | High | M | Makes 20-20-20 survivable; 24 toasts a day is not. |
| 5 | **Today flyout on tray left-click** | High | M | Becomes the home of water logging, pause and quests; no main window needed. |
| 6 | **4-week Sprout species + garden shelf** | High | M | Fills the day 8–30 gap and gives a reason to stay after month 3. |
| 7 | **Stretch filters:** seated only, body areas to avoid | Med | S | Accessibility and trust; also widens the audience. |
| 8 | **Recap insight** (longest sitting block, best day) | Med | M | The only stat users cannot work out themselves. |
| 9 | **"Away until…" and timed pause with a reason** | Med | S | Protects streaks from holidays; first user of the hold model. |
| 10 | **Answer a nudge from the keyboard** (one global key: Start, again: Later) | Med | S | Keyboard users and accessibility; no mouse trip to the corner. |
| 11 | **Shareable recap card** through the Windows share sheet, counts only | Med | M | Free word of mouth; keep health numbers off by default. |
| 12 | **Seasonal accessories by date** (scarf in December) | Low | S | Delight with no server and no shop. |
| 13 | **Export my data** (CSV) | Low | S | Supports the local-only promise in the listing. |

### Quick wins (under 1 day each)

- Add tag, group and expiry to the toast in `ShowNudge()`; open the card on body click.
- Rename the toast buttons to Start / In 10 min and move Skip to the toast context menu.
- Tray: state in the tooltip, left-click handler, and "Pause for 1 hour" in the menu as the first hold.
- Rewrite the eight stretch instructions as self-contained sentences.
- Render PNG versions of the poses for toast images.
- Cap or rotate `%APPDATA%\DeskPal\logs\deskpal.log`; it is append-only today.
- Edit `BRIEF.md`: interruption budget, 20-minute grid, eye breaks as quiet cue, combos redefined, caffeine log and seeds moved to "later".

---

## 2. Automation Portal

### Top 3 problems or risks

1. **There is no first run.** No welcome, starter or tour code exists in `AutomationPortal.App` or `AutomationPortal.Editor`.
   - A Store user lands on an empty Library ("Let's make something happen") with two buttons, one of which leaves the page.
   - Nobody is told about Quick Run (Win+Alt+Space), the tray Quick Panel, "Run on selected text", or that automations need the app running at sign-in. Those are the four habits that make the app stick, and all are found only by reading Settings.
   - With about 225 actions the blank editor is the worst possible first screen.
2. **A failed run is a dead end outside the editor.**
   - `RunHistoryPage` shows each step and its error, but its details pane has no buttons: no Run again, no "Open at the failed step", no Copy details.
   - The editor has live variables and "Run from here", but no way to run one step at a time or stop before a step. A 30-step shortcut that fails at step 22 means re-running and watching.
   - Automations show only the last skip reason and a "Problem:" line. "Why didn't it fire at 9:00?" has no answer after the next event overwrites it.
3. **Breadth has outrun the surfaces that explain it.**
   - 37 gallery starters cover about 225 actions and 39 triggers. Only one starter uses a sibling-app or power-plan action.
   - `SettingsPage.xaml` is a single scroll of about 15 cards with leftovers: a single-provider model card (Fast / Smart / Best) under the newer multi-provider panel, "Phone notifications topic" sitting under that same heading, and raw Latitude / Longitude boxes in an app whose spec says "non-technical everyday users".
   - Sibling depth is lopsided: Power Plan Manager has 11 actions, a trigger and a condition; Files Portal has one action (`app.revealInFilesPortal`).

### Improvements to existing features

**First run**
- Three cards over the empty Library:
  1. "Pick a few to start": six tick-box tiles from the Get Started section, three pre-ticked (Copy as Plain Text, Grab Text from Screen, Clean Link).
  2. "Press Win+Alt+Space now": the card completes only when Quick Run really opens.
  3. "Keep automations ready": the Start with Windows switch.
- Replace the empty-state's "Browse the Gallery" jump with three inline one-click starter tiles. Leaving the page to fix an empty page loses people.
- After the first successful run, one tip: "Right-click a tile to pin it to the tray panel."

**Shortcut editor**
- **Step mode:** "Run next step" and a per-card "Pause here". The run feedback, inspector and "Run from here" already exist, so this is a small change to the run loop with a large effect on fixing shortcuts.
- **Test with sample input** in the editor header. The Automations page has "Test now" with sample trigger input, but the place where the shortcut is built does not.
- Find in shortcut (Ctrl+F), collapse all, and "Make a shortcut from the selected steps" for long flows.
- When action search finds nothing, offer "Describe it instead" and the nearest gallery starter rather than an empty list.

**Finding the right action among 225**
- On each action's help card add "Starters that use this" (two or three gallery tiles). A working example teaches more than a description.
- Add outcome-style search words where missing ("shrink photo" → Resize Images, "paste without formatting" → Copy as Plain Text).
- Gallery tiles get requirement badges: needs classic Outlook, needs a model key, changes files, uses the internet.
- Gallery filters by trigger and by app ("Outlook", "Spotify", "Downloads folder").

**Errors and debugging**
- Run History details: **Run again**, **Run again with the same input**, **Open at the failed step**, **Copy details**.
- The Library failure bar ("See details") gets a Run again button too.
- Automations get an **Activity** list per row: fired, skipped and why (condition, cooldown, paused, already running), last 50 entries.
- "Explain this error" on a failed step, using the existing AskAi plumbing over the error text and step summaries. It was item H2 in the feature survey and is still open.

**Tray and Quick Panel**
- "Pause automations" is open-ended and only the tooltip changes (`TrayService.UpdateTooltip`). Add "Pause for 1 hour / until tomorrow" and a paused icon. Forgotten pauses look like broken automations.
- Add "Stop running shortcuts" to the tray menu. A shortcut started by an automation cannot be stopped from the tray today unless it happens to be pinned.
- Show the last failure in the menu ("Organize Downloads didn't finish · See details").
- The menu takes the first 10 pinned shortcuts with no hint that more exist.

**Settings**
- Split into the same sections as the nav uses elsewhere: General, Keyboard, Library, Actions, Model providers, Phone, Notifications, Links, Connected apps, About.
- Remove or fold the leftover single-provider model card into the provider panel.
- Replace Latitude / Longitude with a city search (the `loc.find` lookup already exists) and a "Use my location" button.
- Add "Back up all shortcuts…" and "Restore…" next to the library location. The only whole-library option today is moving the folder.

**Sibling apps**
- Files Portal users who replaced Explorer lose `file.selected` (it reads Explorer windows only) and the "current folder" context. Add "Get Selected Files in Files Portal" and "Open Folder in Files Portal (new tab)".
- Add a **Connected apps** card in Settings listing Power Plan Manager, Files Portal and Desk Pal with state (connected / installed but not running / not installed) and one Store link each. Today the integration is invisible unless the sibling is installed.
- Add a "Works with your other apps" gallery section; show it only when at least one sibling is detected.

### New feature ideas, ranked

| # | Idea | Value | Effort | Why |
|---|---|---|---|---|
| 1 | **First-run starter picker + Quick Run lesson** | High | S | Time to first success decides Store reviews; nothing exists today. |
| 2 | **Run History actions** (Run again, Open at failed step, Copy details) | High | S | Turns the best diagnostic page from read-only into a repair tool. |
| 3 | **Step mode in the editor** (Run next step, Pause here) | High | M | The missing half of debugging; competitors in the survey all have it. |
| 4 | **Automation activity log** with skip reasons | High | S | Answers "why didn't it run?", the most common automation complaint. |
| 5 | **Preview changes, then apply, plus "Undo last run"** for move, rename, delete | High | L | `file.renamePattern` already plans and rolls back; make that the rule for every file-changing action. Trust is the product. |
| 6 | **"When a notification arrives" trigger** (app, contains text) | High | M | Rated top in the survey, blocked then on package identity; the Store build now has it. |
| 7 | **Convert or compress video and audio** | High | M | "This video is too big to send" is an everyday office problem; Windows has the transcoder built in. |
| 8 | **Wait Until** (any condition: file exists, window appears, internet back) + **window-title trigger** | Med | S | Only `app.waitFor` exists; removes most blind `Wait 5 seconds` steps and fixes recorded macros. |
| 9 | **Version history per shortcut** (last 10 saves, Restore) | Med | S | An edit that breaks a working shortcut is unrecoverable after the editor closes. |
| 10 | **Run a missed schedule at next start** (option on time triggers) | Med | S | Laptop users expect "daily at 9:00" to run at 9:40 when the lid opens. Not found in the trigger code; confirm first. |
| 11 | **Calendar events from the .ics link** for `cal.getEvents` | Med | S | The trigger already reads .ics; the action still needs classic Outlook, which fewer people have each month. |
| 12 | **Live toggle tiles** in the Quick Panel (mic muted, dark mode, awake) | Med | M | State at a glance makes the panel a daily habit. Survey item G5, still open. |
| 13 | **Office pack:** Save Outlook attachments, Print file, Word/Excel → PDF, Eject drive | Med | M | The gaps an office worker hits in week 1; none are in the catalog. |
| 14 | **Clipboard history actions** | Med | S | Survey item A4; pairs with the existing `clip.changed` trigger. |
| 15 | **Online starter refresh** from the existing static site | Med | M | More than 37 starters without a Store submission; the import review already guards it. |
| 16 | **More triggers:** media starts or stops, lid opened or closed, low disk space | Low | S each | Cheap, each unlocks a starter. |

### Quick wins (under 1 day each)

- Add Run again and Copy details to `RunHistoryPage` and the Library failure bar.
- Timed pause and a paused tray icon in `TrayService`.
- "Stop running shortcuts" in the tray menu.
- Tidy `SettingsPage.xaml`: remove the leftover model card, give Phone its own heading, swap the coordinate boxes for a city search.
- Three inline starter tiles in the Library empty state.
- Requirement badges on gallery tiles.
- Add two Power Plan Manager starters and one Files Portal starter to `gallery/`.
- Add the sibling pipe contract (`ping`, `list`, `run`, `import`, request and reply shapes) as a section of `docs/CLI.md`.

---

## 3. Integration and suite

### Top 3 problems or risks

1. **The plan's best feature is locked behind owning two apps.** Row 1 of its own ranking, the meeting-aware hold, "removes the worst annoyance", yet only for users who also run Automation Portal and install a template. Microphone-in-use detection is a small registry watch (Automation Portal's `call.started` uses exactly that). Desk Pal should do it natively and leave screen-share, calendar and weather to the integration.
2. **Automations end up managed in two places.** The plan adds an event → shortcut mapping page inside Desk Pal (dropdowns filled from `list`, Test buttons, skip counters). That is a second, weaker copy of Automation Portal's Automations page: no conditions, no cooldown, no "Ask before running", no activity. Runs started this way are also recorded as command-line runs, so a user asking "why did my PC mute?" finds no automation to look at.
3. **Two independently updated Store apps share an unfrozen contract.** Automation Portal's pipe shape is described only in the corrections section; freezing it is "a doc task when work starts". Store rollouts are staged, so old Desk Pal × new Automation Portal (and the reverse) will exist in the wild. Paired events make it worse: if Automation Portal exits during a focus session, `focus.ended` is skipped and the PC stays muted with apps closed.

### What is missing

- **Hold expiry during long calls.** The template pauses for 90 min and resumes on CallEnded. A two-hour workshop gets a stretch card at minute 91. Use the 240-min cap for the reserved `call`, `meeting` and `presenting` sources; `resume` and expiry already cover crashes.
- **Always-on microphones.** Voice typing, voice access and game chat hold the mic for hours. Native and template call detection both need an "ignore these apps" list and should prefer camera or a known meeting app as the stronger signal.
- **A defined connect flow.** `deskpal://open?page=connect` exists but nothing says who starts it or what it shows. See the shared onboarding idea below.
- **Deleted or renamed targets.** A mapped shortcut that was deleted needs a visible "Shortcut no longer exists" state, not a silent skip.
- **Held time and scoring.** State plainly that minutes under a seated hold never lower the daily checklist, streak or Sprout's mood.
- **Open question 2 is answered.** Automation Portal has `dict.fromInput` (Get Dictionary from Input, JSON) and `dict.getValue` with paths, so structured event input is usable today.
- **A correction to the templates claim.** "No template includes HTTP" is not true of Sunny lunch walk: the weather read calls Open-Meteo with the user's location. Word the privacy sentence accordingly.

### What is over-engineered

- **Per-caller consent** (`GetNamedPipeClientProcessId`, package family or exe path, remembered answers, revoke list). Any same-user process can already run arbitrary shortcuts on Automation Portal's pipe with no identity check. Guarding "pause nudges for at most 4 hours, shown in the tray" harder than "run any shortcut" is effort in the wrong place, and it breaks honest callers (`dp.exe`, PowerShell, Stream Deck). Replace it with one switch, "Let other apps control Desk Pal", off by default and turned on by the connect flow, plus the caps and the visible tray state already in the plan.
- **State-changing `deskpal://` links with confirm cards** (water, pause, focus, nudge). Nobody needs a link that opens a card that needs a click. Ship `deskpal://open?page=…` only.
- **Three transports at once** (pipe, `dp.exe`, URI) plus nine verbs with individual rate limits. For v1: pipe with `ping`, `status`, `pause`, `resume`, `focus.start`, `focus.stop`, `water`; `dp.exe` as a thin client for the same seven. Add `nudge` once the scheduler's merge logic has run in public for a month; `cheer` after that.
- **"Before the first MSIX submission."** Protocol and alias declarations can be added in any later update. Do not let step 5b delay the first Store release.
- **The estimate.** 3–4 days plus 2–3 days does not cover the settings page, template import, the fake-server fixtures and a cross-version test pass. Plan two weeks, or cut as above and it fits.

### What is risky

- `status` is "always allowed" and returns water, caffeine and sitting minutes to any same-user process, even when external control is off. Return only `ping` data until the user connects.
- The plain-sentence event input ("Focus ended after 25 min, round 2") becomes an interface the day someone splits it with Find in Text. Never localise it, or always send the dictionary with a `text` field inside.
- Portable and Store builds side by side share the pipe name and the `deskpal:` scheme. The "second instance exits with a message" rule needs a test, because the elevated dev shell hides this class of bug.
- Both apps greet on return ("When I come back" and Desk Pal's welcome-back). The plan says "document that one should be chosen"; users do not read. Desk Pal should skip its greeting when a shortcut ran in the last 10 seconds, or the template should not include one.

### Improvements to the plan

- **Move the events to Automation Portal as triggers.** Add `deskpal.focusStarted`, `focusEnded`, `breakStarted`, `goalReached` and `daySummary` to the Automations wizard, fed by a new pipe message kind `event` that Desk Pal sends. Users then get conditions, cooldown, Ask first, Test now and history for free, and runs are labelled "Run by an automation". Desk Pal's page shrinks to connection state, the switch, "Add starter automations" and "Open in Automation Portal". If Automation Portal work cannot be scheduled, keep the mapping page as the fallback, but treat it as temporary.
- **Make Deep focus one shortcut, not a pair.** Start → mute and close apps → wait for focus to end (the new Wait Until on the Desk Pal state condition) → restore. Restore then lives in the same run and cannot be orphaned.
- **Freeze both contracts in one shared page now**, with a `contract` number on Automation Portal's `ping` reply as well, and a fake-server fixture in each test suite before either side ships.
- **Keep the lease model.** `{source, reason, until, seated}` is the best idea in the plan. Building it at step 1 is right, and it is also what makes the native call hold nearly free.

### New feature ideas, ranked

| # | Idea | Value | Effort | Why |
|---|---|---|---|---|
| 1 | **Shared connect card:** when both apps are present, each shows once "Connect Desk Pal and Automation Portal" with five tick-box starters, calls pre-ticked | High | M | One decision replaces a settings page, a consent prompt and five imports. |
| 2 | **Desk Pal triggers and a state condition inside Automation Portal** | High | M | One place to manage automations; reuses everything the wizard already does. |
| 3 | **One "Focus" across the suite:** Desk Pal focus → Automation Portal mutes and closes apps → Power Plan Manager quiet preset → restore | High | S | The only story that shows three apps as one product; a strong Store screenshot. |
| 4 | **Connected apps page in every app,** same layout, same wording, detection by pipe ping | Med | S | Tasteful cross-promotion: visible when the user looks, silent otherwise. |
| 5 | **Desk Pal tiles in the Quick Panel** (Log water, Start focus, Pause 1 h) with live state | Med | M | Depends on live tiles; puts Sprout one click from the tray panel users already open. |
| 6 | **Suite design sheet:** one page of shared tokens (8/12 px corners, Mica, accent use, card settings rows, sentence-case labels, "near the clock" wording) | Med | S | WPF-UI and WinUI 3 already look close; a sheet stops drift as Desk Pal's UI is built. |
| 7 | **Screen-share guard starter** (slideshow, sharing toolbar or OBS in front → presenting hold) | Med | S | The case native detection cannot cover cheaply. |
| 8 | **Sprout cheers a finished automation** (`cheer`) | Low | M | Charming, but only after the basics; easy to overuse. |

**Cross-promotion rules.** Mention a sibling only at the moment it solves the problem on screen (Desk Pal's Focus settings: "Also mute apps during focus? Works with Automation Portal"). One line, user-clicked Store link, shown once, dismissible for good. No toasts, no startup launches, no badges. In Store listings use "Works with…" and never "requires".

### Quick wins (under 1 day each)

- Edit the integration plan: native call hold in Desk Pal, 240-min cap for reserved sources, single external-control switch, `open`-only links, v1 verb list of seven.
- Record the answers to the open questions (JSON input exists, weather read exists) and fix the "no HTTP" sentence.
- Write the shared contract page and copy it into both repos.
- Add the Connected apps card to Desk Pal's settings design now, even with only a "Get Automation Portal" row.
- Agree the vocabulary once for both apps and all text: "away", "in a call", "paused", "held", "focus".

---

## Not verified

- No app was run. Toast appearance for the unpackaged Desk Pal build (app name, icon, button truncation) and Automation Portal's real first-launch behaviour are inferred from code.
- "No first run" in Automation Portal is based on finding no welcome, starter or tour code; a server-driven or resource-only flow would not show up that way.
- Catalog gaps (print, eject, video convert, notification trigger, missed-schedule option, media and lid triggers, HEIC input) come from `docs/SPEC.md` and keyword searches of the providers, not from the live action list.
- Whether the soft-3D SVGs (focal radial gradients) render correctly in WinUI's SVG image source is untested; pre-rendered PNGs avoid the question.
- Whether "Desk Pal" is free as a Store name, and how Files Portal currently hands selections to Automation Portal, were not checked.
