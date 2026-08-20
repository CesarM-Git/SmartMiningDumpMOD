# Smart Mining Dump

A mod for [Captain of Industry](https://www.captain-of-industry.com/) that adds a
per-tower toggle telling mining trucks to dump waste inside the tower's own zone
instead of hauling it to storage.

**Version 1.0.3** · Game **0.8.7** · by Nimb

## What it does

Every mining tower inspector gains a **"Prefer Dumping"** checkbox. When it is on,
trucks assigned to that tower will try to dump dumpable materials into dumping or
levelling designations inside the tower's zone **before** falling back to normal
storage delivery.

Useful when you are levelling terrain near a mine and would rather the overburden
went into the hole next to it than across the map into a storage you will have to
empty later.

- **Per tower.** Each tower has its own setting; towers default to off.
- **Console command.** `smart_dump` toggles the preference without opening the
  inspector.
- **Persistent.** The setting is saved per game.
- **Falls back cleanly.** If there is nowhere to dump, trucks behave exactly as
  they normally would.

## Install

Extract the release zip into your Captain of Industry mods folder, then enable
**Smart Mining Dump** in the in-game mod list.

Safe to add to and remove from existing saves. Preferences live in a JSON file
inside the mod folder, one per save, so removing the mod just returns every tower
to standard behaviour.

## Notes and limits

- Only materials the game marks **dumpable** are affected. Ore still goes where
  ore goes.
- Dumping targets must be inside the **tower's own zone** — designations
  elsewhere on the map are not considered.
- The mod co-exists with other mods that replace the mining tower inspector: it
  detects an existing replacement and stacks on top of it rather than clobbering
  it.

## Compatibility

Requires game **0.8.7**. For 0.8.3–0.8.6 use **v1.0.0**.

Every version is verified against the decompiled game assemblies before release —
see [`CHANGELOG.md`](CHANGELOG.md) for what was checked each time.

## Building from source

Requires the game installed and two environment variables:

| Variable | Value |
| --- | --- |
| `COI_ROOT` | your Captain of Industry install directory |
| `COI_MODS` | the game's mods directory |

```bash
dotnet build src/SmartMiningDumpMOD.csproj -c Release
```

The build reads the version from `src/manifest.json`, deploys to
`%COI_MODS%\SmartMiningDumpMOD\`, and produces `SmartMiningDumpMOD_<version>.zip`
next to it.

## License

See [`LICENSE`](LICENSE).
