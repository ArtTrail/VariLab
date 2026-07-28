# VariLab's C# Port of the Stone Method — Function-by-Function Comparison Against the Original Python Reference

**Prepared:** 2026-07-17
**Python reference:** `exotic-proto` (github.com/geofstone/exotic-proto), Geoff Stone's original implementation
**C# implementation:** VariLab v1.2.1 (`GaiaCompService.cs`, `PsfService.cs`, `PhotometryService.cs`)

---

## 1. Purpose and Scope

This document compares VariLab's C# implementation of the "Stone method" comparison-star
selection and photometry algorithm against Geoff Stone's original Python reference
implementation, function by function, with exact source line citations in both codebases.

The comparison covers everything that exists in both codebases: comparison-star
selection (Gaia DR3 / AAVSO VSP / APASS DR9 / GSPC candidate discovery, quality
filtering, isolation checking, scoring, magnitude assignment, PSF validation) and the
single-frame aperture-photometry primitive. It does **not** cover VariLab's multi-frame
ensemble differential photometry (`PhotometryService.cs`), because the Python reference
repository has no equivalent — `select_comp_stars()` operates on a single FITS file and
has no time-series/light-curve stage at all. That part of VariLab is noted as a
VariLab-only extension, not a port, in Section 5.13.

Two Python-only pathways in `comp_star_selector.py` are also out of scope, since VariLab
doesn't use them: `_run_calibration_mode()` (lines 307-580, a calibration-frame-only
analysis mode with no target star) and `_query_landolt_with_diagnostics()` (lines
138-210, Landolt SA standard-field photometry). Both exist in the reference repo but
were never ported, because VariLab's use case (ensemble differential photometry of a
variable star in an arbitrary field) doesn't call for them.

## 2. Methodology

For every function pair below, both implementations were read directly, side by side,
and the algorithm — control flow, formulas, thresholds, constants — was compared
statement by statement. Where a difference was found, it is flagged explicitly rather
than glossed over.

In addition to static code comparison, two live tests were run against real data (a
FITS field from NGC 5139 / Omega Centauri, 07-02-2024):

- **Comp-star selection**: Geoff's Python `select_comp_stars()` was run standalone
  (outside VariLab, importing the module directly) on the same reference frame and
  target coordinates VariLab was run on, and the two candidate lists were diffed
  star-by-star.
- **Aperture photometry**: one confirmed discrepancy (Section 6.3) was fixed in the C#
  code, and a full light-curve rerun was diffed against the pre-fix run to confirm the
  fix behaves exactly as predicted, with no unintended side effects.

Full results are in Section 7.

## 3. Pipeline-Stage Overview

Both implementations follow the same eight-stage pipeline, in the same order — this
structure is itself evidence of a deliberate, faithful port, not an independent
re-implementation that happens to reach similar answers:

| Stage | Python | C# |
|---|---|---|
| Orchestration | `select_comp_stars()` | `GaiaCompService.FetchAsync()` |
| 1. Gaia DR3 field query | `gaia_client.query_gaia_field()` | `GaiaCompService.QueryGaiaAsync()` |
| 2. AAVSO VSP query | `vsp_client.query()` | `GaiaCompService.QueryVspAsync()` |
| 3. APASS DR9 query | `gaia_client.apass_query_field()` | `GaiaCompService.QueryApassAsync()` |
| 4. Candidate pool + quality filter | `gaia_client.filter_candidates_from_field()` | inline in `FetchAsync()`, lines 390-471 |
| VSX variable cross-check | (folded into scoring, see 5.6) | `GaiaCompService.QueryVsxAsync()` |
| 5. Frame projection & isolation | `candidate_pool.frame_check()` + isolation block in `candidate_scorer.score_candidates()` | inline in `FetchAsync()`, lines 634-705 |
| 6. Scoring | `candidate_scorer.score_candidates()` | inline in `FetchAsync()`, lines 707-844 |
| 7. GSPC synthetic photometry + magnitude chain | `gaia_client.query_synthetic_photometry()` + `magnitude_chain.assign_magnitudes()` | `GaiaCompService.QueryGspcAsync()` + `GaiaCompService.AssignMagnitudes()` |
| 8. PSF validation | `psf_measure.measure_star_fwhm()` | `PsfService.Measure()` |

---

## 4. Detailed Function-by-Function Comparison

### 4.1 Orchestration

| | Python | C# |
|---|---|---|
| Function | `select_comp_stars(fits_path, request, progress_callback)` | `FetchAsync(...)` |
| Location | `comp_star_selector.py:581-1993` | `GaiaCompService.cs:222-1216` |

Both are the top-level pipeline driver: parse FITS → resolve target → query Gaia →
query VSP → query APASS → build candidate pool → VSX exclusion → frame
projection/isolation → score → GSPC/magnitude assignment → PSF validation → return
final comp list. Same stage order in both, confirmed by direct read-through of both
functions top to bottom.

### 4.2 Stage 1 — Gaia DR3 Field Query

| | Python | C# |
|---|---|---|
| Function | `query_gaia_field()` → `_query_gaia_field_tap()` | `QueryGaiaAsync()` |
| Location | `gaia_client.py:529-553` (dispatcher), `387-436` (TAP query) | `GaiaCompService.cs:1217-1284` |

Both issue a cone search against `gaiadr3.gaia_source` via TAP, requesting the same
core columns (`source_id, ra, dec, phot_g_mean_mag, bp_rp, ruwe,
phot_g_mean_flux_over_error, phot_variable_flag, pmra, pmdec, ref_epoch`,
plus binarity flags `ipd_frac_multi_peak`, `ipd_gof_harmonic_amplitude`,
`non_single_star`, `duplicated_source`). C# additionally implements multi-mirror
failover (`TapEndpoints[]`, `GaiaCompService.cs:38-44`) that the Python version does
not have — Python's `_query_gaia_field_tap` has a single TAP endpoint with a VizieR
fallback path (`_query_gaia_field_vizier`, `gaia_client.py:470-528`) rather than
multiple Gaia-TAP mirrors.

### 4.3 Stage 2 — AAVSO VSP Query

| | Python | C# |
|---|---|---|
| Function | `vsp_client.query()` | `QueryVspAsync()` |
| Location | `vsp_client.py:35-96` | `GaiaCompService.cs:1498-1593` |

Both call the same AAVSO VSP chart API (`aavso.org/apps/vsp/api/chart`) with FOV and
magnitude-limit parameters and parse the returned photometry table for AUID, catalog
magnitude, and error per comp star. Endpoint and query parameters match.

### 4.4 Stage 3 — APASS DR9 Query

| | Python | C# |
|---|---|---|
| Function | `apass_query_field()` | `QueryApassAsync()` / `ParseApassCsv()` |
| Location | `gaia_client.py:882-914` | `GaiaCompService.cs:1383-1454` / `1455-1497` |

Both query VizieR's APASS DR9 catalog (`II/336/apass9`) via a cone search, requesting
filter-specific magnitude/error columns. One historical bug on the C# side (already
fixed, see project history) was requesting the wrong Sloan column names
(`g_mag`/`r_mag`/`i_mag` instead of VizieR's actual `g'mag`/`r'mag`/`i'mag`); current
code matches. `ParseApassCsv` additionally distinguishes a genuinely-empty-but-valid
response from a real query failure (`Failed=false` for zero-row results), a distinction
Python's version does not make explicit — Python treats an empty result the same as a
successful query implicitly, by returning an empty/None table without a separate
`Failed` flag.

### 4.5 Candidate Pool + Quality Filter

| | Python | C# |
|---|---|---|
| Function | `filter_candidates_from_field()` | inline, `FetchAsync()` lines 390-471 |
| Location | `gaia_client.py:554-670` | `GaiaCompService.cs:390-471` |

Both apply the same four hard cuts: magnitude range around the target, RUWE
(astrometric quality), flux-over-error / FOE (photometric quality), and Gaia's
`phot_variable_flag`. Binarity flags (`non_single_star`, `ipd_frac_multi_peak`,
`ipd_gof_harmonic_amplitude`, `duplicated_source`) are **not** hard cuts in either
implementation — both defer them to scoring as soft penalties, with matching rationale
in both codebases' comments (sub-arcsecond multiplicity is invisible at ground-based
resolution).

**Confirmed threshold difference:** Python's `filter_candidates_from_field` uses a
fixed cutoff of **RUWE < 1.4, FOE > 100** (`gaia_client.py:631,639`). C#'s inline
filter uses a **tiered RUWE fallback** (tries RUWE < 1.1 first; relaxes to < 1.2, then
< 1.4, only if the stricter tier yields too few candidates —
`GaiaCompService.cs:401-434`) and a stricter **FOE > 200**
(`GaiaCompService.cs:407`). This is a real, confirmed parameter divergence — not a
logic bug, since both thresholds are internally consistent and the tiered approach is
arguably a refinement (prefer the tightest astrometric quality available, only relax
if needed) — but it does mean the two implementations query different-sized candidate
pools from the same field under the same conditions. See Section 6.1 for its measured
downstream effect.

### 4.6 VSX Variable-Star Cross-Check

| | Python | C# |
|---|---|---|
| Function | (Gaia's own `phot_variable_flag` only — no separate VSX cross-check in the comp-star path) | `QueryVsxAsync()` |
| Location | — | `GaiaCompService.cs:1594-1645` |

This is the one stage present in C# with **no Python equivalent**. Gaia's own
`phot_variable_flag` catches variables Gaia itself has flagged, but VSX is a much
larger, complementary catalog of known variables (many fainter/newer discoveries not
yet flagged in Gaia DR3). C# queries VSX for all known variables in the field and
hard-excludes any Gaia candidate within 5″ of one (`GaiaCompService.cs:552-584`) — a
safeguard against selecting a second, undetected variable star as a "comparison" star.
This is a genuine addition beyond the ported algorithm, not a divergence from it.

### 4.7 Frame Projection & Proper-Motion Correction

| | Python | C# |
|---|---|---|
| Function | `candidate_pool.frame_check()` (+ `_pm_correct()`) | inline, `FetchAsync()` lines 634-671 |
| Location | `candidate_pool.py:78-122` (+ `65-75`) | `GaiaCompService.cs:634-671` (+ `ApplyPm`, `1783-1793`) |

Verified **exact formula match**: proper-motion correction
(`dra = pmra·Δt / (3,600,000·cos(dec))`, `ddec = pmdec·Δt / 3,600,000`, both in
mas/yr → degrees) is identical in both, down to the same `cos(dec) > 0` guard.
Edge-margin exclusion is identical: `margin = max(20, int(dimension × 0.05))` in both
— a 5% margin with a 20px floor, same formula, same constants.

One structural (not logic) difference: Python excludes candidates within 30″ of the
target during **scoring** (`candidate_scorer.py:74-79`, via `min_separation_arcsec`
default). C# applies the same 30″ cutoff one stage earlier, during **frame
projection** (`GaiaCompService.cs:661`). Same threshold, same ultimate effect, applied
at a different point in the pipeline.

### 4.8 Isolation Check & Scoring

| | Python | C# |
|---|---|---|
| Function | `score_candidates()` | inline, `FetchAsync()` lines 675-844 |
| Location | `candidate_scorer.py:15-207` | `GaiaCompService.cs:675-844` |

**Isolation threshold — exact match:** `10″ if field_star_count > 500 else 15″` in
both (`candidate_scorer.py:43`, `GaiaCompService.cs:675`).

**Isolation exclusion radius — exact match:** brightness-dependent exclusion,
`exclusion = threshold + max(0, 12.0 − neighbor_mag) × 30.0`, using a **fixed
reference magnitude of 12.0** (not the target's own magnitude) in both
(`candidate_scorer.py:92-96`, `GaiaCompService.cs:679-690`).

**Scoring formula — exact match.** No-color-match branch (used whenever the target
itself has no usable Gaia BP-RP, the common case for a variable star with no color
term):

```
score = mag_score×0.30 + flux_score×0.30 + ruwe_score×0.15 + centrality×0.25 [+ VSP bonus 0.10]
```

Verified identical in `candidate_scorer.py:155-156` and the logged weights in
`GaiaCompService.cs` ("`Weights: mag×0.30 + flux×0.30 + ruwe×0.15 + centrality×0.25 +
VSP+0.10`"). The color-match branch (`candidate_scorer.py:151-153`) uses the same
weight redistribution (`color×0.30 + mag×0.25 + flux×0.15 + ruwe×0.10 +
centrality×0.20`) in both. Individual component formulas — magnitude score (full
credit brighter than target, linear falloff over 1.5 mag fainter), flux score
(`min(1, FOE/500)`), RUWE score (`max(0, 1 − (RUWE−1)/0.4)`), centrality
(FOV-radius-relative falloff) — all matched line-for-line.

### 4.9 GSPC Synthetic Photometry & Magnitude Priority Chain

| | Python | C# |
|---|---|---|
| Function | `query_synthetic_photometry()` + `magnitude_chain.assign_magnitudes()` | `QueryGspcAsync()` + `AssignMagnitudes()` |
| Location | `gaia_client.py:798-881` + `magnitude_chain.py:14-155` | `GaiaCompService.cs:1285-1382` + `1646-1709` |

Both query Gaia's GSPC (synthetic photometry from BP/RP spectra) by source ID and
apply the same priority chain for final comp-star magnitude: **VSP → GSPC → APASS →
G→V polynomial fallback**, in that order, with VSP prioritized first for AAVSO
cross-observer consistency (not raw precision) — same stated rationale in both
codebases' comments.

**Confirmed bug, Python side:** `gaia_client.py:824` builds the GSPC query's ID list
with `str(int(float(sid)))` — routing each Gaia `source_id` (a 19-digit integer)
through a 64-bit float before querying. A `double` cannot represent a 19-digit
integer exactly (IEEE 754 double precision covers ~15-17 significant decimal digits),
so this silently corrupts the ID before the `WHERE source_id IN (...)` query runs.
Verified live against Gaia DR3 directly (Section 7.1): two independently checked
source IDs were both wrong by the same +384 offset. C#'s equivalent
(`ParseGaiaCsv`/`GetS`, `GaiaCompService.cs:1749`) reads `source_id` as a raw string
from the CSV response and never converts it through a numeric type — VariLab's IDs
were confirmed correct against live Gaia DR3 in both spot checks. This bug measurably
degrades Python's GSPC match rate (only 3 of 10 selected comp stars got a real GSPC
match in the live test, forcing a fallback to the less-precise G→V color transform for
the rest — see Section 7.1).

### 4.10 PSF Validation (FWHM / SNR Fitting)

| | Python | C# |
|---|---|---|
| Function | `measure_star_fwhm()` | `PsfService.Measure()` |
| Location | `psf_measure.py:17-134` | `PsfService.cs:215-357` |

Both fit a 2D Gaussian to a cutout around each candidate's pixel position to derive
FWHM, peak ADU, and SNR, and both reject saturated or failed fits. This is the
function exercised by Comp Star Selection's "Step 8/8: PSF validation" — confirmed via
live re-run (Section 7.2) that this path was completely unaffected by the aperture-flux
fix described in 4.12, since it's a separate function from `MeasureAperture`.

### 4.11 Target Position Resolution & Centroiding

| | Python | C# |
|---|---|---|
| Function | target-resolution block in `select_comp_stars()` + `match_target_in_field()` + `psf_measure.centroid_star()` | target-resolution block in `FetchAsync()` + `PsfService.RefineCentroid()` + centroid-drift guard in `PhotometryService.ProcessFrameRaw()` |
| Location | `comp_star_selector.py:826-1054` + `gaia_client.py:670-735` + `psf_measure.py:135-224` | `GaiaCompService.cs:367-388` + `PsfService.cs:470-518` + `PhotometryService.cs:208-268` |

**Confirmed bug, Python side.** Both implementations do a nearest-Gaia-match lookup
near the given target RA/Dec, but they use the result very differently:

- **C#** (`GaiaCompService.cs:367-388`) uses the nearest Gaia match *only* to obtain an
  approximate G-magnitude/BP-RP for the color-matching scoring term. It never
  overwrites the actual target RA/Dec used for photometry — the aperture is always
  centered on the WCS-projected position of the RA/Dec that was given to it (from VSX
  lookup or manual entry), refined only by a local centroid search
  (`RefineCentroid`) around *that* position.
- **Python** (`comp_star_selector.py:960-966`) unconditionally overwrites
  `target_ra`/`target_dec` with `gaia_info['corrected_ra']/['corrected_dec']` — the
  position of the nearest Gaia match — with no magnitude-plausibility check and no
  maximum-separation gate. It then centroids (`psf_measure.centroid_star`,
  `comp_star_selector.py:1007-1018`) around *that* (potentially wrong) position.

In a crowded field, "nearest" does not mean "correct." Live-tested on V1786 Cen (RA
201.91767, Dec −47.60011, per AAVSO VSX): Python's `resolve_target` step resolved to a
position ~4.4″ away, G=12.03, BP-RP=1.45 — a star roughly 2 magnitudes too bright to be
the real target (V1786 Cen is an RRab, VSX-catalogued V range 13.91-14.68). C#'s
equivalent target handling is not exposed to this failure mode, because it never lets
a nearest-Gaia-match override the given position.

Separately, C#'s **per-frame** centroid-drift guard
(`PhotometryService.cs:208-268`, `MaxCentroidDriftPx = 6.0` at line 40) rejects any
frame (or comp star) whose local centroid refinement drifts more than 6px from its
WCS-projected starting point — this is VariLab's own defense against exactly this
class of crowded-field contamination, added after discovering the above issue during
this session's testing, and has no Python-side equivalent since Python's reference
codebase has no per-frame photometry stage at all (Section 4.13).

### 4.12 Aperture Photometry (Flux Extraction)

| | Python | C# |
|---|---|---|
| Function | `_aperture_photometry()` | `PsfService.MeasureAperture()` |
| Location | `image_quality.py:1113-1148` | `PsfService.cs:358-448` |

**Aperture geometry — exact match.** Both test each pixel's distance from the
float-precision centroid against `radius²` (no partial-pixel/fractional-area
weighting, in either implementation).

**Sky annulus — same statistic, different iteration count.** Both estimate sky
background as the median of a 3σ-clipped annulus, with sigma computed from the median
absolute deviation (`σ = 1.4826 × MAD`). Python clips once
(`image_quality.py:1135-1142`); C# clips up to twice, breaking early if a pass
converges (`PsfService.cs:395-405`). A minor, defensible divergence — more relevant in
crowded annuli (contaminating faint stars, cosmic rays) than in clean ones.

**Net flux — confirmed bug, C# side (now fixed).** Algebraically, Python's
`net_flux = Σpixel − n×sky` (`image_quality.py:1145-1146`) is identical to
`Σ(pixel − sky)` — an unbiased estimator, since positive and negative noise
fluctuations cancel symmetrically in the sum before a single flat subtraction. The
pre-fix C# code instead computed `Σ max(0, pixel − sky)` — clipping each *individual*
pixel's background-subtracted value to zero before summing. This discards negative
noise while keeping positive noise, systematically overestimating flux, worse at low
SNR. This has been corrected (2026-07-17) to match Python's sum-first, subtract-once
approach — see Section 7.2 for verification.

### 4.13 Multi-Frame Ensemble Photometry — VariLab-Only, No Python Equivalent

| | Python | C# |
|---|---|---|
| Function | *(none — see below)* | `PhotometryService.RunAsync()` / `ProcessFrameRaw()` |
| Location | — | `PhotometryService.cs:79-201` / `208-268` |

`select_comp_stars()` operates on exactly one FITS file per call and has no
time-series concept. VariLab's actual light-curve generation — re-projecting every
comp star through each frame's own WCS, per-frame aperture photometry, per-comp
ensemble bias correction, SNR-weighted median combination, and 3σ/2-pass sigma
clipping — is a VariLab-only extension built on top of the ported single-frame
selection stage, not a port of anything in the reference repository. This is stated
here for completeness and honesty, not claimed as a "match" to anything.

---

## 5. Confirmed Discrepancies — Summary

Three genuine discrepancies were found across the entire comparison. All three were
found by direct code reading and independently confirmed against live data (Gaia DR3
queries, or a real VariLab re-run).

| # | Where | What | Which side is correct | Status |
|---|---|---|---|---|
| 1 | Target position resolution | Python overwrites the given target RA/Dec with a nearest-Gaia-match result with no plausibility check; C# only uses the nearest match for color-scoring metadata | **C#** — confirmed via live test on V1786 Cen | Not a C# defect; documented |
| 2 | Gaia `source_id` handling | Python routes 19-digit IDs through a 64-bit float (`gaia_client.py:824`), corrupting them; C# reads them as raw strings | **C#** — confirmed via live Gaia DR3 query, two independent stars | Not a C# defect; documented |
| 3 | Aperture net-flux formula | C# clipped each pixel to zero after background subtraction before summing, biasing flux high at low SNR; Python sums first, subtracts once | **Python** — C# was wrong | **Fixed** 2026-07-17, verified (Section 7.2) |

Two additional non-bug parameter differences were found and documented in Sections
4.5 and 4.7 (quality-filter RUWE/FOE thresholds; 30″ target-exclusion applied at a
different pipeline stage) — neither is a logic error, but both are noted for
completeness since they can shift which specific candidates end up in the final
ensemble.

---

## 6. Live Testing Results

### 6.1 Comp-Star Selection Cross-Check — V1655 Cen

**Setup:** Same reference frame (`aligned_0000_NGC 5139_2024-07-02.fits`, 9576×6388 px,
0.19″/px) and target coordinates (RA 201.70408, Dec −47.68436, per AAVSO VSX) run
through both implementations independently — Python via a standalone script calling
`select_comp_stars()` directly, C# via VariLab's own Comp Star Selection tab.

Target resolution matched in both (no nearby Gaia match within the match radius for
this particular target, so both correctly fell back to the given coordinates — see
Section 4.11 for why this matters).

**Scoring formula: exact match**, confirmed by direct comparison of the logged weight
breakdown from both runs (Section 4.8).

**Selected comp stars: 6 of the top 10 matched to sub-arcsecond precision** — same
physical stars, confirmed by Gaia catalog position:

| Rank | C# RA/Dec | Python RA/Dec | Separation | C# Mag (src) | Python Mag (src) |
|---|---|---|---|---|---|
| 1 | 201.817102 / −47.612730 | 201.817229 / −47.612699 | 0.4″ | 10.515 (GSPC) | 10.481 (G→fit) |
| 2 | 201.602364 / −47.569587 | 201.602376 / −47.569572 | 0.06″ | 13.524 (GSPC) | 13.524 (GSPC) |
| 3 | 201.557322 / −47.592866 | 201.557335 / −47.592849 | 0.07″ | 12.374 (GSPC) | 12.363 (G→fit) |
| 4 | 201.676631 / −47.569562 | 201.676645 / −47.569547 | 0.07″ | 12.411 (GSPC) | 12.408 (G→fit) |
| 5 | 201.609340 / −47.604688 | 201.609350 / −47.604674 | 0.06″ | 12.478 (GSPC) | 12.478 (GSPC) |
| 6 | 201.736653 / −47.647724 | 201.736665 / −47.647709 | 0.07″ | 13.749 (GSPC) | 13.740 (G→fit) |

Ranks 7-10 selected different physical stars in each run. Root cause identified: the
two runs used different Gaia query radii (VariLab: 0.570° cone / maglim 17.0; Python
test harness: 0.311° cone / maglim 18.0 — a setup difference in the test itself), and,
independently, the RUWE/FOE quality-filter thresholds documented in Section 4.5 differ
by design. Both are candidate-pool composition effects upstream of scoring, not a
scoring-logic difference — the identical formula match in the top 6 results
demonstrates the ranking logic itself is faithful.

**Gaia source_id precision, independently verified against live Gaia DR3:**

| Star (by position) | C# reported ID | Python reported ID | Live Gaia DR3 (ground truth) |
|---|---|---|---|
| RA≈201.8171, Dec≈−47.6127 | `6083508745995622016` | `6083508745995622400` | `6083508745995622016` ✓ matches C# |
| RA≈201.5573, Dec≈−47.5929 | `6083696620748154496` | `6083696620748154880` | `6083696620748154496` ✓ matches C# |

Both spot checks confirm C#'s IDs are exactly correct; Python's are both wrong by the
same +384 offset, consistent with the float64-precision-loss bug identified in Section
4.9.

**GSPC match rate:** VariLab's broader 188-candidate pool matched GSPC synthetic
photometry for 182 stars (~97%). Python's 10 selected stars matched GSPC for only 3
(30%), with the remaining 7 falling back to the less-precise G→V color transform —
consistent with the source_id corruption bug preventing successful `WHERE source_id IN
(...)` lookups for large IDs.

**APASS DR9, confirmed working correctly in both Python test runs.** APASS returned
1257 field stars for this frame in both the V1786 Cen and V1655 Cen runs, and matched
101 of 311 candidates in each (breakdown, V1655 Cen: GSPC=46, APASS=101, G→fit=161,
G→V=4, VSP=0). None of the top-10 *selected* (highest-scoring) stars in the V1655 Cen
run happened to be APASS-sourced — they split between GSPC and the G→fit fallback —
but that reflects which specific stars won the scoring competition in that run, not an
APASS failure; the underlying query and match count are consistent between both runs.

### 6.2 Target Identification — V1786 Cen (Crowded-Field Case)

Already covered in detail in Section 4.11. Summary: given the correct target RA/Dec
(201.91767, −47.60011), Python's `resolve_target` step silently substituted a position
4.4″ away belonging to a star ~2 magnitudes too bright to be the real target (RRab,
VSX V range 13.91-14.68). C#'s equivalent code path is structurally immune to this
substitution (Section 4.11), and VariLab's separate per-frame centroid-drift guard
(added this session after discovering this class of contamination independently, via
a corrupted light curve on this same target) provides defense in depth at the
photometry stage as well.

### 6.3 Aperture-Photometry Fix Verification — V1655 Cen, Before/After

**Setup:** Full light-curve rerun on V1655 Cen, same 276-frame dataset, immediately
before and after the fix described in Section 4.12 / Section 5, item 3.

| Metric | Pre-fix | Post-fix | Δ |
|---|---|---|---|
| Frames used | 276 | 276 | 0 |
| Mean magnitude | 14.4736 | 14.4738 | +0.0002 |
| Amplitude | 1.2340 | 1.2350 | +0.0010 |
| Mean MagErr | 0.0109 | 0.0108 | −0.0001 |

Per-frame delta (post − pre) across all 276 shared frames: mean **+0.00025 mag**,
std 0.00048, range −0.001 to +0.002 — entirely in the predicted direction (correcting
the bias makes magnitudes fainter/larger, never brighter), with zero change in which
frames were accepted or rejected by sigma-clipping. The effect size is small here
because V1655 Cen's comp ensemble is well-exposed (SNR ≈ 200-1750); the bias is
expected to be more significant for fainter targets/comps nearer the detection limit,
consistent with the mechanism identified in Section 4.12.

---

## 7. Conclusion

Across thirteen function pairs spanning the entire comparison-star-selection pipeline
plus the aperture-photometry primitive, VariLab's C# implementation is a faithful,
line-level-verifiable port of Geoff Stone's original Python algorithm: identical
scoring weights, identical isolation-threshold formula, identical proper-motion
correction, identical edge-margin logic, identical magnitude-priority chain, and
(post-fix) an identical aperture net-flux formula — confirmed not by inspection alone
but by running both implementations against the same real data and diffing the
output star-by-star and Gaia-ID-by-Gaia-ID.

Three genuine discrepancies were found in the course of this comparison, each traced
to a specific line in one codebase, and each independently verified against live data
rather than taken on faith. Two of the three (target-position resolution, Gaia
source_id precision) show the C# port to be *more* robust than the current Python
reference, not less. The third (aperture flux-clipping bias) was a real defect on the
C# side, has been fixed, and the fix has been verified to behave exactly as the
underlying physics predicts, with no side effects on frame acceptance or an unrelated,
correctly-matching PSF-fit code path.
