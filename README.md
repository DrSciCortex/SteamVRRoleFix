# SteamVRRoleFix

A plugin for Resonite's renderer (BepInEx 5, [BepInExRenderer](https://thunderstore.io/c/resonite/p/ResoniteModding/BepInExRenderer/)).
It fixes frozen hands, dead input and stuck buttons after switching controllers in SteamVR.

## The problem

Resonite's renderer takes a device as a hand's controller only when SteamVR reports that device connecting, and only
if the device holds that hand's role at that moment. SteamVR can also move a hand's role without any device
connecting. For example:

- a device connects before SteamVR gives it the role, then gets the role;
- a device connects while it's hidden (never tracked), is shown later, then gets the role.

This happens when you switch between controllers and hand tracking, or when a driver takes a hand back from the
controllers (the [CyberFinger](https://github.com/DrSciCortex) SteamVR driver does). Resonite then keeps reading the
device it had before, even though SteamVR has disconnected or hidden it. The hand freezes and its buttons, including
the dash, stop working. The SteamVR system button still works, because SteamVR handles it itself.

The hand comes back only when some device happens to reconnect while the right one holds the role. For example,
switching Resonite from desktop to VR, or going in and out of the Quest menus, can bring it back.

There's a second problem. The renderer turns any controller that connects without a role into a tracker, so a hand
controller that is waiting for its hand becomes a tracker. Resonite then draws a tracker model on your hand. Waiting
for the hand happens to a CyberFinger taking the hand back, or to the headset's hand tracking while controllers hold it.
If that device later becomes the hand's controller, the renderer removes its tracker by serial number. SteamVR may
report a different serial by then (controller emulation), so the tracker stays.

And a third. When a hand switches controllers, the renderer stops reading the old controller but keeps sending
Resonite its last input state, and Resonite combines the inputs of every controller it has seen on a side. A button
held at the moment of the switch stays held for the rest of the session. With Touch controllers, a stuck dash button
on one hand means X or A no longer opens the dash, and a double press of the other hand's button toggles UI edit
mode instead.

## The fix

Every 100 ms, the plugin asks SteamVR which device holds each hand role. When the holder is a connected device that
Resonite isn't using, and it keeps the role for 0.25 s, the plugin passes it to the renderer's own
device-connected handling, exactly as if the device had just connected. The plugin retries once a second, and after
three tries every 10 s. It logs each device it passes on to `BepInEx/LogOutput.log`.

For the second problem, the plugin also:

- never makes a tracker of a device that SteamVR lists as a controller with a left- or right-hand role hint;
- removes the tracker of any device that is currently a hand's controller, matching by device index rather than
  serial.

For the third, when the renderer marks a controller as no longer read, the plugin clears its inputs too: buttons
up, triggers, grips and sticks at rest. It logs any input it found held.

The same change, written into the renderer itself, is proposed upstream for
[Renderite.Unity.Renderer](https://github.com/Yellow-Dog-Man/Renderite.Unity.Renderer). Once that ships, this
plugin isn't needed. It doesn't get in the way either: it only acts when a hand is on the wrong device, or on
a controller the renderer has stopped reading.

## Build and install

With Resonite and BepInExRenderer installed (the build references them and redistributes neither):

```powershell
dotnet build -c Release -p:Deploy=true
```

That copies `SteamVRRoleFix.dll` into `Renderer\BepInEx\plugins\DrSciCortex-SteamVRRoleFix\`. The build uses Gale's
Default profile if you have one, otherwise the game folder. Pass `-p:ModProfilePath=...` to pick another profile.
Restart Resonite to load it.

## Licence

MIT, see [LICENSE](LICENSE).
