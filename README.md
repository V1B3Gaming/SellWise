<img src="SellWise/images/icon.png" width="96" align="right" alt="">

# SellWise

A Dalamud plugin (API 15) that looks at everything you own (bags, crystals, saddlebags, every retainer and their market listings), pulls live prices from [Universalis](https://universalis.app), and tells you what to **list, relist, vendor, or hold**, with a price to type in.

## What it does

The window has a sidebar (Sell, Listings, Craft, Retainers, plus City, Repair and Settings) and a list-and-detail layout: pick something on the left, see the answer on the right. The accent colour (violet, teal or silver) is in Settings.

| Screen | Shows |
|---|---|
| **What to sell** | Every stack you own, grouped by item and quality, with a verdict, suggested price (click to copy), net gil after tax, estimated days to sell, and a detail panel with lowest listing, recent median, sales per day, data-center cheapest and a recent-sales chart. A dot marks the best picks for your free market slots. |
| **My listings** | Everything your retainers have on the board: **Undercut** (relist at X), **Raise price** (you're far below the next seller), or **OK**. |
| **Craft for profit** | Scans every recipe you can make, ranks them by batch profit, and runs the gather-and-craft for you (see below). |
| **Retainers** | Which retainers have been scanned and when, listing slots used, and gil. |

The server info bar shows `SellWise: N undercut` when any of your listings get undercut.

### How prices are chosen
- **Fair price** is the *median* of recent sales of the same quality (default: last 14 days). Median rather than mean, so one troll sale doesn't skew it.
- **Suggested price** is the cheapest competitor minus 1 gil (or a %). Your own retainers' listings are ignored.
- **Dumped listings.** Any listing below the floor (65% of the median) is treated as a dump. You get **Hold**, with a price that puts you next in line once the dump sells, instead of racing it to the bottom.
- **Competition by quality.** HQ sellers only compete with HQ listings. NQ sellers compete with everything, because a cheap HQ listing takes NQ buyers too.
- **Vendor check.** If the NPC vendor pays within 1.25x of what the market would net after tax, the verdict is **Vendor**, since it isn't worth a slot.
- **Days to sell** = (units listed cheaper than you + your quantity) ÷ units sold per day. More than 14 days is marked **List (slow)**.

Every threshold can be changed under `/sellwise config`.

## Craft for profit
1. **Scan** (`/sellwise craft`). Covers every marketable recipe; expert and specialist recipes are skipped by default. Each result is marked **Unlocked** or **Locked**, and hovering a locked one shows why: job level too low, a master recipe book you don't have, or an unfinished quest. Locked recipes can't be sent to Vulcan or Artisan; tick **Unlocked only** to hide them. It prices every result through Universalis' bulk endpoint, keeps items that sell at least once a day, then prices their materials. A scan takes about a minute; prices are cached for 30 minutes.
2. **Material costs.** Each material is costed at the cheapest of gathering it, buying it from an NPC vendor, buying it on the market board, or crafting it. Intermediates are costed recursively, up to 3 levels deep.
   - **Profit** counts gathered materials at market value (their opportunity cost).
   - **Cash profit** counts them as free.
3. **Ranking by batch profit.** Batch profit = profit per craft × the number of crafts your world's market absorbs in about 2 days (max 99). "Market gil/day" is available as a sort, but it assumes you capture every sale on the world, so treat it as an upper bound.
4. **Make it.** Pick a quantity, then choose a backend:
   - **Gather + craft with GatherBuddy (Vulcan):** runs `/vulcan craft <recipe> <qty>`. GatherBuddy Reborn gathers missing materials, pulls from retainers, and crafts, intermediates included.
   - **Craft with Artisan:** crafts only. SellWise queues missing intermediates first, then the final item, through Artisan's IPC. Everything must already be in your bags; the materials table shows what's missing, with a **Go gather** button (`/gather`) for each gatherable material.
5. **Price it.** SellWise watches your bags until the items arrive, fetches fresh prices, and shows the list price.

### Quality check
Each recipe's detail shows whether you'll hit **HQ** (or, for scrip collectables, the top collectability tier) with your stats:
- SellWise remembers each crafting job's stats whenever you're on that job with no food or medicine active, because the game only shows the current job's stats.
- A built-in simulator, using the same formulas as Teamcraft's, searches for a rotation. If it finds one, you can hit the target. If it falls short, a full solver might still do a little better.
- If you're short, it tries crafting foods and medicines from the game data and suggests the cheapest combination that gets you there: free if it's in your bags, otherwise the current market price.
- It assumes NQ materials, so HQ materials only make things easier.

Gathering cordials and food are handled by GatherBuddy Reborn's own consumable settings.

### Gear repair
- Durability is checked before every craft job and between Artisan steps.
- Below your threshold (Settings > Gear), SellWise **self-repairs with Dark Matter** when your crafters are high enough.
- Otherwise it **teleports to a random enabled city that has a mender**, walks there with vnavmesh, and repairs everything.
- During a Vulcan run, GatherBuddy Reborn repairs on its own; set its threshold in its settings.
- The sidebar's wrench shows your lowest durability; click it to repair now.

The gathering and crafting are done by GatherBuddy Reborn and Artisan, and carry the same risk as running them yourself. Stay at the keyboard while they run.

## Commands
- `/sellwise` (or `/sw`): open the window
- `/sellwise city`: teleport to a random major city you've attuned to (pick which cities in settings)
- `/sellwise bell`: walk to the nearest summoning bell in your current zone (needs **vnavmesh**). Only runs when you ask.
- `/sellwise repair`: repair gear now (self-repair or a city mender)
- `/sellwise stop`: stop everything SellWise started (repair trip, craft job, walking)
- `/sellwise refresh`: force a price refresh
- `/sellwise config`: settings

## Retainers need one visit
The game only loads a retainer's inventory while you're talking to it. Visit a summoning bell and open each retainer once; opening the sell list also captures its listings. SellWise saves each snapshot to `inventory.json` in its config folder and updates it every time you open that retainer again. Saddlebags work the same way: open them once.

## Integrations
- **vnavmesh** (optional): "Walk to nearest bell" pathfinds to the closest summoning bell and targets it when you arrive.
- **GatherBuddy Reborn** (optional): the Vulcan gather-and-craft pipeline, plus `/gather` for individual materials.
- **Artisan** (optional): crafts through its `CraftItem` IPC.
- **ECommons** (bundled): clicks the repair and confirmation windows the same way GatherBuddy, Artisan and AutoDuty do.
- **Universalis** (required): market data. It's only as fresh as the last time someone with Dalamud viewed that item's market board page.

SellWise never lists, buys, or changes prices for you. It's advisory only, and you enter prices yourself.

## Building
```
dotnet build SellWise/SellWise.csproj -c Release
dotnet test SellWise.Tests
```
Needs the .NET 10 SDK and XIVLauncher's Dalamud dev files at `%AppData%\XIVLauncher\addon\Hooks\dev`.
Set `SELLWISE_LIVE=1` to also run a smoke test against the real Universalis API.

## Installing from the custom repository
1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories**: add
   `https://raw.githubusercontent.com/V1B3Gaming/SellWise/main/repo.json`, tick **Enabled**, click **+**, then **Save and Close**.
2. `/xlplugins` → search **SellWise** → **Install**. Dalamud offers updates automatically when a new release is published.

## Publishing a release
Push a version tag and the GitHub Actions workflow does the rest: it builds against the current Dalamud, runs the tests, attaches `latest.zip` to a GitHub release, and bumps `repo.json`.
```
git tag v0.2.0
git push origin v0.2.0
```

## Loading a local build (development)
1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: add the full path to `SellWise\bin\Release\SellWise.dll`, then click **+** and save.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **SellWise**.

Don't have the dev build and the repository version enabled at the same time; they share an internal name.
