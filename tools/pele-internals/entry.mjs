// Test entry for tools/capture-fixtures.mjs (PORT-008 Task 3): the Pele 1.3.0 internals that the vendored core.js
// does not export, bundled from the tag source at C:\projects\pele (detached at 1.3.0, commit 35a83130). Rebuild with the
// pinned esbuild 0.28.2 (Pele's package-lock) from the repo root:
//   node <esbuild 0.28.2>/bin/esbuild tools/pele-internals/entry.mjs --bundle --format=esm --target=es2022 --charset=ascii
//     --legal-comments=none --outfile=tools/pele-internals/pele-internals.mjs
// The built file is committed so the capture never depends on an esbuild install; the capture cross-checks it against
// the vendored core.js (detectType) so a drifted build fails loudly.
export { detect } from 'C:/projects/pele/src/detect.ts';
export { PeleError, locate, syntaxError } from 'C:/projects/pele/src/errors.ts';
export { preprocess, encodeEntities, cleanupComments } from 'C:/projects/pele/src/preprocess.ts';
export { decodeEntities } from 'C:/projects/pele/src/text/entities.ts';
export { metricsMeasurer } from 'C:/projects/pele/src/text/measurer.ts';
export { layoutLabel } from 'C:/projects/pele/src/text/label.ts';
export { num, esc, escText, labelSvg, spanStyle, sharedStyle, textStyle } from 'C:/projects/pele/src/svg/builder.ts';
export { resolveStyle, seriesColor, fontStyle, classNames, ROOT_STYLE, RADIUS, CRITICAL } from 'C:/projects/pele/src/svg/theme.ts';
export { svgDocument } from 'C:/projects/pele/src/svg/root.ts';
export { wordAt } from 'C:/projects/pele/src/diagrams/common/caseless.ts';
export * as tokens from 'C:/projects/pele/src/diagrams/common/tokens.ts';
export { fitWidth, titleRoom, capWidth } from 'C:/projects/pele/src/diagrams/common/fit-width.ts';
