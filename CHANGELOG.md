# Changelog

All notable changes to Forzavista Free Roam are documented here.

## 1.1.1 - 2026-09-07

### Fixed

- Microsoft Store/Xbox status checks now reuse a validated controller lookup
  instead of repeating the large fallback scan for every poll and action.
- Door and panel commands no longer stall behind repeated Xbox discovery work.
- Max Detail requests are no longer delayed by the Xbox status scan.

## 1.1.0 - 2026-09-07

### Added

- Support for the Microsoft Store/Xbox app version of Forza Horizon 6 build
  3.440.853.0.
- Support for the updated Steam build 6.440.853.0 while retaining support for
  Steam build 6.430.771.0.
- Bindable Xbox controller buttons and button combinations.

### Changed

- Game builds can be identified from the running executable's PE metadata when
  direct file hashing is unavailable.
- Microsoft Store service discovery uses a validated runtime lookup with a
  fast known-build hint and a bounded fallback scan.
- Keyboard shortcuts are registered only while the game is focused, preventing
  them from interfering with typing in other applications.
- Status text now displays the detected Steam or Microsoft Store build.

### Verified

- Existing Free Roam panel controls continue to work on Steam after the update.
- Microsoft Store current-car discovery and render-mode lookup validate against
  a live Free Roam session.

## 1.0.0 - 2026-09-05

### Added

- Initial public release.
- Individual door, hood, trunk, roof, storage, active-aero, and vent controls
  where supported by the current vehicle.
- Open-all, close-all, reset, and full-detail presentation actions.
- Bindable keyboard shortcuts.
- Self-contained Windows x64 single-file publishing profile.
