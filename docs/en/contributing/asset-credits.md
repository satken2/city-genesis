# Assets and credits

Unity and Tauri share one game-wide list of external asset sources. When importing images, audio, fonts, 3D models, or textures, verify their source and terms and record credits in the same change. Separating import from attribution can make sources impossible to trace later.

## Authoritative records

`CREDIT_ENTRIES` in `tauri/src/data/credits.ts` is authoritative; comments in that file define the fields. The public repository's `.claude/skills/asset-credits/SKILL.md` describes verification.

Do not guess authors or terms. Preserve required attribution wording without translation or abridgment, and include required copyright notices and license documents in distributions.

An editing-software signature or filename alone does not identify a source. Check that a source URL corresponds to the actual image, and distinguish records for a replaced image from the source of the image currently in use.

## Scope

Original assets are excluded from the external-asset list. External terrain and geographic data are credited, with their source and terms, by the scenarios that contain them (including tutorial cities), not by the game-wide list. Maps are added, removed, and distributed separately from the game, and scenarios derived from a scenario inherit its credits.

Engine and OSS-library notice obligations are separate from the asset credits screen. Manage their required copyright notices and license texts in the distribution.
