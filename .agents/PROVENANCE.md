# Provenance and maintenance

## Supplied material

This revision starts from the user's uploaded `.agents.zip`. Its original SHA-256 and
per-file hashes, sizes, and ZIP modes are recorded in `provenance/input-manifest.json`.
The original Super Code entry point describes its reference library as derived from
"superpowers"; the input did not include a pinned upstream revision or a dedicated license
file. No exact upstream repository, revision, authorship, or license has been invented.
No additional upstream skill source was downloaded during this revision.

Existing vendor reference text, source links, examples, and historical experiment notes have
been retained where practical and clearly labeled as non-normative when not portable. Before
redistributing independently, verify upstream provenance and preserve applicable notices.
This revision does not purport to relicense inherited material.

## Revision scope

Shared activation/permission policy, root loaders, capability fallbacks, proportionate workflow
entry points, safe-state wording, and portable deterministic helpers were revised or added.
`CHANGELOG.md` records intentional behavior/API changes; `VALIDATION.md` records actual testing.
`SOURCES.md` contains the official documentation consulted for loading/format compatibility.

## Updating

Compare future upstream updates against the recorded input, not just filenames. Preserve local
changes to activation, permissions, capability fallbacks, task identity, and safe-state rules.
Do not reinstate an always-on dispatcher through a copied reference. Regenerate
`AGENT_CONTEXT.md`, rerun deterministic checks, then run behavioral scenarios in each real host.
Keep project-specific facts in `ARCHITECTURE.md`, not hard-coded into vendor loaders.
