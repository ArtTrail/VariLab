"""
psf_engine.py — headless PSF-fit photometry engine, Phase 1 prototype.

Standalone, no GUI. Reads a JSON job file describing a target, a comparison-star
ensemble, and a directory of plate-solved FITS frames; for every frame, builds a fresh
empirical PSF (ePSF) from isolated field stars, fits the target + comp ensemble (and
any catalog neighbor close enough to overlap them) SIMULTANEOUSLY against that ePSF,
and writes one row per frame to an output CSV.

This is meant to be a drop-in replacement for Pass 1 (raw per-frame flux extraction)
of VariLab's existing aperture-photometry pipeline (PhotometryService.cs) — Pass 2
(ensemble bias correction, weighted-median combination, sigma-clipping) is unchanged
and stays in C#, reading this script's output CSV the same way it currently reads
per-frame aperture flux.

Job JSON schema:
{
  "input_dir": "C:\\path\\to\\fits",
  "output_csv": "C:\\path\\to\\output.csv",
  "target": {"ra": 201.69700, "dec": -47.47947, "label": "Target"},
  "comps": [
    {"ra": 201.7010, "dec": -47.4820, "mag": 14.21, "label": "C1"},
    ...
  ],
  "group_radius_mult": 3.0,      # optional, x FWHM — how far a catalog neighbor can be
                                  # and still be pulled into the same simultaneous fit
  "isolation_mult": 4.0,         # optional, x FWHM — min separation for an ePSF-building
                                  # star to count as "isolated"
  "min_snr": 5.0,                # optional — a fitted flux below this SNR is rejected
                                  # outright rather than kept as a low-confidence point
  "neighbor_search_radius_arcsec": 15.0, # optional — Gaia cone search radius around the
                                  # TARGET only (not comps) for a real, known close
                                  # neighbor to always seed into the fit every frame,
                                  # rather than relying on the residual-finder to
                                  # rediscover it — confirmed unreliable on real data
                                  # (V1786 Cen): the finder didn't group a known 4.4"
                                  # neighbor with the target on every frame, so the
                                  # target's own single-star fit silently absorbed
                                  # the neighbor's flux on those frames instead.
  "max_qfit": 3.0,                # optional — reject a fit whose qfit (sum of abs
                                  # residuals / fitted flux; 0 = perfect fit) exceeds
                                  # this. Matches MAOPhot's own "Max qfit" setting —
                                  # its HelpFile explicitly names this as the intended
                                  # way to catch a fit that "succeeded" (no flags) but
                                  # doesn't actually match the data well.
  "saturation_adu": 60000.0       # optional — MAOPhot's own default "Linearity Limit".
                                  # Any star (target, comp, seeded neighbor, or ePSF-
                                  # calibration star) whose peak pixel exceeds this is
                                  # excluded — matches VariLab's own C# saturation
                                  # convention (PsfService.EstimateSaturation) in spirit.
}

Usage: python psf_engine.py job.json
"""

import json
import os
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.stats import sigma_clipped_stats
from astropy.table import Table
from astropy.time import Time
from astropy.wcs import WCS
from photutils.background import LocalBackground, MMMBackground
from photutils.detection import DAOStarFinder
from photutils.psf import EPSFBuilder, IterativePSFPhotometry, SourceGrouper, extract_stars

# Work around a real astropy==7.2.0 / photutils==2.3.0 incompatibility (confirmed on this
# exact pinned combo, the same one MAOPhot's own requirements.txt specifies): EPSFBuilder's
# internal per-star fit (photutils/psf/epsf.py -> photutils.utils.cutouts._overlap_slices ->
# astropy.nddata.utils.overlap_slices) sometimes calls astropy's overlap_slices with
# small_array_shape as a numpy array rather than a plain tuple. astropy's own top-of-function
# normalization only wraps *scalar* inputs into a tuple, so an already-array-like
# small_array_shape passes through unchanged, and the `small_array_shape != (0, 0)` check
# later in that function does an element-wise array comparison instead of the intended scalar
# one, raising "ValueError: truth value of an array... is ambiguous" instead of the
# PartialOverlapError/NoOverlapError photutils expects and normally handles gracefully (that's
# the "star cannot be fit because its fitting region extends beyond..." warning seen
# elsewhere). Coercing to a plain int tuple before the real function runs restores the
# intended behavior — a real star that's genuinely too close to its cutout edge still (and
# correctly) produces that same graceful warning instead of crashing the whole run.
import photutils.utils.cutouts as _pu_cutouts

_orig_overlap_slices = _pu_cutouts.overlap_slices


def _patched_overlap_slices(large_array_shape, small_array_shape, position, mode="partial"):
    if not isinstance(small_array_shape, tuple) or not all(isinstance(v, int) for v in small_array_shape):
        small_array_shape = tuple(int(v) for v in np.atleast_1d(small_array_shape))
    return _orig_overlap_slices(large_array_shape, small_array_shape, position, mode=mode)


_pu_cutouts.overlap_slices = _patched_overlap_slices


def log(msg):
    # Plain, line-buffered stdout — matches the "Frame i/N: filename" style the C# side
    # (IProgress<string>) already expects from other engines (see TransitLab/EXOTIC).
    print(msg, flush=True)


def load_job(path):
    with open(path, "r", encoding="utf-8") as f:
        job = json.load(f)
    job.setdefault("group_radius_mult", 3.0)
    job.setdefault("isolation_mult", 4.0)
    job.setdefault("min_snr", 5.0)
    job.setdefault("neighbor_search_radius_arcsec", 15.0)
    job.setdefault("max_qfit", 3.0)
    job.setdefault("saturation_adu", 60000.0)
    return job


def is_saturated(image, cx, cy, saturation_adu, radius=3):
    """True if any pixel within `radius` of (cx, cy) meets or exceeds the saturation
    level — matches VariLab's own C# convention (PsfService.EstimateSaturation) in
    spirit. Used both for target/comp/neighbor stars and for candidate ePSF-calibration
    stars: MAOPhot's HelpFile calls this check "much-needed" for the former and applies
    it to the latter too (Find Peaks discards saturated peaks before they can corrupt
    the ePSF model)."""
    rows, cols = image.shape
    ix, iy = int(round(cx)), int(round(cy))
    y0, y1 = max(0, iy - radius), min(rows, iy + radius + 1)
    x0, x1 = max(0, ix - radius), min(cols, ix + radius + 1)
    if y0 >= y1 or x0 >= x1:
        return True  # fully off-image — treat as unusable, same as saturated
    return bool(np.max(image[y0:y1, x0:x1]) >= saturation_adu)


def find_target_neighbors(target_ra, target_dec, radius_arcsec, max_delta_mag=3.5):
    """One-time Gaia cone search around the TARGET's own position — a real, known close
    companion (confirmed on V1786 Cen: 4.4" away, 2.2 mag brighter) needs to be seeded into
    every frame's fit explicitly, since relying on the residual-finder to rediscover it
    fresh each frame was confirmed unreliable on real data (it didn't group the neighbor
    with the target on every frame, so the target's own single-star fit silently absorbed
    the neighbor's flux on the frames where it wasn't caught).

    Magnitude-filtered, not just radius-filtered — confirmed necessary on real data: a plain
    15" cone search in this crowded cluster core returned 49 other Gaia sources, nearly all
    of them 5+ mag fainter (G~18-21 against the target's own G~14.3) and irrelevant to the
    photometry. Only sources within max_delta_mag of the target's own Gaia G magnitude (its
    own nearest cone-search match) are kept — this correctly narrows the V1786 Cen case down
    to just its one real, meaningfully-bright companion (G=12.03, 2.24 mag brighter, easily
    passes) while dropping the noise.

    Returns a list of (ra, dec) tuples, brightest-first. Best effort: returns an empty list
    (not an error) if the query fails, since a missing network connection shouldn't block an
    otherwise-working run.
    """
    try:
        from astroquery.gaia import Gaia
        from astropy.coordinates import SkyCoord
        import astropy.units as u

        coord = SkyCoord(ra=target_ra, dec=target_dec, unit="deg")
        radius = radius_arcsec * u.arcsec
        job = Gaia.cone_search_async(coord, radius=radius)
        results = job.get_results()
        if results is None or len(results) == 0:
            return []

        target_gmag = None
        candidates = []
        for row in results:
            ra, dec = float(row["ra"]), float(row["dec"])
            gmag = float(row["phot_g_mean_mag"]) if row["phot_g_mean_mag"] is not None else None
            sep_arcsec = coord.separation(SkyCoord(ra=ra, dec=dec, unit="deg")).arcsec
            if sep_arcsec < 1.5:
                target_gmag = gmag  # the target's own Gaia match
                continue
            candidates.append((ra, dec, sep_arcsec, gmag))

        if target_gmag is None:
            log("Gaia neighbor lookup: couldn't identify the target's own match to use as a "
                "brightness reference — skipping magnitude filtering, keeping none to be safe.")
            return []

        neighbors = [(ra, dec, sep) for ra, dec, sep, gmag in candidates
                     if gmag is not None and gmag <= target_gmag + max_delta_mag]
        neighbors.sort(key=lambda n: n[2])
        return [(ra, dec) for ra, dec, _sep in neighbors]
    except Exception as ex:
        log(f"Gaia neighbor lookup failed (continuing without it): {ex}")
        return []


def list_fits(input_dir):
    """Only real science frames — confirmed on real data that a calibration directory can
    have master bias/dark/flat frames sitting right next to the light frames (same folder,
    same .fits extension, no naming convention to reliably distinguish them by filename
    alone: this dataset's were named "mbias.fits"/"mdark.fits"/"mflat.fits", but nothing
    guarantees that convention elsewhere). IMAGETYP is the standard, reliable way to tell
    them apart — checking it here means a bias/dark/flat frame is silently excluded before
    it can ever reach the photometry loop and be measured as if it were a real observation
    of the target."""
    exts = (".fits", ".fit", ".fts")
    candidates = sorted(p for p in Path(input_dir).iterdir() if p.suffix.lower() in exts)

    files = []
    for p in candidates:
        try:
            imagetyp = str(fits.getheader(p).get("IMAGETYP", "")).strip().upper()
        except Exception:
            imagetyp = ""
        if imagetyp and imagetyp != "LIGHT":
            log(f"Skipping non-light frame: {p.name} (IMAGETYP={imagetyp})")
            continue
        files.append(p)
    return files


def get_jd(header):
    date_obs = header.get("DATE-OBS")
    if date_obs is None:
        return None
    try:
        return Time(date_obs, format="isot", scale="utc").jd
    except Exception:
        try:
            return Time(date_obs, scale="utc").jd
        except Exception:
            return None


def sky_to_pixel(wcs, ra, dec):
    x, y = wcs.all_world2pix(ra, dec, 0)
    return float(x), float(y)


def estimate_fwhm_px(data, mean, std, positions, box=15):
    """Rough FWHM estimate (2nd-moment, not a fit) from a handful of bright, presumably
    isolated stars — used only to size the ePSF-building/grouping radii below, refined
    implicitly every frame since it's recomputed fresh each time."""
    r = box // 2
    fwhms = []
    for x, y in positions:
        ix, iy = int(round(x)), int(round(y))
        if ix - r < 0 or iy - r < 0 or ix + r >= data.shape[1] or iy + r >= data.shape[0]:
            continue
        cut = data[iy - r:iy + r + 1, ix - r:ix + r + 1].astype(float) - mean
        cut[cut < 0] = 0
        total = cut.sum()
        if total <= 0:
            continue
        yy, xx = np.mgrid[0:cut.shape[0], 0:cut.shape[1]]
        cx = (xx * cut).sum() / total
        cy = (yy * cut).sum() / total
        varx = (cut * (xx - cx) ** 2).sum() / total
        vary = (cut * (yy - cy) ** 2).sum() / total
        sigma = np.sqrt(max(varx, 0) + max(vary, 0)) / np.sqrt(2)
        fwhm = sigma * 2.3548
        if 1.0 < fwhm < box:
            fwhms.append(fwhm)
    return float(np.median(fwhms)) if fwhms else 4.0


def build_epsf(data, mean, std, exclude_xy, isolation_px, size=25, saturation_adu=60000.0):
    """Detect stars, keep only ones isolated from every other detection AND from the
    target/comp positions (exclude_xy) by at least isolation_px, then fit an empirical
    PSF (Anderson & King 2000-style) from their cutouts — same approach MAOPhot uses.
    Also drops any saturated candidate — MAOPhot's own Find Peaks step explicitly
    excludes saturated peaks before they can corrupt the ePSF model; a clipped,
    saturated profile doesn't represent the true PSF shape."""
    finder = DAOStarFinder(fwhm=isolation_px / 2.0, threshold=8.0 * std)
    sources = finder(data - mean)
    if sources is None or len(sources) < 5:
        return None, None

    xy = np.column_stack([sources["xcentroid"], sources["ycentroid"]])

    # Isolation filter: drop any detection with another detection, or a target/comp
    # position, closer than isolation_px — these are exactly the stars we do NOT trust
    # for building a clean PSF model (they're the ones a real fit would need to deblend).
    # Also drop any saturated candidate here.
    keep = np.ones(len(xy), dtype=bool)
    for i, (x, y) in enumerate(xy):
        if is_saturated(data, x, y, saturation_adu):
            keep[i] = False
            continue
        d_other = np.hypot(xy[:, 0] - x, xy[:, 1] - y)
        d_other[i] = np.inf
        if d_other.min() < isolation_px:
            keep[i] = False
            continue
        if exclude_xy:
            d_ex = min(np.hypot(ex - x, ey - y) for ex, ey in exclude_xy)
            if d_ex < isolation_px:
                keep[i] = False

    # Also drop any candidate whose cutout would touch the crop edge — extract_stars()
    # already skips these with a warning, but on real data a partially-clipped cutout
    # occasionally hit a photutils internal bug (ambiguous truth value comparing an
    # overlap-shape array) instead of cleanly skipping. Filtering before extract_stars()
    # ever sees them avoids that edge case entirely rather than working around it.
    half = size // 2 + 2
    in_bounds = (
        (xy[:, 0] >= half) & (xy[:, 0] < data.shape[1] - half) &
        (xy[:, 1] >= half) & (xy[:, 1] < data.shape[0] - half)
    )
    keep = keep & in_bounds

    stars_tbl = sources[keep]
    stars_tbl.sort("flux")
    stars_tbl.reverse()
    stars_tbl = stars_tbl[: min(40, len(stars_tbl))]  # cap for speed
    if len(stars_tbl) < 5:
        return None, None

    nddata_tbl = Table()
    nddata_tbl["x"] = stars_tbl["xcentroid"]
    nddata_tbl["y"] = stars_tbl["ycentroid"]

    from astropy.nddata import NDData

    nddata = NDData(data=data - mean)
    stars = extract_stars(nddata, nddata_tbl, size=size)
    if len(stars) < 5:
        return None, None

    builder = EPSFBuilder(oversampling=2, maxiters=8, progress_bar=False)
    epsf, _ = builder(stars)
    return epsf, len(stars)


def crop_local(data, cx, cy, pad):
    x0 = int(max(0, cx - pad))
    x1 = int(min(data.shape[1] - 1, cx + pad))
    y0 = int(max(0, cy - pad))
    y1 = int(min(data.shape[0] - 1, cy + pad))
    return data[y0:y1 + 1, x0:x1 + 1], (x0, y0)


def build_shared_epsf(reference_path, anchor_xy_full, fwhm_guess_px, build_radius_px=800, saturation_adu=60000.0):
    """Build ONE ePSF from a generously large region of the reference (first) frame,
    reused fixed across every star and every frame — mirrors VariLab's own aperture path,
    which sizes its aperture once from the reference frame and holds it fixed rather than
    adapting per-frame. Confirmed on real data: rebuilding a fresh ePSF per star per frame
    from a small (~400-500px) local crop — too few calibration stars, too noisy a sample —
    was a real source of flux instability; a single ePSF built from a larger, cleaner
    sample (~40 stars from an 800px-radius region) fixed it."""
    with fits.open(reference_path) as hdul:
        data = hdul[0].data.astype(float)
    crop, (x0, y0) = crop_local(data, anchor_xy_full[0], anchor_xy_full[1], build_radius_px)
    mean, median, std = sigma_clipped_stats(crop, sigma=3.0)
    isolation_px = 4.0 * fwhm_guess_px
    epsf, n_stars = build_epsf(crop, median, std, [(anchor_xy_full[0] - x0, anchor_xy_full[1] - y0)],
                                isolation_px, saturation_adu=saturation_adu)
    return epsf, n_stars


def fit_one_star(full_data, epsf, primary_xy_full, other_xy_full, fwhm_guess_px, job):
    """Fit ONE star (the target, or one comp) in its own small local crop, against the
    shared ePSF built once by build_shared_epsf() — rather than one shared crop spanning
    the whole comp ensemble. Real comps are often chosen for photometric quality, not
    proximity — this dataset's comps are up to ~480" (~2465px at this frame's 0.195"/px
    scale) from the target, so a single shared crop covering target+all comps balloons to
    nearly the size of the full mosaic and reintroduces the same slowness a small crop was
    meant to fix. Any OTHER target/comp position that happens to land inside THIS star's
    own local crop is still included in the simultaneous group fit (so a genuinely close
    pair is still deblended correctly) — it just isn't assumed for every star by default."""
    pad = max(200, 30 * fwhm_guess_px)
    crop, (x0, y0) = crop_local(full_data, primary_xy_full[0], primary_xy_full[1], pad)

    # Reject a saturated primary star outright — a clipped peak doesn't match the ePSF
    # template (built from unsaturated calibration stars), so a saturated fit's flux is
    # systematically wrong. MAOPhot calls this check "much-needed"; we had none.
    saturation_adu = job["saturation_adu"]
    if is_saturated(full_data, primary_xy_full[0], primary_xy_full[1], saturation_adu):
        return None, None, "saturated"

    # An "other" position accepted right up to the crop's exact edge caused a
    # photutils/astropy cutout bug (an out-of-bounds fit_shape cutout around it triggers
    # an ambiguous-truth-value crash rather than a clean skip — see the overlap_slices
    # monkeypatch above, which fixes the common case but this margin avoids it entirely
    # for the fit's own init positions).
    edge_margin = 25
    local_positions = [(primary_xy_full[0] - x0, primary_xy_full[1] - y0)]
    for ox, oy in other_xy_full:
        lx, ly = ox - x0, oy - y0
        if edge_margin <= lx < crop.shape[1] - edge_margin and edge_margin <= ly < crop.shape[0] - edge_margin:
            local_positions.append((lx, ly))

    mean, median, std = sigma_clipped_stats(crop, sigma=3.0)
    fwhm_px = estimate_fwhm_px(crop, median, std, [local_positions[0]])
    fit_shape = int(2 * np.ceil(2.5 * fwhm_px) + 1)
    fit_shape = max(7, min(fit_shape, 41))
    if fit_shape % 2 == 0:
        fit_shape += 1
    # MAOPhot's own grouping formula (its HelpFile: "critical separation is computed as
    # FWHM/2 + Fitting Width/2 + Min Separation Bias") is more generous than our previous
    # flat 3xFWHM — its docs explicitly warn under-grouping "can lead to poor fits and
    # high qfit values," exactly our symptom. A seeded neighbor position that isn't
    # actually placed in the same fit GROUP as the target still gets fit independently,
    # silently defeating the whole point of seeding it.
    group_radius = max(job["group_radius_mult"] * fwhm_px, fwhm_px / 2 + fit_shape / 2)

    init_tbl = Table()
    init_tbl["x_0"] = [p[0] for p in local_positions]
    init_tbl["y_0"] = [p[1] for p in local_positions]

    grouper = SourceGrouper(min_separation=group_radius)
    # A fixed 5-15px annulus was smaller than this data's own ~8-9px FWHM — meaning the
    # "background" ring sat partly INSIDE the star's own PSF core, sampling real stellar
    # flux as if it were sky and subtracting it out. That bias is proportionally worse
    # for a faint star (a fixed amount of misattributed flux is a bigger fraction of a
    # small signal), which lines up exactly with the low-SNR degradation seen on real
    # data. Scaling the annulus to the fitted FWHM (matching VariLab's own aperture-mode
    # annulus convention, PhotometryService.cs: 3-5x FWHM) keeps it clear of the star's
    # profile regardless of how wide that profile actually is on a given frame.
    localbkg = LocalBackground(3.0 * fwhm_px, 5.0 * fwhm_px, MMMBackground())
    # A fitted position wandering more than a few FWHM from its WCS-projected starting
    # point is a fit gone wrong, not a legitimate refinement — confirmed on real data:
    # without this bound, fits occasionally diverged to nonsensical positions thousands
    # of pixels outside the crop entirely. Same spirit as VariLab's own centroid-drift
    # guard (PhotometryService.cs, MaxCentroidDriftPx) for the aperture path.
    xy_bound = 3.0 * fwhm_px

    # Always use the iterative, residual-finder-driven fit — not just when a known catalog
    # comp happens to land in this crop. Confirmed on real data (V1786 Cen): a target's own
    # genuine close neighbor is essentially never a Stone-method comp (comps are deliberately
    # chosen to be isolated), so gating deblending on "another catalog position is in-crop"
    # missed exactly the case PSF fitting exists to solve — the neighbor never showed up as
    # an "other" position, so the target's own crop always took the plain single-star path
    # and silently attributed the neighbor's blended flux entirely to the target. The
    # instability that originally motivated gating this off predates several fixes made
    # since (xy_bounds, the FWHM-scaled background annulus, the overlap_slices version-
    # compatibility patch) — re-tested with the iterative path always on and it's stable now.
    residual_finder = DAOStarFinder(fwhm=fwhm_px, threshold=8.0 * std)
    # MAOPhot's HelpFile recommends SLSQP as "useful in crowded fields" — tried it, but
    # confirmed directly on real data that photutils doesn't populate flux_err (returns
    # NaN) for a non-least-squares fitter like SLSQP, since it can't derive a covariance-
    # based error the way TRF's Jacobian does. A NaN error silently defeats the SNR floor
    # (comparisons against NaN are always False, so it wouldn't be caught) and would
    # corrupt VariLab's own SNR-based weighting downstream. Reverted to the default
    # least-squares fitter — explicit now rather than implicit, but not SLSQP.
    # maxiters set explicitly rather than left at IterativePSFPhotometry's own default,
    # so a busier multi-neighbor group gets enough passes to actually converge.
    photometry = IterativePSFPhotometry(
        psf_model=epsf,
        fit_shape=fit_shape,
        finder=residual_finder,
        grouper=grouper,
        localbkg_estimator=localbkg,
        aperture_radius=fwhm_px,
        xy_bounds=xy_bound,
        maxiters=5,
        mode="new",
    )

    result = photometry(crop - median, init_params=init_tbl)
    if result is None or len(result) == 0:
        return None, fwhm_px, "PSF fit produced no results"

    fx, fy = np.array(result["x_fit"]), np.array(result["y_fit"])
    px, py = local_positions[0]
    d = np.hypot(fx - px, fy - py)
    i = int(np.argmin(d))
    if d[i] > fwhm_px * 2:
        return None, fwhm_px, "fit did not converge near expected position"

    # Reject on photutils' own "possible non-convergence" flag (bit 8) — confirmed as the
    # real driver of an amplitude-inflating failure mode on V1786 Cen, a target with a
    # genuine close (4.4"), bright (2.2 mag) neighbor: the simultaneous/joint fit doesn't
    # reliably converge to the right target/neighbor flux split on every single frame, and
    # when it doesn't, the target's flux comes back systematically too bright — 567 of these
    # warnings fired across one 283-frame run. The SNR floor above doesn't catch this (these
    # fits report normal-looking SNR, they're just wrong), so it needs its own explicit check.
    if "flags" in result.colnames:
        flags = int(result["flags"][i])
        if flags & 8:
            return None, fwhm_px, f"possible fit non-convergence (flags={flags})"

    # Reject on qfit (sum of abs. fit residuals / fitted flux; 0 = perfect fit) — this is
    # the metric MAOPhot's own "Max qfit" setting is built around specifically because
    # `flags` alone misses fits that "succeed" numerically but don't actually match the
    # data (confirmed directly on V1786 Cen: the bad frame's target fit had flags=0 but
    # qfit=3.55, visibly elevated against neighboring sources' qfit~1-2 in the same group).
    if "qfit" in result.colnames:
        qfit = float(result["qfit"][i])
        max_qfit = job["max_qfit"]
        if qfit > max_qfit:
            return None, fwhm_px, f"qfit too high ({qfit:.2f} > {max_qfit})"

    flux = float(result["flux_fit"][i])
    flux_err = float(result["flux_err"][i]) if "flux_err" in result.colnames else None

    # Reject outright rather than return a low-confidence value — confirmed on real data
    # (V1655 Cen near its faint pulsation minimum): below a certain SNR, the PSF fit
    # doesn't just get noisier, it can converge to a confidently-wrong, extreme flux value
    # (SNR ~3-15 frames measured a target 4-50x fainter than a normal, high-SNR frame,
    # despite a normal-looking WCS position and no crash). A fixed aperture degrades more
    # gracefully at low SNR than a PSF fit does, which is exactly why this needs an
    # explicit floor here rather than trusting the fit's own error bar to self-regulate —
    # the error bar DID grow for these points, it just didn't grow enough to flag how
    # wrong the central value was.
    min_snr = job.get("min_snr", 5.0)
    if flux_err is None or flux_err <= 0 or flux / flux_err < min_snr:
        return None, fwhm_px, f"SNR below floor ({min_snr})"

    return (flux, flux_err), fwhm_px, None


# Per-worker-process state for the ProcessPoolExecutor pool below. Set once per worker via
# _init_worker (its initializer), not passed as a per-task argument, so the shared epsf/job
# aren't re-pickled on every single frame — only once per worker process at pool startup.
_WORKER_STATE = {}


def _init_worker(job, epsf, fwhm_guess_px, target_neighbors_radec):
    _WORKER_STATE["job"] = job
    _WORKER_STATE["epsf"] = epsf
    _WORKER_STATE["fwhm_guess_px"] = fwhm_guess_px
    _WORKER_STATE["target_neighbors_radec"] = target_neighbors_radec


def _run_frame_worker(path):
    return run_frame(
        path,
        _WORKER_STATE["job"],
        _WORKER_STATE["epsf"],
        _WORKER_STATE["fwhm_guess_px"],
        _WORKER_STATE["target_neighbors_radec"],
    )


def run_frame(path, job, epsf, fwhm_guess_px, target_neighbors_radec=()):
    with fits.open(path) as hdul:
        header = hdul[0].header
        full_data = hdul[0].data.astype(float)

    wcs = WCS(header)
    target_xy_full = sky_to_pixel(wcs, job["target"]["ra"], job["target"]["dec"])
    comp_xy_full = [sky_to_pixel(wcs, c["ra"], c["dec"]) for c in job["comps"]]
    # Known real neighbor(s) of the TARGET specifically (from a one-time Gaia lookup in
    # main(), not the comp list) — projected fresh per frame, same as target/comps, and
    # always included as "other" positions for the target's own fit below so a genuine
    # close companion gets deblended deterministically every frame, not only on the
    # frames where the residual-finder happens to rediscover it on its own.
    neighbor_xy_full = [sky_to_pixel(wcs, ra, dec) for ra, dec in target_neighbors_radec]
    all_xy_full = [target_xy_full] + comp_xy_full
    labels = [job["target"].get("label", "Target")] + [c.get("label", f"C{i+1}") for i, c in enumerate(job["comps"])]

    out = {"jd": get_jd(header), "airmass": header.get("AIRMASS")}
    fwhms = []
    any_ok = False
    for i, label in enumerate(labels):
        primary = all_xy_full[i]
        others = [xy for j, xy in enumerate(all_xy_full) if j != i]
        if i == 0:  # the target
            others = others + neighbor_xy_full
        res, fwhm_px, err = fit_one_star(full_data, epsf, primary, others, fwhm_guess_px, job)
        if fwhm_px:
            fwhms.append(fwhm_px)
        if res is None:
            out[f"{label}_flux"] = None
            out[f"{label}_flux_err"] = None
            continue
        flux, flux_err = res
        out[f"{label}_flux"] = flux
        out[f"{label}_flux_err"] = flux_err
        any_ok = True

    if not any_ok:
        return None, "no star in this frame could be fit"
    out["fwhm_px"] = float(np.median(fwhms)) if fwhms else None
    return out, None


def main():
    if len(sys.argv) != 2:
        print("usage: python psf_engine.py job.json", file=sys.stderr)
        sys.exit(2)

    job = load_job(sys.argv[1])
    files = list_fits(job["input_dir"])
    if not files:
        print("no FITS files found in input_dir", file=sys.stderr)
        sys.exit(1)

    labels = [job["target"].get("label", "Target")] + [c.get("label", f"C{i+1}") for i, c in enumerate(job["comps"])]
    fieldnames = ["jd", "airmass", "fwhm_px"]
    for label in labels:
        fieldnames += [f"{label}_flux", f"{label}_flux_err"]

    log("Checking for a known close neighbor of the target (Gaia cone search)...")
    target_neighbors = find_target_neighbors(
        job["target"]["ra"], job["target"]["dec"], job["neighbor_search_radius_arcsec"])
    if target_neighbors:
        log(f"Found {len(target_neighbors)} neighbor(s) within "
            f"{job['neighbor_search_radius_arcsec']}\" of the target — will always be "
            f"fit simultaneously with it.")
    else:
        log("No neighbor found — target will be fit as an isolated star.")

    # A rough FWHM estimate on the reference frame, just to size the ePSF-building crop
    # and its isolation threshold — refined per-star/per-frame later regardless.
    with fits.open(files[0]) as hdul:
        ref_header = hdul[0].header
        ref_data = hdul[0].data.astype(float)
    ref_wcs = WCS(ref_header)
    target_xy_ref = sky_to_pixel(ref_wcs, job["target"]["ra"], job["target"]["dec"])
    _, ref_median, ref_std = sigma_clipped_stats(
        ref_data[max(0, int(target_xy_ref[1]) - 100):int(target_xy_ref[1]) + 100,
                 max(0, int(target_xy_ref[0]) - 100):int(target_xy_ref[0]) + 100], sigma=3.0)
    fwhm_guess_px = estimate_fwhm_px(ref_data, ref_median, ref_std, [target_xy_ref])
    log(f"Initial FWHM estimate: {fwhm_guess_px:.2f}px")

    log("Building shared ePSF from reference frame...")
    epsf, n_epsf_stars = build_shared_epsf(files[0], target_xy_ref, fwhm_guess_px,
                                            saturation_adu=job["saturation_adu"])
    if epsf is None:
        print("Could not build a shared ePSF from the reference frame", file=sys.stderr)
        sys.exit(1)
    log(f"Shared ePSF built from {n_epsf_stars} stars — reused fixed for every frame below.")

    import csv

    # Resumable: writes each frame's row immediately (flushed) rather than buffering
    # everything in memory until the very end, and tracks which frames were already
    # attempted in a sidecar ".progress" file — confirmed necessary on real data, this
    # run (always-iterative fitting, now with a multi-neighbor group for some targets)
    # got killed by a background-task time limit partway through more than once, and
    # restarting from scratch every time wasted the work already done. Re-running the
    # same job.json now picks up where it left off instead.
    out_path = Path(job["output_csv"])
    out_path.parent.mkdir(parents=True, exist_ok=True)
    progress_path = out_path.with_name(out_path.name + ".progress")

    done_files = set()
    if progress_path.exists():
        done_files = set(progress_path.read_text(encoding="utf-8").splitlines())
        log(f"Resuming: {len(done_files)} frame(s) already attempted in a previous run.")

    write_header = not out_path.exists() or out_path.stat().st_size == 0
    csv_f = open(out_path, "a", newline="", encoding="utf-8")
    writer = csv.DictWriter(csv_f, fieldnames=fieldnames)
    if write_header:
        writer.writeheader()
    progress_f = open(progress_path, "a", encoding="utf-8")

    n_written = 0
    n_skipped_resume = 0
    pending = [p for p in files if p.name not in done_files]
    n_skipped_resume = len(files) - len(pending)

    # Each frame is fit independently against the one shared, already-built ePSF — same
    # independence property the C# Aperture path relies on for its own parallelization —
    # so this was previously the single biggest reason a PSF Fit run only ever used one core
    # regardless of machine size. Real parallelism needs processes here, not threads: this is
    # CPU-bound numpy/photutils work, which the GIL would serialize under threads anyway.
    # epsf/job/fwhm_guess_px/target_neighbors are sent to each worker once via the pool
    # initializer (module-level _WORKER_STATE) rather than re-pickled on every single frame.
    # Capped well below core count, matching the same choice (and the same reasoning) made in
    # PhotometryService.cs's Aperture-mode parallelization: measured directly on real data (20
    # frames), 4 workers finished in the same wall time as (cpu_count() or 1) - 1 = 23 workers
    # (17.3s vs 17.1s) — no throughput benefit from the higher count here, just 23 concurrent
    # Python processes each independently loading astropy/photutils/numpy for no payoff. Don't
    # raise this back toward core count without re-measuring on real data first.
    n_workers = min(4, max(1, (os.cpu_count() or 1) - 1))
    log(f"Running {len(pending)} frame(s) across {n_workers} worker process(es) "
        f"({n_skipped_resume} already done from a previous run)...")

    with ProcessPoolExecutor(
        max_workers=n_workers,
        initializer=_init_worker,
        initargs=(job, epsf, fwhm_guess_px, target_neighbors),
    ) as executor:
        futures = {executor.submit(_run_frame_worker, path): path for path in pending}
        completed = 0
        for future in as_completed(futures):
            path = futures[future]
            completed += 1
            try:
                row, err = future.result()
            except Exception as ex:
                import traceback

                traceback.print_exc()
                row, err = None, f"exception: {ex}"

            log(f"Frame {completed}/{len(pending)}: {path.name}")
            progress_f.write(path.name + "\n")
            progress_f.flush()

            if row is None:
                log(f"  skipped: {err}")
                continue
            writer.writerow(row)
            csv_f.flush()
            n_written += 1

    csv_f.close()
    progress_f.close()
    log(f"Done: wrote {n_written} new row(s) this run "
        f"({n_skipped_resume} already done from a previous run) to {out_path}")


if __name__ == "__main__":
    main()
