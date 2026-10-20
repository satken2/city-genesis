/**
 * Resolve prop thumbnails at src/assets/images/props/{type ID}.png. tools/render_prop_thumbnails.py generates
 * tracked images from normal FBX models. Bundle all images at build time using import.meta.glob; runtime only
 * looks up URLs by type ID. Shared by free placement type tiles and public transport vehicle selection (using
 * free-placement vehicle alias images as appearance samples).
 */

const thumbnails = import.meta.glob<string>("../assets/images/props/*.png", {
  eager: true,
  query: "?url",
  import: "default",
});

/**
 * Return a prop type's thumbnail URL. undefined if not generated; callers display a named fallback.
 */
export function resolvePropThumbnail(id: number): string | undefined {
  return thumbnails[`../assets/images/props/${id}.png`];
}
