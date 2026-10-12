# QSBVRTracking

Lets nearby players in a [Quantum Space Buddies](https://github.com/qsb-dev/quantum-space-buddies) session see the head and hand movements of players using [NomaiVR](https://github.com/Raicuparta/nomai-vr).

It only synchronizes visuals: the VR player's head, hands and arms on their multiplayer avatar. It adds no gameplay, physics or interactions.

## Requirements

| Player | Needs |
|---|---|
| VR player | Quantum Space Buddies, NomaiVR, QSBVRTracking |
| Desktop player who wants to see VR movement | Quantum Space Buddies, QSBVRTracking |
| Anyone else | Quantum Space Buddies only. They can still join and play normally |

Quantum Space Buddies is not installed automatically with this mod. Install it yourself from the Outer Wilds Mod Manager.

## Network usage

Tracking is only sent to players who have this mod **and** are close to the VR player. Nothing is sent while nobody is in range, and almost nothing while the VR player holds still.

## Settings

Change these in the mod's options menu, in game or from the title screen. They apply immediately. Settings are per player: the VR player's settings decide who receives their tracking.

| Key | Default | Range | Description |
|---|---|---|---|
| `EnableDistanceCulling` | `true` | | Only send tracking to players within the activation distance. If disabled, tracking is sent to every player that has this mod, which increases network usage. |
| `ActivationDistance` | `10` | 1–50 m | Players closer than this start receiving tracking. |
| `HysteresisMargin` | `2` | 0–10 m | Extra distance before a player stops receiving tracking. With the defaults, tracking starts within 10 m and stops beyond 12 m. |
| `TrackingUpdateRate` | `10` | 5–15 Hz | Maximum tracking updates per second. |
| `EnableAdaptiveUpdateRate` | `true` | | Use the minimum update rate while moving slowly. |
| `MinimumUpdateRate` | `5` | 1–15 Hz | Update rate during slow movement. Never higher than `TrackingUpdateRate`. |
| `EnableDebugVisualization` | `false` | | Show markers at the received head and hand positions. |
| `EnableNetworkDiagnostics` | `false` | | Print tracking network statistics to the console every 5 seconds. |
| `DebugSimulateVR` | `false` | | Send synthetic poses without a headset, for testing. |

For a poor connection, lower `TrackingUpdateRate` to 5 and `ActivationDistance` to 5.

## Building

```
dotnet build
```

The build output goes straight into the Outer Wilds Mod Manager's mods folder.
