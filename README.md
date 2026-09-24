<img src="SellWise/images/icon.png" width="112" align="right" alt="SellWise icon">

# SellWise

**Know what to sell, where, and for how much.** A Dalamud plugin for FINAL FANTASY XIV that looks at everything you own (bags, crystals, saddlebags, and every retainer's inventory and market listings), prices it with live [Universalis](https://universalis.app) data, and tells you what to **list, relist, vendor or hold**, with a price ready to paste. It also finds the most profitable things you can craft, hands the gathering and crafting to GatherBuddy Reborn or Artisan, keeps your gear repaired, and tells you whether your stats (plus which food) will hit HQ.

SellWise never lists, buys or changes prices on the market board for you. You paste the prices yourself.

> The screenshots below are renders of the plugin's screens made from its UI code and real item icons. In game, the fonts and spacing follow your Dalamud settings.

---

## What to sell

![What to sell](docs/images/sell.png)

Every item you own, grouped by item and quality, with a verdict and a price:

| Verdict | Meaning |
|---|---|
| **List** | Undercut the cheapest listing and it should sell soon. The price is ready to copy. |
| **Slow** | Worth listing, but it will take a while; use a spare slot. |
| **Hold** | Someone has dumped the price far below what it really sells for. Wait, or list just behind the dump. |
| **Vendor** | An NPC pays about as much as the market would after tax. Not worth a slot. |

The detail panel shows the lowest listing, the recent median sale, sales per day, the cheapest price on your data center, and a chart of recent sales. A dot marks the best picks for your free retainer market slots.

**How prices are chosen**
- The fair price is the **median** of recent same-quality sales, so one troll sale can't skew it.
- Your own retainers' listings are ignored when looking for the cheapest competitor.
- Listings far below the median are treated as **dumps**: you're told to line up behind them instead of racing them to the bottom.
- HQ sellers compete only with HQ listings. NQ sellers compete with everything, because a cheap HQ listing takes NQ buyers too.
- Market tax, the undercut amount, the dump floor, and the vendor margin can all be changed in Settings.

## My listings

Everything your retainers have on the board, flagged **Undercut** (with the price to change to), **Raise price** (you're far below the next seller), or **OK**. The server info bar shows `SellWise: N undercut` so you know without opening the window.

## Craft for profit

![Craft for profit](docs/images/craft.png)

- **Scan:** prices every marketable recipe on your world (about a minute; results are cached for 30 minutes).
- **Material costs:** each material is costed at the cheapest of gathering it, NPC vendors, the market board, or crafting it yourself. Intermediate crafts are costed too, up to 3 levels deep.
- **Ranking:** by **batch profit**, which is profit per craft × how many crafts your world's market absorbs in about two days. That keeps you from flooding your own market.
- **Locked recipes:** marked **Locked**, with the reason (level, master recipe book, or quest).
- **Quality check:** uses your saved stats for that job to say whether you'll hit **HQ**, or the top collectability tier for scrip collectables. If you won't, it suggests the **cheapest food and potion** that gets you there, counting what's already in your bags as free.
- **Materials list:** shows what's in your bags and on your retainers, with one-click **Gather** buttons.

### While it runs

![Craft pipeline](docs/images/craft-running.png)

Press **Gather + craft** and GatherBuddy Reborn's Vulcan pipeline gathers what's missing, pulls from retainers, and crafts. Or press **Craft with Artisan** if you already have the materials. The screen shows a four-step pipeline (gather, craft parts, craft, sell), highlights the material being gathered right now, and fetches a fresh price once your crafts arrive.

## Gear, travel and retainers

- **Auto-repair:** before a craft job (and between Artisan steps), gear below your threshold is **self-repaired with Dark Matter** if your crafters are high enough. Otherwise SellWise **teleports to a random city that has a mender**, walks there with vnavmesh, and repairs everything.
- **City teleport:** one click takes you to a random major city you've attuned to; choose which cities in Settings.
- **Retainers:** the game only lets plugins read a retainer's inventory while you're talking to it, so open each retainer once at a summoning bell. SellWise remembers what it saw until your next visit.

## Commands

| Command | Does |
|---|---|
| `/sellwise` or `/sw` | Open the window |
| `/sw craft` | Open Craft for profit |
| `/sw refresh` | Refresh prices now |
| `/sw repair` | Repair gear now (Dark Matter, or a city mender) |
| `/sw city` | Teleport to a random attuned major city |
| `/sw bell` | Walk to the nearest summoning bell in this zone (vnavmesh) |
| `/sw stop` | Stop everything SellWise started (repair trip, craft job, walking) |
| `/sw config` | Settings |

## Installing

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories** and add:
   ```
   https://raw.githubusercontent.com/V1B3Gaming/SellWise/main/repo.json
   ```
   Tick **Enabled**, click **+**, then **Save and Close**.
2. Open `/xlplugins`, search for **SellWise**, and click **Install**. Updates arrive automatically.

## Dependencies

**Required**

| | What for |
|---|---|
| [Dalamud](https://github.com/goatcorp/Dalamud) (API 15, via XIVLauncher) | Runs the plugin. |
| [Universalis](https://universalis.app) | Market listings, sale history and prices. No account needed; data is as fresh as the last time someone with Dalamud viewed that item's market board. |

**Bundled** (installed with SellWise; nothing to do)

| | What for |
|---|---|
| [ECommons](https://github.com/NightmareXIV/ECommons) | Clicks the repair, confirmation and NPC menu windows, the same way GatherBuddy Reborn, Artisan and AutoDuty do. |

**Optional plugins** (SellWise works without them; the related buttons are disabled until they're installed)

| Plugin | Used for |
|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) | Walking to a summoning bell or a mender. |
| [GatherBuddy Reborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn) | **Gather + craft** (its Vulcan pipeline), the per-material **Gather** buttons, and cordials/food while gathering (set in its own settings). |
| [Artisan](https://github.com/PunishXIV/Artisan) | **Craft with Artisan** when you already have the materials. |

**Building from source**

| | Version |
|---|---|
| .NET SDK | 10 |
| Dalamud.NET.Sdk | 15.0.0 (uses the Dalamud files XIVLauncher installs) |
| xUnit | 2.9 (tests only) |

```
dotnet build SellWise/SellWise.csproj -c Release
dotnet test SellWise.Tests
```
Set `SELLWISE_LIVE=1` to also run the tests that call the real Universalis API.

## Releasing

Push a version tag and GitHub Actions builds against the current Dalamud release, runs the tests, attaches `latest.zip` to a GitHub release, and bumps `repo.json` so Dalamud offers the update:
```
git tag v0.2.0
git push origin v0.2.0
```

## Good to know

- SellWise is **advisory for selling**. It never lists, buys or reprices on the market board.
- Gathering and crafting are done by GatherBuddy Reborn and Artisan, and carry the same risk as running those plugins yourself. Stay at the keyboard.
- Crafting quality estimates assume NQ materials and no lucky conditions, so real results can only be the same or better.
- Price data comes from Universalis uploads. An item nobody has looked at recently may show stale prices.

## Credits

Market data by [Universalis](https://universalis.app). Crafting formulas follow the [Teamcraft simulator](https://github.com/ffxiv-teamcraft/simulator). FINAL FANTASY XIV © SQUARE ENIX CO., LTD. SellWise is a fan-made tool and is not affiliated with Square Enix.
