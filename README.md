# Yesla - Assisted Driving For Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/yesla](https://support.doodesch.de/yesla).

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Status](https://img.shields.io/badge/status-beta-yellow)

> An electric sedan for Schedule I that drives you where you send it.

## What it does

Jeremy at the dealership sells the **Yesla Model 3** for $60,000. It hums instead of running an engine, takes
paint like any other car, and is saved with the game.

**Yesla Assisted Driving.** Pick a destination in the Yesla phone app: a point on the map, one of your
properties, a customer, a dealer, a shop, or the target of your current quest or deal. Sit in the driver seat
and press **Y**. The car drives on the game's own road AI: it keeps to speed limits, stops at red lights, waits
for traffic and reverses out of dead ends. Steering, throttle or brake, or pressing **Y** again hands control
back at once.

**First-person view** in the car with **C**. The driving visualisation sits on the centre screen.

**Driving visualisation.** **N** cycles panel, full screen and off. It shows the road lanes, the blue route,
cars, people, traffic lights with their colour, your speed, the speed limit and the next turn.

**Remote controls in the phone app**, owner only:

- **Actual Super Summon** - the car drives itself to you. If you stand where no car can reach, it stops at the
  nearest road.
- **Honk and flash the lights.**
- **Lock** - nobody gets in, not even NPCs.
- **Sentry Mode** - the phone tells you when someone hits the car or stands next to it.

The app icon shows once you own a Yesla.

## Keys

| Key | What it does |
|---|---|
| `Y` | Assisted Driving (driver seat, your own car) |
| `C` | First person |
| `N` | Visualisation |

All three can be changed in `UserData/MelonPreferences.cfg` under `[Yesla]`: `EngageKey`, `ViewKey` and
`VisualisationKey`, as Unity KeyCode names.

## Multiplayer

Everyone needs the mod. Every player can buy and own a Yesla. Remote controls and Assisted Driving answer only
the owner, and a client's own car drives on the client.

## Uninstalling

A save with a Yesla in it cannot load the car without the mod, because the game does not know the vehicle
code. Sell or leave the car before you remove the mod.

## Requirements

- [MelonLoader](https://melonwiki.xyz/) 0.7.3 or newer
- [S1API](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) 3.2.1 or newer
- [Sideload](https://thunderstore.io/c/schedule-i/p/DooDesch/Sideload/) for the phone app (optional - without it
  everything but the app works)

## Credits

Built by DooDesch on [S1API](https://github.com/ifBars/S1API) by ifBars.
Car model "Tesla 2018 Model 3" by Ameer Studio, licensed
[CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/), modified for this mod (see
[LICENSE-MODEL.txt](LICENSE-MODEL.txt)).
App icons from [Lucide](https://lucide.dev), licensed [ISC](https://lucide.dev/license).

"Tesla" and "Model 3" are trademarks of Tesla, Inc. This mod is not affiliated with or endorsed by Tesla, Inc.

## License

DooDesch, see [LICENSE.md](LICENSE.md). Forks, changes and pull requests are welcome. Publishing or reuploading the mod or a changed copy needs written permission. The car model is CC-BY-4.0, see [LICENSE-MODEL.txt](LICENSE-MODEL.txt).
