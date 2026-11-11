# Desk Pal — project brief

Hobby project. Build a Windows desktop wellbeing companion for office workers who sit at a desk all day. Ship a portable beta (.exe) first, then package as MSIX for the Microsoft Store.

## Concept
A tray app with a friendly mascot that nudges the user to stretch, drink water, rest their eyes and take short walks. It should feel rewarding, not naggy. One smart scheduler merges nudges so reminders never stack (e.g. "Stretch time, grab some water while you're up").

## Characters
- **Bloop**: round blue blob (#7FB3E8), dot eyes (#22324A) with white glints, pink cheeks (#F4A6B8). The guide: demonstrates stretches and reacts (high-five, wave, celebrate).
- **Sprout**: potted plant on the desk. Terracotta pot (#C97B4A, rim #B5683A) with a face, green leaves (#6BBF59 / #8AD17A). The long-term progress tracker.
  - Moods: happy (on track), thirsty (behind on water, droopy yellowish leaves + water drop), sleepy (sitting too long, folded leaves, closed eyes, "z"), celebrating (daily goal hit, leaves raised, flower, sparkles).
  - Growth: Day 1 seedling → Week 1 young → Month 1 bushy → Month 3 in bloom (unlock flower colours).
- Keep both simple: 4–5 expressions, flat shapes, easy to animate. Never guilt-trippy; droopy at worst, quickly cheered up.
- Personality setting for message tone: cheerful / calm / dry-witty.

## Features
**Movement**
- Interval reminders (default 30 min; 20/30/45/60/custom)
- Short guided stretches (20–60 s) demonstrated by Bloop, rotating by body area: overhead reach, side bend, shoulder shrug, neck roll, seated twist, wrist stretch, hip flexor, calf raises
- Escalation: after 3–4 desk stretches, suggest a walk, stairs, refill water, or step outside; lunchtime daylight nudge
- Optional sit/stand alternation for standing desks
- Idle detection: away from PC 5+ min counts as a break and resets the timer

**Hydration**
- Daily goal + cup/bottle size
- One-click logging from the toast ("+250 ml") or tray
- Progress shown in tray / Sprout's mood
- Optional caffeine log

**Eyes**: 20-20-20 reminder (every 20 min, look ~20 ft away for 20 s)

**Focus & mind**: Pomodoro/focus mode that holds nudges then catches up; 1-min box breathing; end-of-day wind-down + day summary

**Smart behaviour**
- Working hours/days; no weekend nags
- Respect Windows Focus Assist / Do Not Disturb and full-screen apps (later: Outlook calendar)
- Toast buttons: Done / Snooze 5 min / Skip
- Adapt timing if a reminder is always skipped

**Motivation**: daily score, streaks, weekly recap card, achievements, unlockable cosmetics for Sprout/Bloop (earned only, never paid)

**Windows**: system tray, optional start with Windows, follows light/dark theme, all data stored locally in %APPDATA% (no account). Optional small floating desktop buddy, off by default.

Use "wellbeing tips" wording, no medical claims (simplifies Store certification).

## Tech
- **WinUI 3 / .NET 10** (C#). Unpackaged portable beta first, packaged as MSIX for the Microsoft Store later.
- Tauri was considered and dropped: its Windows toast support (action buttons, rich toasts) is weak.
- Avoid Electron (too heavy for an always-on tray app).
- Portable beta: single unsigned .exe; testers will see SmartScreen → "More info → Run anyway". Store release via MSIX + Partner Center (Microsoft signs it).

## MVP order
1. Tray app + single scheduler + stretch reminders with toast buttons + Bloop stretch cards
2. Hydration logging
3. Walk escalation + idle detection
4. Eye breaks + stats/streaks + Sprout moods and growth
5. Focus mode, meeting/DND awareness, MSIX packaging

## First step
Scaffold the Tauri project, get a tray icon showing, fire one test toast with Done/Snooze buttons, and build a portable .exe to confirm the toolchain works end to end on Windows.
