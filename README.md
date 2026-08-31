# JDFixer — per-map fork

> **This is not the official JDFixer.** It is an unofficial fork of
> [zeph-yr/JDFixer](https://github.com/zeph-yr/JDFixer) by Zephyr, who wrote essentially all of
> the mod. This fork adds one feature and retargets it at newer game versions.
>
> **Do not report problems with this build to Zephyr.** If something here misbehaves, it is far
> more likely to be a fault in this fork than in the original — raise it
> [here](https://github.com/kaedon78/JDFixer/issues) instead. If you want the original mod,
> supported by its author, get it from
> [zeph-yr/JDFixer](https://github.com/zeph-yr/JDFixer).

Everything JDFixer already does is unchanged and documented in
**[README.upstream.md](README.upstream.md)** — automated NJS-based JD/RT preferences, beat-fraction
snapping, song-speed handling, the tournament and multiplayer UIs. Read that first; this page only
covers what the fork adds.

## What this fork adds: per-map JD/RT memory

JDFixer remembers the jump distance and reaction time each beatmap was last played at, and restores
it when you select that beatmap again.

A remembered value takes precedence over Automated Preferences for that map, so a map you have tuned
by hand keeps what you gave it instead of being pulled back to an NJS setpoint. Maps you have never
tuned are unaffected and still go through Preferences exactly as before.

Values are found in this order:

1. **A local BeatLeader replay** — what the game actually used. BeatLeader keeps one replay per
   beatmap, so that folder is already a "last played" index. Read from disk; no network access.
2. **Your own plays**, recorded as you play them.
3. **A download from BeatLeader** — off by default. When enabled, it asks about a selected beatmap
   that has no local replay and no play of its own.
4. **A configured default**, so an unplayed map does not silently inherit the previous map's slider.

Speed modifiers are divided back out, so one run at 0.85× does not permanently move a map's
setpoint. Practice-speed plays are skipped, because that factor is not recoverable from a replay.

A value follows a map across a re-upload, matched on song name and mapper — useful because a
re-upload changes the hash the value is stored under.

### Using it

A **Per-Map Values** section appears in both gameplay tabs, next to the sliders it acts on, with a
Forget/Restore button whose state tracks whether the current map has a stored value, plus a capture
button and a default toggle. Mod Settings gains the replay options and **Forget All**.

**Forget** writes a tombstone rather than deleting the entry. The replay is still on disk, so a
deleted entry would simply be seeded straight back in. A tombstone also blocks the download for that
map, so pressing Forget does not result in a request to a third party about that very beatmap.

### Settings

| Setting | Default | |
|---|---|---|
| `remember_per_map` | **off** | the feature itself — turn this on first |
| `use_replay_values` | on | seed values from local BeatLeader replays |
| `download_replay_values` | **off** | ask beatleader.xyz about maps with no local replay |
| `use_default_for_unsaved` | off | use a fixed default for maps with no stored value |
| `default_jumpDistance` | 20 | |
| `default_reactionTime` | 500 | |

`download_replay_values` is the only setting that causes network traffic, and it is off unless you
turn it on.

Values live in `UserData/JDFixer_MapValues.json`, deliberately separate from `UserData/JDFixer.json`
— the latter is a BSIPA-generated store rewritten whole on every config change, while this
collection grows with your song library.

## Which build do I want?

| Beat Saber | Branch | Release |
|---|---|---|
| 1.40.5 | [`BS_1.40.5`](../../tree/BS_1.40.5) | `v8.0.0` |
| 1.44.3 | [`BS_1.44.3`](../../tree/BS_1.44.3) | `v8.5.0` |
| 1.45.0 | [`BS_1.45.0`](../../tree/BS_1.45.0) | `v8.5.1` |

The 1.44.3 and 1.45.0 builds are identical apart from the declared game version. `BS_1.26_Offset` is
kept untouched at the commit this fork was made from, for reference.

For older game versions, use the original mod's own releases — this fork has nothing to offer there.

## Install

Download `JDFixer.dll` from the [release](../../releases) matching your game version and drop it into
your Beat Saber `Plugins` folder, replacing any existing `JDFixer.dll`.

Requires **BSIPA**, **BSML** and **SiraUtil**, as the original does. Still incompatible with
**NjsFixer** and **LevelTweaks**.

## Differences you should know about

- **The Custom Campaigns integration is compiled out.** That mod's assembly was not available to
  build against. JDFixer's own runtime check already fell back to the base mission handler whenever
  Custom Campaigns is absent, so for anyone not running it the behaviour is identical — but if you
  do run Custom Campaigns, its dedicated handling is not in these builds. Define `CUSTOM_CAMPAIGNS`
  and rebuild to restore it.
- **Version numbers diverge from upstream.** This fork starts at 8.0.0; the original's own numbering
  is unrelated to it.

## Credits

JDFixer is by **Zephyr** — [zeph-yr/JDFixer](https://github.com/zeph-yr/JDFixer),
Copyright © 2021–2025, www.xephai.com. Original documentation is preserved verbatim in
[README.upstream.md](README.upstream.md), including how to support the author's work.

Originally derived from Kylemc1413's [NjsFixer](https://github.com/Kylemc1413/NjsFixer).

Licensing is unchanged — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
