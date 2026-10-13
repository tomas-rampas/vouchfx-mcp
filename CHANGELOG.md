# Changelog

## Unreleased

### Fixed

- Stdio client disconnection now starts host shutdown while `run_suite` is active. The existing
  cancellation path waits for CLI teardown before recording an `Inconclusive` outcome. The host
  allows 50 seconds for graceful teardown, kill confirmation, output draining and persistence;
  this is a shutdown budget, not a promise that every environment stops successfully.
