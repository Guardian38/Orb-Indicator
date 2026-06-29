# Defect Orb Damage Indicator

**English** | [한국어](README.ko.md)

> A *Slay the Spire 2* mod · by **GuardianGD**
> Minimum game version `v0.107.1` · version `0.1.0`

A mod that previews, on each enemy's health bar, how much **Defect Orb damage** and **Poison** will land — and **what will kill that enemy**. It extends the game's existing preview conventions (Poison/Doom markers), so there's nothing new to learn.

---

## Why it exists

The Defect's orbs deal passive damage to enemies at the end of every turn. If an enemy is **killed by orbs, no damage is wasted**; if it survives, it acts before the next orb tick — so *"does it die to orbs?"* directly drives combat decisions.

- **The more orbs you have**, the harder it is to add up Lightning / Glass / Dark / Hailstorm / evoke damage in your head.
- **In multiplayer**, players often waste energy killing an enemy with a card when it was already going to die to orbs.

This mod visualizes each orb's damage and whether it lands a kill, right on the health bar, to support these calls.

---

## Features

### At a glance

- Draws an **orb-damage preview segment** (Defect blue) onto the enemy health bar.
- Uses the **HP number color** to tell you *what lands the killing blow*.
- **Hover an enemy** to see a per-source breakdown and the resulting HP.

### HP number color — by killing blow

In the timeline chain (**Orbs → Poison → Doom**), the color is decided by *who finishes the enemy off*.

| Color | Meaning | Notes |
|-------|---------|-------|
| 🔵 **Blue** (`#87CEEB`) | Killed by orbs alone | Dies at *your* turn end → can't exploit enemy-turn mechanics like Stun (a warning) |
| 🟢 **Green** | Killed by orbs + Poison (Poison finishes) | Dies at the start of the enemy turn |
| 🟣 **Purple** | Killed only once Doom resolves | Left to the game's own logic (vanilla) |

> Why the warning color (blue) applies **only to orb-only kills**: if orbs land the killing blow, the enemy dies at your turn end and you lose access to enemy-turn mechanics like Stun. If Poison/Doom finishes instead, the enemy dies during the enemy turn and those mechanics still work — so the vanilla colors are kept.

### Orb segment visualization

The health bar lays out slices in timeline order — **remaining HP → (Doom) → Poison → Orbs → lost HP** — and paints the orb slice Defect blue, with a chevron seam that blends smoothly into the existing Poison/Doom previews.

### Hover tooltip

Hovering an enemy breaks the incoming damage down one line per source — **(in-game icon) (localized name) (amount) damage** — with the **remaining HP** at the bottom ("Killed" if lethal). Lines are sorted by trigger order.

```
⚡ Lightning Orb     8 damage
🔶 Glass Orb        12 damage
☠  Poison            6 damage

Remaining HP        24
```

### What the calculation covers

It simulates the live game state, so it accounts for all of the following:

- **Orb types**: Lightning, Glass, Dark (Consuming Shadow evoke), and Frost + Hailstorm. (Frost on its own and Plasma deal no damage → not shown.)
- **Passive / evoke**: end-of-turn passives, Consuming Shadow evoke, **Thunder** bonus damage, and trigger order (Hailstorm → passive → evoke).
- **Damage boosts**: Focus and Infused Core are reflected automatically via live values; multi-trigger passives such as **Gold-Plated Cables** (including Glass losing value per trigger).
- **Block & mitigation**: orbs are blocked, Poison pierces; Intangible / Hard to Kill caps; consumable mitigation (Hardened Shell / Buffer / Slippery); Poison Accelerant amplification and the Hardened Shell cap.
- **Multiplayer**: stacks multiple Defects' cumulative damage in seat order, following the per-phase trigger sequence.

### Extras

- **Localization**: English (default) and Korean finalized; Simplified Chinese and Japanese are drafts. Mod strings live in external `localization/*.lang` (JSON), so translation fixes need no rebuild.
- **Debug**: the console command `orbdebug` toggles a raw-number dump in the tooltip.

---

## Installation

### Steam Workshop (recommended)

Subscribe to the mod on the Workshop and it installs and updates automatically. It loads on game start.

### Direct download (GitHub Releases)

If you don't use the Workshop, grab a build from [Releases](../../releases) and drop it into the game's mods folder.

1. Extract the release and place the `orb_dmg_indicator` folder (containing `orb_dmg_indicator.dll`, `mod_manifest.json`, and `localization/`) at:
   `…\Slay the Spire 2\mods\orb_dmg_indicator\`
2. Launch the game; the mod loader scans the `mods` folder and loads it automatically.

> `affects_gameplay` is `false` — it only adds indicators and does not change combat logic.

---

## Compatibility

- All patches are wrapped in try/catch so our errors never break the game or other mods, with multi-mod coexistence in mind.
- **BetterSpire2** integration (so its incoming-damage total excludes enemies that will die to orbs) is planned.
