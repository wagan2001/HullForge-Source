# Slope-fill references

`vertical-fill-40x15x11.json` is an independent geometry capture of the user's
`handmade_ground_truth/small_no_fill.blueprint` and `small_vfill.blueprint`,
read on 2026-09-08. Parameters: 40×15×11, Bp54 S0 Cp35, Metal, deck, beamified.

Each row is `[x, y, z, rotation, shape]`. `baseBlocks` contains all 599 unchanged
base placements; `slopes` contains all 86 hand-placed additions, on both sides.
Shapes were resolved from each file's own ItemDictionary GUIDs against the
installed structural catalog. Lengths were checked using ArrayPositionsUsed.
Player state, colors, author details, and file-local item IDs are omitted.

The expected rotations remain exactly as hand-saved. Tests normalize only the
1 m wedge equivalents 4/12 and 6/14; all multi-cell rotations remain exact.
Do not regenerate expected slopes from the implementation under test. Updating
this oracle requires a new independently hand-edited reference and its provenance.

`horizontal-fill-40x15x11.json` captures all 108 slopes from the user's
`small_hfill.blueprint` on the same date. Its non-slope placements were checked
against `small_no_fill` and are identical, so the base geometry is shared with
the vertical fixture. The same row schema and catalog resolution apply.
Horizontal expectations use exact shapes, anchors, and rotations 16–19, with
no equivalent-rotation normalization.

`corrected-fill-medium.json` records compact normalized fingerprints for four
medium hulls the user loaded, inspected, and corrected by hand: Typical VFill,
WideShallow HFill, and both LongSternRisers fills. Their unchanged game re-saves
were geometry-identical to the generated candidates before correction. The
fixture retains parameters, bounds, base-cell hashes, placement counts, and
exact smoothing shape/anchor/rotation hashes. It omits player state, item IDs,
colors, block order, and other game-owned metadata.

The three matching BASE files and four corrected fills are preserved under the
user's `handmade_ground_truth/medium_fill_corrections_2026-09-08` folder. The
source path for each corrected fill is recorded in the JSON fixture.
