#!/usr/bin/env python3
"""Build the vouchfx-mcp GitHub Pages site.

Copies the static landing page (site/) into the output directory, then renders
this repository's own markdown guides — what vouchfx-mcp is, installation and
MCP-client registration, the tool & resource reference, troubleshooting — plus
the repository README, into styled HTML that matches the engine and the other
three satellite sites (vouchfx-providers, vouchfx-samples,
vouchfx-telemetry-backend). The markdown files remain the single source of
truth; this generates their HTML on every run, so a CI deploy keeps the
published pages current with every push.

The rendering machinery is shared with the rest of the fleet — see
https://github.com/tomas-rampas/vouchfx/tree/main/scripts/site-tools (the
vouchfx-site-tools package, vouchfx issue #200). This file only carries what
is specific to this repository's own site: the doc set and the page/portal
HTML.

    python scripts/build_site.py [output_dir]   # default: _site

Requires: markdown, pygments, vouchfx-site-tools
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"


def _bootstrap_site_tools() -> None:
    """Resolve vouchfx_site_tools in four steps: (1) an already-installed
    package — this is what CI's pip install satisfies; (2) VOUCHFX_SITE_TOOLS,
    if set, pointing at a scripts/site-tools/src checkout; (3) the maintainer's
    usual local layout, all four (now five) repos checked out side by side.
    Each step is tried independently so a wrong VOUCHFX_SITE_TOOLS still falls
    through to the sibling checkout instead of failing outright."""
    try:
        import vouchfx_site_tools  # noqa: F401

        return
    except ImportError:
        pass

    env_path = os.environ.get("VOUCHFX_SITE_TOOLS")
    if env_path:
        sys.path.insert(0, env_path)
        try:
            import vouchfx_site_tools  # noqa: F401

            return
        except ImportError:
            sys.path.pop(0)

    sibling = (ROOT / ".." / "vouchfx" / "scripts" / "site-tools" / "src").resolve()
    sys.path.insert(0, str(sibling))
    try:
        import vouchfx_site_tools  # noqa: F401

        return
    except ImportError:
        sys.path.pop(0)

    raise SystemExit(
        "vouchfx-site-tools is not installed and no local checkout was found.\n"
        "Install it with:\n"
        '  pip install "vouchfx-site-tools @ git+https://github.com/tomas-rampas/vouchfx.git@<sha>'
        '#subdirectory=scripts/site-tools"\n'
        "(substitute <sha> for the pinned commit in .github/workflows/pages.yml), "
        "or set VOUCHFX_SITE_TOOLS to a local scripts/site-tools/src checkout, "
        "or check out vouchfx as a sibling of this repository."
    )


_bootstrap_site_tools()

from vouchfx_site_tools import SiteConfig, build  # noqa: E402

# Markdown files to render, in sidebar order. (source path relative to ROOT,
# nav group, label, OPTIONAL 4th "description" element). The description, when
# present, is used verbatim by write_llms_txt() for that page's llms.txt entry
# (in place of the generic meta_description_prefix + " — " + label fallback);
# every description below is reused, existing site copy (the docs.html portal
# card text for the first five, the validation doc's own "Scope:" line for the
# sixth) — no newly written marketing.
#
# Every DOCS source path must be matched by a paths: glob in
# .github/workflows/pages.yml (superset invariant) — a page that renders here
# but whose source path a push to main doesn't trigger on would silently drift.
DOCS: list[tuple[str, ...]] = [
    # Start
    (
        "docs/overview.md", "Start", "What vouchfx-mcp is",
        "What it wraps and what it doesn't, the thirteen tools and two documentation resources plus "
        "the error-catalogue resource family at a glance, honest prerelease status, secret "
        "hygiene, and the engine pin.",
    ),
    (
        "docs/install.md", "Start", "Install & registration",
        "The dotnet tool install command, the .mcp.json registration snippet, and what "
        "run_suite additionally needs on PATH.",
    ),
    (
        "docs/tools-and-resources.md", "Start", "Tool & resource reference",
        "Every tool's parameters, result shape and notable behaviours — plus the two "
        "vendored-document MCP resources.",
    ),
    (
        "docs/troubleshooting.md", "Start", "Troubleshooting",
        "CLI pin/version mismatches, Docker daemon unavailability, timeouts and "
        "cancellation, and validation timeouts.",
    ),

    # Project
    (
        "README.md", "Project", "Repository README",
        "Overview, engine pin, secret hygiene, and how the thirteen tools fit together.",
    ),
    (
        "docs/implementation-map.md", "Project", "Implementation map",
        "How the wider vouchfx.ai proposal maps onto this repo — what's implemented under repo "
        "names, deliberately dropped, or blocked on upstream engine work.",
    ),

    # Validation — explicit short label so the derived meta description (this
    # repo's meta_description_prefix + " — " + label) stays within the SEO
    # ≤160-char budget; the file's own H1 is longer and, left to auto-render
    # (derive_label from the first "# " line), pushed the description to 161
    # chars. This entry is config data only — no change to the render/derive
    # machinery itself.
    (
        "docs/validation/live-validation-2026-07-21.md", "Validation", "Live validation — vouchfx-mcp",
        "A live, uninterrupted validation run of every vouchfx-mcp tool and resource "
        "against the real vouchfx CLI, a real Docker engine, and a real sample suite — "
        "including graceful-teardown evidence.",
    ),
    # Same treatment as its sibling above: left to auto-render (derive_label
    # from the file's own H1, "Graceful-teardown live drill procedure"), the
    # derived description sat at 159/160 chars — one character from breaching
    # the SEO budget on the next word added to meta_description_prefix or the
    # H1. An explicit short label gives it headroom.
    (
        "docs/validation/graceful-teardown-drill.md", "Validation", "Graceful-teardown drill",
        "Verifies the MCP's graceful-shutdown grace stays safe against the current engine "
        "pin, on a real Docker topology — the mandatory gate before every ENGINE_PIN advance.",
    ),
]

# Any additional markdown that is link-reachable but not in the sidebar.
EXTRA: list[str] = []

# Markdown that must never be published, even when present on a maintainer's
# disk. build() auto-renders docs/**/*.md minus these (see vouchfx-site-tools),
# so anything internal under docs/ MUST be listed here or it ships to the
# public site on the next Pages deploy. Nothing tracked under docs/ is
# internal today; internal working material (e.g. the vouchfx.ai spec/plan
# proposals) lives in specs/ — gitignored per this repo's convention, and
# additionally covered by SKIP_PREFIXES below as a fail-safe.
SKIP: set[str] = set()
SKIP_PREFIXES: tuple[str, ...] = ("specs/",)

PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>{title} · vouchfx-mcp</title>
<meta name="description" content="{desc}" />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="{root}favicon.svg" type="image/svg+xml" />
<link rel="canonical" href="{canonical}" />

<!-- Open Graph / social -->
<meta property="og:type" content="article" />
<meta property="og:site_name" content="vouchfx-mcp" />
<meta property="og:title" content="{title}" />
<meta property="og:description" content="{desc}" />
<meta property="og:url" content="{canonical}" />

<!-- Twitter card -->
<meta name="twitter:card" content="summary" />
<meta name="twitter:title" content="{title}" />
<meta name="twitter:description" content="{desc}" />

<link rel="stylesheet" href="{root}styles.css" />
<link rel="stylesheet" href="{root}docs.css" />
<link rel="stylesheet" href="{root}pygments.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="{root}index.html" aria-label="vouchfx-mcp home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx-mcp</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="{root}index.html">Home</a>
      <a href="{root}docs.html">Docs</a>
      <a href="{root}docs/tools-and-resources.html">Tool reference</a>
      <a href="https://vouchfx.io/">Engine docs</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx-mcp" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="doc-shell">
  <aside class="doc-side">{sidebar}</aside>
  <main class="doc-main">
    <div class="doc-breadcrumb"><a href="{root}docs.html">Documentation</a> / {crumb}</div>
    <article class="prose">{body}</article>
  </main>
  <nav class="doc-toc"><p class="doc-toc__label">On this page</p>{toc}</nav>
</div>
{mermaid_script}
</body>
</html>
"""

PORTAL = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>Documentation · vouchfx-mcp</title>
<meta name="description" content="vouchfx-mcp documentation — the local stdio MCP server wrapping the vouchfx end-to-end integration testing CLI for AI agents." />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="favicon.svg" type="image/svg+xml" />
<link rel="canonical" href="https://vouchfx-mcp.vouchfx.io/docs.html" />

<!-- Open Graph / social -->
<meta property="og:type" content="website" />
<meta property="og:site_name" content="vouchfx-mcp" />
<meta property="og:title" content="Documentation · vouchfx-mcp" />
<meta property="og:description" content="vouchfx-mcp documentation — the local stdio MCP server wrapping the vouchfx end-to-end integration testing CLI for AI agents." />
<meta property="og:url" content="https://vouchfx-mcp.vouchfx.io/docs.html" />

<!-- Twitter card -->
<meta name="twitter:card" content="summary" />
<meta name="twitter:title" content="Documentation · vouchfx-mcp" />
<meta name="twitter:description" content="vouchfx-mcp documentation — the local stdio MCP server wrapping the vouchfx end-to-end integration testing CLI for AI agents." />

<link rel="stylesheet" href="styles.css" />
<link rel="stylesheet" href="docs.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="index.html" aria-label="vouchfx-mcp home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx-mcp</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="index.html">Home</a>
      <a href="docs/tools-and-resources.html">Tool reference</a>
      <a href="https://vouchfx.io/">Engine docs</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx-mcp" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="container portal">
  <div class="portal__head">
    <p class="eyebrow">Documentation</p>
    <h1 class="section__title">A native vouchfx toolbelt for AI agents.</h1>
    <p class="section__lede">These pages are rendered straight from the repository's markdown on every push,
      so they never drift from the code they describe.</p>
  </div>

  <section class="portal__group">
    <h2>Start here</h2>
    <p>What the server is, how to install and register it, the full tool contract, and how to fix the problems you are most likely to hit.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/overview.html">
        <span class="doc-card__k">1 · OVERVIEW</span><h3>What vouchfx-mcp is</h3>
        <p>What it wraps and what it doesn't, the thirteen tools and two documentation resources plus the error-catalogue resource family at a glance, honest prerelease status, secret hygiene, and the engine pin.</p>
      </a>
      <a class="doc-card" href="docs/install.html">
        <span class="doc-card__k">2 · GUIDE</span><h3>Install &amp; registration</h3>
        <p>The dotnet tool install command, the .mcp.json registration snippet, and what run_suite additionally needs on PATH.</p>
      </a>
      <a class="doc-card" href="docs/tools-and-resources.html">
        <span class="doc-card__k">3 · REFERENCE</span><h3>Tool &amp; resource reference</h3>
        <p>Every tool's parameters, result shape and notable behaviours — plus the two vendored-document MCP resources.</p>
      </a>
      <a class="doc-card" href="docs/troubleshooting.html">
        <span class="doc-card__k">4 · GUIDE</span><h3>Troubleshooting</h3>
        <p>CLI pin/version mismatches, Docker daemon unavailability, timeouts and cancellation, and validation timeouts.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Project</h2>
    <p>How this repository is run.</p>
    <div class="doc-cards">
      <a class="doc-card" href="README.html"><span class="doc-card__k">README</span><h3>Repository README</h3><p>Overview, engine pin, secret hygiene, and how the thirteen tools fit together.</p></a>
      <a class="doc-card" href="https://github.com/tomas-rampas/vouchfx-mcp" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SOURCE</span><h3>vouchfx-mcp on GitHub</h3><p>Issues, the spec → build → review history, and the Apache-2.0 licence.</p></a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Validation</h2>
    <p>Live evidence runs against the real vouchfx CLI, a real Docker engine, and real sample suites.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/validation/live-validation-2026-07-21.html">
        <span class="doc-card__k">VALIDATION</span><h3>Live validation — vouchfx-mcp</h3>
        <p>A live, uninterrupted validation run of every vouchfx-mcp tool and resource against the real vouchfx CLI, a real Docker engine, and a real sample suite — including graceful-teardown evidence.</p>
      </a>
      <a class="doc-card" href="docs/validation/graceful-teardown-drill.html">
        <span class="doc-card__k">VALIDATION</span><h3>Graceful-teardown drill</h3>
        <p>Verifies the MCP's graceful-shutdown grace stays safe against the current engine pin, on a real Docker topology — the mandatory gate before every ENGINE_PIN advance.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Ecosystem</h2>
    <p>Where the rest of the vouchfx fleet lives.</p>
    <div class="doc-cards">
      <a class="doc-card" href="https://vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">ENGINE</span><h3>vouchfx project site</h3><p>The architecture blueprint, the YAML DSL specification, and the language reference this server vendors.</p></a>
      <a class="doc-card" href="https://providers.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">HUB</span><h3>Provider hub</h3><p>The community provider registry — every step type list_step_types/describe_step_type report comes from the Core set this catalogue governs.</p></a>
      <a class="doc-card" href="https://samples.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SAMPLES</span><h3>vouchfx samples</h3><p>Four production-shaped applications proving the same engine end-to-end, in four stacks.</p></a>
      <a class="doc-card" href="https://telemetry.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">TELEMETRY</span><h3>Telemetry backend</h3><p>The optional, privacy-allowlisted run-metadata aggregation service.</p></a>
    </div>
  </section>
</div>

<footer class="footer">
  <div class="container footer__inner">
    <div class="footer__brand">
      <span class="brand__mark" aria-hidden="true"></span>
      <div><strong>vouchfx-mcp</strong><p>A local stdio MCP server wrapping the packaged vouchfx CLI — thirteen tools, two vendored documentation resources, and an error-catalogue resource family for AI coding agents.</p></div>
    </div>
    <div class="footer__links">
      <a href="index.html">Home</a>
      <a href="https://github.com/tomas-rampas/vouchfx-mcp" target="_blank" rel="noopener noreferrer">Repository</a>
      <a href="https://vouchfx.io/" target="_blank" rel="noopener noreferrer">Engine docs</a>
      <a href="https://github.com/tomas-rampas/vouchfx-mcp/blob/main/LICENSE" target="_blank" rel="noopener noreferrer">Licence (Apache-2.0)</a>
    </div>
  </div>
</footer>
</body>
</html>
"""

CONFIG = SiteConfig(
    root=ROOT,
    default_repo="tomas-rampas/vouchfx-mcp",
    docs=DOCS,
    page_template=PAGE,
    portal_html=PORTAL,
    meta_description_prefix="vouchfx-mcp — the local stdio MCP server wrapping vouchfx, the end-to-end integration testing framework, for AI agents",
    extra=EXTRA,
    skip=SKIP,
    skip_prefixes=SKIP_PREFIXES,
    # This repository is served from the custom domain vouchfx-mcp.vouchfx.io,
    # following the same *.vouchfx.io convention as the other satellite sites,
    # so site_url is that origin rather than the default GitHub Pages one.
    # Setting it (rather than leaving it unset) is not just cosmetic: PAGE and
    # PORTAL above use {canonical}, and render_markdown() only ever adds that
    # str.format() kwarg when config.site_url is truthy — an unset site_url
    # here would therefore raise KeyError on every page build.
    site_url="https://vouchfx-mcp.vouchfx.io/",
    # True: a rendered page's own "# Title" is a real <h1> (was <h2> under
    # the old baselevel=2 default — the audit's D-09 "no <h1>" finding), and
    # each sidebar nav-group label renders as the non-heading
    # <p class="doc-side__group"> instead of <h4>. Paired with the matching
    # site/docs.css selector shift (SEO fleet audit REQ-002 / D-09).
    semantic_headings=True,
    # One-paragraph llms.txt summary (llms.txt convention, REQ-005): the same
    # ≤160-char landing description shipped for the SEO fleet audit, expanded
    # slightly using only existing page copy — the hero lede's "works with
    # .e2e.yaml suites directly", the footer's "one taxonomy-faithful verdict
    # every time", and the honest-status stat row's verdict list — not newly
    # written marketing.
    llms_summary=(
        "A local stdio MCP server wrapping the packaged vouchfx CLI: thirteen tools, two "
        "documentation resources, and an error-catalogue resource family for end-to-end "
        "integration testing, so an AI agent works with .e2e.yaml suites directly and gets "
        "one taxonomy-faithful verdict every time — pass, fail, environment error or "
        "inconclusive, never conflated."
    ),
)


def main() -> None:
    build(CONFIG, OUT)


if __name__ == "__main__":
    main()
