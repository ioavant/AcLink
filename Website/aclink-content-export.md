# AC Link — Website Content Export

Flat content facts for adapting into the site's own design (not styled markup — restyle freely). Source: Autodesk App Store listing copy, 2026-07-26.

## Product

- **Name:** AC Link
- **Publisher:** Vixeldorf
- **Platform:** Autodesk Revit 2022–2027 (single installer, pick versions during setup)
- **Category:** Mechanical / HVAC coordination

## Tagline (short, for a hero section)

Links indoor and outdoor units into VRF / multi-split AC systems in Revit.

## One-line description

Groups indoor and outdoor HVAC units into named VRF / multi-split systems directly inside Revit — no external database, just native Revit parameters that survive worksharing, copy/paste, and linking.

## Overview

AC Link groups indoor and outdoor HVAC units into named VRF / multi-split systems directly inside Revit — no external database, no spreadsheet tracking, just native Revit parameters that survive worksharing, copy/paste, and linking.

## How It Works

Select the units that belong together (one outdoor condenser + any number of indoor evaporators) and click AC Link. The add-in auto-detects which unit is the outdoor one (by size and family/type keywords) and proposes a system name you can edit. From then on, the system is identified purely by that name and a hidden system key stamped onto each unit — reopen the model any time and AC Link finds the same systems automatically.

## Key Features

- **Auto role detection** — creating a system pre-guesses outdoor vs. indoor units, editable before saving.
- **Cooling-load balance check** — reads each unit's own cooling-load parameter (Btu/h) and shows the outdoor unit's load factor against the sum of its indoor units, so oversized or undersized systems are obvious at a glance.
- **Level detection** — works even when the outdoor unit sits on a different level than its indoor units (e.g. condenser on the roof).
- **Color-by-system** — view overrides for a fast visual QA of which units belong to which system.
- **Copy protection** — duplicating a unit in Revit does not silently duplicate its system membership.
- **Cross-model systems** — a system can span linked models (e.g. indoor units in an apartment model, outdoor units in a roof/mechanical model), tied together by a short, shareable system key.
- **No external database** — everything lives as shared parameters on the elements themselves; no Extensible Storage, fully compatible with worksharing and central models.

## Who It's For

MEP and HVAC coordinators working with VRF or multi-split systems who need a fast way to see which indoor units are actually served by which outdoor unit, catch load-balance mismatches, and keep that information attached to the model itself rather than in a separate spreadsheet.

## Usage Workflow (for a "how it works" / docs section)

1. Select the units that make up one AC system: one outdoor (condenser) unit + any number of indoor (evaporator) units.
2. Click AC Link on the Vixeldorf ribbon tab. The Create System dialog opens with roles pre-guessed and a suggested name — adjust if needed, then save.
3. Reopen AC Link any time (nothing selected) to see the Systems list: unit counts, load balance, level(s), and — for cross-model systems — which linked model each unit lives in.
4. Click a system to open it, add/remove units, toggle color-by-system, or "Show" to zoom to any unit (including ones in a linked model).
5. For systems split across linked models, use the system key (Copy key / Paste key) to join units from another model instead of relying on matching names.

## Version History

- **1.0.1** — First public release. Create and manage VRF / multi-split AC systems in Revit: link indoor and outdoor units by name, auto-detect roles, calculate cooling-load balance, color-code systems in views, and connect systems split across linked models via a shareable system key. Includes copy protection so duplicating a unit does not duplicate its system membership.

## Current Limitations

- Revit 2022 only: selecting a unit in a linked model zooms to it but doesn't highlight it (Selection API limitation fixed in 2023+).
- Color-by-system view overrides apply to host-model units only; linked-model units are not recolored.
- For elements inside a Revit group, the built-in Comments field isn't updated (Revit doesn't allow it to vary by group instance); linking, load calc, and level detection are unaffected.
- Systems created before AC Link was first installed are matched by name only until the add-in runs once in that model, assigning a permanent system key.

## Support

- **Email:** yoav@vixeldorf.com
- **Response time:** 5 business days
- Please include Revit version, AC Link version (shown in the window title), and a short description or screenshot of the issue.

## Links

- Publisher site: https://www.vixeldorf.com
- Autodesk App Store listing: *(add once published)*
- Download / releases: *(add once the GitHub releases page or MSI link is public-facing)*
