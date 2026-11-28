# Bloop alive: characters and idle life

## What Bloop can do today

- **Model:** `bloop.glb`, one skinned mesh (24 nodes: root, body, four jelly bones, two 3-bone arms, two 2-bone
  stubby legs, a head socket for hats). 12 morph targets: squish, stretch, lean L/R, breathe, blink, eyes happy,
  eyes squeeze, mouth smile, mouth O, mouth grin, cheeks up.
- **Clips (11):** idle_breathe, blink, poke_squish, wave, stretch_overhead, side_bend, shoulder_shrug, walk,
  star_jump, high_five, celebrate. All eight stretches in `StretchLibrary` fall back to one of three clips.
- **Springs (`BloopMotion`):** squash, roll, pitch, yaw, hop, dent and two arm springs at 240 Hz sub-steps; morph
  pulses and sustains. Reactions: tap (escalating moods), long press (pat on the head melts him), rub/tickle,
  drag an arm, swipe, a "settle down" shrug.
- **Idle:** breathing (fades out after 10 s), blinks (rare when asleep), body turn towards the pointer, and on Home a
  wave or shrug every 20-40 s for 3 minutes after the last touch. Rendering stops at 0 fps when nothing moves.
- **Sounds:** squish, boing, giggle, purr, whoosh, clap, hop, bonk (off unless sounds are on; quiet in calls/Focus).
- **Looks (Pro):** six colours, three hats, three pots. 2D flat art, soft 3D pictures and the live 3D view.

## Ideas, ranked by delight per effort

| # | Idea | Delight | Effort | Status |
|---|---|---|---|---|
| 1 | **Pip**, a second character with arms and legs, chosen in Customise; built in code (jointed, soft, low-poly) and driven by the same animator, springs and gestures | very high | M-L | built |
| 2 | Pip demonstrates all eight stretches properly (lunge for hip flexors, calf raises, seated twist, neck roll, wrist stretch) instead of three shared clips | very high | M | built |
| 3 | Pip **moonwalks** for the walk reminder (body turned three-quarters, feet sliding back in turn) | very high | S | built |
| 4 | Look around: now and then Bloop glances left and right, then back at you | high | S | built |
| 5 | Yawn: a big O mouth, squeezed eyes and a stretch, more often late in the day | high | S | built |
| 6 | Dozing at night (23:00-06:00): heavy eyelids that cost nothing (drawn once, no loop), the odd nod-off; a touch wakes him with a little start | high | S | built |
| 7 | A kind sulk when a reminder is snoozed: droop, eyes down, then a brave smile | high | S | built |
| 8 | Rare easter egg: a full happy spin with a hop (about 1 in 25 idle moments) | medium | S | built |
| 9 | Morning wake-up stretch the first time Home shows after 06:00 | medium | S | built (as part of idle choice) |
| 10 | Props per nudge: a water glass for water, eye-mask for eye rest | high | M | later |
| 11 | Seasonal touches: a scarf in December, sunglasses in summer | medium | M | later |
| 12 | Garden/Home moments: Sprout's leaves clap when a goal is met, confetti quads | medium | M | later |
| 13 | Pip-specific idles: foot tap, weight shift, hands on hips | medium | S | later |
| 14 | Eyes (not only the body) track the cursor using an eye-offset morph | medium | M | later |
| 15 | Colour looks for Pip (tint his hoodie with the Bloop colour) | low | S | later |

## Rules kept

- Character motion stays springy; UI chrome uses `Motion.Settle`.
- Reduced motion: idle glances, spins, nod-offs and the moonwalk body turn are skipped or made static; stretch demos
  still show (they are the content) but without bounce.
- Idle life never plays a sound, never opens anything and never takes focus. It runs only while the panel is shown,
  only within the existing idle window, and stops the render loop when done.
- The chosen character is saved in `Look.Character` (a plain string; old files load as Bloop). The character choice
  is free; colours, hats and pots stay Evolution (Pro).
