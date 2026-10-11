# Design notes: a shareable "3D window" mod

Status: brainstorm (2026-10-10). The current prototype proves that stereo rendering works. How it
presents is not good enough to share: SteamVR shows our floating 3D window *next to* the Steam Frame's
own flat screen for the game, and nothing lines the two up.

## The core problem

On the Frame, a desktop game is shown by gamescope as a SteamVR **dashboard tab** overlay
(`valve.steam.desktopgame.<appid>`, 2.67 m wide). We can't move it, hide it, read where it is, or attach
to it from inside the game: subviews must have a parent in the same process, overlay-relative transforms
are gone in OpenVR 2.x, and dashboard tabs report no transform. gamescope has no stereo option
(`--vr-overlay-*` covers width, curvature, key, modal and control bar only).

So any robust design must **either own the screen the user looks at, or get gamescope to show our
stereo image.** Trying to line a second window up over the first can't be made robust.

## Options

### A. Our own dashboard tab (stereo)
Create our own dashboard overlay (`CreateDashboardOverlay`) from inside the game process, mark it
`SideBySide_Parallel`, submit the eye pair to it, and switch the dashboard to it (`ShowDashboard(key)`).
The dashboard shows one tab at a time, so the user sees *only* our 3D tab, sized and placed by the
Frame like the normal game screen. It's owned by the Ori process, so focus and input should stay with
the game.
- Unknowns: does the dashboard honour SBS flags on a dashboard overlay? Does the Frame keep our tab up
  during play, or switch back to the gamescope tab? Does controller input still reach the game?
- Cost: small. It's a hot-reloadable spike on the current code.
- **Spike result (2026-10-10): doesn't work on the Frame.** The tab is created and keeps
  `SideBySide_Parallel` (flags 0x8400), and `ShowDashboard` makes it active. But the Frame's game
  screen is *not* a tab that ours replaces: our tab appears as an extra, smaller flat panel in front of
  the curved game screen, with both Ori icons in the dashboard's side bar. Dashboard overlays are only
  visible while the dashboard is open, and entering Gamepad mode (needed to play) closes the dashboard,
  so our tab disappears and input goes to the Frame's screen. The game is shown with
  `IsDashboardVisible()==false` during normal play. Code kept behind `[VR] Mode = Tab` (Ctrl+F11).

### B. Become a VR scene app (the UEVR / REFramework model)
`VR_Init(Scene)` from inside the game, render a virtual screen ourselves (a curved quad in a dark or
passthrough environment) into per-eye textures, and `IVRCompositor::Submit` each frame. A focused scene
app replaces the theater/dashboard, so there is only one screen, and we control everything:
size, distance, curvature, recentering, environment.
- Bonus: we know the head pose every frame, so the eye cameras can follow the head relative to the
  virtual screen ("magic window"/head-coupled parallax). Leaning in reveals depth, which is far stronger
  than fixed stereo.
- Unknowns: does the Frame hide the gamescope tab when the game process becomes a scene app? Does
  controller input still reach the game (Steam Input virtual pad)? Frame pacing at the game's frame rate,
  with compositor reprojection covering the gaps.
- Cost: medium. We need our own quad rendering + Submit loop. It's also the path that works the same on
  PC SteamVR.

### C. Upstream: stereo support in gamescope's OpenVR backend
Patch gamescope so a window property (e.g. `GAMESCOPE_VR_STEREO=sbs`) sets `SideBySide_Parallel` on its
overlay. The mod then just renders half-SBS into its normal window, and the Frame's own screen becomes 3D,
with no second window, native sizing and theater mode intact. Any game mod could use it.
- Cost: low to write, but it ships on Valve's schedule. Swapping in a patched system gamescope isn't
  something users should do. Worth filing as an issue/PR in parallel either way.

### D. Keep the floating overlay and paper over it
Black out the flat screen, auto-size, and recenter heuristically. It always leaves either a black rectangle or a
misalignment. Only a fallback.

## Recommendation
1. ~~Spike **A**~~: tried, fails (see above).
2. Spike **B** in parallel or next. It's the long-term answer if A's tab gets swapped out or loses SBS,
   and it unlocks head-coupled parallax and PC SteamVR parity.
3. Open a gamescope issue for **C**.
4. Keep **D** only as the fallback when neither A nor B is available.

## Everything else a shareable mod needs
- **Install**: one script or zip. BepInEx's in-game interop generation crashes under FEX, so ship the
  native generator (self-contained linux-arm64/x64 build, no SDK needed) and run it at install. Don't
  redistribute generated interop (it's derived from game code). Report the FEX crash upstream.
- **Platforms**: Frame (ARM + FEX), Linux PC + SteamVR, Windows + SteamVR. All go through OpenVR, so it's
  the same code.
- **Controls without a keyboard**: a controller chord to toggle stereo and recenter, plus a small settings
  panel (dashboard overlay or in-game). Defaults should be good enough that most people never touch them.
- **Comfort, automatic**: a disparity budget computed from screen size and distance (uncrossed disparity
  never exceeds about the eye spacing, crossed is limited), using the live depth histogram; snap rather than
  ease on camera cuts; mono when paused (done); scale with subject distance (done).
- **Completeness**: screen-space UI (main menu text, subtitles, some prompts) is drawn after the camera and is
  missing from the eye images. Composite those canvases into both eyes at screen depth.
- **Performance**: per-eye resolution scale; optional depth-based reprojection for the second eye
  on weak GPUs; watch memory (see the 2026-10-10 OOM incident in NOTES.md).
- **Resilience**: detect game updates (GameAssembly hash) and prompt to regenerate interop; never leave
  the camera modified on errors (done); keep the previous session's log.
