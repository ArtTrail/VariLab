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

**Availability**: self-contained builds for Windows, macOS (Apple Silicon and Intel),
and Linux are all available from the GitHub Releases page
(github.com/ArtTrail/VariLab/releases) — no separate .NET install needed on any
platform.

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
   - [Exports](#exports-automatic)
6. [Batch Process](#6-batch-process)
7. [Known limitations](#7-known-limitations)
8. [Appendix A: Sources for AAVSO-related design decisions](#appendix-a-sources-for-aavso-related-design-decisions)
9. [Appendix B: PSF photometry vs. difference imaging in crowded fields](#appendix-b-psf-photometry-vs-difference-imaging-in-crowded-fields)
10. [Appendix C: From Python to C# — How VariLab Was Built](#appendix-c-from-python-to-c--how-varilab-was-built)
11. [Appendix D: How VariLab Measures a Star's Brightness (Aperture, PSF Fit, and ePSF)](#appendix-d-how-varilab-measures-a-stars-brightness-aperture-psf-fit-and-epsf)

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
| Target Directory | Folder of FITS frames — must be pre-calibrated and pre-plate-solved (valid WCS in each file). All frames should be the **same filter** — VariLab does not currently separate frames by filter, so a folder with mixed bands will produce a meaningless mixed-band light curve. As soon as a valid folder is set, VariLab reads the first FITS file's header for `OBJECT` and `FILTER` and auto-fills Target Name and Filter below (see next row) — then automatically runs the RA/Dec lookup. **Pointing this at a new folder clears the Comp Stars, Photometry, and Results tabs** — selected comps, the light curve, and period-search output from whatever dataset was open before are reset, so they can't be mistaken for results from the new one. Remembers its own last-used folder across sessions, independently of every other directory field in the app (Output Directory, and the Batch window's own Input/Results Directories each remember their own separately). |
| Output Directory | Where the `VariLab_ResultsN` export folders (AAVSO `.txt`, Stellar Variability `.png`, Excel report) are saved — see [Exports](#exports). Defaults to match Target Directory whenever it changes, so exports normally land right next to the source frames with no extra setup. Browse to a different folder here to save exports somewhere else instead (e.g. a shared results folder, or alongside another tool's output for comparison) — once you do, it stops auto-following Target Directory (so it's safe to switch datasets afterward without it snapping back), and that explicit choice is remembered as this field's own last-used value for next session, taking over from the auto-follow default at startup too. |
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

**Target neighbor pre-flight check** — comp candidates are isolation-checked (above),
but the target itself never was, since it can't be swapped out for a cleaner one the
way a bad comp candidate can. This check applies the identical isolation standard
used for comps to the target's own position: if a Gaia-catalogued neighbor falls
within that same exclusion radius, a warning appears on this tab (and in the pipeline
log) *before* Photometry spends minutes on a run likely to have amplitude/mean-mag
contamination — see Appendix B. Non-blocking; confirmed on two real cases (V1786 Cen,
V1615 Cen) that would have tripped this immediately.

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
  the last column. An animated progress bar and an elapsed-time counter appear
  while selection is running.

---

## 4. Photometry tab

Runs multi-frame differential photometry using the comp ensemble from the previous
tab. **Aperture** and **PSF Fit** are independent checkboxes, not an either/or choice
— check one, or both. Checking both runs Aperture fully (including its own Results
export) and then PSF Fit fully, one after the other, rather than requiring two
separate manual Run clicks. An elapsed-time counter is shown next to the status line
while a run is in progress.

An overview of the pipeline itself:

1. **Aperture sizing** — determined once from the reference frame (aperture =
   1.7×FWHM, sky annulus 3–5×FWHM), then held fixed across every frame. A size
   that adapted per-frame would inject spurious flux changes into the light curve
   that have nothing to do with real variability. The sizing FWHM is the *median
   of the comp ensemble's* measured FWHM, not the target's own — comps are chosen
   to be isolated, clean point sources (the comp-selection step's isolation
   check), so their FWHM is a more trustworthy basis than a single measurement
   taken directly on the target. This matters because a neighbor close enough to
   blend into the target's own reference-frame measurement — without being close
   enough to trip the centroid-drift guard in step 7 — can otherwise inflate the
   target's own FWHM and, from that, oversize the aperture for the whole run,
   capturing more of the neighbor's flux every frame (confirmed on a real case:
   KELT-8, with a G=11.8 Gaia neighbor 9″ away biasing the target's own FWHM to
   11px against the comp ensemble's clean 4.6–5.5px). The target's own
   reference-frame FWHM is still measured as a fallback, used only if no comp
   star is measurable at all. Each FWHM measurement only counts flux that exceeds
   the local sky background by a noise-based threshold (2σ over a robust sky
   sigma) — at low SNR, ordinary background noise scattered across the
   measurement window would otherwise get counted as signal and inflate the
   computed FWHM, producing a badly oversized aperture.
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
8. **Nearby-star warning** — a complementary, non-blocking check for the case a
   neighbor is close enough to blend into the target's photometry without pulling
   the centroid past the 6px threshold in step 7. If the target's own
   reference-frame FWHM (measured in step 1) is 1.5× or more the comp ensemble's
   median FWHM, VariLab shows a warning in the Photometry tab's status line (and
   logs it to the session log) naming the measured ratio, so you know to check the
   field for a close companion. It doesn't stop or alter the run — aperture sizing
   already defends against this in step 1 by using the comp median instead of the
   target's own reading — this is just a flag that contamination may still be
   present even after that defense, since a wide or partial blend can survive it.

---

## 5. Results tab

- **Crowding flags** — runs automatically alongside the period search. Every comp
  star's magnitude is correlated against per-frame FWHM (a good comp is non-variable,
  so its own magnitude *is* its residual); the target's magnitude is first detrended
  against a binned phase-fold (skipped if the period search's best power is too low to
  trust) before the same correlation is run on what's left. Any star with a
  statistically significant correlation (p < 0.01) is flagged — a bold warning on this
  tab, in the AAVSO NOTES field, and as its own sheet in the Excel report (always
  present, even with zero rows, so its absence is a checked-and-clean result rather
  than ambiguous with "never checked"). See Appendix B for why this check exists and
  what it catches that a clean-looking light curve can still hide.
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
completes — no export button needed. Each run is saved into its own
`VariLab_Results_{Target}_{ObsDate}_{Mode}_N` subfolder of the Output
Directory from the Data tab (e.g. `VariLab_Results_V1585 Cen_20240702_
PsfFit_1`) — the target name, observing date, and photometry mode are all in
the folder name so an Aperture run and a PSF Fit run on the same target/night
are never mistaken for the same result without opening either one. The
trailing number still disambiguates repeat runs of the same target/night/mode
combination. Each filename also still carries its own `{yyyyMMdd_HHmmss}`
timestamp, and the Status line reports exactly where the files were saved.

**If a run fails** at any stage — Comp Stars, Photometry, or Results — instead
of a `VariLab_Results_...` folder, a `VariLab_FAILED_{Target}_{ObsDate}_
{Mode}_N` folder appears in the same place, containing a single
`failure_reason.txt` with the specific reason (e.g. no comparison stars
found, every frame rejected for centroid drift, too few accepted frames to
fold a light curve). This is the only record of a failed run — check here
before assuming a missing results folder means the target simply wasn't
tried yet.

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
  - **Comp Stars** — full detail on the selected ensemble (comp number "C1"/"C2"/...
    matching the field image and photometry labels, position, magnitude, source,
    FWHM, SNR, Gaia ID, AUID, separation, color, RUWE)
  - **Rejected Comps** — candidates that failed PSF validation, with the specific
    reason
  - **Plots** — the Light Curve and Phase-Folded chart images embedded directly in
    the workbook
- **CompDiagnostics (subfolder)** — one CSV and one quick-look PNG light curve per
  star, the target plus every comp: `JD, Mag, Weight, Airmass, Flux, PeakADU,
  Background, FWHM_X, FWHM_Y, FWHM_Mean`.
  **Important — what the `Mag` column / plot actually shows:** every file here plots
  the **target's** magnitude, *not* the brightness of the star named in the title.
  The "Target" file is the final ensemble (weighted-median) light curve; each "Cn"
  file ("…measured via comp Cn only") is that *same target* measured against that
  one comp star alone. So a "Cn" plot showing the target's pulsation shape is
  expected and correct — it does **not** mean the comp is variable. A comp's own
  brightness is essentially constant; what varies is the target in the numerator of
  the differential ratio. (The comp's own raw counts are the `Flux` column, which
  stays steady frame-to-frame and drifts only slowly with airmass/transparency.)
  The real diagnostic value is cross-checking *agreement*: if one "Cn" curve
  disagrees in shape with the others, that comp is suspect; if they all agree (the
  normal case), the ensemble is healthy.
  Mag/Weight are the same bias-corrected
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
- **Field Image (.png)** — a stretched crop of the reference frame around the
  target and comp ensemble, with the target's own name labeled (not the generic
  word "Target") and every comp labeled "C1"/"C2"/... matching the Excel report
  and photometry output. In Aperture mode, the actual fixed aperture and sky
  annulus circles used for the whole run are drawn to scale around the target;
  in PSF Fit mode (which has no single fixed aperture size — each frame's fit has
  its own footprint) a small marker circle is drawn instead, purely to show where
  the target and comps are, not a real photometric footprint.

---

## 6. Batch Process

**Tools → Batch Process…** runs the same Comp Stars → Photometry → Results
pipeline the four main tabs already do, but across a list of targets instead
of one at a time — the same underlying code, just driven by a loop instead of
clicking Run repeatedly. Useful for working through many targets from the same
night's dataset (e.g. every RR Lyrae candidate in a cluster field).

- **Input Directory** — the same folder of pre-calibrated, plate-solved FITS
  frames used for every target in the batch (see [Data tab](#2-data-tab)).
  Remembers its own last-used folder across sessions, independently of every
  other directory field in the app.
- **Results Directory** — a single base folder; each target gets its own
  subfolder created automatically underneath it (named after the target, with
  any characters invalid in a Windows folder name replaced). No pre-existing
  folder structure is required — subfolders are created as the batch reaches
  each target. Also remembers its own last-used value independently.
- **Targets** — one target name per line, exactly as it would be typed into
  the Data tab's Target Name field. Either type/paste the list directly, or
  click **Import from file…** to load one from a CSV or XLSX file: pick the
  file, then pick which column holds the target names from the dropdown that
  appears, then **Use column** to fill the target list from it. The list stays
  editable afterward — importing just fills in a starting point.
- **Filter / Max comp stars / AAVSO Observer Code** — same meaning as the Data
  and Comp Stars tabs, applied to every target in the batch.
- **Aperture / PSF Fit** — check either or both; whichever are checked run for
  every target, one after the other (Aperture first).
- **Pop up the results plot** — off by default. When checked, opens a preview
  window for each result plot as it's produced during the run. Windows stay
  open and cascade (each offset diagonally from the last) rather than closing
  or replacing one another, so a long batch — or one running both photometry
  methods — builds up its full visual history as it goes rather than only
  showing the most recent plot.

For each target, in order: resolve its name to RA/Dec (same AAVSO VSX → NASA
Exoplanet Archive → SIMBAD chain as the Data tab), run Comp Stars, then run
each checked photometry mode — each of which auto-exports via the Results tab
exactly as it would from a manual run. If a target's name can't be resolved,
if Comp Stars finds no usable comparison stars, or if a photometry run
accepts too few frames to fold a light curve, that target is skipped and the
batch moves on to the next one — the specific reason is written to a
`VariLab_FAILED_{Target}_{Date}_{Mode}_N\failure_reason.txt` file inside that
target's subfolder, the same failure-reporting mechanism used everywhere else
in VariLab (see [Known limitations](#7-known-limitations) note on this below).
A source (VSP/Gaia/APASS) that fails to respond doesn't stop a batch run
either — it's treated the same as clicking "Continue Anyway" would be during
a manual run, using whichever sources did respond.

Targets are processed **strictly one at a time** — there's no option yet to
run several in parallel. The per-frame PSF Fit computation itself isn't any
faster than running it manually; what Batch Process removes is the time spent
between steps (switching tabs, waiting, re-entering the next target by hand).

---

## 7. Known limitations

- No per-filter frame separation — point the Target Directory at a single-filter
  folder.
- No calibration or frame-registration pipeline — frames must already be
  calibrated and plate-solved.
- No transformation-coefficient computation (raw differential photometry only,
  `TRANS=NO`).
- No variable-star sequence generator (for fields with no existing AAVSO chart).
- Batch Process (see [above](#6-batch-process)) runs multiple targets
  sequentially, but not concurrently, and there's no live-monitor mode (new
  frames arriving during a run).
- Period-search uncertainty is not formally estimated — only the best period and
  its power are reported.

---

## Appendix A: Sources for AAVSO-related design decisions

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
| `FILT` export mapping for filters with no valid AAVSO ShortName (`L`→`CV`, `C`→`CV`, `CBB`→`CR`, `Rc`→`R`, `Ic`→`I`) — confirmed `L` isn't an accepted ShortName, and that AAVSO's own documented practice for Luminance/Clear/CBB imaging is to submit as CV or CR referenced to whichever comp-star band was used | [Filter Band ShortNames API](https://vsx.aavso.org/index.php?view=api.bands), [How to submit and measure images with a clear or luminance filter?](https://www.aavso.org/how-submit-and-measure-images-clear-or-luminance-filter), [Luminance filter](https://www.aavso.org/luminance-filter) |

---

## Appendix B: PSF photometry vs. difference imaging in crowded fields

**Source**: *"Globular Cluster RR Lyrae Program — Critique of the Proposed Plan, and a
Revised Program for the 0.61 m CDK24"* (internal document, provided by a collaborator;
analysis of a telescope-time proposal for Dimension Point Observatory, Mayhill, NM,
covering M3/M5/M53/NGC 5466 and M2/NGC 6934/NGC 6981/M15 — a different instrument and
cluster sample than VariLab's own NGC 5139/Omega Centauri testing, which is too far
south to observe from that site). Reviewed 2026-08-14.

### The core finding

The document's method-comparison table (§1.3) states, without qualification:

> **PSF photometry** — Fails in cluster cores at any seeing; mandatory failure at 2.5″
> → replaced by **Difference Image Analysis (DIA)**, Alard & Lupton kernel

This matches VariLab's own real-world experience building and testing PSF Fit mode
against NGC 5139 (a considerably more crowded core than any cluster in that document's
sample). Two independent close-neighbor targets (V1786 Cen, V1786's Gaia companion 4.4″
away and 2.2 mag brighter; V1615 Cen, a comparable case) both produced amplitude
inflation relative to their VSX reference values, traced to the same root cause: PSF
fitting has to solve a per-frame flux *split* between blended stars, and for a close,
high-contrast pair, more than one split can fit the pixel data almost equally well.
`qfit`/`flags` (Photutils' own fit-quality metrics) don't reliably distinguish a correct
split from a plausible-but-wrong one — this is a structural property of simultaneous
PSF fitting on blended sources, not an engine bug, and every mitigation tried (grouping
radius, saturation checks, Gaia neighbor seeding, per-frame ePSF rebuilding) failed to
resolve it.

**Why DIA sidesteps this**: DIA never computes absolute flux via multi-star
deblending. It convolves a stable reference/template image to match each new frame's
PSF, subtracts it, and measures the *residual* — a star's own flux change shows up
directly at its catalog position without ever having to decide how much of the
blended light belongs to which star. A constant blend contribution simply subtracts
out. This is why every difference-imaging pipeline built specifically for crowded
fields (Bramich 2008; Bramich et al. 2013, "DanDIA") exists as a separate method from
PSF photometry rather than a variant of it.

### What this means for VariLab's PSF Fit mode

PSF Fit mode remains valid and useful for **isolated targets** (validated end-to-end
against V1655 Cen: mean-mag and amplitude within ~0.01 mag of Aperture mode, plus 4
extra usable frames). It is **not** the right tool for the close-neighbor crowded-core
case that originally motivated building it — that case needs difference imaging, which
VariLab does not currently implement. This is a real, documented limitation of the
method, not a defect to keep chasing with parameter tuning.

### Actionable ideas from this document, independent of whether DIA is ever built

- **FWHM-residual crowding regression** (§2.9, called "not optional" in the source
  document) — **implemented**: see `Services/CrowdingFlagService.cs`, wired into the
  Results tab, AAVSO NOTES, and the Excel report's "Crowding Flags" sheet. Comp stars
  are checked directly (magnitude vs. FWHM); the target is checked against
  phase-detrended residuals (`PeriodSearchService.PhaseResiduals`), skipped if the
  period search's best power doesn't clear a confidence floor. Also implemented: a
  **target neighbor pre-flight check** (Comp Stars tab, before Photometry ever runs)
  — see its section above. (A frame-level seeing cutoff was also implemented here at
  one point but was removed in v1.5.1 — it wasn't found to meaningfully affect
  results in practice.)
- **Self-referential crowding limit** (§2.4) — define the crowding limit as the
  cluster-centric radius at which a star's photometric scatter reaches 2× the scatter
  of isolated stars of matched brightness on the *same* frames. Self-calibrates per
  night/dataset rather than depending on an assumed noise floor.
- **Times-of-maximum (O–C) as a blend-resistant measurement** (§2.2) — a constant
  blend contribution shifts mean magnitude and compresses amplitude, but does not move
  the phase of maximum light. For a target where amplitude will never be trustworthy
  by any method (a genuinely close, bright neighbor), epoch-of-maximum timing may still
  be a usable, robust measurement — a fundamentally different thing to optimize for
  than amplitude/mean-mag accuracy.

---

---

## Appendix C: From Python to C# — How VariLab Was Built

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

---

## Appendix D: How VariLab Measures a Star's Brightness (Aperture, PSF Fit, and ePSF)

A plain-English tour of what actually happens when VariLab measures a star — no math
background needed. (A more technical companion is [Appendix C](#appendix-c-from-python-to-c--how-varilab-was-built).)

### The basic question

Every brightness measurement is really answering one question: of all the light that
landed on the camera, how much came from *this* star — not from the sky glow around it,
or from a neighboring star? VariLab offers two ways to answer it, **Aperture** and
**PSF Fit**, and you can run either or both.

### Aperture photometry — the bucket method

Draw a circle around the star, add up all the light inside it, then estimate the sky
background from a thin ring just outside that circle and subtract it. Whatever's left is
the star's light. It's simple, fast, and very robust — the right choice for most stars.
Its one weakness: the circle can't tell whose light is whose, so if another star sits
close to your target, some of that neighbor's light falls inside the circle and gets
counted as the target's. In an empty field that never happens; in a crowded field (like
a globular-cluster core) it can.

### PSF Fit — the shape method

Instead of a fixed circle, PSF Fit uses the fact that every star in a given image has
essentially the same *shape* (see below). It takes that shape and fits it to the
target — and, crucially, can fit the target and any close neighbors **at the same
time**, each with the same shape but its own brightness. Because it solves for all of
them together, it can pull apart light that a plain circle would have lumped into one
blob. That makes it the better tool in crowded fields.

### So what is a "PSF"?

PSF stands for **Point Spread Function**. A star is so far away it's effectively a
single point of light — but by the time that light passes through the atmosphere and
your telescope and lands on the sensor, it's been smeared into a small fuzzy blob a few
pixels across (that smearing is what "seeing" refers to). The PSF is simply the *shape*
of that blob. The key idea: within one image, that shape is almost identical for every
star — bright stars just make a taller version of the same shape, faint stars a shorter
one. So if you know the shape, you can recognize and measure any star in the frame.

### And what's the "e" in ePSF?

There are two ways to describe the blob shape. One is to **assume** a standard
mathematical curve (a bell curve, roughly) — quick, but real star images have subtle
asymmetries, wings, and quirks a formula doesn't capture. The other is to **measure**
the real shape directly from your own image — and that's an **ePSF**, an *empirical* PSF
(empirical = measured from the data, not assumed). VariLab builds its ePSF by finding a
few dozen bright, isolated, unsaturated stars in the reference frame and averaging them
into one high-resolution template of exactly what a star looks like in that image, then
fits that template to the target and its neighbors. It's a bit like learning a person's
handwriting from many samples so you can read a smudged word they wrote — rather than
assuming everyone writes in the same standard font.

### Which should I use?

For an isolated target, Aperture is simple and excellent — start there. Reach for PSF
Fit when a neighbor sits close enough that a circle would catch its light (dense or
cluster fields). You can also run both and compare; on an isolated star they should
agree closely. One honest caveat: when a neighbor is extremely close *and* bright, even
simultaneous PSF fitting can mis-split their light — see
[Appendix B](#appendix-b-psf-photometry-vs-difference-imaging-in-crowded-fields) for
when a fundamentally different technique (difference imaging) is needed.

### Under the hood

Aperture photometry is written in native C# (no Python needed). PSF Fit is the one part
of VariLab that runs a small bundled Python engine, because it relies on **photutils** —
a mature, widely-used library in the **astropy** ecosystem — to build the ePSF and do
the simultaneous fitting. That engine uses **astropy** for reading the FITS images,
sky-to-pixel coordinate conversion, background/noise statistics, and time handling, plus
**numpy** and **scipy** for the number-crunching. It's set up once, automatically, the
first time you use PSF Fit (Photometry tab → PSF Engine Setup) — you don't need Python
installed yourself.
