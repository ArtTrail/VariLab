# VariLab User Guide

VariLab is a standalone desktop tool for processing stellar variability data — light
curves for variable stars, eclipsing binaries, or any target where you want a
magnitude-vs-time series rather than a transit fit. It takes a folder of
**pre-calibrated, plate-solved FITS frames** (VariLab does not do dark/bias/flat
calibration or frame registration itself — that must already be done, e.g. in NINA,
PixInsight, or FITS Calibrator) and produces a differential-photometry light curve,
a period search, and export files ready for AAVSO submission or further analysis.

VariLab shares its comparison-star selection method ("Stone method," see below) and
several core services with **TransitLab**, and is intended to eventually fold into a
future TransitLab version as a variability-analysis mode.

---

## Table of Contents

1. [Workflow overview](#1-workflow-overview)
2. [Data tab](#2-data-tab)
3. [Comp Stars tab — the Stone Method](#3-comp-stars-tab--the-stone-method)
   - [Pre-flight checks](#pre-flight-checks)
   - [What it queries, and why](#what-it-queries-and-why)
   - [Selection criteria](#selection-criteria)
   - [Magnitude assignment — priority chain](#magnitude-assignment--priority-chain)
   - [Controls](#controls)
4. [Photometry tab](#4-photometry-tab)
5. [Results tab](#5-results-tab)
   - [Exports](#exports)
6. [Known limitations](#6-known-limitations)
7. [Appendix: Sources for AAVSO-related design decisions](#appendix-sources-for-aavso-related-design-decisions)
8. [Appendix: From Python to C# — How VariLab Was Built](#appendix-from-python-to-c--how-varilab-was-built)

---

## 1. Workflow overview

VariLab has four tabs, used in order:

1. **Data** — point to your input folder and identify the target
2. **Comp Stars** — automatically select comparison stars (the Stone method)
3. **Photometry** — extract the differential light curve across all frames
4. **Results** — search for periodicity, view plots, export

---

## 2. Data tab

| Field | Purpose |
|---|---|
| Target Directory | Folder of FITS frames — must be pre-calibrated and pre-plate-solved (valid WCS in each file). All frames should be the **same filter** — VariLab does not currently separate frames by filter, so a folder with mixed bands will produce a meaningless mixed-band light curve. As soon as a valid folder is set, VariLab reads the first FITS file's header for `OBJECT` and `FILTER` and auto-fills Target Name and Filter below (see next row) — then automatically runs the RA/Dec lookup. **Pointing this at a new folder clears the Comp Stars, Photometry, and Results tabs** — selected comps, the light curve, and period-search output from whatever dataset was open before are reset, so they can't be mistaken for results from the new one. |
| Output Directory | Where the `VariLab_ResultsN` export folders (AAVSO `.txt`, Stellar Variability `.png`, Excel report) are saved — see [Exports](#exports). Defaults to match Target Directory whenever it changes, so exports normally land right next to the source frames with no extra setup. Browse to a different folder here to save exports somewhere else instead (e.g. a shared results folder, or alongside another tool's output for comparison) — once you do, it stops auto-following Target Directory, so it's safe to switch datasets afterward without it snapping back. |
| Target Name | The star's designation (e.g. `V0338 Cen`, `KELT-8`). Used for the AAVSO export's `#NAME` field and as the title on exported plots. Auto-filled from the FITS header's `OBJECT` keyword when a Target Directory is set — a trailing lowercase exoplanet-letter suffix is stripped (e.g. `KELT-8 b` → `KELT-8`, `TOI-4463 A b` → `TOI-4463 A`, keeping the uppercase host-star component letter). Edit it manually any time. **Trust this over the FITS header if they disagree** — see the Comp Stars tab's "Target not in frame" check below for a real case where a stale `OBJECT` header pointed VariLab at the wrong star entirely. |
| Target RA / Target Dec | The target's sky position. Accepts **decimal degrees** (`213.9153`) or **sexagesimal** (`14:15:39.7` for RA, `-47:28:46` for Dec) — enter either format directly, whatever your source data gives you. Auto-filled once Target Name is populated (see Look Up RA/Dec below). |
| **Look Up RA/Dec** button | Enter a Target Name and click this to auto-fill RA/Dec. It queries, in order: **AAVSO VSX** (variable star index) → **NASA Exoplanet Archive** (host star or planet name) → **SIMBAD** (general object database, resolves almost any common designation). The first catalog that recognizes the name wins; the status line reports which one matched and under what name. This same lookup runs automatically right after Target Name is auto-filled from a FITS header. |
| Filter | The observation's filter band. Matters for comp-star magnitude lookups — pick the one that actually matches your images. Auto-filled from the FITS header's `FILTER` keyword when a Target Directory is set. **Required** — Comp Star Selection refuses to run without one selected (blank, or a FITS header value that doesn't match any option in the dropdown, both block with a status message telling you to pick one here first). |
| AAVSO Observer Code | Your AAVSO observer code, written into the `#OBSCODE` field of AAVSO exports. Optional but needed for a real AAVSO submission. Persists between sessions, so it doesn't need re-typing every time. |

---

## 3. Comp Stars tab — the Stone Method

This is the most important step: VariLab needs a set of comparison stars with known
magnitudes to turn raw target flux into a calibrated differential magnitude. This
selection process is called the **Stone method**, named for Geoff Stone, author of
the `CompStarSelector` / `exotic-proto` project this logic was ported from (native
C#, no Python/Docker dependency). The same method is already used inside TransitLab
for exoplanet transit work.

### Pre-flight checks

Before spending any time on catalog queries, Comp Star Selection checks two things
and stops with a dialog if either fails:

- **Plate solve required** — if fewer than half the frames in the Target Directory
  have a WCS solution, there's nothing for the pipeline to project comp stars onto.
  Plate-solve the dataset first (Astrometry.net is recommended for small/undersampled
  formats like MObs's 650×500, where ASTAP's quad-matching is unreliable).
- **Target not in frame** — the resolved target position is checked against the
  reference frame's own plate solution. If it's more than a degree (or three
  frame-widths, whichever is larger) away from where the frame is actually centered,
  a dialog reports both positions and the separation, then stops. This catches a real
  failure mode: a FITS `OBJECT` header left over from a *different* target earlier in
  the same imaging session (the frame is correctly plate-solved and really is
  pointing somewhere else — the header is just wrong). Without this check, the
  pipeline used to run its full ~15–90s Gaia/APASS/VSP/GSPC sequence and only fail
  much later with an opaque "0 candidates in frame" — this catches it immediately and
  says why. If you hit this, check Target Name/RA/Dec on the Data tab against what
  this dataset actually is, and consider that other frames in the same folder may
  have the same stale header.

### What it queries, and why

1. **Gaia DR3** — one cone search around the target, returning every field star
   brighter than a magnitude limit. This is the base candidate pool.
2. **AAVSO VSP** (Variable Star Plotter) — AAVSO's own vetted comparison-star
   sequences, when one exists for the field. Always queried alongside Gaia.
3. **APASS DR9** — an all-sky photometric survey (Johnson B/V, Sloan g'/r'/i'),
   used to fill in magnitudes for Gaia-only candidates.
4. **Gaia GSPC** synthetic photometry — Gaia's own synthetic magnitudes derived
   from its low-resolution spectra, generally the most precise catalog source
   available (~0.005–0.015 mag).
5. **AAVSO VSX** (Variable Star Index) — cross-checked against every candidate to
   hard-exclude any star within 5″ of a known variable (a bad comp star is one that
   varies itself).

### Selection criteria

Candidates are scored and ranked on:

- **Color match** (Gaia BP−RP close to the target's) — most important when color
  match is possible, since mismatched colors introduce differential atmospheric
  extinction over the course of a night
- **Magnitude similarity** — stars brighter than the target get full credit; dimmer
  stars are penalized on a sliding scale
- **Astrometric quality** (Gaia RUWE < 1.4 — a well-behaved single-star solution)
- **Isolation** — excluded if a brighter neighbor is too close (the exclusion radius
  scales with the neighbor's brightness)
- **Centrality** in the field of view
- A **+0.10 bonus** for AAVSO VSP stars, since they're independently vetted

After scoring, candidates are checked for **PSF quality** directly on the image
(not just from catalog data): rejected if saturated, low SNR (<75), an outlier FWHM
vs. the field median, or an elongated PSF (blended star). Rejected candidates are
automatically replaced from the backfill pool, for up to 3 passes.

### Magnitude assignment — priority chain

Each comp star's actual magnitude value comes from one of four sources, tried in
this order:

1. **VSP** — AAVSO's own vetted sequence value. Preferred first not because it's
   necessarily the most numerically precise, but because it's the *shared*
   reference every other observer submitting data for this star uses — that
   consistency matters more for the AAVSO International Database than shaving a
   few millimagnitudes.
2. **GSPC** synthetic photometry (~0.005–0.015 mag accuracy)
3. **APASS DR9** (~0.02–0.05 mag accuracy)
4. **G→V** — a hardcoded Gaia G-band → Johnson V polynomial conversion (Evans et
   al. 2018), used only as a last resort when nothing else matches. This is the one
   source that ignores your actual observation filter (it always estimates V), so
   if you're shooting in a non-V filter and a comp star falls through to this tier,
   its magnitude may not perfectly match your bandpass. The pipeline log and the
   Comp Stars grid show `MagSource` per star so you can see when this happened.

### Controls

- **Max comp stars** (1–25, default 10) — AAVSO's own guidance suggests 12–20 when
  the field supports it; the default of 10 is reasonable for typical fields but you
  can raise it for dense fields or lower it for sparse ones.
- **Run** executes the Stone method against the *first* FITS frame in the input
  directory (used only as a reference frame — the actual photometry pass in the
  next tab re-projects every comp star through *each* frame's own WCS).
- The **Pipeline Log** panel shows the full step-by-step trace (Gaia/VSP/APASS
  query results, scoring weights, PSF pass/fail per star) — check this first if a
  run produces "No candidates" or an unexpectedly small ensemble.
- One combined grid shows every candidate — a green ✓ marks a selected comp, a red
  ✗ marks one that was rejected during PSF validation, with the specific reason in
  the last column. An animated progress bar appears while selection is running.

---

## 4. Photometry tab

Runs multi-frame differential photometry using the comp ensemble from the previous
tab:

1. **Aperture sizing** — determined once from the reference frame's measured target
   FWHM (aperture = 1.7×FWHM, sky annulus 3–5×FWHM), then held fixed across every
   frame. A size that adapted per-frame would inject spurious flux changes into the
   light curve that have nothing to do with real variability. The FWHM measurement
   only counts flux that exceeds the local sky background by a noise-based
   threshold (2σ over a robust sky sigma) — at low target SNR, ordinary background
   noise scattered across the measurement window would otherwise get counted as
   signal and inflate the computed FWHM, producing a badly oversized aperture.
2. **Per-frame photometry** — for every FITS file, the target and every comp star's
   sky position is re-projected through *that frame's own WCS* (not reused from the
   reference frame), then refined to the actual local intensity centroid before the
   fixed aperture is measured. This guards against plate solves that are only
   accurate to a few pixels (e.g. ASTAP on some undersampled formats) and against
   real frame-to-frame drift — without it, a small but consistent centering error
   clips real target flux every frame, with no redundancy to average it out the way
   the multi-star comp ensemble has.
3. **Per-comp magnitude estimate** — `target_mag = comp_mag − 2.5·log10(target_flux / comp_flux)`
   for each comp star independently.
4. **Ensemble bias correction** — different comp stars' magnitude sources (VSP vs.
   GSPC vs. APASS vs. G→V) can disagree with each other by a small, *constant*
   amount across the whole run — that's a catalog calibration difference, not
   noise. VariLab estimates each comp's own median offset from the ensemble
   consensus (over all frames) and removes it before combining, so the reported
   error reflects genuine per-frame measurement noise rather than inter-catalog
   disagreement.
5. **Weighted-median combination** — the final magnitude for each frame is the
   bias-corrected, SNR-weighted median across all comps; the uncertainty is the
   weighted MAD across comps (or a photon-limited estimate if only one comp was
   usable that frame).
6. **Sigma-clipping** — outlier frames (3σ, 2 passes) are flagged rejected rather
   than deleted, so you can still see them in the frame-by-frame grid.
7. **Centroid-drift rejection** — the local-centroid refinement in step 2 is a
   flux-weighted centroid over a search window around the WCS-projected position, so
   if a much brighter star happens to fall inside that window, it can dominate the
   weighted average and pull the centroid mostly or entirely onto itself instead of
   the real target — this was confirmed in a dense cluster field, where a Gaia
   neighbor only 4.4″ away and ~2.2 mag brighter corrupted about 65% of a target's
   frames with a bogus, much-brighter reading that alternated with the real one from
   frame to frame. Any frame (or individual comp star) whose refined centroid moves
   more than 6px from its WCS-projected starting point is now rejected rather than
   trusted — this is checked separately for the target and for each comp star, and
   also applied to the one-time reference-frame FWHM measurement that sizes the
   aperture for the whole run. This only ever matters when a bright neighbor falls
   within the centroid search radius, which in practice means dense or cluster
   fields — an isolated target's centroid should never legitimately drift anywhere
   near 6px, so this is invisible in normal use. Rejected frames show up in the
   frame-by-frame grid with reason "target centroid drifted N.Npx from WCS position".

---

## 5. Results tab

- **Period search** — runs automatically as soon as Photometry finishes, no button
  click needed (a "Re-run Period Search" button remains for after adjusting the
  min/max period range or fold period). Uses a native Lomb-Scargle periodogram
  (generalized/floating-mean formulation, Zechmeister & Kürster 2009 — the same one
  `astropy.timeseries.LombScargle` implements by default), no Python dependency.
  The periodogram curve itself isn't shown — a single night's search is only a
  local diagnostic (short-timescale signals like pulsators or flares), since
  genuine long-period variability detection is what AAVSO's archive-scale,
  multi-observer analysis is for, not a single VariLab session. The best period and
  its power are reported in the summary line instead.
- **Phase folding** — folds the light curve at the best-fit period, or any period
  you type in manually (useful for checking aliases, e.g. half or double the
  detected period).
- Two plots: **Light Curve** and **Phase-Folded Light Curve** — rendered in a plain
  matplotlib-style look (white background, tomato error bars) matching the style of
  EXOTIC's own `Stellar_Variability.png` output.

### Exports (automatic)

All three files below are saved automatically the moment the period search
completes — no export button needed. Each run is saved into its own numbered
`VariLab_ResultsN` subfolder of the Target Directory from the Data tab
(`VariLab_Results1`, `VariLab_Results2`, `VariLab_Results3`, ...) — the next
unused number is picked automatically each time, so separate runs never mix
their files together. Each filename also still carries its own
`{yyyyMMdd_HHmmss}` timestamp, and the Status line reports exactly where the
files were saved.

- **AAVSO Extended Format (.txt)** — ready for AAVSO WebObs upload. Uses
  `CNAME=ENSEMBLE` (AAVSO's documented convention when there's no single comp star
  to name) and `TRANS=NO` (VariLab does not compute transformation coefficients).
  The NOTES field lists every ensemble comp star used, and automatically flags a
  meridian (pier) flip if the FITS `PIERSIDE` header changes mid-run — the
  magnitudes/errors themselves are never altered, only noted.
- **Stellar Variability (.png)** — a single magnitude-vs-JD plot in the EXOTIC
  visual style, for sharing or quick reference. Use the "View Stellar Variability
  PNG" button to open the most recently saved one in-app.
- **Excel Report (.xlsx)** — a four-sheet workbook:
  - **Data** — every accepted frame's JD, magnitude, error, and airmass
  - **Comp Stars** — full detail on the selected ensemble (position, magnitude,
    source, FWHM, SNR, Gaia ID, AUID, separation, color, RUWE)
  - **Rejected Comps** — candidates that failed PSF validation, with the specific
    reason
  - **Plots** — the Light Curve and Phase-Folded chart images embedded directly in
    the workbook
- **CompDiagnostics (subfolder)** — one CSV and one quick-look PNG light curve per
  star, the target plus every comp: `JD, Mag, Weight, Airmass, Flux, PeakADU,
  Background, FWHM_X, FWHM_Y, FWHM_Mean`. Mag/Weight are the same bias-corrected
  per-comp values that feed the weighted-median combination on the Photometry tab;
  Flux/PeakADU/Background are the same aperture-photometry values already computed
  for the main pipeline; FWHM_X/FWHM_Y come from an added per-frame PSF moment
  measurement, kept separate rather than averaged together since a directional PSF
  elongation (e.g. from differential chromatic refraction) would be washed out by
  an isotropic mean. All filtered to the same accepted frames as the main light
  curve, so every file lines up frame-for-frame with the rest of the export and
  with each other. Meant for diagnosing a light curve trend that doesn't have an
  obvious cause — is it isolated to one comp star, shared across the whole
  ensemble, or does it track a real PSF/seeing change over the session?

---

## 6. Known limitations

- No per-filter frame separation — point the Target Directory at a single-filter
  folder.
- No calibration or frame-registration pipeline — frames must already be
  calibrated and plate-solved.
- No transformation-coefficient computation (raw differential photometry only,
  `TRANS=NO`).
- No variable-star sequence generator (for fields with no existing AAVSO chart).
- Single target per run; one-shot batch processing only (no live-monitor mode).
- Period-search uncertainty is not formally estimated — only the best period and
  its power are reported.

---

## Appendix: Sources for AAVSO-related design decisions

Several of VariLab's design choices are based on AAVSO's own published guidance rather
than assumption. This appendix lists the specific claim and the source it came from, so
any decision here can be independently checked rather than taken on faith.

| Decision / claim | Source |
|---|---|
| Magnitude priority chain (VSP → GSPC → APASS → G→V): "Always use the VSP values for comparison stars unless you have experience... and good familiarity with the available photometric catalogs." | [AAVSO Guide to Photometric Uncertainty](https://www.aavso.org/sites/default/files/publications/Uncertainty-V9.pdf) |
| Ensemble size guidance (12–20 comps typical; Kent Honeycutt uses every star in frame if properly weighted) — informs VariLab's 1–25 range, default 10 | [AAVSO Guide to Photometric Uncertainty](https://www.aavso.org/sites/default/files/publications/Uncertainty-V9.pdf) |
| G→V polynomial conversion (last-resort magnitude source, used only when no VSP/GSPC/APASS value exists) | Evans, D. W., et al. 2018, "Gaia Data Release 2: Photometric content and validation," *A&A* 616, A4 — [full text](https://www.aanda.org/articles/aa/full_html/2018/08/aa32756-18/aa32756-18.html) / [arXiv:1804.09368](https://arxiv.org/abs/1804.09368) |
| `CNAME=ENSEMBLE`, `CMAG=na` is AAVSO's documented, permitted convention for ensemble photometry | [Comparison star and check star labels when submitting observations](https://www.aavso.org/comparison-star-and-check-star-labels-when-submitting-observations), [Transforming Ensembles](https://www.aavso.org/transforming-ensembles) |
| Ensemble submissions should list which comp stars were used (AUID or chart label) in the NOTES field, since the Extended Format record itself carries no per-comp detail | [Comparison star and check star labels when submitting observations](https://www.aavso.org/comparison-star-and-check-star-labels-when-submitting-observations) |
| NOTES field supports several thousand characters — no truncation risk from listing a full comp ensemble | [AAVSO Extended File Format](https://www.aavso.org/aavso-extended-file-format) |
| Non-VSP comp stars (APASS, Gaia, VizieR/ATLAS refcat2) are an AAVSO-endorsed alternative when no official VSP sequence exists for a field, not a workaround | [What to do if no AAVSO Comp Stars](https://www.aavso.org/what-do-if-no-aavso-comp-stars) |
| `TRANS=NO` (no transformation coefficients) is standard, accepted practice for this citizen-science exoplanet-host workflow — confirmed directly against a real EXOTIC 4.3.1 AAVSO submission file (NASA/JPL Exoplanet Watch's own pipeline), which also submits `TRANS=NO` | User-provided file: `AID_AAVSO_Qatar-1_20-JUN-2026.txt`. General AAVSO preference for transformed data, for context: [Use of transformation coefficients](https://www.aavso.org/use-transformation-coefficients) |
| AAVSO's own period-analysis tool (VStar) is built around DCDFT, not literally "Lomb-Scargle" by name — but DCDFT and the Generalized/floating-mean Lomb-Scargle periodogram (what VariLab implements) are mathematically equivalent least-squares sinusoid+constant fits | [Time Series Tutorial](https://www.aavso.org/time-series-tutorial), Benn, D., 2012, "Algorithms + Observations = VStar," *JAAVSO* 40, [852](https://www.aavso.org/sites/default/files/jaavso/v40n2/852.pdf) |
| Lomb-Scargle period-search algorithm (generalized/floating-mean formulation) implemented natively in `Services/PeriodSearchService.cs` | Zechmeister, M. & Kürster, M. 2009, "The generalised Lomb-Scargle periodogram," *A&A* 496, 577 — [full text](https://www.aanda.org/articles/aa/full_html/2009/11/aa11296-08/aa11296-08.html) |

---

## Appendix: From Python to C# — How VariLab Was Built

A shorter, more informal note than the rest of this guide — the story of what's native
C# vs. what's still Python under the hood, and what actually got tested along the way,
for anyone curious rather than just looking up how to use a field.

### The Python heritage

VariLab's algorithms trace back to a Python scientific-computing lineage — the same one
TransitLab and its underlying EXOTIC pipeline are built on. Comp-star selection descends
from Geoff Stone's CompStarSelector/exotic-proto project; period search follows the same
generalized Lomb-Scargle formulation `astropy.timeseries.LombScargle` implements by
default. Rather than run that Python code directly (which would mean shipping a Python
interpreter and a stack of scientific packages just to run a few thousand lines of
logic), most of it was rewritten from scratch as native C# — reading the original
algorithm, then reimplementing its math directly in the app's own language, rather than
translating line-by-line.

### What's native C# (no Python at all)

- Comp-star selection (the Stone method) — Gaia DR3/VSP/APASS/GSPC queries, scoring,
  isolation checks
- Aperture photometry — sizing, per-frame centroiding, ensemble combination,
  sigma-clipping
- Period search — the generalized/floating-mean Lomb-Scargle periodogram and phase
  folding
- FITS header parsing and WCS (sky-to-pixel/pixel-to-sky) math

### What's still real Python — PSF Fit mode

PSF Fit is the one exception, and deliberately so: photutils' empirical-PSF (ePSF)
fitting is a mature, extensively-used implementation that would take a great deal of
work to reproduce faithfully in C#, for a feature that's genuinely optional (Aperture
mode alone covers most use cases). Instead, PSF Fit runs a small bundled Python script
(astropy, photutils, numpy, scipy) inside its own isolated virtual environment under
`%AppData%\VariLab\pyenv` — installed automatically the first time PSF Fit is used (a
one-time "PSF Engine Setup" step), invoked as a background subprocess per run, never
requiring you to already have Python installed. Everything upstream of it (frame
selection, comp-star list, WCS) and downstream of it (ensemble combination, export)
stays C# — Python's involvement is scoped to exactly the one algorithm that benefits
from it.

### What actually got tested

- **Lomb-Scargle**: validated by formulation, not just by running it — the
  generalized/floating-mean variant implemented here is the same one
  `astropy.timeseries.LombScargle` uses by default (Zechmeister & Kürster 2009), not a
  simpler classical periodogram that would give subtly different power values.
- **PSF Fit vs. Aperture**: cross-checked end-to-end on a real, isolated target
  (V1655 Cen) — mean magnitude and amplitude agreed to within ~0.01 mag, and PSF Fit
  additionally recovered 4 frames Aperture mode had rejected outright.
- **A real limitation this testing found, not a theoretical one**: on a target with a
  genuinely close, bright neighbor (a few arcsec), simultaneous PSF fitting can land on
  a plausible-looking but wrong flux split between the two stars — confirmed on two real
  cases (V1786 Cen, V1615 Cen), both showing amplitude inflated well above their known
  VSX reference values, and not something photutils' own fit-quality flags reliably
  catch. This is a structural property of simultaneous PSF fitting on blended sources,
  not a bug in this app or in photutils — the correct tool for that specific case is
  difference-image analysis (DIA), which VariLab doesn't implement.
- **Real bugs, found on real data, not synthetic test cases**: an unrelated bright Gaia
  neighbor silently oversizing the whole run's aperture (KELT-8); a nearby bright star
  hijacking the target's own centroid in a dense cluster field; and, during a routine
  speed audit, a completely unrelated discovery that AAVSO had quietly moved two of the
  URLs this app's own comp-star pipeline depends on, silently degrading every affected
  run for some time before anyone noticed.
| `FILT` export mapping for filters with no valid AAVSO ShortName (`L`→`CV`, `C`→`CV`, `CBB`→`CR`, `Rc`→`R`, `Ic`→`I`) — confirmed `L` isn't an accepted ShortName, and that AAVSO's own documented practice for Luminance/Clear/CBB imaging is to submit as CV or CR referenced to whichever comp-star band was used | [Filter Band ShortNames API](https://vsx.aavso.org/index.php?view=api.bands), [How to submit and measure images with a clear or luminance filter?](https://www.aavso.org/how-submit-and-measure-images-clear-or-luminance-filter), [Luminance filter](https://www.aavso.org/luminance-filter) |
