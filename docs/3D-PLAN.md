# Desk Pal — live 3D mascot plan

Design only; nothing here is built yet. Figures marked "est." are estimates and get replaced by
measurements in milestone 1.

## 1. Goal and guard rails

Owner's wish: live, interactive 3D "everywhere possible", pre-rendered or scripted 3D where it is not.

- **Light.** At rest (no live surface on screen) the 3D feature costs 0 MB, 0% CPU and 0% GPU.
  Flat and Soft 3D styles never load a graphics device at all.
- **Never annoying.** No focus stealing, no motion during focus, quiet hours or full-screen apps,
  full respect for the Windows "Animation effects" setting.
- **Style switch keeps working**, and the MSIX story is unchanged (no extra runtimes).

## 2. Renderer decision

| Criterion | (a) WebView2 + three.js / `<model-viewer>` | (b) Direct3D 11 in a `SwapChainPanel` (Vortice.Windows + SharpGLTF) |
|---|---|---|
| Extra RAM while live | est. 180–300 MB across 5–6 `msedgewebview2.exe` processes (browser, GPU, renderer, utilities, crash handler) | est. 25–45 MB, in-process (device, driver, shaders, meshes, textures) |
| Each extra surface | another renderer process, est. +40–80 MB | one small swap chain, est. +1–3 MB; device and assets shared |
| Cold start to first frame | est. 0.4–1.5 s (process launch, page, WebGL context, glb parse) | est. 80–200 ms (device created off the UI thread, glb parse and upload under 50 ms) |
| Transparency | transparent background works; rectangular hit area only | per-pixel premultiplied alpha, composited over the acrylic card like any XAML element |
| No-activate card | fragile: a click inside the web content wants focus and fights `WS_EX_NOACTIVATE` | pointer events arrive through XAML as they do for today's buttons; the panel is never focusable |
| Idle GPU | GPU process stays alive; relies on Chromium throttling | nothing presented means nothing drawn; we own the loop |
| Skinning and animation | free (GLTFLoader, AnimationMixer, morph targets) | SharpGLTF.Runtime evaluates clips, skins and morphs; a ~40-line vertex shader skins on the GPU |
| Solo-dev effort | lowest: days | medium: est. 1,500–2,000 lines once (device, one material, shadow, loop, fallback) |
| Packaging | needs the Evergreen runtime (not guaranteed on Windows 10) or a fixed runtime of ~200 MB; a user-data folder to manage | managed DLLs only (est. 3–4 MB); `d3d11`, `dxgi`, `dcomp` are inbox; shaders precompiled at build |

(c) Others, all rejected. **Win2D** is 2D only: no meshes, depth buffer or skinning. **SkiaSharp**
(already shipped) has no depth buffer or real lighting; it stays for decoding and compositing
images. **Filament** is C++ with no maintained .NET binding. **HelixToolkit** is a full scene graph
on an archived DirectX binding. **Composition scene meshes** cannot skin. **Game engines**: far too big.

### Recommendation: (b), native Direct3D 11 with Vortice.Windows and SharpGLTF

The brief already rules out Electron as "too heavy for an always-on tray app". WebView2 is the same
Chromium cost: six to ten times the memory, a process group visible in Task Manager, a slow first
frame on a card that should simply appear, and a focus model that fights the no-activate card. Its
real advantage is authoring speed, a one-off cost here: two characters, one material family and one
fixed light rig need only a small purpose-built renderer. Vortice over Silk.NET: smaller, more
idiomatic, and it ships the WinUI 3 `SwapChainPanel` interop.

Honest limit: real time will not equal Cycles, but a matte vinyl blob is close to the best case.
Baked ambient occlusion, image-based lighting from the same HDRI as the Cycles scene, wrap diffuse,
a rim sheen, a soft contact-shadow quad and 4x MSAA should land near the photoreal target sample.

**Architecture.** `Stage3D` (one per app) owns the device, shaders, characters and light rig.
`LiveViewport` (one per surface) owns a small composition swap chain, a camera and character
instances, hosted in a `SwapChainPanel` or, for the buddy, a bare DirectComposition window. An
`IAvatarPresenter` (SVG, sprite or live) is picked by style, surface and device health.

**Keeping it light (rules, not suggestions):**

1. **Lazy.** No device until the first live surface is shown in the 3D style.
2. **Poster first.** A live surface shows its pre-rendered still at once, then cross-fades (150 ms)
   to the live view. The poster is also the failure path.
3. **Render on demand.** No free-running loop: `CompositionTarget.Rendering` is subscribed only
   while a clip, spring or drag is active. Settled means present once, unsubscribe, 0 fps.
4. **Caps.** 60 fps only during direct manipulation, 30 fps otherwise and always on battery.
   Energy saver or a remote session switches to pre-rendered. Hidden surfaces stop at once.
5. **Tear down** device and assets two minutes after the last live surface hides: memory returns
   to near baseline, and the device exists for a few minutes per hour at most.
6. **One renderer.** All surfaces share one device and one copy of each mesh and texture.
7. **Integrated GPU.** Use the minimum-power adapter; never software (WARP): fall back instead.

## 3. Surface map

| Surface | Mode | Why |
|---|---|---|
| Tray icon (16–32 px) | Scripted vector, all styles | The shell only takes an icon handle; nothing can render live there. Hand-tuned vector frames read better at 16 px than a shrunken photoreal render. |
| Toast image | Pre-rendered PNG still | The shell draws toasts from static image files: no animation, no input. |
| Nudge card | **Live 3D** | The main stage: short-lived, on screen for a reason, where poke, eye-follow and stretch demos pay off. Avatar slot grows from 72 to about 96 dip. |
| Eye-cue pill | Pre-rendered sprite, never live | It lasts 20 seconds and exists to make the user look away from the screen. A toy there defeats the feature. |
| Today window | **Live 3D** (Sprout, with Bloop) | Opened on purpose, so interaction is welcome. Home of the water gesture and the bond meter. |
| Meet Bloop / playground | **Live 3D**, richest | A normal window: orbit, poke, clip gallery, accessory try-on. Also the renderer's test bench. |
| Desktop buddy | **Live 3D**, opt-in, off by default | The purest form of the wish. Static sleep pose most of the time; still image as fallback. |
| Garden shelf | Stills + one live plant | Many plants must not mean many viewports. Tapping a plant "picks it up" into the single live viewport. |
| Achievement pop | Live if the card is open, else pre-rendered badge + scripted confetti | A warm device celebrates for free; a three-second pop does not justify starting one. |
| Weekly recap card | Pre-rendered stills + scripted count-up | It must export as a static, shareable image anyway. Composed with SkiaSharp. |

## 4. Interactions

| # | Idea | Where | Effort |
|---|---|---|---|
| 1 | **Poke to squish.** Volume-preserving squash on a damped spring, arms lag behind. In Flat and Soft 3D it is a XAML scale spring on the SVG. | everywhere | S |
| 2 | **Eyes follow the cursor.** Polled at 10 Hz, redraw only on change, stops after 2 s without movement. | live surfaces | S |
| 3 | **Drag to rotate.** Turntable with spring-back in the card; free orbit in the playground. | card, playground | S |
| 4 | **Pet.** A slow stroke across the top: happy closed eyes, brighter cheeks. | live surfaces | S |
| 5 | **Stretch demo mirrors progress.** The clip is bound to the countdown ring: reach, hold, release in step with the timer. | card | M |
| 6 | **High-five to finish.** When the timer ends Bloop raises a hand; clicking it counts as Done. The button stays. | card | M |
| 7 | **Water Sprout.** Drag the cup onto Sprout (or click the pot) to log a drink; leaves perk up. A 5-second Undo chip, because this changes logged data. | Today, shelf | M |
| 8 | **Continuous Sprout mood.** Leaf droop and colour follow hydration percent smoothly, not four fixed states. | Today, shelf, buddy | S |
| 9 | **Idle fidgets.** Blink, glance, tiny yawn, arm wiggle: one every 8–20 s, under a second each. | live surfaces | S |
| 10 | **Time of day.** Morning stretch-yawn, warmer light towards evening, sleepy eyes late, a Friday wave. | live surfaces | S |
| 11 | **Celebrate.** Celebrate clip, up to 60 confetti quads for 1.5 s, Sprout's flower pops open. | card, Today | M |
| 12 | **Pick up, drop, spin.** Drag Bloop up and let go: a fall, squash and bounce. Spin him fast and he gets spiral eyes for two seconds. | playground, buddy | M |
| 13 | **Thanks and good morning.** A heart chip after Done says thanks; clicking Bloop's first wave of the day waves back. No typing, no microphone. | card, buddy | S |
| 14 | **Buddy desk life.** Sits on the taskbar edge, drag to move, naps while the user is idle, peeks up for low-priority nudges, double-click opens Today. | buddy | L |

Rules for all of them:

- **Nothing unprompted for longer than two seconds**, no endless loops, sound off by default.
- **Focus, quiet hours, full-screen** (`FocusState.IsBusy`, scheduler holds): no motion at all.
  Cards are already held; the buddy hides or freezes in its sleep pose.
- **Reduced motion** (`UISettings.AnimationsEnabled` is false): static poses and instant pose
  changes; no springs, fidgets or confetti. Stretch demos show the hold pose.
- **No focus taking.** The viewport is not a tab stop and has an automation name ("Bloop, waving").
  Every interaction is optional: buttons and the tray menu always do the same job.
- **Private.** The cursor is read only while a live surface is visible and is never stored.

## 5. Evolution and bond

The characters grow with the user. One local file (`bond.json` in `%APPDATA%`) holds bond points,
the stage, unlocked items and the family record.

**What drives it**

- Healthy actions: stretch done +3, eye break +1, walk +4, each drink +1, water goal +5, a kept
  streak day +5.
- Affirming interactions, +1 each, at most 5 a day: a pet or gentle poke, the thanks chip, watering
  Sprout by drag, the good-morning wave-back. Kindness counts, but clicking cannot be farmed.
- Daily cap 40 (a typical good day earns about 30), so there is nothing to grind.
- **Never punish.** Points and stages only go up. A missed day costs nothing; at worst progress
  pauses. Rough handling just makes Bloop dizzy.

**Pacing (approved).** About a month to stage 5: thresholds of 40, 200, 450 and 900 points give
the first visible change on day 1–2, then one about weekly (typically days 7, 15 and 30). Sprout
takes about four weeks from seedling to bloom, then moves to the garden shelf.

| Bloop stage | When | What changes |
|---|---|---|
| 1 New friend | day 0 | Base Bloop: idle and blink, shy glances. |
| 2 Buddy | day 1–2 | Warmer blush and a small tuft on top. Wave-back emote, arm-wiggle fidget, a soft "pop" cue. |
| 3 Pal | week 1 | A touch rounder and 4% bigger. Yawn and look-around fidgets, heart eyes when petted, the first accessory slot (head), a chime cue. |
| 4 Best pal | week 2 | Slightly longer arms, satin sheen. Dance emote, second accessory slot (neck or hand). |
| 5 Radiant | week 4 | A gentle rim glow (steady; it pulses only while celebrating), a spin-jump celebrate clip, a sparkle cue. |

Sound cues are short voice-free WAVs behind one Sounds switch (off by default, muted in quiet
hours). A stage-up never interrupts: it plays at the next calm moment (the card after Done, or on
opening Today) as a 1.5-second blend, or as a cross-fade and one line of text with reduced motion.

**Sprout.** Growth uses the same good days: seedling, young (week 1), bushy (week 2), bloom
(week 4). It never shrinks: thirsty and sleepy are passing moods, not lost growth. Leaf-bone scale
grows smoothly between stages, so there is a little more plant every day.

**Generations.** When Bloop reaches stage 5 a **Baby Bloop** hatches and the cycle starts again
with the baby, who also looks after the new seedling. The grown-up Bloop stays on as a mentor in
the garden and Today view: it teaches the baby (who copies its stretches a beat late), cheers the
user on and drops into the occasional celebration. Over months a family and a garden form: a
visible long-term record that is never lost. The record is append-only, and a save writes a new
file, checks it and only then swaps it in, keeping the previous copy.

- **Baby in 3D:** the same rig and mesh with a `baby` shape key (big head, short arms) at about
  60% scale, plus its own clips (`baby-idle`, `baby-wobble`, `baby-copy`): est. +70 KB. Each
  generation gets a small trait (tuft shape, blush hue) as shader parameters, to tell them apart.
- **Live:** the garden scene in the Meet Bloop playground shows the family; Today shows the
  current Bloop, its mentor and the current Sprout. All instances share one mesh and texture set.
- **Cap:** at most 3 Bloops and 6 plants are rendered live (the newest). Everyone else lives in a
  gallery of stills: snapshots of the live viewport saved when a generation grows up or a plant is
  shelved. The recap card uses a family-portrait snapshot.

**Personality.** A big personality, minus the guilt. One strong, consistent voice
across cards, tooltips and recaps in the brief's three tones (cheerful, calm, dry-witty),
occasional funny reactions, and big-energy celebrations at 7, 30 and 100 streak days, with the
mentor joining in. The streak freeze is one free rest day per calendar week; a kept week gives +20
and, after stage 5, a keepsake (accessory, pot or flower colour). Explicitly **not** in scope:
guilt-tripping, sad-mascot pressure messages, loss framing, and (for now) social leagues.

**How a stage is built in 3D**

| Part | Technique | Size |
|---|---|---|
| Size and shape | Shape keys in `bloop.glb` (`tuft`, `plump`, `arms-long`, `baby`), exported without morph normals | est. 40 KB each |
| Look | Per-stage shader parameters in `stages.json` (blush, sheen, rim glow, scale); no new textures | 1 KB |
| Accessories | Separate `accessories.glb`, each under 300 triangles on a shared 256 px atlas, attached to socket nodes; only worn items are loaded | est. 15 KB each, 200 KB for ten |
| New clips | One glTF animation per fidget or emote, key-reduced | est. 5–15 KB each, 130 KB for eleven |
| Sound cues | Mono WAV under 0.4 s, played with the plain Win32 sound call | est. 15 KB each |

Live payload grows by about 0.6 MB. On pre-rendered surfaces the stage shows through a **portrait
set**: Cycles stills of `idle` and `wave` for each stage, adult and baby (20 stills, est. 1.5 MB),
used by toasts, the recap card, stage-up badges and the fallback poster. Action posters stay
stage-neutral, with scale and glow added as 2D layers. A chosen accessory reaches those surfaces
through the cached viewport snapshot. The tray icon and eye pill stay stage-neutral. Flat and Soft
3D show stages, the baby and the family with SVG overlays from the same bond data.

## 6. Asset pipeline

Source of truth is one `.blend` per character in the Blender asset pipeline, run headless
(`blender -b <file> -P <script>`). Both outputs come from the same scene so they cannot drift.

**Live path: Blender to glTF**

- `bloop.glb`: one skinned mesh, at most 6k triangles and 12 bones (root, body, squash, two per
  arm, two eyes), morph targets for blink, mouth and stage shapes, socket nodes for accessories.
- `sprout.glb`: pot, face, two bones per leaf, the four growth stages as switchable nodes, a
  flower. Leaf yellowing and flower colour are shader parameters, not textures.
- Clips at 30 fps, one glTF animation per action: `idle`, `blink`, `wave`, `stretch-overhead`,
  `side-bend`, `shrug`, `walk`, `celebrate`, `poke-squish`, `sleepy`, `drink`; Sprout `happy`,
  `thirsty`, `sleepy`, `celebrating`. The brief's other stretches follow the same pattern.
- Export: Y up, modifiers applied, no cameras or lights, no mesh compression, ambient occlusion
  in vertex colours, one 512 px face atlas. The same script writes `stage.json` (a camera per
  surface, key light, HDRI lighting coefficients) so the live frame lines up with its poster.
- Budgets: `bloop.glb` 800 KB, `sprout.glb` 600 KB, accessories 200 KB, lighting and shaders
  200 KB. Live payload at most 2 MB.

**Pre-rendered path: Cycles to images**

- Cycles for stills and sequences alike (under 300 frames at 256 px: minutes of render time), so
  the look never shifts between a poster and a sprite.
- Sequences are **sprite sheets** (an atlas plus a manifest like the tray `manifest.json`), not
  animated WebP or APNG: WinUI plays neither, and a sheet's decoded memory is bounded.
- PNG where the shell loads the file (toasts); lossy WebP with alpha elsewhere, decoded through
  SkiaSharp so no system codec is needed.
- Budgets: poster about 80 KB, eye-pill sheet 150 KB, badge 20 KB; pre-rendered total at most
  5 MB. The whole 3D style adds at most 11 MB to a build, libraries included.
- No metadata in exports (render stamp, PNG text chunks, glTF `generator`, `extras`). Outputs go
  to `Assets/Characters/3d/`; work files and test renders stay in the temp folder.

## 7. Style switch and fallback

`AvatarStyle` gains a third value: `flat`, `soft3d`, `3d`. Flat and Soft 3D keep their SVGs (poke
squish is a XAML spring) and never create a graphics device. 3D uses the live viewport on live
surfaces and Cycles stills and sprite sheets elsewhere. The tray keeps its vector loops throughout.

Fallback in the 3D style, each step silent and logged: live viewport, photoreal poster, soft3d
SVG, flat SVG (the last two are today's `ResolveAvatarPath`). Triggers: no hardware device, device
creation over one second, device removed twice in a session, energy saver, remote session, a bad
asset, or any exception while rendering. A render failure never takes down the card.

## 8. Milestones

Each is one focused build. Checks run from a temp copy, use UI Automation and debug triggers (no
injected input) and close every window they open.

**M1 — One live Bloop in the card.** `Stage3D`, `LiveViewport`, `bloop.glb` with `idle` and
`blink`, matte material and contact shadow, poster first, render on demand, teardown, fallback.
Behind a debug flag. Day one is a spike: a transparent swap chain over the acrylic no-activate card.
- RAM: at most +40 MB private memory while the card shows, compared with Soft 3D; within +8 MB of
  baseline three minutes after it hides.
- Focus: the foreground window is the same before the card, while it shows and after a poke.
- Static means 0 fps: the present counter in the debug log stops once the pose settles.
- CPU at most 1% during the idle clip; live view within 300 ms of the poster.
- A forced device failure shows the Soft 3D card; Flat and Soft 3D load no Direct3D modules.
- Look: beside the Cycles poster at card size, the difference is not obvious at arm's length.

**M2 — Bloop comes alive.** Interactions 1–6, 9–11 and 13, stretch clips bound to the countdown,
reduced-motion and battery policies, the bond engine and stage 2.
- Every interaction settles to 0 fps within two seconds; reduced motion presents only pose changes.
- Done, Snooze and Skip behave as before. Bond points never decrease in any test sequence.

**M3 — Sprout, Today and the playground.** `sprout.glb` with growth and mood blending, the water
gesture with Undo, the bond meter, stages 3–5 with accessories, the Meet Bloop window.
- Two live surfaces share one device (logged) and stay within +50 MB.
- Water by gesture equals water by button; Undo restores the exact previous total.
- A stage-up never shows during focus or full-screen and never takes focus.

**M4 — Pre-rendered everywhere else.** Render script, toast stills, eye-pill sprite, the portrait
set, badges, recap compositing, the three-way style menu out of debug.
- Every surface in section 3 shows the right asset in all three styles and all five stages.
- Build grows by at most 11 MB; no image carries metadata.

**M5 — Desktop buddy, garden and family.** A bare DirectComposition window (per-pixel alpha, tool
window, no-activate) with a window region matching the silhouette, so clicks beside Bloop reach
the window underneath; the garden scene with Baby Bloop, the mentor, the shelf and the gallery.
- Buddy off by default; hidden during full-screen and focus. Asleep means 0 fps.
- The full scene (3 Bloops, 6 plants) stays within +60 MB; the family record survives a stop mid-save.
- A 20-minute soak: under 5 MB memory growth, under 0.3% CPU. Survives sleep and device removal.

## 9. Risks and plan B

| Risk | Answer |
|---|---|
| Real-time look falls short of Cycles | Judged in M1. If it fails, live surfaces rest on the Cycles poster and go live only during interaction. |
| Transparent swap chain misbehaves over acrylic; buddy silhouette hit-testing fails | Spikes at the start of M1 and M5. Fallbacks: an opaque viewport with a matching backdrop colour; a tight rounded buddy window. |
| Renderer grows into an engine | Hard scope: two characters, one material family, one light rig, springs instead of physics. |

Plan B if M1 misses its memory or look targets: ship "3D" as pre-rendered sprite 3D, a Cycles
turntable (24 angles per pose) scrubbed by drag with eyes and squish as 2D layers. It keeps poke,
rotate and eye-follow and needs no graphics device.
