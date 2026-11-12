# Desk Pal pipe contract v1

Two directions, both on the same PC and the same Windows user:

1. **Events**: Desk Pal sends to Automation Portal's pipe `AutomationPortal-<SID>` in Automation Portal's own message shape.
2. **Control**: other apps send commands to Desk Pal's pipe `DeskPal-<SID>` in Desk Pal's `v/cmd/args` shape.

Both pipes use `PipeOptions.CurrentUserOnly`, one UTF-8 JSON line in and one line out (ending in `\n`), camelCase. `CurrentUserOnly` refuses a connection between an elevated and a non-elevated process; both sides treat that as "not running", not as an error.

## 1. Events (Desk Pal to Automation Portal)

### Sending

- Off by default. The user turns on Settings › Automation Portal › "Share events with Automation Portal".
- Desk Pal connects with a 150 ms timeout. It never starts Automation Portal. If nothing answers, the event is dropped and counted ("N events skipped today").
- Desk Pal first sends `{"kind":"capabilities","json":true,...}` and caches the reply for 5 minutes. It sends events only when `kinds` contains `"event"`; otherwise they are skipped and counted.
- The request (fire-and-forget, `wait:false`; Desk Pal reads the short reply and closes):

```json
{"kind":"event","args":[],"name":"deskpal.focus.on","input":"{\"event\":\"deskpal.focus.on\",\"at\":\"2026-11-12T10:00:00+01:00\",\"cause\":\"user\",\"minutes\":25}","files":[],"wait":false,"json":false,"source":"deskpal"}
```

- Expected reply: `{"exitCode":0,...}`. Any other exit code counts as skipped.
- `name` is the event name. `input` is the payload below, as a JSON string.

### Payload

Every payload has `event` (the name), `at` (local time with offset, `yyyy-MM-ddTHH:mm:sszzz`) and `cause`:

| cause | meaning |
|---|---|
| `user` | the user did it in Desk Pal |
| `schedule` | Desk Pal's timers did it (a reminder came due, a hold ran out, the day ended) |
| `external` | a control-pipe command did it; **never sent** (loop guard) |

Payloads hold wellbeing counts and times only: no window titles, app names, calendar subjects or `reason` text.

### Event set

| name | extra fields | example payload (after `event`, `at`, `cause`) |
|---|---|---|
| `deskpal.break.started` | `kind`: `stretch` \| `water` \| `eyes` \| `walk` | `"kind":"stretch"` |
| `deskpal.break.done` | `kind`; `minutes` (int, only when 1 or more) | `"kind":"walk","minutes":4` |
| `deskpal.break.skipped` | `kind` | `"kind":"water"` |
| `deskpal.focus.on` | `minutes` (int) | `"minutes":25` |
| `deskpal.focus.off` | `reason`: `ended` (ran out) \| `stopped` | `"reason":"ended"` |
| `deskpal.pause.on` | `until` (local time with offset) | `"until":"2026-11-12T11:30:00+01:00"` |
| `deskpal.pause.off` | none | |
| `deskpal.goal.reached` | `goal`: `stretches` \| `eyeBreaks` \| `walks` | `"goal":"stretches"` |
| `deskpal.day.summary` | `counts`: `{stretches, eyeBreaks, walks}` | `"counts":{"stretches":6,"eyeBreaks":11,"walks":2}` |

Notes:
- `kind` is the main kind of the nudge. A stretch with a water reminder merged into it is `stretch`.
- Eye breaks are a silent cue: they raise `break.started` with `kind:"eyes"` only, never `done` or `skipped`.
- `goal.reached` fires once a day per goal, when the count first equals that day's target.
- `day.summary` fires once a day, at the first check after working hours end. It doesn't fire if Desk Pal wasn't running during working hours.
- A renewed pause or focus (a new one while one is already on) raises no new `on` event.

### Limits

- Each event name is sent at most once per 60 seconds; extra events are dropped (not queued, not counted).
- Up to 32 events wait to be sent; beyond that they are counted as skipped.

## 2. Control (other apps to Desk Pal)

### Pipe name

`DeskPal-<user SID>`. A Desk Pal debug run (data root `%APPDATA%\DeskPal\debug`) appends `-` and the first 8 hex digits of SHA-256 over the lower-cased full data root path without a trailing slash, as Automation Portal's `InstanceNames.For` does. Clients use the plain name.

### Request and reply

```json
{"v":1,"cmd":"pause","args":{"minutes":60}}
```

- `v` is optional; if present it must be the number `1`.
- `args` is optional; unknown args are ignored.

Success reply. Every success reply carries fresh `status`:

```json
{"ok":true,"result":"held","status":{...}}
```

Error reply:

```json
{"ok":false,"error":"not-allowed","message":"Turn on \"Allow Automation Portal and other apps to control Desk Pal\" in Desk Pal Settings."}
```

`message` is plain English for people. Branch on `error` only.

### Commands

| cmd | args (default, range) | result | needs permission |
|---|---|---|---|
| `ping` | none | `{"app":"DeskPal","version":"0.12.0","contract":1}` | no |
| `status` | none | `"status"` | no |
| `open` | `page` (optional): `home` \| `today` \| `bloop` \| `garden` \| `settings` \| `about`; `section` (optional, `automation` with `settings`). An unknown page opens Home. | `"shown"` | no |
| `break.start` | `kind` (required): `stretch` \| `water` \| `eyes` \| `walk` | `"shown"` | yes |
| `focus.on` | `minutes` (25, clamped to 5–120) | `"started"` or `"renewed"` | yes |
| `focus.off` | none | `"stopped"` or `"not-active"` | yes |
| `pause` | `minutes` (30, clamped to 5–480) | `"held"` | yes |
| `resume` | none | `"resumed"` or `"not-paused"` | yes |

- `minutes` must be a JSON number. It is rounded, then clamped into range.
- `focus.on` while focus is on restarts it with the new length. `pause` while paused replaces the end time.
- `pause` and `resume` act on the same pause as Desk Pal's own tray Pause, so the tray shows it ("Paused until 11:30").
- Focus holds reminders back like a pause and ends by itself. Nudges that came due during focus show when it ends.
- `break.start` shows the break card now, even outside working hours or during a pause.
- Events caused by these commands have `cause:"external"` and are never sent back to Automation Portal.

### Permission

The read-only commands `ping`, `status` and `open` always work. `open` only shows the Desk Pal window; it never changes state. It is what a second `deskpal:` launch sends to the running copy. The other commands need both of these:

1. The setting "Allow Automation Portal and other apps to control Desk Pal" (off by default; `status.allowExternalControl` shows it). If it is off, the reply is `not-allowed`.
2. A one-time approval for the calling app. Desk Pal finds the caller with `GetNamedPipeClientProcessId`, then its package family name (packaged apps) or full exe path. The first time, it shows a card: "Allow <app> to control Desk Pal?" with **Allow** and **Not now**.
   - The request waits up to 30 seconds for the answer. **Allow** runs the command and remembers the app. The user can remove it in Settings.
   - **Not now** (or closing the card) replies `denied`. That app gets `denied` without a new card for 10 minutes.
   - With no answer in 30 seconds, the reply is `needs-approval`. The card stays up, and an answer given later still counts. Clients should keep their own read timeout above 30 s for the first call, or retry after `needs-approval`.

### Status

```json
{"contract":1,"app":"DeskPal","version":"0.12.0","edition":"portable","allowExternalControl":true,
 "state":"focus",
 "holds":[{"source":"focus","until":"2026-11-12T10:25:00+01:00","seated":false}],
 "focus":{"active":true,"endsAt":"2026-11-12T10:25:00+01:00"},
 "pausedUntil":"2026-11-12T11:30:00+01:00",
 "nextNudge":{"kind":"stretch","dueAt":"2026-11-12T10:30:00+01:00"},
 "today":{"stretches":3,"eyeBreaks":5,"walks":1},
 "goals":{"stretches":17,"eyeBreaks":25,"walks":5}}
```

- `state` is the first that applies: `offHours` > `dnd` (Focus Assist, full screen or a call) > `focus` > `paused` > `break` (a nudge is showing) > `active`.
- `holds[].source` values: `manual` (pause), `focus`, `snooze`, `dnd`, `call`.
- `pausedUntil` and `nextNudge` are left out when they don't apply.
- Fields are only ever added. A breaking change bumps `contract`.

### Error codes

| error | when |
|---|---|
| `bad-request` | not JSON, not an object, or no `cmd` |
| `unsupported-version` | `v` present and not `1` |
| `unknown-command` | `cmd` not in the table |
| `bad-args` | `args` not an object, missing or invalid `kind`, `minutes` not a number |
| `not-allowed` | the setting is off, or the caller couldn't be identified |
| `needs-approval` | the approval card got no answer within 30 s |
| `denied` | the user chose Not now (for 10 minutes) |
| `busy` | `break.start` while a nudge is showing, or more than 8 requests at once |
| `rate-limited` | more than 20 state-changing commands a minute, or a second `break.start` within 5 minutes |
| `too-large` | the request line is over 64 KB |
| `timeout` | no complete line within 10 s of connecting, or Desk Pal didn't finish within 45 s |
| `internal` | an unexpected failure inside Desk Pal (logged) |

### Limits

- Request line: 64 KB at most (UTF-8, without the line break).
- Read timeout: 10 s from connect to the end of the request line.
- At most 8 requests are handled at once.
- One request per connection; Desk Pal closes the pipe after replying.
