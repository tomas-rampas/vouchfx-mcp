# examples/ — the suites served under `vouchfx://examples/{name}`

Every `.e2e.yaml` file in this directory is embedded into `Vouchfx.Mcp.dll` as a manifest
resource and served verbatim by the `vouchfx://examples/{name}` MCP resource template
(Sprint 5 / US-S5-01). They exist so a host authoring a suite can read a **complete, working
document** rather than assembling one from the language reference's per-field tables.

## Rules these files are held to

1. **Schema-valid, and tested as such.** `ExampleSuiteCatalogueTests` runs every example
   through the same `SuiteValidator` pipeline `validate_suite` uses, against the same vendored
   composed schema, and asserts zero schema errors. An example that drifts from what the engine
   accepts fails the build, which is the whole point of shipping them from this repository
   rather than from prose.
2. **Comment-annotated.** Every step carries a comment saying what it does and why the fields
   are shaped the way they are. That annotation is the deliverable: an uncommented valid suite
   is already available from `vouchfx-docs:///recipes`.
3. **No literal secret, ever.** Credentials appear only as `${secret:...}` references — the
   form the engine resolves at step-execution time and redacts from every output. A literal
   would both violate this server's secret-hygiene invariant and trip the VFX-D-1207 semantic
   rule the examples are supposed to model good practice against.
4. **No engine or Docker dependency at test time.** These are documents, not a test fixture:
   nothing in `dotnet test` runs them. "Passing" is claimed on their shape (schema-valid,
   idiomatic, drawn from the pinned engine's own `vendored/recipes.md` patterns), not measured
   against a live environment — see `ExampleSuites`' own remarks for that distinction stated
   in code.

## Adding one

Add the file here, add an `ExampleSuite` entry in `src/Vouchfx.Mcp/Examples/ExampleSuites.cs`,
and add the matching `<EmbeddedResource>` item to `src/Vouchfx.Mcp/Vouchfx.Mcp.csproj`.
`ExampleSuiteCatalogueTests` fails loudly if the three fall out of step — a catalogue entry
with no embedded file, or a file on disk with no catalogue entry.

**This directory is not `vendored/`.** These files are this repository's own work and may be
edited freely; the drift gate in `scripts/sync-vendored.ps1` does not look at them.
