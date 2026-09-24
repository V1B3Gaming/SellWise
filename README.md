<p align="center">
  <img src="SellWise/images/icon.png" width="128" alt="SellWise icon">
</p>

<h1 align="center">SellWise</h1>

<p align="center">
  <b>Know what to sell, where, and for how much.</b><br>
  A market, crafting and retainer helper for FINAL FANTASY XIV, built on Dalamud.
</p>

<p align="center">
  <a href="#installing">Install</a> ·
  <a href="#what-to-sell">Selling</a> ·
  <a href="#craft-for-profit">Crafting</a> ·
  <a href="#dependencies">Dependencies</a>
</p>

---

Ever stared at a full inventory and a stack of retainers and wondered what's actually worth anything? That's what SellWise is for.

It looks through your bags, your chocobo saddlebag and your retainers, checks live prices on [Universalis](https://universalis.app), and tells you what to put on the market board, what to sell to a vendor, and what to sit on for now. When you want to make gil rather than just clear space, it finds the crafts worth making on your world and can hand the gathering and crafting off to GatherBuddy Reborn or Artisan.

It won't touch the market board for you. It tells you the price; you do the listing.

<sub>The screenshots are renders of SellWise's screens, drawn from its UI code with real item icons. In game, fonts and spacing follow your Dalamud settings.</sub>

## What to sell

![What to sell](docs/images/sell.png)

Everything you own shows up in one list, with a suggestion for each item:

- **List:** it'll sell. Undercut the cheapest listing at the price shown (there's a Copy button).
- **Slow:** worth listing, but don't expect it to move quickly. Good for a spare slot.
- **Hold:** someone's dumped it way below what it normally sells for. Wait it out, or list just behind them.
- **Vendor:** an NPC pays about as much as the market would. Not worth a retainer slot.

Click an item and you'll see what it's been selling for, how many sell a day, the cheapest one on your data center, and a quick chart of recent sales. Items marked with a dot are the best use of your free retainer slots.

A few things it does so you don't get burned:
- It judges prices by the **median** of recent sales, so one weird sale doesn't throw everything off.
- It ignores your own retainers when looking for who to undercut.
- If someone dumps an item for a fraction of its worth, it won't tell you to chase them to the bottom.
- HQ and NQ are priced separately, since a cheap HQ listing steals NQ buyers too.

You can tweak the undercut amount, tax rate and the rest in Settings.

## My listings

This screen keeps an eye on what your retainers already have up. If someone undercuts you, it tells you the new price to use. If you've priced something way too low, it tells you that too. A little `SellWise: 2 undercut` note shows up in the server info bar so you know without opening anything.

## Craft for profit

![Craft for profit](docs/images/craft.png)

Hit **Scan** and SellWise prices every recipe that sells on your world. It takes about a minute the first time, then it's cached for half an hour. For each recipe it works out:

- **What the materials really cost:** the cheapest of gathering them, buying from a vendor, buying on the market, or crafting the parts yourself.
- **How much you'd actually make:** it ranks by what one batch earns, sized to what your world's market can take in a couple of days, so you don't end up undercutting yourself.
- **Whether you can make it:** locked recipes say why (level, a master recipe book, or a quest).
- **Whether you'll hit HQ:** it uses your saved stats for that job and simulates the craft. If you'd fall short, it suggests the cheapest food and potion to get you there, and anything already in your bags counts as free. For scrip collectables it aims for the top reward tier.

### Letting it run

![Craft pipeline](docs/images/craft-running.png)

**Gather + craft** hands the job to GatherBuddy Reborn, which goes and gathers whatever's missing, grabs anything sitting on your retainers, and crafts it. If you've already got the materials, **Craft with Artisan** skips straight to crafting. You can watch it move through gathering, crafting the parts, crafting the item and selling. When it's done, SellWise pulls a fresh price so you know what to list at.

## The little extras

- **Repairs:** before a craft job starts (and between Artisan steps), SellWise checks your gear. If it's getting low, it repairs with Dark Matter if you can, or pops over to a city with a mender and gets it done there.
- **City teleport:** one click sends you to a random major city you've unlocked. You pick which ones count in Settings.
- **Retainers:** the game only lets plugins see a retainer's items while you're talking to them, so open each retainer once at a summoning bell. SellWise remembers what it saw after that.

## Commands

| Command | What it does |
|---|---|
| `/sw` | Open SellWise (`/sellwise` works too) |
| `/sw craft` | Jump to Craft for profit |
| `/sw refresh` | Refresh prices |
| `/sw repair` | Repair your gear now |
| `/sw city` | Teleport to a random major city |
| `/sw bell` | Walk to the nearest summoning bell (needs vnavmesh) |
| `/sw stop` | Stop whatever SellWise is doing |
| `/sw config` | Open Settings |

## Installing

1. In game, open `/xlsettings`, go to **Experimental**, and add this under **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/V1B3Gaming/SellWise/main/repo.json
   ```
   Tick **Enabled**, click **+**, then **Save and Close**.
2. Open `/xlplugins`, search for **SellWise**, and install it. Updates come through automatically.

## Dependencies

**You need**
- [Dalamud](https://github.com/goatcorp/Dalamud) (API 15, through XIVLauncher)
- An internet connection for [Universalis](https://universalis.app) prices. No account needed. Prices are only as fresh as the last time someone checked that item's market board.

**Comes bundled**
- [ECommons](https://github.com/NightmareXIV/ECommons), which handles clicking through the repair and confirmation windows. It's installed with SellWise; you don't need to do anything.

**Nice to have** (SellWise works without these; the buttons that need them just stay greyed out)
- [vnavmesh](https://github.com/awgil/ffxiv_navmesh): walking to summoning bells and menders
- [GatherBuddy Reborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn): Gather + craft, the per-material Gather buttons, and potions/food while gathering (turned on in its own settings)
- [Artisan](https://github.com/PunishXIV/Artisan): Craft with Artisan

**Building it yourself**
- .NET 10 SDK and Dalamud.NET.Sdk 15.0.0, which uses the Dalamud files XIVLauncher installs. Tests use xUnit.
```
dotnet build SellWise/SellWise.csproj -c Release
dotnet test SellWise.Tests
```

## Worth knowing

- SellWise only **suggests** prices. It never lists, buys or reprices anything on the market board.
- The gathering and crafting are done by GatherBuddy Reborn and Artisan, so the same rules apply as when you use them on their own: stay at your keyboard.
- The HQ check assumes normal-quality materials and no lucky conditions, so real crafts should do at least as well.
- Prices come from what other players have uploaded to Universalis. Rarely-checked items can have stale prices.

## Credits

Market data from [Universalis](https://universalis.app). The crafting math follows the [Teamcraft simulator](https://github.com/ffxiv-teamcraft/simulator). FINAL FANTASY XIV © SQUARE ENIX CO., LTD. SellWise is a fan project and isn't affiliated with Square Enix.

## License

SellWise is copyright © 2026 VIB3 and released under the [GNU Affero General Public License v3.0 or later](LICENSE). You're free to use, change and share it; if you share a modified version, share its source under the same licence.

It bundles [ECommons](https://github.com/NightmareXIV/ECommons), which is MIT-licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## AI-generated code

SellWise's code, tests, icon and the screenshot renders in this README were made with an AI assistant (Anthropic's Claude), directed and reviewed by the project owner. The core logic has automated tests, but the parts that drive the game (repairs, window clicks, teleports and crafting hand-offs) have had limited in-game testing. Please use it with that in mind, and open an issue if something acts up.
